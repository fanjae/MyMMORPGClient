using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

public sealed class TcpSession : IDisposable
{
    public const int HeaderSize = 4;
    public const int MaxPacketSize = 4096;

    private TcpClient _client;
    private NetworkStream _stream;
    private CancellationTokenSource _cancellation;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event Action<ushort, byte[]> PacketReceived;
    public event Action Disconnected;

    public bool IsConnected => _client != null && _client.Connected;

    public async Task ConnectAsync(string host, int port)
    {
        // 재연결 전에 기존 소켓과 수신 루프 정리
        Dispose();

        _client = new TcpClient();
        _cancellation = new CancellationTokenSource();

        try
        {
            await _client.ConnectAsync(host, port);
            _stream = _client.GetStream();

            // 수신 루프는 Unity 메인 스레드 밖에서 실행하고 패킷 이벤트만 상위 계층에 전달한다.
            NetworkStream stream = _stream;
            CancellationToken cancellationToken = _cancellation.Token;
            _ = Task.Run(() => ReceiveLoopAsync(stream, cancellationToken));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public async Task SendAsync(ushort opcode, byte[] payload)
    {
        if (_stream == null)
            throw new InvalidOperationException("Session is not connected.");

        payload ??= Array.Empty<byte>();

        int packetSize = HeaderSize + payload.Length;

        if (packetSize > MaxPacketSize)
            throw new InvalidOperationException("Packet exceeds maximum size.");

        byte[] packet = new byte[packetSize];

        // 서버와 동일한 little-endian 형식으로 Size와 Opcode 기록
        packet[0] = (byte)(packetSize & 0xFF);
        packet[1] = (byte)((packetSize >> 8) & 0xFF);
        packet[2] = (byte)(opcode & 0xFF);
        packet[3] = (byte)((opcode >> 8) & 0xFF);

        Buffer.BlockCopy(payload, 0, packet, HeaderSize, payload.Length);

        // 여러 송신 요청이 겹쳐도 TCP 패킷 바이트가 서로 섞이지 않도록 직렬화한다.
        await _sendLock.WaitAsync();

        try
        {
            if (_stream == null)
                throw new InvalidOperationException("Session is not connected.");

            await _stream.WriteAsync(packet, 0, packet.Length);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        try
        {
            byte[] header = new byte[HeaderSize];

            while (!cancellationToken.IsCancellationRequested)
            {
                // TCP는 패킷 경계를 보장하지 않으므로 헤더 길이만큼 정확히 수신
                await ReadExactAsync(stream, header, HeaderSize, cancellationToken);

                ushort size = (ushort)(header[0] | (header[1] << 8));
                ushort opcode = (ushort)(header[2] | (header[3] << 8));

                if (size < HeaderSize || size > MaxPacketSize)
                    throw new InvalidDataException($"Invalid packet size: {size}");

                int payloadSize = size - HeaderSize;
                byte[] payload = new byte[payloadSize];

                // 헤더에 기록된 크기만큼 Payload를 모두 수신한 뒤 상위 계층에 전달
                if (payloadSize > 0)
                    await ReadExactAsync(stream, payload, payloadSize, cancellationToken);

                PacketReceived?.Invoke(opcode, payload);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                Dispose();
                Disconnected?.Invoke();
            }
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int length, CancellationToken cancellationToken)
    {
        int offset = 0;

        // 한 번의 ReadAsync로 요청한 크기가 모두 들어온다는 보장이 없으므로 누적 수신
        while (offset < length)
        {
            int received = await stream.ReadAsync(buffer, offset, length - offset, cancellationToken).ConfigureAwait(false);

            if (received == 0)
                throw new IOException("Remote endpoint closed the connection.");

            offset += received;
        }
    }

    public void Dispose()
    {
        // 수신 루프 취소 후 네트워크 리소스 해제
        _cancellation?.Cancel();
        _stream?.Dispose();
        _client?.Dispose();

        _cancellation?.Dispose();

        _cancellation = null;
        _stream = null;
        _client = null;
    }
}
