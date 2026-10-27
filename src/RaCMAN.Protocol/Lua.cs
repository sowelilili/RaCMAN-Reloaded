using System.Text;

namespace RaCMAN.Protocol;

/// <summary>
/// The LUA_EXEC request (revision 1.27): <c>u16 len, u16 flags (0), char src[len]</c>, big-endian,
/// with 1..8192 bytes of Lua 5.0 source and no NUL at the end.
/// </summary>
public static class LuaExecRequest
{
    /// <summary>The most source one request carries.</summary>
    public const int MaxSourceBytes = 8192;

    /// <summary>The request's bytes. Throws <see cref="ArgumentException"/> for empty source or more than 8192 bytes of it.</summary>
    public static byte[] Encode(string source)
    {
        var text = Encoding.UTF8.GetBytes(source ?? string.Empty);
        if (text.Length == 0) throw new ArgumentException("there is no Lua source to run", nameof(source));
        if (text.Length > MaxSourceBytes)
        {
            throw new ArgumentException($"Lua source is at most {MaxSourceBytes} bytes, this is {text.Length}", nameof(source));
        }

        var bytes = new byte[4 + text.Length];
        var w = new SpanWriter(bytes);
        w.WriteU16((ushort)text.Length);
        w.WriteU16(0);
        w.WriteBytes(text);
        return bytes;
    }

    /// <summary>The fake console's side: the source of a request, or null for one laid out wrong.</summary>
    public static string? Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;

        var r = new SpanReader(payload);
        int length = r.ReadU16();
        r.ReadU16();
        if (length == 0 || length > MaxSourceBytes || r.Remaining < length) return null;
        return Encoding.UTF8.GetString(r.ReadSpan(length));
    }
}

/// <summary>
/// The LUA_EXEC reply, revision 1.27:
/// <code>
/// s32 lua_status, u32 seq, u8 out_type, u8 out_flags, u16 out_len, char out[out_len]
/// </code>
/// A 12-byte head, big-endian, and the text with no NUL at the end. For <see cref="LuaExecStatus.Ok"/>
/// the text is the chunk's first result as text; for a Lua error (1..5) it is the message.
/// </summary>
public sealed record LuaExecResult(LuaExecStatus Status, uint Seq, LuaType OutType, LuaOutFlags OutFlags, string Output)
{
    public const int HeadSize = 12;

    /// <summary>The chunk ran and returned.</summary>
    public bool Ok => Status == LuaExecStatus.Ok;

    /// <summary>The text was cut to fit the reply.</summary>
    public bool Truncated => (OutFlags & LuaOutFlags.Truncated) != 0;

    /// <summary>
    /// A head shorter than 12 bytes, or text that stops short of <c>out_len</c>, throws
    /// <see cref="ProtocolException"/>. Bytes past the text are left for a later revision.
    /// </summary>
    public static LuaExecResult Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeadSize)
        {
            throw new ProtocolException($"LUA_EXEC needs {HeadSize} bytes, got {payload.Length}");
        }

        var r = new SpanReader(payload);
        var status = (LuaExecStatus)r.ReadI32();
        uint seq = r.ReadU32();
        var type = (LuaType)r.ReadU8();
        var flags = (LuaOutFlags)r.ReadU8();
        int length = r.ReadU16();
        if (r.Remaining < length)
        {
            throw new ProtocolException($"LUA_EXEC says {length} bytes of text and has {r.Remaining}");
        }

        return new LuaExecResult(status, seq, type, flags, Encoding.UTF8.GetString(r.ReadSpan(length)));
    }

    /// <summary>The other direction, for the fake console the tests drive.</summary>
    public byte[] ToBytes()
    {
        var text = Encoding.UTF8.GetBytes(Output);
        if (text.Length > ushort.MaxValue) text = text[..ushort.MaxValue];

        var bytes = new byte[HeadSize + text.Length];
        var w = new SpanWriter(bytes);
        w.WriteU32(unchecked((uint)(int)Status));
        w.WriteU32(Seq);
        w.WriteU8((byte)OutType);
        w.WriteU8((byte)OutFlags);
        w.WriteU16((ushort)text.Length);
        w.WriteBytes(text);
        return bytes;
    }
}

/// <summary>
/// The LUA_STATUS reply, revision 1.27:
/// <code>
/// u8 installed, u8 phase, u16 pad, u32 heartbeat, u32 state, u32 err_seq, u32 err_vm,
/// char err_script[64], u16 err_len, u16 pad, char err[err_len]
/// </code>
/// An 88-byte head, big-endian. <see cref="Heartbeat"/> counts gameplay frames, <see cref="State"/>
/// is the game's lua_State (0 between levels), and <see cref="ErrorSeq"/> goes up by one for each Lua
/// error the game's own scripts report, the last of which is <see cref="ErrorScript"/> and
/// <see cref="Error"/>. The helper's fields are all 0 while <see cref="Installed"/> is not Yes.
/// </summary>
public sealed record LuaStatusInfo(
    LuaInstalled Installed,
    LuaPhase Phase,
    uint Heartbeat,
    uint State,
    uint ErrorSeq,
    uint ErrorVm,
    string ErrorScript,
    string Error)
{
    public const int HeadSize = 88;

    private const int ScriptWidth = 64;

    /// <summary>The helper is in the running game.</summary>
    public bool IsInstalled => Installed == LuaInstalled.Yes;

    /// <summary>
    /// A head shorter than 88 bytes, or an error text that stops short of <c>err_len</c>, throws
    /// <see cref="ProtocolException"/>.
    /// </summary>
    public static LuaStatusInfo Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeadSize)
        {
            throw new ProtocolException($"LUA_STATUS needs {HeadSize} bytes, got {payload.Length}");
        }

        var r = new SpanReader(payload);
        var installed = (LuaInstalled)r.ReadU8();
        var phase = (LuaPhase)r.ReadU8();
        r.Skip(2);
        uint heartbeat = r.ReadU32();
        uint state = r.ReadU32();
        uint errSeq = r.ReadU32();
        uint errVm = r.ReadU32();
        string script = r.ReadFixedString(ScriptWidth);
        int length = r.ReadU16();
        r.Skip(2);
        if (r.Remaining < length)
        {
            throw new ProtocolException($"LUA_STATUS says {length} bytes of error and has {r.Remaining}");
        }

        string error = Encoding.UTF8.GetString(r.ReadSpan(length)).TrimEnd('\0');
        return new LuaStatusInfo(installed, phase, heartbeat, state, errSeq, errVm, script, error);
    }

    /// <summary>The other direction, for the fake console the tests drive.</summary>
    public byte[] ToBytes()
    {
        var text = Encoding.UTF8.GetBytes(Error);
        var bytes = new byte[HeadSize + text.Length];
        var w = new SpanWriter(bytes);
        w.WriteU8((byte)Installed);
        w.WriteU8((byte)Phase);
        w.WriteZeros(2);
        w.WriteU32(Heartbeat);
        w.WriteU32(State);
        w.WriteU32(ErrorSeq);
        w.WriteU32(ErrorVm);
        w.WriteFixedString(ErrorScript, ScriptWidth);
        w.WriteU16((ushort)text.Length);
        w.WriteZeros(2);
        w.WriteBytes(text);
        return bytes;
    }
}
