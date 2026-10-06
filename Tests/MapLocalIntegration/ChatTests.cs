using System.Text;

internal static class ChatTests
{
    internal static int Run()
    {
        try
        {
            Test("map chat trims input and normalizes pasted line breaks", () =>
            {
                Program.Check(ChatCommand.TryParse("　 hello\r\nworld\t! 　", out ChatCommand command, out _) && command.Operation == ChatOperation.Map && command.Message == "hello  world !", "Map command normalization");
            });
            Test("whisper command accepts character IDs and preserves message spaces", () =>
            {
                Program.Check(ChatCommand.TryParse(" /M 2001 안녕  다른 맵 ", out ChatCommand command, out _) && command.Operation == ChatOperation.Whisper && command.TargetCharacterId == 2001 && command.Message == "안녕  다른 맵", "Whisper command");
                Program.Check(ChatCommand.TryParse("/m\t4294967295 message", out command, out _) && command.TargetCharacterId == uint.MaxValue, "Maximum character ID");
            });
            Test("invalid command and target are never sent as map chat", () =>
            {
                foreach (string text in new[] { "/m", "/m 2001", "/m 0 hello", "/m -1 hello", "/m +1 hello", "/m 4294967296 hello", "/m player hello", "/unknown message" })
                    Program.Check(!ChatCommand.TryParse(text, out _, out string error) && error.Length > 0, $"Accepted invalid command: {text}");
            });
            Test("empty, embedded NUL, control and invalid surrogate input are rejected", () =>
            {
                foreach (string text in new[] { "", "　\t", "a\0b", "a\u0001b", "a\u0085b", "a\ud800b" })
                    Program.Check(!ChatCommand.TryParse(text, out _, out _), "Invalid input accepted");
            });
            Test("UTF-8 byte limit applies to body rather than whisper command", () =>
            {
                string boundary = new string('가', 42) + "a";
                Program.Check(Encoding.UTF8.GetByteCount(boundary) == 127 && ChatCommand.TryParse("/m 2001 " + boundary, out ChatCommand command, out _) && command.Message == boundary, "127-byte body rejected");
                Program.Check(!ChatCommand.TryParse(boundary + "b", out _, out _) && !ChatCommand.TryParse(new string('가', 43), out _, out _), "Overlong input accepted");
            });
            Test("whisper request matches packed C++ layout", () =>
            {
                byte[] bytes = GameProtocol.CreateWhisperRequest(2001, "귓속말");
                Program.Check(bytes.Length == 132 && BitConverter.ToUInt32(bytes) == 2001 && Encoding.UTF8.GetString(bytes, 4, 9) == "귓속말" && bytes.Skip(13).All(value => value == 0), "Whisper layout mismatch");
            });
            Test("whisper response preserves Unicode names and IDs", () =>
            {
                using PacketWriter writer = new();
                writer.Write(1001u); writer.Write(2001u);
                writer.WriteFixedString("발신자", GameProtocol.MaxPlayerNameLength);
                writer.WriteFixedString("수신자", GameProtocol.MaxPlayerNameLength);
                writer.WriteFixedString("hello", GameProtocol.MaxChatMessageLength);
                byte[] bytes = writer.ToArray();
                WhisperData data = GameProtocol.ReadWhisper(bytes);
                Program.Check(bytes.Length == 266 && data.SenderCharacterId == 1001 && data.TargetCharacterId == 2001 && data.SenderName == "발신자" && data.TargetName == "수신자" && data.Message == "hello", "Whisper decoded incorrectly");
            });
            Test("chat rejection decodes retry and rejects corrupt payloads", () =>
            {
                using PacketWriter writer = new();
                writer.Write((byte)ChatOperation.Whisper); writer.Write((byte)ChatResult.RateLimited);
                writer.Write(2001u); writer.Write(750u);
                byte[] bytes = writer.ToArray();
                ChatResponseData data = GameProtocol.ReadChatResponse(bytes);
                Program.Check(data.Operation == ChatOperation.Whisper && data.Result == ChatResult.RateLimited && data.TargetCharacterId == 2001 && data.RetryAfterMs == 750, "Chat rejection decoded incorrectly");
                foreach (byte[] invalid in new[] { bytes[..9], bytes.Concat(new byte[1]).ToArray(), new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, new byte[] { 0, 5, 0, 0, 0, 0, 0, 0, 0, 0 } })
                {
                    bool rejected = false;
                    try { GameProtocol.ReadChatResponse(invalid); }
                    catch (Exception) { rejected = true; }
                    Program.Check(rejected, "Corrupt rejection payload accepted");
                }
            });
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[FAIL] {exception.Message}");
            return 1;
        }
    }

    private static void Test(string name, Action test)
    {
        test();
        Console.WriteLine($"[PASS] {name}");
    }
}
