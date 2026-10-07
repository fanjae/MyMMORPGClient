internal static class ManualLatencyProxy
{
    internal static async Task<int> RunAsync(string[] args)
    {
        using CancellationTokenSource stop = new();
        ConsoleCancelEventHandler cancel = (_, signal) => { signal.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            int delay = Read("--delay=", 150), jitter = Read("--jitter=", 60), port = Read("--listen=", 27777);
            if (port == 0) throw new ArgumentException("A fixed listen port is required.");
            while (!stop.IsCancellationRequested)
            {
                await using DelayedGameProxy proxy = new(delay, jitter, port);
                Console.WriteLine($"[READY] 127.0.0.1:{proxy.Port} -> 127.0.0.1:7777, one-way {delay}±{jitter}ms; Ctrl+C stops the proxy.");
                await proxy.Completion.WaitAsync(stop.Token);
            }
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception exception) { Console.Error.WriteLine($"[FAIL] manual proxy: {exception.Message}"); return 1; }
        finally { Console.CancelKeyPress -= cancel; }

        int Read(string prefix, int fallback)
        {
            string[] values = args.Where(arg => arg.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (values.Length == 0) return fallback;
            if (values.Length != 1 || !int.TryParse(values[0][prefix.Length..], out int value)) throw new ArgumentException(prefix + " requires one integer.");
            return value;
        }
    }
}
