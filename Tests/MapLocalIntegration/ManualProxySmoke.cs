using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

internal static class ManualProxySmoke
{
    internal static async Task<int> RunAsync()
    {
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
        TcpListener game = new(IPAddress.Loopback, 7777);
        Process proxy = null;
        Task echo = Task.CompletedTask;
        try
        {
            game.Start();
            echo = EchoAsync();
            ProcessStartInfo start = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (string argument in new[] { "--proxy", "--delay=15", "--jitter=5", "--listen=27777" }) start.ArgumentList.Add(argument);
            proxy = Process.Start(start);
            for (int connection = 0; connection < 2; ++connection)
            {
                string ready = await proxy.StandardOutput.ReadLineAsync().WaitAsync(stop.Token);
                Program.Check(ready?.StartsWith("[READY]") == true, "Manual proxy did not start or reopen listener");
                using TcpClient client = new();
                await client.ConnectAsync("127.0.0.1", 27777, stop.Token);
                byte[] bytes = new byte[26];
                BitConverter.GetBytes((ushort)bytes.Length).CopyTo(bytes, 0);
                BitConverter.GetBytes((ushort)GamePacketOpcode.MovementInput).CopyTo(bytes, 2);
                for (int i = 4; i < bytes.Length; ++i) bytes[i] = (byte)(connection + i);
                Stopwatch transfer = Stopwatch.StartNew();
                await client.GetStream().WriteAsync(bytes, stop.Token);
                byte[] response = new byte[bytes.Length];
                await client.GetStream().ReadExactlyAsync(response, stop.Token);
                Program.Check(bytes.SequenceEqual(response) && transfer.ElapsedMilliseconds >= 15, "Proxy changed packet or skipped delay");
            }
            await echo;
            Console.WriteLine("[PASS] manual latency proxy preserves delayed TCP packets and accepts a fresh connection");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine($"[FAIL] manual proxy smoke: {exception.Message}"); return 1; }
        finally
        {
            stop.Cancel(); game.Stop();
            if (proxy != null)
            {
                if (!proxy.HasExited) proxy.Kill(true);
                await proxy.WaitForExitAsync(); proxy.Dispose();
            }
            try { await echo; } catch (OperationCanceledException) { } catch (SocketException) { }
        }

        async Task EchoAsync()
        {
            for (int connection = 0; connection < 2; ++connection)
            {
                using TcpClient server = await game.AcceptTcpClientAsync(stop.Token);
                byte[] bytes = new byte[26];
                await server.GetStream().ReadExactlyAsync(bytes, stop.Token);
                await server.GetStream().WriteAsync(bytes, stop.Token);
                // 검증용 GameServer도 앞쪽 연결 종료가 전달되는지 확인한다.
                byte[] tail = new byte[1];
                int received = await server.GetStream().ReadAsync(tail, stop.Token);
                Program.Check(received == 0, "Front disconnect was not forwarded to the fake server");
            }
        }
    }
}
