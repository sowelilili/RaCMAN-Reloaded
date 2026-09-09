namespace RaCMAN.Protocol;

/// <summary>
/// One run event as qwark detected it, section 5.11 of PROTOCOL.md (revision 1.5). 16 bytes.
/// <para>
/// qwark emits these unconditionally: it watches game memory, says what happened and keeps no
/// timer and no settings. Deciding what a SPLIT means, and whether it reaches LiveSplit at all,
/// is the client's job.
/// </para>
/// <para>
/// <see cref="TimeMs"/> replaced revision 1.4's tick count: milliseconds since the module started,
/// wrapping every 49 days. It is what the client subtracts to measure a load or a pause, so the
/// arithmetic is unsigned and a wrap comes out right on its own.
/// </para>
/// </summary>
public readonly record struct AutosplitEvent(uint Seq, uint TimeMs, AutosplitKind Kind, byte Code, uint Arg)
{
    public const int Size = 16;

    /// <summary>
    /// The one reason code that means the same thing in every game: the player entered a planet,
    /// and <see cref="Arg"/> is its index in PLANET_LIST order. Every other code is per-game.
    /// </summary>
    public const byte PlanetEnteredCode = 1;

    /// <summary>True when this is the "planet entered" SPLIT the planet route applies to.</summary>
    public bool IsPlanetEntered => Kind == AutosplitKind.Split && Code == PlanetEnteredCode;

    /// <summary>
    /// Milliseconds from an earlier event's <see cref="TimeMs"/> to this one's. The subtraction is
    /// unchecked and unsigned, so the module's 32-bit millisecond counter wrapping between the two
    /// still gives the real gap.
    /// </summary>
    public static uint Elapsed(uint fromMs, uint toMs) => unchecked(toMs - fromMs);

    public static AutosplitEvent Parse(ReadOnlySpan<byte> entry)
    {
        if (entry.Length < Size)
        {
            throw new ProtocolException($"AutosplitEvent needs {Size} bytes, got {entry.Length}");
        }

        var r = new SpanReader(entry[..Size]);
        uint seq = r.ReadU32();
        uint timeMs = r.ReadU32();
        var kind = (AutosplitKind)r.ReadU8();
        byte code = r.ReadU8();
        r.Skip(2);
        uint arg = r.ReadU32();
        return new AutosplitEvent(seq, timeMs, kind, code, arg);
    }

    public void Write(Span<byte> destination)
    {
        var w = new SpanWriter(destination);
        w.WriteU32(Seq);
        w.WriteU32(TimeMs);
        w.WriteU8((byte)Kind);
        w.WriteU8(Code);
        w.WriteU16(0);
        w.WriteU32(Arg);
    }

    public byte[] ToBytes()
    {
        var buffer = new byte[Size];
        Write(buffer);
        return buffer;
    }
}

/// <summary>
/// What one reason code of the running game means, from AUTOSPLIT_DESCRIBE. 32 bytes as of
/// revision 1.5, which added <paramref name="ParamUs"/> and the two timing flags.
/// The label is the client's key for the user's per-code setting, so it is also what the settings
/// file stores; a game that relabels a code takes its default again, which is the safe way round.
/// <para>
/// A row is either a split option the user ticks (<see cref="AutosplitKind.Split"/>) or a timing
/// row that is always applied: a load pair (<see cref="AutosplitKind.LoadStart"/> with a LOAD_END
/// of the same code) or a pause pair (<see cref="AutosplitKind.Pause"/> with a RESUME of the same
/// code). A SPLIT row may also carry <see cref="AutosplitEventFlags.Flat"/>, in which case it is
/// both.
/// </para>
/// </summary>
public sealed record AutosplitEventDesc(
    byte Code, AutosplitKind Kind, AutosplitEventFlags Flags, uint ParamUs, string Label)
{
    public const int Size = 32;

    /// <summary>Whether the user's checkbox starts ticked when the settings file says nothing.</summary>
    public bool EnabledByDefault => (Flags & AutosplitEventFlags.EnabledByDefault) != 0;

    /// <summary>True for the code the planet route applies to, which is only ever code 1.</summary>
    public bool PlanetRoute => (Flags & AutosplitEventFlags.PlanetRoute) != 0;

    /// <summary><see cref="ParamUs"/> comes off game time every time this event happens.</summary>
    public bool Flat => (Flags & AutosplitEventFlags.Flat) != 0;

    /// <summary>Whatever this row's pair lasts beyond <see cref="ParamUs"/> comes off game time.</summary>
    public bool Normalise => (Flags & AutosplitEventFlags.Normalise) != 0;

    /// <summary>True for a row the panel shows as a checkbox; a timing row is never an option.</summary>
    public bool IsSplitOption => Kind == AutosplitKind.Split;

    /// <summary>True when this row corrects game time at all, by either of the two rules.</summary>
    public bool IsTiming => Flat || Normalise;

    public static AutosplitEventDesc Parse(ReadOnlySpan<byte> entry)
    {
        if (entry.Length < Size)
        {
            throw new ProtocolException($"AutosplitEventDesc needs {Size} bytes, got {entry.Length}");
        }

        var r = new SpanReader(entry[..Size]);
        byte code = r.ReadU8();
        var kind = (AutosplitKind)r.ReadU8();
        var flags = (AutosplitEventFlags)r.ReadU8();
        r.Skip(1);
        uint paramUs = r.ReadU32();
        string label = r.ReadFixedString(24);
        return new AutosplitEventDesc(code, kind, flags, paramUs, label);
    }

    public byte[] ToBytes()
    {
        var buffer = new byte[Size];
        var w = new SpanWriter(buffer);
        w.WriteU8(Code);
        w.WriteU8((byte)Kind);
        w.WriteU8((byte)Flags);
        w.WriteU8(0);
        w.WriteU32(ParamUs);
        w.WriteFixedString(Label, 24);
        return buffer;
    }

    /// <summary>The AUTOSPLIT_DESCRIBE payload: <c>u8 n, EventDesc[n]</c>.</summary>
    public static AutosplitEventDesc[] ParseList(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        int n = r.ReadU8();
        var result = new AutosplitEventDesc[n];
        for (int i = 0; i < n; i++) result[i] = Parse(r.ReadSpan(Size));
        return result;
    }

    public static byte[] EncodeList(IReadOnlyList<AutosplitEventDesc> descriptors)
    {
        var buffer = new byte[1 + descriptors.Count * Size];
        var w = new SpanWriter(buffer);
        w.WriteU8((byte)descriptors.Count);
        foreach (var desc in descriptors) w.WriteBytes(desc.ToBytes());
        return buffer;
    }
}

