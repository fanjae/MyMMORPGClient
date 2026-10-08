using System;
using System.Collections.Generic;
using System.IO;

public enum MovementActionKind : byte { Input = 0, Jump = 1, Land = 2, Checkpoint = 3, Respawn = 4, Fall = 5 }

public struct MovementActionData
{
    public ulong Sequence, ClientTick, JumpId;
    public PlatformState State;
    public int Horizontal;
    public MovementActionKind Kind;
}

public struct RelayedMovementAction
{
    public uint CharacterId;
    public ulong LatestClientTick;
    public MovementActionData Action;
}

public sealed class MovementActionBatch
{
    public uint MapId;
    public ulong Generation, BatchSequence, LatestClientTick;
    public readonly List<MovementActionData> Actions = new();
}

public sealed class MovementActionBroadcast
{
    public uint MapId;
    public ulong Generation, ServerTick;
    public readonly List<RelayedMovementAction> Actions = new();
}

public static class MovementActionProtocol
{
    public const int ActionSize = 63;
    public const int MaxActions = 32;
    public const int MaxBroadcastActions = 54;

    public static byte[] CreateBatch(MovementActionBatch batch)
    {
        if (batch == null || batch.Actions.Count == 0 || batch.Actions.Count > MaxActions)
            throw new InvalidDataException("Invalid movement action count");
        using PacketWriter writer = new();
        writer.Write(batch.MapId); writer.Write(batch.Generation); writer.Write(batch.BatchSequence);
        writer.Write(batch.LatestClientTick); writer.Write((ushort)batch.Actions.Count);
        foreach (MovementActionData action in batch.Actions)
            WriteAction(writer, action);
        return writer.ToArray();
    }

    public static void WriteAction(PacketWriter writer, MovementActionData action)
    {
        writer.Write(action.Sequence); writer.Write(action.ClientTick); writer.Write(action.JumpId);
        writer.Write(Quantize(action.State.X)); writer.Write(Quantize(action.State.Y));
        writer.Write(Quantize(action.State.VelocityX)); writer.Write(Quantize(action.State.VelocityY));
        writer.Write(action.State.FootholdId); writer.Write(unchecked((byte)(sbyte)action.Horizontal));
        writer.Write(action.State.Grounded ? (byte)1 : (byte)0); writer.Write((byte)action.Kind);
    }

    public static MovementActionBroadcast ReadBroadcast(byte[] payload)
    {
        if (payload == null || payload.Length < 22)
            throw new InvalidDataException("Truncated movement broadcast");
        int count = payload[20] | payload[21] << 8;
        if (count == 0 || count > MaxBroadcastActions || payload.Length != 22 + count * (12 + ActionSize))
            throw new InvalidDataException("Invalid movement broadcast count or size");
        PacketReader reader = new(payload, payload.Length);
        MovementActionBroadcast batch = new() { MapId = reader.ReadUInt32(), Generation = reader.ReadUInt64(), ServerTick = reader.ReadUInt64() };
        reader.ReadUInt16();
        for (int i = 0; i < count; ++i)
        {
            RelayedMovementAction relay = new() { CharacterId = reader.ReadUInt32(), LatestClientTick = reader.ReadUInt64() };
            MovementActionData action = new() { Sequence = reader.ReadUInt64(), ClientTick = reader.ReadUInt64(), JumpId = reader.ReadUInt64() };
            action.State = new PlatformState
            {
                X = reader.ReadInt64() / 1000.0, Y = reader.ReadInt64() / 1000.0,
                VelocityX = reader.ReadInt64() / 1000.0, VelocityY = reader.ReadInt64() / 1000.0,
                FootholdId = reader.ReadUInt32()
            };
            action.Horizontal = unchecked((sbyte)reader.ReadByte());
            byte grounded = reader.ReadByte();
            action.Kind = (MovementActionKind)reader.ReadByte();
            if (grounded > 1 || action.Kind > MovementActionKind.Fall || action.Horizontal < -1 || action.Horizontal > 1 ||
                action.Sequence == 0 || action.ClientTick > relay.LatestClientTick)
                throw new InvalidDataException("Invalid movement action");
            action.State.Grounded = grounded != 0;
            relay.Action = action;
            batch.Actions.Add(relay);
        }
        return batch;
    }

    private static long Quantize(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidDataException("Non-finite movement state");
        return checked((long)Math.Round(value * 1000, MidpointRounding.AwayFromZero));
    }
}
