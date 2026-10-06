using System.Diagnostics;

internal static class ChatIntegration
{
    private static string _stage = "chat startup";
    internal static async Task<int> RunAsync()
    {
        try
        {
            using PlatformPeer a = new();
            using PlatformPeer b = new();
            await a.EnterAsync("test", 1001, false);
            await b.EnterAsync("test2", 2001, false);
            await a.WaitAsync(() => a.Players.Contains(2001));
            await a.ChangeAsync(100000001);
            await b.WaitAsync(() => !b.Players.Contains(1001));

            _stage = "cross-map whisper uses authenticated identities and echoes once";
            await a.SendAsync(GamePacketOpcode.WhisperRequest, GameProtocol.CreateWhisperRequest(2001, "다른 맵 안녕하세요"));
            await a.WaitAsync(() => a.Whispers.Count == 1);
            await b.WaitAsync(() => b.Whispers.Count == 1);
            WhisperData whisper = b.Whispers[0];
            Program.Check(whisper.SenderCharacterId == 1001 && whisper.TargetCharacterId == 2001 && whisper.SenderName == a.Name && whisper.TargetName == b.Name && whisper.Message == "다른 맵 안녕하세요" && a.Chat.Count == 0 && b.Chat.Count == 0, "Whisper identity or map scope");
            await a.PumpAsync(60);
            await b.PumpAsync(60);
            Program.Check(a.Whispers.Count == 1 && b.Whispers.Count == 1, "Duplicate whisper echo");
            Pass();

            _stage = "reverse cross-map whisper and self whisper routing";
            await b.SendAsync(GamePacketOpcode.WhisperRequest, GameProtocol.CreateWhisperRequest(1001, "reply"));
            await a.WaitAsync(() => a.Whispers.Count == 2);
            await b.WaitAsync(() => b.Whispers.Count == 2);
            await a.SendAsync(GamePacketOpcode.WhisperRequest, GameProtocol.CreateWhisperRequest(1001, "self"));
            await a.WaitAsync(() => a.Whispers.Count == 3);
            await a.PumpAsync(60); await b.PumpAsync(60);
            Program.Check(a.Whispers.Count == 3 && b.Whispers.Count == 2 && a.Whispers[2].SenderCharacterId == 1001 && a.Whispers[2].TargetCharacterId == 1001, "Self whisper duplicated or leaked");
            Pass();

            _stage = "missing target and malformed chat return explicit errors without disconnect";
            await a.SendAsync(GamePacketOpcode.WhisperRequest, GameProtocol.CreateWhisperRequest(999999999, "missing"));
            await a.WaitAsync(() => a.ChatResponses.Count == 1);
            Program.Check(a.ChatResponses[0].Result == ChatResult.TargetNotFound && a.ChatResponses[0].TargetCharacterId == 999999999, "Missing target result");
            byte[] malformed = new byte[128];
            malformed[0] = 0xc0; malformed[1] = 0x80;
            await a.SendAsync(GamePacketOpcode.ChatRequest, malformed);
            await a.WaitAsync(() => a.ChatResponses.Count == 2);
            Program.Check(a.ChatResponses[1].Result == ChatResult.InvalidMessage, "Invalid UTF-8 accepted or sender disconnected");
            Pass();

            _stage = "map and whisper share burst limit across map transitions";
            await Task.Delay(5100);
            a.ChatResponses.Clear(); a.Chat.Clear(); a.Whispers.Clear(); b.Whispers.Clear();
            Stopwatch burst = Stopwatch.StartNew();
            for (int i = 0; i < 5; ++i)
                await a.SendAsync(i % 2 == 0 ? GamePacketOpcode.ChatRequest : GamePacketOpcode.WhisperRequest, i % 2 == 0 ? GameProtocol.CreateChatRequest("burst") : GameProtocol.CreateWhisperRequest(2001, "burst"));
            await a.WaitAsync(() => a.Chat.Count == 3 && a.Whispers.Count == 2);
            await b.WaitAsync(() => b.Whispers.Count == 2);
            await a.ChangeAsync(100000000);
            await a.WaitAsync(() => a.Players.Contains(2001));
            await b.WaitAsync(() => b.Players.Contains(1001));
            Program.Check(burst.ElapsedMilliseconds < 900, "Burst test too slow to verify one-second refill");
            await a.SendAsync(GamePacketOpcode.WhisperRequest, GameProtocol.CreateWhisperRequest(2001, "limited"));
            await a.WaitAsync(() => a.ChatResponses.Count == 1);
            ChatResponseData limited = a.ChatResponses[0];
            await b.PumpAsync(60);
            Program.Check(limited.Operation == ChatOperation.Whisper && limited.Result == ChatResult.RateLimited && limited.RetryAfterMs > 0 && limited.RetryAfterMs <= 1000 && b.Whispers.Count == 2, "Shared limiter reset on map change or leaked rejected whisper");
            Pass();

            _stage = "chat allowance recovers after advertised retry interval";
            await Task.Delay((int)limited.RetryAfterMs + 80);
            await a.SendAsync(GamePacketOpcode.WhisperRequest, GameProtocol.CreateWhisperRequest(2001, "recovered"));
            await a.WaitAsync(() => a.Whispers.Count == 3);
            await b.WaitAsync(() => b.Whispers.Count == 3);
            Program.Check(a.Whispers[^1].Message == "recovered" && a.ChatResponses.Count == 1, "Retry did not recover");
            Pass();

            _stage = "disconnect, offline target and reconnect rebuild state without stale chat";
            a.Dispose();
            await b.WaitAsync(() => !b.Players.Contains(1001));
            await b.SendAsync(GamePacketOpcode.WhisperRequest, GameProtocol.CreateWhisperRequest(1001, "offline"));
            await b.WaitAsync(() => b.ChatResponses.Count == 1);
            Program.Check(b.ChatResponses[0].Result == ChatResult.TargetNotFound && !b.States.ContainsKey(1001), "Disconnected target retained");
            using PlatformPeer reconnect = new();
            await reconnect.EnterAsync("test", 1001, false);
            await b.WaitAsync(() => b.Players.Contains(1001));
            Program.Check(reconnect.Local.X == 0 && reconnect.Local.Y == 0 && reconnect.Whispers.Count == 0 && reconnect.Chat.Count == 0 && reconnect.ChatResponses.Count == 0, "Reconnect retained previous state");
            await b.SendAsync(GamePacketOpcode.WhisperRequest, GameProtocol.CreateWhisperRequest(1001, "welcome back"));
            await reconnect.WaitAsync(() => reconnect.Whispers.Count == 1);
            Program.Check(reconnect.Whispers[0].Message == "welcome back", "Reconnect whisper delivery failed");
            Pass();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[FAIL] {_stage}: {exception.Message}");
            return 1;
        }
    }

    private static void Pass() => Console.WriteLine($"[PASS] {_stage}");
}