/// <summary>
/// The AUTOSPLIT_EVENTS reply: <c>u32 latest_seq, u8 n, Event[n]</c>. The events are the ones with
/// a sequence number above what the client asked for, oldest first, at most 64 of them.
/// <see cref="LatestSeq"/> is what the console has emitted so far, 0 before its first event, and is
/// what a client records on connect so old events in the ring are never acted on.
/// </summary>
public sealed record AutosplitEventsReply(uint LatestSeq, AutosplitEvent[] Events)
{
    /// <summary>One reply carries at most this many events, section 5.11.</summary>
    public const int MaxEvents = 64;

    public static AutosplitEventsReply Empty { get; } = new(0, Array.Empty<AutosplitEvent>());

    /// <summary>The highest sequence number this reply accounts for: the events', else the latest.</summary>
    public uint HighestSeq
    {
        get
        {
            uint highest = LatestSeq;
            foreach (var ev in Events)
            {
                if (ev.Seq > highest) highest = ev.Seq;
            }

            return highest;
        }
    }

    public static AutosplitEventsReply Parse(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        uint latest = r.ReadU32();
        int n = r.ReadU8();
        var events = new AutosplitEvent[n];
        for (int i = 0; i < n; i++) events[i] = AutosplitEvent.Parse(r.ReadSpan(AutosplitEvent.Size));
        return new AutosplitEventsReply(latest, events);
    }

    public byte[] ToBytes()
    {
        var buffer = new byte[5 + Events.Length * AutosplitEvent.Size];
        var w = new SpanWriter(buffer);
        w.WriteU32(LatestSeq);
        w.WriteU8((byte)Events.Length);
        foreach (var ev in Events) w.WriteBytes(ev.ToBytes());
        return buffer;
    }
}

/// <summary>
/// The 20-byte push datagram qwark sends on the telemetry socket the moment it detects an event:
/// <c>'Q','E', u8 version, u8 reserved, Event(16)</c>. It shares the port with the telemetry
/// packet, so a receiver tells the two apart by the magic and the length before parsing either.
/// The same event goes out on three consecutive ticks; the client dedupes by sequence number.
/// </summary>
public static class AutosplitDatagram
{
    public const int Size = 20;

    public const byte Version = 1;

    public static ReadOnlySpan<byte> Magic => "QE"u8;

    /// <summary>True when these bytes are an autosplit push rather than a telemetry packet.</summary>
    public static bool Matches(ReadOnlySpan<byte> datagram) =>
        datagram.Length == Size && datagram[0] == (byte)'Q' && datagram[1] == (byte)'E';

    public static bool TryParse(ReadOnlySpan<byte> datagram, out AutosplitEvent result)
    {
        result = default;
        if (!Matches(datagram)) return false;

        // A version this client does not know carries a layout it cannot read, so it is dropped
        // rather than guessed at; the TCP poll still delivers the event.
        if (datagram[2] != Version) return false;

        result = AutosplitEvent.Parse(datagram[4..]);
        return true;
    }

    public static byte[] Build(AutosplitEvent ev)
    {
        var buffer = new byte[Size];
        var w = new SpanWriter(buffer);
        w.WriteBytes(Magic);
        w.WriteU8(Version);
        w.WriteU8(0);
        w.WriteBytes(ev.ToBytes());
        return buffer;
    }
}
