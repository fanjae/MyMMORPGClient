using System.IO;

public enum LoginResult : byte
{
    Success = 0,
    InvalidCredential = 1,
    ServerError = 2
}

public enum CharacterListResult : byte
{
    Success = 0,
    ServerError = 1
}

public enum CharacterSelectResult : byte
{
    Success = 0,
    InvalidCharacter = 1,
    ServerUnavailable = 2
}

public sealed class CharacterInfo
{
    public uint CharacterId;
    public string Name;
    public ushort Level;
}

public sealed class CharacterListData
{
    public CharacterListResult Result;
    public CharacterInfo[] Characters;
}

public sealed class CharacterSelectData
{
    public CharacterSelectResult Result;
    public ulong AuthKey;
    public ushort GameServerPort;
}

public static class LoginProtocol
{
    public const int MaxLoginIdLength = 32;
    public const int MaxPasswordLength = 64;
    public const int MaxCharacterNameLength = 16;
    public const int MaxCharacterCount = 3;

    public static byte[] CreateLoginRequest(string loginId, string password)
    {
        using PacketWriter writer = new();
        writer.WriteFixedString(loginId, MaxLoginIdLength);
        writer.WriteFixedString(password, MaxPasswordLength);
        return writer.ToArray();
    }

    public static LoginResult ReadLoginResponse(byte[] payload)
    {
        PacketReader reader = new(payload, 1);
        return (LoginResult)reader.ReadByte();
    }

    public static CharacterListData ReadCharacterListResponse(byte[] payload)
    {
        PacketReader reader = new(payload, 2 + MaxCharacterCount * (4 + MaxCharacterNameLength + 2));

        CharacterListResult result = (CharacterListResult)reader.ReadByte();
        byte count = reader.ReadByte();

        if (count > MaxCharacterCount)
            throw new InvalidDataException($"Invalid character count: {count}");

        CharacterInfo[] characters = new CharacterInfo[count];

        // 서버 패킷은 최대 슬롯 수만큼 고정 길이 데이터를 보내므로 모든 슬롯을 읽어 패킷 위치 유지
        for (int i = 0; i < MaxCharacterCount; ++i)
        {
            uint characterId = reader.ReadUInt32();
            string name = reader.ReadFixedString(MaxCharacterNameLength);
            ushort level = reader.ReadUInt16();

            if (i < count)
            {
                characters[i] = new CharacterInfo
                {
                    CharacterId = characterId,
                    Name = name,
                    Level = level
                };
            }
        }

        return new CharacterListData
        {
            Result = result,
            Characters = characters
        };
    }

    public static byte[] CreateCharacterSelectRequest(uint characterId)
    {
        using PacketWriter writer = new();
        writer.Write(characterId);
        return writer.ToArray();
    }

    public static CharacterSelectData ReadCharacterSelectResponse(byte[] payload)
    {
        PacketReader reader = new(payload, 1 + 8 + 2);

        return new CharacterSelectData
        {
            Result = (CharacterSelectResult)reader.ReadByte(),
            AuthKey = reader.ReadUInt64(),
            GameServerPort = reader.ReadUInt16()
        };
    }
}
