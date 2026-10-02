using System;
using System.IO;
using System.Text;

public sealed class PacketReader
{
    private readonly BinaryReader _reader;

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

    public string ReadFixedString(int byteLength)
    {
        byte[] bytes = _reader.ReadBytes(byteLength);

        // 고정 길이 문자열에서 첫 null 문자를 실제 문자열 종료 위치로 사용
        int length = Array.IndexOf(bytes, (byte)0);

        if (length < 0)
            length = bytes.Length;

        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}
