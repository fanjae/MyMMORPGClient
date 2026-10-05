using System;
using System.Collections.Generic;
using System.IO;

public enum MovementMode : byte { Free = 0, Platformer = 1 }
public enum MovementStateReason : byte { Normal = 0, Respawned = 1, InputRejected = 2, InputExpired = 3 }

public struct MovementInputData
{
    public uint MapId;
    public ulong Generation;
    public ulong Sequence;
    public sbyte Horizontal;
    public bool JumpHeld;
}

public struct MovementSnapshot
{
    public uint MapId;
    public ulong Generation;
    public uint CharacterId;
    public ulong ServerTick;
    public ulong Sequence;
    public double X;
    public double Y;
    public double VelocityX;
    public double VelocityY;
    public uint FootholdId;
    public bool Grounded;
    public MovementStateReason Reason;
}

public struct FootholdData
{
    public uint Id;
    public int X1;
    public int Y1;
    public int X2;
    public int Y2;
    public uint PrevId;
    public uint NextId;
}

public struct ColliderData
{
    public uint Id;
    public int MinX;
    public int MinY;
    public int MaxX;
    public int MaxY;
}

public sealed class MapGeometryData
{
    public uint MapId;
    public ulong Generation;
    public uint Version;
    public MovementMode Mode;
    public uint HalfWidth;
    public uint HalfHeight;
    public uint HorizontalSpeed;
    public uint JumpSpeed;
    public uint Gravity;
    public uint MaxFallSpeed;
    public int SpawnX;
    public int SpawnY;
    public uint SpawnFootholdId;
    public ushort FootholdCount;
    public ushort ColliderCount;
    public MapInfoData Bounds;
    public readonly Dictionary<uint, FootholdData> Footholds = new();
    public readonly Dictionary<uint, ColliderData> Colliders = new();
}

public static class PlatformProtocol
{
    public static byte[] CreateInput(MovementInputData data)
    {
        using PacketWriter writer = new();
        writer.Write(data.MapId);
        writer.Write(data.Generation);
        writer.Write(data.Sequence);
        writer.Write(unchecked((byte)data.Horizontal));
        writer.Write(data.JumpHeld ? (byte)1 : (byte)0);
        return writer.ToArray();
    }

    public static MovementSnapshot ReadState(byte[] payload)
    {
        PacketReader reader = new(payload, 70);
        MovementSnapshot state = new();
        state.MapId = reader.ReadUInt32();
        state.Generation = reader.ReadUInt64();
        state.CharacterId = reader.ReadUInt32();
        state.ServerTick = reader.ReadUInt64();
        state.Sequence = reader.ReadUInt64();
        state.X = reader.ReadInt64() / 1000.0;
        state.Y = reader.ReadInt64() / 1000.0;
        state.VelocityX = reader.ReadInt64() / 1000.0;
        state.VelocityY = reader.ReadInt64() / 1000.0;
        state.FootholdId = reader.ReadUInt32();
        byte grounded = reader.ReadByte();
        byte reason = reader.ReadByte();
        if (grounded > 1 || reason > 3)
            throw new InvalidDataException("Invalid movement state flags");

        state.Grounded = grounded != 0;
        state.Reason = (MovementStateReason)reason;
        return state;
    }

    public static MapGeometryData ReadGeometry(byte[] payload)
    {
        PacketReader reader = new(payload, 57);
        MapGeometryData data = new();
        data.MapId = reader.ReadUInt32();
        data.Generation = reader.ReadUInt64();
        data.Version = reader.ReadUInt32();
        data.Mode = (MovementMode)reader.ReadByte();
        data.HalfWidth = reader.ReadUInt32();
        data.HalfHeight = reader.ReadUInt32();
        data.HorizontalSpeed = reader.ReadUInt32();
        data.JumpSpeed = reader.ReadUInt32();
        data.Gravity = reader.ReadUInt32();
        data.MaxFallSpeed = reader.ReadUInt32();
        data.SpawnX = reader.ReadInt32();
        data.SpawnY = reader.ReadInt32();
        data.SpawnFootholdId = reader.ReadUInt32();
        data.FootholdCount = reader.ReadUInt16();
        data.ColliderCount = reader.ReadUInt16();
        if (data.Version == 0 || data.Generation == 0 || data.Mode > MovementMode.Platformer || data.FootholdCount > 4096 || data.ColliderCount > 4096 || data.HalfWidth == 0 || data.HalfHeight == 0)
            throw new InvalidDataException("Invalid map geometry settings");

        return data;
    }

    public static void AddFoothold(MapGeometryData data, byte[] payload)
    {
        PacketReader reader = new(payload, 40);
        CheckEntry(data, reader);
        FootholdData foothold = new();
        foothold.Id = reader.ReadUInt32();
        foothold.X1 = reader.ReadInt32();
        foothold.Y1 = reader.ReadInt32();
        foothold.X2 = reader.ReadInt32();
        foothold.Y2 = reader.ReadInt32();
        foothold.PrevId = reader.ReadUInt32();
        foothold.NextId = reader.ReadUInt32();
        if (data.Footholds.Count >= data.FootholdCount || foothold.Id == 0 || foothold.X1 >= foothold.X2 || foothold.Y1 != foothold.Y2 || !data.Footholds.TryAdd(foothold.Id, foothold))
            throw new InvalidDataException("Invalid or duplicate foothold");
    }

    public static void AddCollider(MapGeometryData data, byte[] payload)
    {
        PacketReader reader = new(payload, 32);
        CheckEntry(data, reader);
        ColliderData collider = new();
        collider.Id = reader.ReadUInt32();
        collider.MinX = reader.ReadInt32();
        collider.MinY = reader.ReadInt32();
        collider.MaxX = reader.ReadInt32();
        collider.MaxY = reader.ReadInt32();
        if (data.Colliders.Count >= data.ColliderCount || collider.Id == 0 || collider.MinX >= collider.MaxX || collider.MinY >= collider.MaxY || !data.Colliders.TryAdd(collider.Id, collider))
            throw new InvalidDataException("Invalid or duplicate collider");
    }

    public static void Complete(MapGeometryData data, byte[] payload)
    {
        CheckEntry(data, new PacketReader(payload, 12));
        if (data.Footholds.Count != data.FootholdCount || data.Colliders.Count != data.ColliderCount)
            throw new InvalidDataException("Incomplete map geometry");
    }

    private static void CheckEntry(MapGeometryData data, PacketReader reader)
    {
        uint mapId = reader.ReadUInt32();
        ulong generation = reader.ReadUInt64();
        if (data == null || mapId != data.MapId || generation != data.Generation)
            throw new InvalidDataException("Geometry entry mismatch");
    }
}
