using System.Text;

namespace RaCMAN.Protocol.Tests;

public class FramingTests
{
    [Fact]
    public void FrameHeaderIsBigEndianLengthSeqCode()
    {
        var frame = Frame.Request(Opcode.FeatureSet, 0x1234, new byte[] { 0xAA, 0xBB });
        var bytes = frame.Encode();

        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x02, 0x12, 0x34, 0x00, 0x21, 0xAA, 0xBB }, bytes);
    }

    [Fact]
    public void FrameRoundTrips()
    {
        var payload = Encoding.ASCII.GetBytes("qwark");
        var bytes = Frame.Reply(Status.NotIngame, 9, payload).Encode();

        Assert.True(Frame.TryDecode(bytes, out var decoded, out int consumed));
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(9, decoded.Seq);
        Assert.Equal(Status.NotIngame, decoded.Status);
        Assert.Equal(payload, decoded.Payload);
    }

    [Fact]
    public void EmptyPayloadRoundTrips()
    {
        var bytes = Frame.Request(Opcode.Heartbeat, 1).Encode();

        Assert.Equal(Frame.HeaderSize, bytes.Length);
        Assert.True(Frame.TryDecode(bytes, out var decoded, out _));
        Assert.Equal(Opcode.Heartbeat, decoded.Opcode);
        Assert.Empty(decoded.Payload);
    }

    [Fact]
    public void PartialFrameIsNotDecoded()
    {
        var bytes = Frame.Request(Opcode.MemRead, 3, new byte[16]).Encode();

        Assert.False(Frame.TryDecode(bytes.AsSpan(0, 6), out _, out _));
        Assert.False(Frame.TryDecode(bytes.AsSpan(0, bytes.Length - 1), out _, out _));
        Assert.True(Frame.TryDecode(bytes, out _, out _));
    }

    [Fact]
    public void TwoFramesDecodeBackToBack()
    {
        var first = Frame.Request(Opcode.Hello, 1, new byte[] { 1 }).Encode();
        var second = Frame.Request(Opcode.GetState, 2).Encode();
        var buffer = first.Concat(second).ToArray();

        Assert.True(Frame.TryDecode(buffer, out var a, out int consumed));
        Assert.Equal(Opcode.Hello, a.Opcode);
        Assert.True(Frame.TryDecode(buffer.AsSpan(consumed), out var b, out _));
        Assert.Equal(Opcode.GetState, b.Opcode);
        Assert.Equal(2, b.Seq);
    }

    [Fact]
    public void LengthBeyondTheCapIsRejected()
    {
        var header = new byte[] { 0x00, 0x01, 0x00, 0x45, 0, 1, 0, 1 }; // 65605 > 65600
        Assert.Throws<ProtocolException>(() => Frame.DecodeHeader(header));
    }

    [Fact]
    public void MaxPayloadIsAccepted()
    {
        var frame = Frame.Request(Opcode.MemWrite, 1, new byte[Frame.MaxPayload]);
        Assert.Equal(Frame.MaxPayload + Frame.HeaderSize, frame.Encode().Length);
        Assert.Throws<ProtocolException>(() => Frame.Request(Opcode.MemWrite, 1, new byte[Frame.MaxPayload + 1]));
    }

    [Fact]
    public void SpanReaderAndWriterAreBigEndian()
    {
        var buffer = new byte[18];
        var w = new SpanWriter(buffer);
        w.WriteU16(0x0102);
        w.WriteU32(0x03040506);
        w.WriteU64(0x0708090A0B0C0D0Eul);
        w.WriteF32(1.0f);

        Assert.Equal(new byte[]
        {
            0x01, 0x02,
            0x03, 0x04, 0x05, 0x06,
            0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E,
            0x3F, 0x80, 0x00, 0x00,
        }, buffer);

        var r = new SpanReader(buffer);
        Assert.Equal(0x0102, r.ReadU16());
        Assert.Equal(0x03040506u, r.ReadU32());
        Assert.Equal(0x0708090A0B0C0D0Eul, r.ReadU64());
        Assert.Equal(1.0f, r.ReadF32());
        Assert.True(r.AtEnd);
    }

    [Fact]
    public void FixedStringsAreNulPaddedAndNeedNoTerminatorWhenFull()
    {
        var buffer = new byte[12];
        var w = new SpanWriter(buffer);
        w.WriteFixedString("NPEA00385", 12);
        Assert.Equal(0, buffer[9]);
        Assert.Equal("NPEA00385", new SpanReader(buffer).ReadFixedString(12));

        var full = new byte[4];
        new SpanWriter(full).WriteFixedString("ABCD", 4);
        Assert.Equal("ABCD", new SpanReader(full).ReadFixedString(4));
    }

    [Fact]
    public void ShortPayloadThrows()
    {
        Assert.Throws<ProtocolException>(() =>
        {
            var reader = new SpanReader(new byte[3]);
            reader.ReadU32();
        });
    }
}
