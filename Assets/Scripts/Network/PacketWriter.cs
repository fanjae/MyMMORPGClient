using System;
using System.IO;
using System.Text;

public sealed class PacketWriter : IDisposable
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _writer;

    public PacketWriter()
    {
        _writer = new BinaryWriter(_stream, Encoding.UTF8);
    }

    public void Write(byte value)
    {
        _writer.Write(value);
    }

    public void Write(ushort value)
    {
        _writer.Write(value);
    }

    public void Write(uint value)
    {
        _writer.Write(value);
    }

    public void Write(ulong value)
    {
        _writer.Write(value);
    }

    public void Write(int value)
    {
        _writer.Write(value);
    }

    public void Write(long value)
    {
        _writer.Write(value);
    }

    public void WriteFixedString(string value, int byteLength)
    {
        // 고정 길이 필드는 남는 공간을 0으로 유지하기 위해 목적 버퍼를 먼저 생성
        byte[] destination = new byte[byteLength];
        byte[] source = Encoding.UTF8.GetBytes(value);

        if (source.Length >= byteLength)
            throw new ArgumentException($"String exceeds fixed field size: {byteLength}");

        // 실제 문자열 바이트만 복사하고 나머지는 null padding으로 유지
        Array.Copy(source, destination, source.Length);
        _writer.Write(destination);
    }

    public byte[] ToArray()
    {
        return _stream.ToArray();
    }

    public void Dispose()
    {
        _writer.Dispose();
        _stream.Dispose();
    }
}
