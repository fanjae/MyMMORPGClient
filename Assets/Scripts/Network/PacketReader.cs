using System;
using System.IO;
using System.Text;

public sealed class PacketReader
{
    private readonly BinaryReader _reader;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public PacketReader(byte[] data, int expectedLength)
    {
        if (data == null || data.Length != expectedLength)
            throw new InvalidDataException($"Invalid payload size: {data?.Length ?? 0}, expected {expectedLength}");

        _reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
    }

    public byte ReadByte()
    {
        return _reader.ReadByte();
    }

    public ushort ReadUInt16()
    {
        return _reader.ReadUInt16();
    }

    public uint ReadUInt32()
    {
        return _reader.ReadUInt32();
    }

    public ulong ReadUInt64()
    {
        return _reader.ReadUInt64();
    }

    public int ReadInt32()
    {
        return _reader.ReadInt32();
    }

    public long ReadInt64()
    {
        return _reader.ReadInt64();
    }

    public string ReadFixedString(int byteLength)
    {
        byte[] bytes = _reader.ReadBytes(byteLength);

        // 고정 길이 문자열에서 첫 null 문자를 실제 문자열 종료 위치로 사용
        int length = Array.IndexOf(bytes, (byte)0);

        if (length < 0)
            throw new InvalidDataException("Fixed string is not null terminated.");

        try
        {
            return StrictUtf8.GetString(bytes, 0, length);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Invalid UTF-8 string.", exception);
        }
    }
}
