using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

internal static class NetworkReliability
{
    internal static async Task<int> RunAsync()
    {
        try
        {
            using (TcpListener closed = new(IPAddress.Loopback, 0))
            using (TcpSession refused = new())
            {
                closed.Start();
                int closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
                closed.Stop();
                Exception failure = null;
                try { await refused.ConnectAsync("127.0.0.1", closedPort); }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused) { failure = exception; }
                Program.Check(failure != null && !refused.IsConnected, "Closed endpoint did not reject connection");
                string message = ConnectionError.Describe(failure, "127.0.0.1", closedPort, "LoginServer");
                Program.Check(message.Contains($"127.0.0.1:{closedPort}") && message.Contains("10061") && message.Contains("연결 거부"), "Refused connection diagnostic missing endpoint or error code");
                Program.Check(ConnectionError.Describe(new TimeoutException(), "host", 7776, "LoginServer").Contains("시간"), "Timeout reported as connection refusal");
                using TcpListener reopened = new(IPAddress.Loopback, closedPort);
                reopened.Start();
                await refused.ConnectAsync("127.0.0.1", closedPort);
                using TcpClient accepted = await reopened.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
                Program.Check(refused.IsConnected, "Connection refusal prevented retry");
            }
            Console.WriteLine("[PASS] Connection refusal identifies endpoint and permits retry after server startup");

            MainThreadPacketQueue queue = new();
            Program.Check(queue.TryEnqueue(() => { }, MainThreadPacketQueue.MaxQueuedBytes), "Queue rejected exact byte limit");
            Program.Check(!queue.TryEnqueue(() => { }, 1), "Queue exceeded byte limit");
            Program.Check(queue.TryDequeue(out _) && queue.QueuedBytes == 0, "Queue did not release bytes");
            for (int i = 0; i < MainThreadPacketQueue.MaxQueuedPackets; ++i)
                Program.Check(queue.TryEnqueue(() => { }, 0), "Queue packet limit changed");
            Program.Check(!queue.TryEnqueue(() => { }, 0), "Queue exceeded packet limit");
            Console.WriteLine("[PASS] Main-thread packet queue enforces byte and packet limits");

            string name = string.Concat(Enumerable.Repeat("😀", 16));
            using PacketWriter writer = new();
            writer.WriteFixedString(name, GameProtocol.MaxPlayerNameLength);
            Program.Check(new PacketReader(writer.ToArray(), 65).ReadFixedString(65) == name, "16-character UTF-8 name changed");
            foreach (byte[] invalid in new[] { new byte[] { 0xe3, 0x81, 0 }, new byte[] { 1, 2, 3 } })
            {
                bool rejected = false;
                try { new PacketReader(invalid, invalid.Length).ReadFixedString(invalid.Length); }
                catch (InvalidDataException) { rejected = true; }
                Program.Check(rejected, "Malformed fixed string accepted");
            }
            Program.Check(LoginProtocol.CreateLoginRequest("test", "test1234").Length == 100 && GameProtocol.CreateEnterGameRequest(1).Length == 12, "Versioned request sizes changed");
            Console.WriteLine("[PASS] UTF-8 names and versioned request formats agree");

            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using TcpSession session = new();
            Channel<(ushort Opcode, byte[] Payload)> packets = Channel.CreateUnbounded<(ushort, byte[])>();
            session.PacketReceived += (opcode, payload) => packets.Writer.TryWrite((opcode, payload));
            int disconnects = 0;
            session.Disconnected += () => Interlocked.Increment(ref disconnects);
            await session.ConnectAsync("127.0.0.1", port);
            using TcpClient first = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
            byte[] frame = Frame(42, new byte[TcpSession.MaxPacketSize - TcpSession.HeaderSize]);
            await first.GetStream().WriteAsync(frame.AsMemory(0, 3));
            Program.Check(!packets.Reader.TryRead(out _), "Partial header dispatched");
            await first.GetStream().WriteAsync(frame.AsMemory(3));
            await first.GetStream().WriteAsync(Frame(43, new byte[] { 7 }));
            var large = await packets.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            var small = await packets.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Program.Check(large.Opcode == 42 && large.Payload.Length == 4092 && small.Opcode == 43 && small.Payload[0] == 7, "Fragmented frame changed");
            Console.WriteLine("[PASS] Fragmented and coalesced TCP frames preserve packet boundaries");

            session.Dispose();
            await session.ConnectAsync("127.0.0.1", port);
            using TcpClient second = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
            first.Dispose();
            await second.GetStream().WriteAsync(Frame(44, new byte[] { 8 }));
            var reconnected = await packets.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Program.Check(reconnected.Opcode == 44 && session.IsConnected && disconnects == 0, "Old receive loop disconnected new connection");
            Console.WriteLine("[PASS] Old receive completion does not disconnect reconnected session");

            List<Task> oldSends = new();
            byte[] payload = new byte[4092];
            for (int i = 0; i < 2000 && session.IsConnected; ++i)
                oldSends.Add(session.SendAsync(50, payload, 250));
            session.Dispose();
            await session.ConnectAsync("127.0.0.1", port);
            using TcpClient third = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
            try { await Task.WhenAll(oldSends).WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
            Program.Check(oldSends.All(task => task.IsCompleted) && oldSends.Any(task => task.IsFaulted || task.IsCanceled) && session.IsConnected, "Old sends affected new connection or remained blocked");
            await session.SendAsync(51, new byte[] { 9 });
            byte[] marker = new byte[5];
            await third.GetStream().ReadExactlyAsync(marker).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Program.Check(marker.SequenceEqual(Frame(51, new byte[] { 9 })), "Old packet leaked to new connection");
            Console.WriteLine("[PASS] Pending sends are bounded and never migrate to new connection");
            third.Dispose();
            Stopwatch closing = Stopwatch.StartNew();
            while (session.IsConnected && closing.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(10);
            Program.Check(!session.IsConnected, "Remote close not observed");
            Console.WriteLine("[PASS] Remote disconnect clears current session");

            await session.ConnectAsync("127.0.0.1", port);
            using TcpClient fourth = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
            using ManualResetEventSlim eventStarted = new();
            using ManualResetEventSlim eventReleased = new();
            using ManualResetEventSlim reconnectStarted = new();
            Action blocked = () => { eventStarted.Set(); eventReleased.Wait(TimeSpan.FromSeconds(2)); };
            session.Disconnected += blocked;
            fourth.Dispose();
            Program.Check(await Task.Run(() => eventStarted.Wait(TimeSpan.FromSeconds(2))), "Disconnect callback not reached");
            Task newConnection = Task.Run(async () => { reconnectStarted.Set(); await session.ConnectAsync("127.0.0.1", port); });
            Program.Check(await Task.Run(() => reconnectStarted.Wait(TimeSpan.FromSeconds(2))), "Reconnect did not start");
            await Task.Delay(50);
            bool ordered = !newConnection.IsCompleted;
            eventReleased.Set();
            await newConnection.WaitAsync(TimeSpan.FromSeconds(2));
            using TcpClient fifth = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(2));
            session.Disconnected -= blocked;
            Program.Check(ordered && session.IsConnected, "Old disconnect callback overtook new connection");
            Console.WriteLine("[PASS] Connection replacement preserves disconnect callback ordering");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[FAIL] Network reliability: {exception.Message}");
            return 1;
        }
    }

    private static byte[] Frame(ushort opcode, byte[] payload)
    {
        byte[] frame = new byte[4 + payload.Length];
        frame[0] = (byte)frame.Length;
        frame[1] = (byte)(frame.Length >> 8);
        frame[2] = (byte)opcode;
        frame[3] = (byte)(opcode >> 8);
        payload.CopyTo(frame, 4);
        return frame;
    }
}
