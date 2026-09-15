using System.Buffers.Binary;

namespace RaCMAN.Protocol;

/// <summary>
/// One TCP frame: u32 length, u16 seq, u16 opcode-or-status, payload[length].
/// Section 1 of PROTOCOL.md.
/// </summary>
public readonly struct Frame
{
    public const int HeaderSize = 8;

    /// <summary>
    /// QWARK_MAX_PAYLOAD as of revision 1.11: the most a request this client sends may carry, and
    /// the most a build 37 module will answer with. It is what lets the module stop allocating a
    /// 128 KB block per request, so nothing here asks for more than a 16 KB piece of anything.
    /// </summary>
    public const int MaxPayload = 16384;

    /// <summary>
    /// The most a frame arriving may carry. A module from before revision 1.11 still answers a
    /// 64 KB MEM_READ with a 64 KB reply; this client no longer asks for one, but a reply it has
    /// to be able to read is not the same thing as a request it may send, so the receive side
    /// keeps the old cap and works against either module.
    /// </summary>
    public const int MaxReceivePayload = 65600;

    public ushort Seq { get; }

    /// <summary>Opcode on a request, status on a reply.</summary>
    public ushort Code { get; }

    public byte[] Payload { get; }

    public Frame(ushort seq, ushort code, byte[]? payload)
    {
        Seq = seq;
        Code = code;
        Payload = payload ?? Array.Empty<byte>();
        if (Payload.Length > MaxReceivePayload)
        {
            throw new ProtocolException($"payload of {Payload.Length} bytes exceeds the framing cap ({MaxReceivePayload})");
        }
    }

    public Opcode Opcode => (Opcode)Code;

    public Status Status => (Status)Code;

    /// <summary>
    /// A request, which is where the revision 1.11 cap bites: a payload over
    /// <see cref="MaxPayload"/> throws here, before a byte of it reaches the socket, rather than
    /// having the console close the connection on a frame it cannot buffer.
    /// </summary>
    public static Frame Request(Opcode opcode, ushort seq, byte[]? payload = null)
    {
        if (payload is not null && payload.Length > MaxPayload)
        {
            throw new ProtocolException($"payload of {payload.Length} bytes exceeds QWARK_MAX_PAYLOAD ({MaxPayload})");
        }

        return new Frame(seq, (ushort)opcode, payload);
    }

    public static Frame Reply(Status status, ushort seq, byte[]? payload = null) => new(seq, (ushort)status, payload);

    public byte[] Encode()
    {
        var buffer = new byte[HeaderSize + Payload.Length];
        Encode(buffer);
        return buffer;
    }

    public int Encode(Span<byte> destination)
    {
        int total = HeaderSize + Payload.Length;
        if (destination.Length < total)
        {
            throw new ProtocolException($"frame buffer too small: need {total}, have {destination.Length}");
        }

        BinaryPrimitives.WriteUInt32BigEndian(destination[..4], (uint)Payload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(4, 2), Seq);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(6, 2), Code);
        Payload.CopyTo(destination.Slice(HeaderSize, Payload.Length));
        return total;
    }

    /// <summary>Reads the 8-byte header. A length beyond the cap is fatal for the connection.</summary>
    public static (uint Length, ushort Seq, ushort Code) DecodeHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize)
        {
            throw new ProtocolException($"frame header needs {HeaderSize} bytes, got {header.Length}");
        }

        uint length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        if (length > MaxReceivePayload)
        {
            throw new ProtocolException($"frame length {length} exceeds the framing cap ({MaxReceivePayload})");
        }

        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(4, 2));
        ushort code = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(6, 2));
        return (length, seq, code);
    }

    /// <summary>Decodes one whole frame from a buffer, reporting how many bytes it consumed.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> source, out Frame frame, out int consumed)
    {
        frame = default;
        consumed = 0;
        if (source.Length < HeaderSize) return false;

        var (length, seq, code) = DecodeHeader(source);
        int total = HeaderSize + (int)length;
        if (source.Length < total) return false;

        frame = new Frame(seq, code, source.Slice(HeaderSize, (int)length).ToArray());
        consumed = total;
        return true;
    }

    public override string ToString() => $"Frame(seq={Seq}, code=0x{Code:X4}, {Payload.Length} bytes)";
}

/// <summary>A non-OK reply. Every control in the client turns one of these into a toast.</summary>
public sealed class QwarkStatusException : Exception
{
    public QwarkStatusException(Opcode opcode, Status status)
        : base($"{opcode} failed: {status}")
    {
        Opcode = opcode;
        Status = status;
    }

    public Opcode Opcode { get; }

    public Status Status { get; }
}
