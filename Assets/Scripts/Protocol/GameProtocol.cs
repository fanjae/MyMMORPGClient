public enum EnterGameResult : byte
{
    Success = 0,
    InvalidAuthKey = 1,
    AlreadyAuthenticated = 2,
    CharacterLoadFailed = 3,
    AlreadyInGame = 4,
    MapEnterFailed = 5,
    ProtocolMismatch = 6
}

public enum ChangeMapResult : byte
{
    Success = 0,
    MapNotFound = 1,
    AlreadyInMap = 2,
    MapEnterFailed = 3
}

public enum MoveResult : byte
{
    Success = 0,
    OutOfBounds = 1,
    SpeedExceeded = 2,
    MapMismatch = 3,
    InvalidSequence = 4,
    WrongMovementMode = 5
}

public struct MoveRequestData
{
    public uint MapId;
    public ulong Sequence;
    public int X;
    public int Y;
}

public struct MoveResponseData
{
    public ulong Sequence;
    public uint MapId;
    public MoveResult Result;
    public int X;
    public int Y;
}

public struct MapInfoData
{
    public uint MapId;
    public int MinX;
    public int MaxX;
    public int MinY;
    public int MaxY;
    public uint MoveSpeed;
    public uint MoveBurst;
}

public sealed class EnterGameData
{
    public EnterGameResult Result;
    public uint CharacterId;
    public string Name;
    public ushort Level;
    public int X;
    public int Y;
}

public sealed class PlayerEnterData
{
    public uint CharacterId;
    public string Name;
    public ushort Level;
    public int X;
    public int Y;
}

public struct PlayerMoveData
{
    public uint CharacterId;
    public int X;
    public int Y;
}

public struct PlayerChatData
{
    public uint CharacterId;
    public string Message;
}

public struct MonsterEnterData
{
    public uint MonsterId;
    public int X;
    public int Y;
}

public struct ChangeMapData
{
    public ChangeMapResult Result;
    public uint MapId;
    public int X;
    public int Y;
}

public static class GameProtocol
{
    public const int MaxPlayerNameLength = 16;
    public const int MaxChatMessageLength = 128;
    private const int PlayerDataSize = 4 + MaxPlayerNameLength + 2 + 4 + 4;

    public static byte[] CreateEnterGameRequest(ulong authKey)
    {
        using PacketWriter writer = new();
        writer.Write(authKey);
        writer.Write(2u);
        return writer.ToArray();
    }

    public static EnterGameData ReadEnterGameResponse(byte[] payload)
    {
        PacketReader reader = new(payload, 1 + PlayerDataSize);

        return new EnterGameData
        {
            Result = (EnterGameResult)reader.ReadByte(),
            CharacterId = reader.ReadUInt32(),
            Name = reader.ReadFixedString(MaxPlayerNameLength),
            Level = reader.ReadUInt16(),
            X = reader.ReadInt32(),
            Y = reader.ReadInt32()
        };
    }

    public static PlayerEnterData ReadPlayerEnterMap(byte[] payload)
    {
        PacketReader reader = new(payload, PlayerDataSize);

        return new PlayerEnterData
        {
            CharacterId = reader.ReadUInt32(),
            Name = reader.ReadFixedString(MaxPlayerNameLength),
            Level = reader.ReadUInt16(),
            X = reader.ReadInt32(),
            Y = reader.ReadInt32()
        };
    }

    public static uint ReadPlayerLeaveMap(byte[] payload)
    {
        PacketReader reader = new(payload, 4);
        return reader.ReadUInt32();
    }

    public static PlayerMoveData ReadPlayerMove(byte[] payload)
    {
        PacketReader reader = new(payload, 4 + 4 + 4);

        return new PlayerMoveData
        {
            CharacterId = reader.ReadUInt32(),
            X = reader.ReadInt32(),
            Y = reader.ReadInt32()
        };
    }

    public static MonsterEnterData ReadMonsterEnterMap(byte[] payload)
    {
        PacketReader reader = new(payload, 4 + 4 + 4);

        return new MonsterEnterData
        {
            MonsterId = reader.ReadUInt32(),
            X = reader.ReadInt32(),
            Y = reader.ReadInt32()
        };
    }

    public static byte[] CreateMoveRequest(MoveRequestData data)
    {
        using PacketWriter writer = new();
        writer.Write(data.MapId);
        writer.Write(data.Sequence);
        writer.Write(data.X);
        writer.Write(data.Y);
        return writer.ToArray();
    }

    public static MoveResponseData ReadMoveResponse(byte[] payload)
    {
        PacketReader reader = new(payload, 8 + 4 + 1 + 4 + 4);
        return new MoveResponseData
        {
            Sequence = reader.ReadUInt64(),
            MapId = reader.ReadUInt32(),
            Result = (MoveResult)reader.ReadByte(),
            X = reader.ReadInt32(),
            Y = reader.ReadInt32()
        };
    }

    public static MapInfoData ReadMapInfo(byte[] payload)
    {
        PacketReader reader = new(payload, 4 + 4 + 4 + 4 + 4 + 4 + 4);
        return new MapInfoData
        {
            MapId = reader.ReadUInt32(),
            MinX = reader.ReadInt32(),
            MaxX = reader.ReadInt32(),
            MinY = reader.ReadInt32(),
            MaxY = reader.ReadInt32(),
            MoveSpeed = reader.ReadUInt32(),
            MoveBurst = reader.ReadUInt32()
        };
    }

    public static byte[] CreateChangeMapRequest(uint mapId)
    {
        using PacketWriter writer = new();
        writer.Write(mapId);
        return writer.ToArray();
    }

    public static byte[] CreateChatRequest(string message)
    {
        using PacketWriter writer = new();
        writer.WriteFixedString(message, MaxChatMessageLength);
        return writer.ToArray();
    }

    public static PlayerChatData ReadPlayerChat(byte[] payload)
    {
        PacketReader reader = new(payload, 4 + MaxChatMessageLength);

        return new PlayerChatData
        {
            CharacterId = reader.ReadUInt32(),
            Message = reader.ReadFixedString(MaxChatMessageLength)
        };
    }

    public static ChangeMapData ReadChangeMapResponse(byte[] payload)
    {
        PacketReader reader = new(payload, 1 + 4 + 4 + 4);

        return new ChangeMapData
        {
            Result = (ChangeMapResult)reader.ReadByte(),
            MapId = reader.ReadUInt32(),
            X = reader.ReadInt32(),
            Y = reader.ReadInt32()
        };
    }
}
