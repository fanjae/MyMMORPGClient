using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

public sealed class TcpSession : IDisposable
{
    public const int HeaderSize = 4;
    public const int MaxPacketSize = 4096;
    public const int MaxQueuedSendBytes = 1024 * 1024;
    private readonly object _lifecycleLock = new();
    private Connection _connection;

    private sealed class Connection
    {
        public readonly TcpClient Client = new();
        public readonly CancellationTokenSource Cancellation = new();
        public readonly SemaphoreSlim SendLock = new(1, 1);
        public readonly CancellationToken Token;
        public NetworkStream Stream;
        public int QueuedBytes;

        public Connection() { Token = Cancellation.Token; }
        public void Close()
        {
            Cancellation.Cancel();
            Client.Dispose();
            // 송신 대기와 수신 루프가 Token을 보유하므로 CTS는 즉시 Dispose하지 않는다.
        }
    }

    public event Action<ushort, byte[]> PacketReceived;
    public event Action Disconnected;
    public bool IsConnected => Volatile.Read(ref _connection)?.Client.Connected == true;

    public async Task ConnectAsync(string host, int port, int timeoutMs = 5000)
    {
        Dispose();
        Connection connection = new();
        lock (_lifecycleLock)
            Interlocked.Exchange(ref _connection, connection)?.Close();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
        timeout.CancelAfter(timeoutMs);
        using CancellationTokenRegistration registration = timeout.Token.Register(() => connection.Client.Dispose());
        try
        {
            await connection.Client.ConnectAsync(host, port);
            if (timeout.IsCancellationRequested || !ReferenceEquals(Volatile.Read(ref _connection), connection))
                throw new OperationCanceledException();
            connection.Stream = connection.Client.GetStream();
            _ = Task.Run(() => ReceiveLoopAsync(connection));
        }
        catch (Exception exception)
        {
            bool timedOut = timeout.IsCancellationRequested && !connection.Token.IsCancellationRequested;
            Disconnect(connection, false);
            if (timedOut)
                throw new TimeoutException("Connection timed out.", exception);
            throw;
        }
    }

    public async Task SendAsync(ushort opcode, byte[] payload, int timeoutMs = 5000)
    {
        Connection connection = Volatile.Read(ref _connection);
        if (connection?.Stream == null)
            throw new InvalidOperationException("Session is not connected.");
        payload ??= Array.Empty<byte>();
        int packetSize = HeaderSize + payload.Length;
        if (packetSize > MaxPacketSize)
            throw new InvalidOperationException("Packet exceeds maximum size.");

        if (Interlocked.Add(ref connection.QueuedBytes, packetSize) > MaxQueuedSendBytes)
        {
            Interlocked.Add(ref connection.QueuedBytes, -packetSize);
            Disconnect(connection, true);
            throw new IOException("Session send queue limit reached.");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Token);
        timeout.CancelAfter(timeoutMs);
        bool acquired = false;
        try
        {
            byte[] packet = new byte[packetSize];
            packet[0] = (byte)packetSize;
            packet[1] = (byte)(packetSize >> 8);
            packet[2] = (byte)opcode;
            packet[3] = (byte)(opcode >> 8);
            Buffer.BlockCopy(payload, 0, packet, HeaderSize, payload.Length);

            // 대기 중 재접속해도 이전 송신은 처음 요청한 Connection에만 적용한다.
            await connection.SendLock.WaitAsync(timeout.Token);
            acquired = true;
            if (!ReferenceEquals(Volatile.Read(ref _connection), connection))
                throw new OperationCanceledException();
            await connection.Stream.WriteAsync(packet, 0, packet.Length, timeout.Token);
        }
        catch
        {
            Disconnect(connection, true);
            throw;
        }
        finally
        {
            if (acquired)
                connection.SendLock.Release();
            Interlocked.Add(ref connection.QueuedBytes, -packetSize);
        }
    }

    private async Task ReceiveLoopAsync(Connection connection)
    {
        try
        {
            byte[] header = new byte[HeaderSize];
            while (!connection.Token.IsCancellationRequested)
            {
                // TCP는 패킷 경계를 보장하지 않으므로 헤더와 Payload를 각각 누적 수신한다.
                await ReadExactAsync(connection.Stream, header, HeaderSize, connection.Token);
                ushort size = (ushort)(header[0] | (header[1] << 8));
                ushort opcode = (ushort)(header[2] | (header[3] << 8));
                if (size < HeaderSize || size > MaxPacketSize)
                    throw new InvalidDataException($"Invalid packet size: {size}");

                byte[] payload = new byte[size - HeaderSize];
                if (payload.Length > 0)
                    await ReadExactAsync(connection.Stream, payload, payload.Length, connection.Token);
                lock (_lifecycleLock)
                {
                    if (ReferenceEquals(Volatile.Read(ref _connection), connection))
                        PacketReceived?.Invoke(opcode, payload);
                }
            }
        }
        catch (Exception)
        {
            Disconnect(connection, true);
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < length)
        {
            int received = await stream.ReadAsync(buffer, offset, length - offset, cancellationToken).ConfigureAwait(false);
            if (received == 0)
                throw new IOException("Remote endpoint closed the connection.");
            offset += received;
        }
    }

    private void Disconnect(Connection connection, bool notify)
    {
        // 이전 수신 루프의 종료는 새 Connection을 닫거나 종료 이벤트를 발생시키지 않는다.
        lock (_lifecycleLock)
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _connection, null, connection), connection))
            {
                connection.Close();
                if (notify)
                    Disconnected?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        // 연결 교체와 이벤트 전달을 직렬화해 이전 이벤트가 새 연결 뒤에 도착하지 않도록 한다.
        lock (_lifecycleLock)
            Interlocked.Exchange(ref _connection, null)?.Close();
    }
}
