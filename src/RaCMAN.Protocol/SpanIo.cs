using System.Buffers.Binary;
using System.Text;

namespace RaCMAN.Protocol;

/// <summary>Thrown when a payload does not match the layout PROTOCOL.md describes.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
}

/// <summary>Big-endian reader. Every integer and float on the wire is big-endian.</summary>
public ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _span;
    private int _pos;

    public SpanReader(ReadOnlySpan<byte> span)
    {
        _span = span;
        _pos = 0;
    }

    public int Position => _pos;
    public int Length => _span.Length;
    public int Remaining => _span.Length - _pos;
    public bool AtEnd => _pos >= _span.Length;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new ProtocolException($"payload too short: wanted {count} bytes at offset {_pos}, {Remaining} left");
        }

        var slice = _span.Slice(_pos, count);
        _pos += count;
        return slice;
    }

    public byte ReadU8() => Take(1)[0];

    public sbyte ReadI8() => (sbyte)Take(1)[0];

    public ushort ReadU16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

    public short ReadI16() => BinaryPrimitives.ReadInt16BigEndian(Take(2));

    public uint ReadU32() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));

    public int ReadI32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

    public ulong ReadU64() => BinaryPrimitives.ReadUInt64BigEndian(Take(8));

    public long ReadI64() => BinaryPrimitives.ReadInt64BigEndian(Take(8));

    public float ReadF32() => BinaryPrimitives.ReadSingleBigEndian(Take(4));

    public double ReadF64() => BinaryPrimitives.ReadDoubleBigEndian(Take(8));

    public byte[] ReadBytes(int count) => Take(count).ToArray();

    public ReadOnlySpan<byte> ReadSpan(int count) => Take(count);

    public byte[] ReadRest() => Take(Remaining).ToArray();

    public void Skip(int count) => Take(count);

    /// <summary>Fixed-width NUL-padded field; the NUL is optional when the field is full.</summary>
    public string ReadFixedString(int width) => DecodeFixed(Take(width));

    public static string DecodeFixed(ReadOnlySpan<byte> raw)
    {
        int end = raw.IndexOf((byte)0);
        if (end < 0) end = raw.Length;
        return Encoding.UTF8.GetString(raw[..end]);
    }
}

/// <summary>Big-endian writer over a caller-supplied buffer.</summary>
public ref struct SpanWriter
{
    private readonly Span<byte> _span;
    private int _pos;

    public SpanWriter(Span<byte> span)
    {
        _span = span;
        _pos = 0;
    }

    public int Position => _pos;
    public int Remaining => _span.Length - _pos;
    public Span<byte> Written => _span[.._pos];

    private Span<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new ProtocolException($"buffer too small: wanted {count} bytes at offset {_pos}, {Remaining} left");
        }

        var slice = _span.Slice(_pos, count);
        _pos += count;
        return slice;
    }

    public void WriteU8(byte value) => Take(1)[0] = value;

    public void WriteU16(ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Take(2), value);

    public void WriteU32(uint value) => BinaryPrimitives.WriteUInt32BigEndian(Take(4), value);

    public void WriteU64(ulong value) => BinaryPrimitives.WriteUInt64BigEndian(Take(8), value);

    public void WriteF32(float value) => BinaryPrimitives.WriteSingleBigEndian(Take(4), value);

    public void WriteBytes(ReadOnlySpan<byte> value) => value.CopyTo(Take(value.Length));

    public void WriteZeros(int count) => Take(count).Clear();

    /// <summary>Writes a NUL-padded fixed-width field, truncating to fit.</summary>
    public void WriteFixedString(string? value, int width)
    {
        var dest = Take(width);
        dest.Clear();
        if (string.IsNullOrEmpty(value)) return;

        var encoded = Encoding.UTF8.GetBytes(value);
        int n = Math.Min(encoded.Length, width);
        encoded.AsSpan(0, n).CopyTo(dest);
    }
}
