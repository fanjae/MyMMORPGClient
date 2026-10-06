using System;
using System.Globalization;
using System.Text;

public struct ChatCommand
{
    public ChatOperation Operation;
    public uint TargetCharacterId;
    public string Message;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryParse(string input, out ChatCommand command, out string error)
    {
        command = default;
        error = "";
        string text = (input ?? "").Trim();
        if (text.StartsWith("/", StringComparison.Ordinal))
        {
            string[] parts = text.Split((char[])null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !string.Equals(parts[0], "/m", StringComparison.OrdinalIgnoreCase) ||
                !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out command.TargetCharacterId) || command.TargetCharacterId == 0)
            {
                error = "귓속말 사용법: /m 캐릭터ID 메시지";
                return false;
            }
            command.Operation = ChatOperation.Whisper;
            text = parts[2];
        }
        command.Message = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Replace('\u2028', ' ').Replace('\u2029', ' ').Trim();
        if (command.Message.Length == 0)
        {
            error = "메시지를 입력하세요.";
            return false;
        }
        foreach (char character in command.Message)
        {
            if (char.IsControl(character))
            {
                error = "메시지에 사용할 수 없는 문자가 있습니다.";
                return false;
            }
        }
        try
        {
            if (StrictUtf8.GetByteCount(command.Message) >= GameProtocol.MaxChatMessageLength)
            {
                error = "메시지는 최대 127 UTF-8 바이트까지 보낼 수 있습니다.";
                return false;
            }
        }
        catch (EncoderFallbackException)
        {
            error = "메시지에 사용할 수 없는 문자가 있습니다.";
            return false;
        }
        return true;
    }
}
