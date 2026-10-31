using System.Text;

namespace RaCMAN.Protocol;

/// <summary>
/// One of a game's debug pages (DEBUG_TEXT, revision 1.29, PROTOCOL.md 5.16): read-only text the
/// console makes from the game's memory. <see cref="Slow"/> pages read a lot of it, so a client asks
/// for them on the user's word alone, never on a timer.
/// </summary>
public sealed record DebugPage(byte Index, bool Slow, string Title);

/// <summary>DEBUG_TEXT's two replies: the page list (request 0xFF) and one page's text.</summary>
public static class DebugText
{
    /// <summary>The request byte that asks for the list.</summary>
    public const byte ListRequest = 0xFF;

    /// <summary>A page's flags: bit0, SLOW.</summary>
    public const byte FlagSlow = 0x01;

    public const int TitleWidth = 23;

    public const int TextHead = 4;

    /// <summary><c>u8 npages, npages x { u8 flags, char title[23] }</c>; short throws <see cref="ProtocolException"/>.</summary>
    public static IReadOnlyList<DebugPage> ParseList(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
        {
            throw new ProtocolException("DEBUG_TEXT's list is empty");
        }

        int count = payload[0];
        int need = 1 + count * (1 + TitleWidth);
        if (payload.Length < need)
        {
            throw new ProtocolException($"DEBUG_TEXT lists {count} pages in {payload.Length} bytes, needs {need}");
        }

        var r = new SpanReader(payload);
        r.Skip(1);
        var pages = new List<DebugPage>(count);
        for (int i = 0; i < count; i++)
        {
            byte flags = r.ReadU8();
            string title = r.ReadFixedString(TitleWidth);
            pages.Add(new DebugPage((byte)i, (flags & FlagSlow) != 0, title));
        }

        return pages;
    }

    /// <summary><c>u16 len, u16 pad, char text[len]</c>; a text that stops short throws <see cref="ProtocolException"/>.</summary>
    public static string ParseText(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < TextHead)
        {
            throw new ProtocolException($"DEBUG_TEXT's page needs {TextHead} bytes, got {payload.Length}");
        }

        var r = new SpanReader(payload);
        int length = r.ReadU16();
        r.Skip(2);
        if (r.Remaining < length)
        {
            throw new ProtocolException($"DEBUG_TEXT says {length} bytes of text and has {r.Remaining}");
        }

        return Encoding.ASCII.GetString(r.ReadSpan(length));
    }

    /// <summary>The list the other way, for the fake console the tests drive.</summary>
    public static byte[] ListBytes(IReadOnlyList<DebugPage> pages)
    {
        var bytes = new byte[1 + pages.Count * (1 + TitleWidth)];
        var w = new SpanWriter(bytes);
        w.WriteU8((byte)pages.Count);
        foreach (var page in pages)
        {
            w.WriteU8(page.Slow ? FlagSlow : (byte)0);
            w.WriteFixedString(page.Title, TitleWidth);
        }

        return bytes;
    }

    /// <summary>A page's text the other way.</summary>
    public static byte[] TextBytes(string text)
    {
        var body = Encoding.ASCII.GetBytes(text);
        var bytes = new byte[TextHead + body.Length];
        var w = new SpanWriter(bytes);
        w.WriteU16((ushort)body.Length);
        w.WriteZeros(2);
        w.WriteBytes(body);
        return bytes;
    }
}
