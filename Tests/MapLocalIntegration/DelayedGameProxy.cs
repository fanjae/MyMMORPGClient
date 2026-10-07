using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

internal sealed class DelayedGameProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _delay, _jitter;
    private readonly Task _relay;
    private TcpClient _front, _back;
    internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    internal Task Completion => _relay;

    internal DelayedGameProxy(int delay, int jitter, int listenPort = 0)
    {
        if (delay < 0 || jitter < 0 || jitter > delay || delay > 5000 || listenPort < 0 || listenPort > 65535)
            throw new ArgumentOutOfRangeException(nameof(delay));
        _delay = delay; _jitter = jitter;
        if (listenPort != 0) _listener = new TcpListener(IPAddress.Loopback, listenPort);
        _listener.Start();
        _relay = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            _front = await _listener.AcceptTcpClientAsync(_stop.Token);
            _back = new TcpClient();
            await _back.ConnectAsync("127.0.0.1", 7777, _stop.Token);
            _front.NoDelay = _back.NoDelay = true;
            await Task.WhenAll(RelayAsync(_front.GetStream(), _back.GetStream(), 0), RelayAsync(_back.GetStream(), _front.GetStream(), 2));
        }
        catch (OperationCanceledException) { }
        catch (IOException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    private async Task RelayAsync(NetworkStream source, NetworkStream destination, int offset)
    {
        // TCP 순서를 유지하며 패킷 수와 크기로 지연 대기량도 제한한다.
        var frames = Channel.CreateBounded<(byte[] Bytes, long Due)>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true });
        Task read = ReadAsync();
        Task write = WriteAsync();
        try { await await Task.WhenAny(read, write); }
        finally
        {
            // 앞쪽 연결이 닫히면 뒤쪽도 즉시 닫아 서버 Player 정리가 지연되지 않게 한다.
            _stop.Cancel(); _front?.Dispose(); _back?.Dispose(); frames.Writer.TryComplete();
            try { await Task.WhenAll(read, write); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
        }

        async Task ReadAsync()
        {
            long lastDue = 0;
            int index = offset;
            int[] jitter = { 0, _jitter, -_jitter, _jitter / 2, -_jitter / 2 };
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    byte[] header = new byte[4];
                    await source.ReadExactlyAsync(header, _stop.Token);
                    int length = BitConverter.ToUInt16(header);
                    if (length < 4 || length > TcpSession.MaxPacketSize)
                        throw new InvalidDataException("Proxy packet length");
                    byte[] bytes = new byte[length];
                    header.CopyTo(bytes, 0);
                    await source.ReadExactlyAsync(bytes.AsMemory(4), _stop.Token);
                    long due = Math.Max(lastDue, _clock.ElapsedMilliseconds + Math.Max(0, _delay + jitter[index++ % jitter.Length]));
                    lastDue = due;
                    await frames.Writer.WriteAsync((bytes, due), _stop.Token);
                }
            }
            finally { frames.Writer.TryComplete(); }
        }

        async Task WriteAsync()
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(_stop.Token))
            {
                int wait = (int)Math.Max(0, frame.Due - _clock.ElapsedMilliseconds);
                if (wait > 0) await Task.Delay(wait, _stop.Token);
                await destination.WriteAsync(frame.Bytes, _stop.Token);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _listener.Stop(); _front?.Dispose(); _back?.Dispose();
        try { await _relay.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        _stop.Dispose();
    }
}
