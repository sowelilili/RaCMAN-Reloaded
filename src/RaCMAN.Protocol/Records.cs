using System.Text;

namespace RaCMAN.Protocol;

/// <summary>The 164-byte session info block, section 3 of PROTOCOL.md (revision 1.1).</summary>
public sealed record SessionInfo(
    byte ProtocolVersion,
    byte QwarkVersion,
    SessionState State,
    GameId Game,
    uint Generation,
    uint Tick,
    string TitleId,
    SessionFlags Flags,
    byte SelectedSlot,
    byte SelectedPlanet,
    PlanetFlags PlanetFlags,
    byte CurrentPlanet,
    float PosX,
    float PosY,
    float PosZ,
    uint PadMask,
    float[] Analog,
    uint[] Readout,
    ulong ToggleState,
    ulong ToggleAuto,
    ulong FreezeActive,
    uint ModLoaded,
    uint ModAuto,
    uint ModPrevious)
{
    public const int Size = 164;

    /// <summary>Length of <c>readout[]</c>, section 3. Sixteen since revision 1.1.</summary>
    public const int ReadoutCount = 16;

    /// <summary>A <c>Feature.Readout</c> of this value means the feature mirrors no readout.</summary>
    public const byte NoReadout = 0xFF;

    public bool PreviousPending => (Flags & SessionFlags.PreviousPending) != 0;

    /// <summary>qwark is running against an emulator (RPCS3) rather than on a console.</summary>
    public bool IsEmulator => (Flags & SessionFlags.Emulator) != 0;

    /// <summary>
    /// The platform refuses code patches, so everything that would write an instruction — a
    /// WRITES_CODE toggle, a mod, a client patch — is answered UNSUPPORTED. The panels grey those
    /// controls out rather than offer a button whose only outcome is an error toast.
    /// </summary>
    public bool CodePatchesUnsupported => (Flags & SessionFlags.NoCodePatches) != 0;

    /// <summary>
    /// The console is holding every combo off until COMBO_ENABLE turns them back on (revision
    /// 1.12). The switch is the console's, kept in its config, so this is what the checkbox on the
    /// Combos panel draws itself from rather than anything the client remembers.
    /// </summary>
    public bool CombosOff => (Flags & SessionFlags.CombosOff) != 0;

    public bool IsIngame => State == SessionState.Ingame;

    /// <summary>
    /// The console is running a title qwark has no game module for: INGAME, the title id filled in
    /// and the game left at <see cref="GameId.None"/>. The memory ops answer for such a session and
    /// every game op answers UNSUPPORTED, so the client offers the memory tools and nothing else.
    /// </summary>
    public bool IsUnknownGame => State == SessionState.Ingame && Game == GameId.None;

    /// <summary>
    /// What to call the game on screen. "no game" belongs to the XMB, where nothing is running at
    /// all; a title the module has nothing for is a game, just not one this client can name.
    /// </summary>
    public string GameName => IsUnknownGame ? "Unknown game" : Game.DisplayName();

    /// <summary>The readout at <paramref name="index"/>, or null when the index names none.</summary>
    public uint? ReadoutAt(int index) =>
        index >= 0 && index < Readout.Length ? Readout[index] : null;

    public static SessionInfo Empty { get; } = new(
        1, 0, SessionState.Xmb, GameId.None, 0, 0, string.Empty, SessionFlags.None, 0, 0,
        Protocol.PlanetFlags.None, 0, 0, 0, 0, 0, new float[4], new uint[ReadoutCount], 0, 0, 0, 0, 0, 0);

    public static SessionInfo Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < Size)
        {
            throw new ProtocolException($"SessionInfo needs {Size} bytes, got {payload.Length}");
        }

        var r = new SpanReader(payload[..Size]);
        byte protocolVersion = r.ReadU8();
        byte qwarkVersion = r.ReadU8();
        var state = (SessionState)r.ReadU8();
        var game = (GameId)r.ReadU8();
        uint generation = r.ReadU32();
        uint tick = r.ReadU32();
        string titleId = r.ReadFixedString(12);
        var flags = (SessionFlags)r.ReadU8();
        byte selectedSlot = r.ReadU8();
        byte selectedPlanet = r.ReadU8();
        var planetFlags = (PlanetFlags)r.ReadU8();
        byte currentPlanet = r.ReadU8();
        r.Skip(3);
        float x = r.ReadF32();
        float y = r.ReadF32();
        float z = r.ReadF32();
        uint padMask = r.ReadU32();
        var analog = new float[4];
        for (int i = 0; i < 4; i++) analog[i] = r.ReadF32();
        var readout = new uint[ReadoutCount];
        for (int i = 0; i < ReadoutCount; i++) readout[i] = r.ReadU32();
        ulong toggleState = r.ReadU64();
        ulong toggleAuto = r.ReadU64();
        ulong freezeActive = r.ReadU64();
        uint modLoaded = r.ReadU32();
        uint modAuto = r.ReadU32();
        uint modPrevious = r.ReadU32();

        return new SessionInfo(protocolVersion, qwarkVersion, state, game, generation, tick, titleId, flags,
            selectedSlot, selectedPlanet, planetFlags, currentPlanet, x, y, z, padMask, analog, readout,
            toggleState, toggleAuto, freezeActive, modLoaded, modAuto, modPrevious);
    }

    public byte[] ToBytes()
    {
        var buffer = new byte[Size];
        Write(buffer);
        return buffer;
    }

    public void Write(Span<byte> destination)
    {
        var w = new SpanWriter(destination);
        w.WriteU8(ProtocolVersion);
        w.WriteU8(QwarkVersion);
        w.WriteU8((byte)State);
        w.WriteU8((byte)Game);
        w.WriteU32(Generation);
        w.WriteU32(Tick);
        w.WriteFixedString(TitleId, 12);
        w.WriteU8((byte)Flags);
        w.WriteU8(SelectedSlot);
        w.WriteU8(SelectedPlanet);
        w.WriteU8((byte)PlanetFlags);
        w.WriteU8(CurrentPlanet);
        w.WriteZeros(3);
        w.WriteF32(PosX);
        w.WriteF32(PosY);
        w.WriteF32(PosZ);
        w.WriteU32(PadMask);
        for (int i = 0; i < 4; i++) w.WriteF32(i < Analog.Length ? Analog[i] : 0f);
        for (int i = 0; i < ReadoutCount; i++) w.WriteU32(i < Readout.Length ? Readout[i] : 0u);
        w.WriteU64(ToggleState);
        w.WriteU64(ToggleAuto);
        w.WriteU64(FreezeActive);
        w.WriteU32(ModLoaded);
        w.WriteU32(ModAuto);
        w.WriteU32(ModPrevious);
    }
}

/// <summary>One live watch value out of a telemetry packet.</summary>
public readonly record struct WatchValue(byte Id, byte Size, bool Valid, ulong Value);

/// <summary>The UDP telemetry packet, section 4 of PROTOCOL.md. GET_STATE returns these bytes too.</summary>
public sealed record TelemetryPacket(SessionInfo Session, WatchValue[] Watches)
{
    public static ReadOnlySpan<byte> Magic => "QWRK"u8;

    public const int MaxWatches = 64;

    public static TelemetryPacket Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4 + SessionInfo.Size + 1)
        {
            throw new ProtocolException($"telemetry packet too short: {payload.Length} bytes");
        }

        if (!payload[..4].SequenceEqual(Magic))
        {
            throw new ProtocolException("telemetry packet is missing the QWRK magic");
        }

        var session = SessionInfo.Parse(payload.Slice(4, SessionInfo.Size));
        var r = new SpanReader(payload[(4 + SessionInfo.Size)..]);
        int count = r.ReadU8();
        var watches = new WatchValue[count];
        for (int i = 0; i < count; i++)
        {
            byte id = r.ReadU8();
            byte size = r.ReadU8();
            bool valid = r.ReadU8() != 0;
            r.Skip(1);
            ulong value = r.ReadU64();
            watches[i] = new WatchValue(id, size, valid, value);
        }

        return new TelemetryPacket(session, watches);
    }

    public byte[] ToBytes()
    {
        var buffer = new byte[4 + SessionInfo.Size + 1 + Watches.Length * 12];
        var w = new SpanWriter(buffer);
        w.WriteBytes(Magic);
        Session.Write(buffer.AsSpan(4, SessionInfo.Size));
        var tail = new SpanWriter(buffer.AsSpan(4 + SessionInfo.Size));
        tail.WriteU8((byte)Watches.Length);
        foreach (var watch in Watches)
        {
            tail.WriteU8(watch.Id);
            tail.WriteU8(watch.Size);
            tail.WriteU8(watch.Valid ? (byte)1 : (byte)0);
            tail.WriteU8(0);
            tail.WriteU64(watch.Value);
        }

        return buffer;
    }
}

/// <summary>
/// One entry of a DESCRIBE feature table, 48 bytes since revision 1.1. <c>Bits</c> is the width of
/// the field behind a VALUE, which revision 1.7 took from the row's padding; 0 is what every row
/// sent before that revision and stands for 32.
/// </summary>
public sealed record Feature(
    byte Id,
    FeatureKind Kind,
    byte Group,
    byte Aux,
    FeatureFlags Flags,
    byte Readout,
    uint Min,
    uint Max,
    string Label,
    byte Bits = 0)
{
    public const int Size = 48;

    public bool Auto => (Flags & FeatureFlags.Auto) != 0;

    public bool WritesCode => (Flags & FeatureFlags.WritesCode) != 0;

    /// <summary>Triggering this ACTION makes the game write its save to the tempsave path.</summary>
    public bool SavesAside => Kind == FeatureKind.Action && (Flags & FeatureFlags.SaveAside) != 0;

    /// <summary>Triggering this ACTION makes the game load the tempsave file.</summary>
    public bool LoadsAside => Kind == FeatureKind.Action && (Flags & FeatureFlags.LoadAside) != 0;

    /// <summary>
    /// This TOGGLE mirrors a game-memory byte qwark polls: its state comes from the game, and it
    /// has no "on boot" of its own because qwark refuses FEATURE_SET_AUTO for it.
    /// </summary>
    public bool IsLive => Kind == FeatureKind.Toggle && (Flags & FeatureFlags.Live) != 0;

    /// <summary>The ENUM option count. Zero for every other kind since revision 1.1.</summary>
    public byte OptionCount => Kind == FeatureKind.Enum ? Aux : (byte)0;

    /// <summary>
    /// The width in bits of the field behind this VALUE: 8, 16 or 32, and 32 for the 0 an older
    /// module sends and for anything the console names that is not one of the three. Meaningless
    /// for the other kinds, which never carry a width.
    /// </summary>
    public int FieldBits => Bits is 8 or 16 or 32 ? Bits : 32;

    /// <summary>
    /// The field behind this VALUE is two's complement, so what the readout carries is bits rather
    /// than a number and the client has to sign-extend it (revision 1.7).
    /// </summary>
    public bool IsSigned => Kind == FeatureKind.Value && (Flags & FeatureFlags.Signed) != 0;

    /// <summary>
    /// True when there is a range worth showing the user: the console named one, or the row is
    /// signed and its width names one for it.
    /// </summary>
    public bool HasRange => IsSigned || Max > Min;

    /// <summary>
    /// The smallest value this feature accepts. A signed row takes its floor from the width, a
    /// bounded one from <see cref="Min"/>, and an unbounded unsigned one bottoms out at zero.
    /// </summary>
    public long RangeMin => IsSigned ? -(1L << (FieldBits - 1)) : Max > Min ? Min : 0L;

    /// <summary>
    /// The largest value this feature accepts, by the same reading: the width's ceiling, the
    /// console's <see cref="Max"/>, or the whole unsigned word.
    /// </summary>
    public long RangeMax =>
        IsSigned ? (1L << (FieldBits - 1)) - 1 : Max > Min ? Max : uint.MaxValue;

    /// <summary>
    /// The number behind a readout: the raw word for an unsigned feature, and the same word read
    /// as two's complement in <see cref="FieldBits"/> bits for a signed one, so the -1 the game
    /// holds in a halfword reads as -1 rather than 65535.
    /// </summary>
    public long SignExtend(uint raw)
    {
        if (!IsSigned) return raw;

        int bits = FieldBits;
        if (bits >= 32) return (int)raw;

        long mask = (1L << bits) - 1;
        long value = raw & mask;
        long sign = 1L << (bits - 1);
        return (value ^ sign) - sign;
    }

    /// <summary>
    /// What FEATURE_SET carries for <paramref name="value"/>: the low <see cref="FieldBits"/> bits,
    /// so -1 on a 16-bit field goes out as 0xFFFF and qwark writes the halfword it always wrote.
    /// The value is clamped to <see cref="RangeMin"/>..<see cref="RangeMax"/> first, so a number
    /// typed past the end of the field never wraps round into a different one.
    /// </summary>
    public uint Encode(long value)
    {
        long clamped = Math.Clamp(value, RangeMin, RangeMax);
        int bits = FieldBits;
        if (!IsSigned || bits >= 32) return unchecked((uint)clamped);
        return (uint)(clamped & ((1L << bits) - 1));
    }

    /// <summary>
    /// The index into <c>SessionInfo.readout[]</c> that mirrors this feature's current value,
    /// or null when the feature names none. Only VALUE, ENUM and COLOR ever do.
    /// </summary>
    public byte? MirrorReadout =>
        Kind is FeatureKind.Value or FeatureKind.Enum or FeatureKind.Color
        && Readout != SessionInfo.NoReadout
        && Readout < SessionInfo.ReadoutCount
            ? Readout
            : null;

    public static Feature Parse(ReadOnlySpan<byte> entry)
    {
        if (entry.Length < Size)
        {
            throw new ProtocolException($"Feature needs {Size} bytes, got {entry.Length}");
        }

        var r = new SpanReader(entry[..Size]);
        byte id = r.ReadU8();
        var kind = (FeatureKind)r.ReadU8();
        byte group = r.ReadU8();
        byte aux = r.ReadU8();
        var flags = (FeatureFlags)r.ReadU8();
        byte readout = r.ReadU8();
        byte bits = r.ReadU8();
        r.Skip(1);
        uint min = r.ReadU32();
        uint max = r.ReadU32();
        string label = r.ReadFixedString(32);
        return new Feature(id, kind, group, aux, flags, readout, min, max, label, bits);
    }

    public byte[] ToBytes()
    {
        var buffer = new byte[Size];
        var w = new SpanWriter(buffer);
        w.WriteU8(Id);
        w.WriteU8((byte)Kind);
        w.WriteU8(Group);
        w.WriteU8(Aux);
        w.WriteU8((byte)Flags);
        w.WriteU8(Readout);
        w.WriteU8(Bits);
        w.WriteZeros(1);
        w.WriteU32(Min);
        w.WriteU32(Max);
        w.WriteFixedString(Label, 32);
        return buffer;
    }
}

/// <summary>The DESCRIBE reply: groups, readout names and the feature table.</summary>
public sealed record DescribeResult(GameId Game, string[] Groups, string[] Readouts, Feature[] Features)
{
    public static DescribeResult Empty { get; } =
        new(GameId.None, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<Feature>());

    public static DescribeResult Parse(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        var game = (GameId)r.ReadU8();

        int ngroups = r.ReadU8();
        var groups = new string[ngroups];
        for (int i = 0; i < ngroups; i++) groups[i] = r.ReadFixedString(24);

        int nreadouts = r.ReadU8();
        var readouts = new string[nreadouts];
        for (int i = 0; i < nreadouts; i++) readouts[i] = r.ReadFixedString(24);

        int nfeatures = r.ReadU8();
        var features = new Feature[nfeatures];
        if (nfeatures > 0)
        {
            if (r.Remaining < nfeatures * Feature.Size)
            {
                throw new ProtocolException(
                    $"DESCRIBE has {r.Remaining} bytes for {nfeatures} features, needs {nfeatures * Feature.Size}");
            }

            for (int i = 0; i < nfeatures; i++) features[i] = Feature.Parse(r.ReadSpan(Feature.Size));
        }

        return new DescribeResult(game, groups, readouts, features);
    }

    /// <summary>The ACTION flagged SAVE_ASIDE, or null when this game has no savefile helper.</summary>
    public Feature? SaveAsideAction => Array.Find(Features, f => f.SavesAside);

    /// <summary>The ACTION flagged LOAD_ASIDE, or null when this game has no savefile helper.</summary>
    public Feature? LoadAsideAction => Array.Find(Features, f => f.LoadsAside);

    /// <summary>
    /// True when the game declares both halves of the savefile helper. Whether the console
    /// actually has a helper for it is SAVEFILE_INFO's <c>supported</c> byte (revision 1.9);
    /// this only says the game asks for one.
    /// </summary>
    public bool HasSaveFileHelper => SaveAsideAction is not null && LoadAsideAction is not null;

    public string GroupName(byte index) => index < Groups.Length ? Groups[index] : $"Group {index}";

    public string ReadoutName(int index) => index >= 0 && index < Readouts.Length ? Readouts[index] : $"readout[{index}]";
}

/// <summary>WATCH_LIST entry.</summary>
public readonly record struct WatchEntry(byte Id, byte Size, uint Address)
{
    public static WatchEntry[] ParseList(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        int n = r.ReadU8();
        var result = new WatchEntry[n];
        for (int i = 0; i < n; i++)
        {
            byte id = r.ReadU8();
            byte size = r.ReadU8();
            r.Skip(2);
            uint addr = r.ReadU32();
            result[i] = new WatchEntry(id, size, addr);
        }

        return result;
    }
}

/// <summary>FREEZE_LIST entry.</summary>
public readonly record struct FreezeEntry(byte Id, byte Size, uint Address, ulong Value)
{
    public static FreezeEntry[] ParseList(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        int n = r.ReadU8();
        var result = new FreezeEntry[n];
        for (int i = 0; i < n; i++)
        {
            byte id = r.ReadU8();
            byte size = r.ReadU8();
            r.Skip(2);
            uint addr = r.ReadU32();
            ulong value = r.ReadU64();
            result[i] = new FreezeEntry(id, size, addr, value);
        }

        return result;
    }
}

/// <summary>PATCH_LIST entry.</summary>
public sealed record PatchEntry(uint FirstAddress, ushort WordCount, PatchKind Kind, string Name)
{
    public static PatchEntry[] ParseList(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        int n = r.ReadU8();
        var result = new PatchEntry[n];
        for (int i = 0; i < n; i++)
        {
            uint addr = r.ReadU32();
            ushort words = r.ReadU16();
            var kind = (PatchKind)r.ReadU8();
            r.Skip(1);
            string name = r.ReadFixedString(32);
            result[i] = new PatchEntry(addr, words, kind, name);
        }

        return result;
    }
}

/// <summary>One position slot of POS_LIST.</summary>
public readonly record struct PositionSlot(byte Slot, bool Filled, float X, float Y, float Z);

public sealed record PositionList(byte Planet, PositionSlot[] Slots)
{
    public static PositionList Empty { get; } = new(0, Array.Empty<PositionSlot>());

    public static PositionList Parse(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        byte planet = r.ReadU8();
        int n = r.ReadU8();
        var slots = new PositionSlot[n];
        for (int i = 0; i < n; i++)
        {
            bool filled = r.ReadU8() != 0;
            r.Skip(3);
            float x = r.ReadF32();
            float y = r.ReadF32();
            float z = r.ReadF32();
            slots[i] = new PositionSlot((byte)i, filled, x, y, z);
        }

        return new PositionList(planet, slots);
    }
}

/// <summary>UNLOCK_LIST entry, 44 bytes.</summary>
public sealed record Unlock(byte Id, byte Category, byte Fields, uint[] Values, string Name)
{
    public const int Size = 44;

    public bool HasField(int field) => (Fields & (1 << field)) != 0;

    public static Unlock Parse(ReadOnlySpan<byte> entry)
    {
        var r = new SpanReader(entry);
        byte id = r.ReadU8();
        byte category = r.ReadU8();
        byte fields = r.ReadU8();
        r.Skip(1);
        var values = new uint[4];
        for (int i = 0; i < 4; i++) values[i] = r.ReadU32();
        string name = r.ReadFixedString(24);
        return new Unlock(id, category, fields, values, name);
    }
}

/// <summary>
/// What one of the four Unlock value slots means for the running game, 16 bytes (revision 1.3).
/// The console names the slot and says how to edit it, so the client no longer has to guess that
/// slot 1 is a "Gold" flag: in RaC3 it is the weapon's version and slot 2 its XP.
/// A slot with an empty name is one this game does not use, and is not drawn at all.
/// </summary>
public readonly record struct UnlockField(string Name, UnlockFieldKind Kind, byte Max)
{
    public const int Size = 16;

    /// <summary>The stand-in for a slot the reply did not describe: nothing is drawn for it.</summary>
    public static UnlockField None { get; } = new(string.Empty, UnlockFieldKind.Flag, 0);

    public bool IsNamed => !string.IsNullOrEmpty(Name);

    /// <summary>A number slot's ceiling, or null when the descriptor names no limit.</summary>
    public uint? Ceiling => Kind == UnlockFieldKind.Number && Max > 0 ? Max : null;

    public static UnlockField Parse(ReadOnlySpan<byte> entry)
    {
        var r = new SpanReader(entry);
        string name = r.ReadFixedString(12);
        var kind = (UnlockFieldKind)r.ReadU8();
        byte max = r.ReadU8();
        r.Skip(2);
        return new UnlockField(name, kind, max);
    }

    public byte[] ToBytes()
    {
        var buffer = new byte[Size];
        var w = new SpanWriter(buffer);
        w.WriteFixedString(Name, 12);
        w.WriteU8((byte)Kind);
        w.WriteU8(Max);
        w.WriteZeros(2);
        return buffer;
    }
}

public sealed record UnlockList(string[] Categories, UnlockField[] Fields, Unlock[] Unlocks)
{
    /// <summary>Every entry carries four value slots, described or not.</summary>
    public const int SlotCount = 4;

    /// <summary>
    /// Slot 0, every game's "do I have this" flag and the one the bulk buttons drive. The other
    /// three mean whatever the descriptors say, so only this one is named here.
    /// </summary>
    public const byte PrimarySlot = 0;

    public static UnlockList Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<UnlockField>(), Array.Empty<Unlock>());

    /// <summary>The descriptor for one slot, or <see cref="UnlockField.None"/> when there is none.</summary>
    public UnlockField FieldAt(int slot) =>
        slot >= 0 && slot < Fields.Length ? Fields[slot] : UnlockField.None;

    public static UnlockList Parse(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        int ncat = r.ReadU8();
        var categories = new string[ncat];
        for (int i = 0; i < ncat; i++) categories[i] = r.ReadFixedString(24);

        var fields = new UnlockField[SlotCount];
        for (int i = 0; i < SlotCount; i++) fields[i] = UnlockField.Parse(r.ReadSpan(UnlockField.Size));

        int n = r.ReadU8();
        var unlocks = new Unlock[n];
        for (int i = 0; i < n; i++) unlocks[i] = Unlock.Parse(r.ReadSpan(Unlock.Size));
        return new UnlockList(categories, fields, unlocks);
    }
}

/// <summary>MOD_LIST entry, 120 bytes.</summary>
public sealed record ModEntry(
    byte Index,
    ModFlags Flags,
    uint Hash,
    string DirName,
    string Name,
    string Version,
    string Author)
{
    public const int Size = 120;

    public bool Loaded => (Flags & ModFlags.Loaded) != 0;

    public bool Auto => (Flags & ModFlags.Auto) != 0;

    public bool NeedsLua => (Flags & ModFlags.NeedsLua) != 0;

    public bool Previous => (Flags & ModFlags.Previous) != 0;

    public bool ParseError => (Flags & ModFlags.ParseError) != 0;

    public static ModEntry Parse(ReadOnlySpan<byte> entry)
    {
        var r = new SpanReader(entry);
        byte index = r.ReadU8();
        var flags = (ModFlags)r.ReadU8();
        r.Skip(2);
        uint hash = r.ReadU32();
        string dirName = r.ReadFixedString(32);
        string name = r.ReadFixedString(32);
        string version = r.ReadFixedString(16);
        string author = r.ReadFixedString(32);
        return new ModEntry(index, flags, hash, dirName, name, version, author);
    }

    public byte[] ToBytes()
    {
        var buffer = new byte[Size];
        var w = new SpanWriter(buffer);
        w.WriteU8(Index);
        w.WriteU8((byte)Flags);
        w.WriteZeros(2);
        w.WriteU32(Hash);
        w.WriteFixedString(DirName, 32);
        w.WriteFixedString(Name, 32);
        w.WriteFixedString(Version, 16);
        w.WriteFixedString(Author, 32);
        return buffer;
    }

    public static ModEntry[] ParseList(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        int n = r.ReadU8();
        var result = new ModEntry[n];
        for (int i = 0; i < n; i++) result[i] = Parse(r.ReadSpan(Size));
        return result;
    }
}

/// <summary>DIR_LIST entry.</summary>
public sealed record DirEntry(bool IsDirectory, uint Size, string Name)
{
    public static DirEntry[] ParseList(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        int n = r.ReadU16();
        var result = new DirEntry[n];
        for (int i = 0; i < n; i++)
        {
            bool isDir = r.ReadU8() != 0;
            int namelen = r.ReadU8();
            uint size = r.ReadU32();
            string name = Encoding.UTF8.GetString(r.ReadSpan(namelen));
            result[i] = new DirEntry(isDir, size, name);
        }

        return result;
    }
}

/// <summary>COMBO_LIST entry.</summary>
public readonly record struct ComboEntry(ComboAction Action, uint Mask)
{
    public static ComboEntry[] ParseList(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        int n = r.ReadU8();
        var result = new ComboEntry[n];
        for (int i = 0; i < n; i++)
        {
            var action = (ComboAction)r.ReadU8();
            r.Skip(3);
            uint mask = r.ReadU32();
            result[i] = new ComboEntry(action, mask);
        }

        return result;
    }
}

public readonly record struct PreviousFreeze(byte Size, uint Address, ulong Value);

public readonly record struct PreviousPatch(uint FirstAddress, ushort WordCount);

/// <summary>PREVIOUS_LIST: what the last same-game session left behind.</summary>
public sealed record PreviousSession(
    ulong Toggles,
    uint Mods,
    PreviousFreeze[] Freezes,
    PreviousPatch[] Patches)
{
    public static PreviousSession Empty { get; } =
        new(0, 0, Array.Empty<PreviousFreeze>(), Array.Empty<PreviousPatch>());

    public bool IsEmpty => Toggles == 0 && Mods == 0 && Freezes.Length == 0 && Patches.Length == 0;

    public static PreviousSession Parse(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        ulong toggles = r.ReadU64();
        uint mods = r.ReadU32();

        int nfreeze = r.ReadU8();
        var freezes = new PreviousFreeze[nfreeze];
        for (int i = 0; i < nfreeze; i++)
        {
            byte size = r.ReadU8();
            r.Skip(3);
            uint addr = r.ReadU32();
            ulong value = r.ReadU64();
            freezes[i] = new PreviousFreeze(size, addr, value);
        }

        int npatch = r.ReadU8();
        var patches = new PreviousPatch[npatch];
        for (int i = 0; i < npatch; i++)
        {
            uint addr = r.ReadU32();
            ushort words = r.ReadU16();
            r.Skip(2);
            patches[i] = new PreviousPatch(addr, words);
        }

        return new PreviousSession(toggles, mods, freezes, patches);
    }
}

/// <summary>MOBY_TABLE reply: where the client should point MEM_READ.</summary>
public readonly record struct MobyTableInfo(uint TablePointerAddress, uint TableEndPointerAddress, ushort Stride)
{
    public static MobyTableInfo Parse(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        uint tablePtr = r.ReadU32();
        uint endPtr = r.ReadU32();
        ushort stride = r.ReadU16();
        return new MobyTableInfo(tablePtr, endPtr, stride);
    }
}

/// <summary>One word of a client patch.</summary>
public readonly record struct PatchWord(uint Address, uint Word);

/// <summary>
/// SAVEFILE_INFO, sections 5.12 and 5.13 of PROTOCOL.md. Eight bytes of helper state, and since
/// revision 1.10 twelve more describing the copy the console runs between one of its own savefiles
/// and the aside buffer.
/// </summary>
public readonly record struct SaveFileInfo(
    bool Supported,
    bool Installed,
    bool Running,
    byte Pending,
    uint Size,
    uint Done = 0,
    uint Total = 0,
    SaveFileError Error = SaveFileError.None)
{
    /// <summary>Twenty bytes since revision 1.10; the first eight are unchanged.</summary>
    public const int WireSize = 20;

    /// <summary>What a module built against revision 1.9 answers with.</summary>
    public const int WireSize19 = 8;

    /// <summary>bit0: the set-aside the client asked for has not been answered yet.</summary>
    public const byte PendingSetAside = 0x01;

    /// <summary>bit1: the load the client asked for has not been answered yet.</summary>
    public const byte PendingLoad = 0x02;

    /// <summary>bit2 (revision 1.10): the console is copying a savefile in or out right now.</summary>
    public const byte PendingTransfer = 0x04;

    public bool SetAsidePending => (Pending & PendingSetAside) != 0;

    public bool LoadPending => (Pending & PendingLoad) != 0;

    public bool TransferPending => (Pending & PendingTransfer) != 0;

    /// <summary>0 to 1 through the running transfer, and 0 when there is nothing to show.</summary>
    public float Progress => Total == 0 ? 0f : Math.Clamp((float)Done / Total, 0f, 1f);

    /// <summary>What a client shows before it has asked, and what an unsupported game means.</summary>
    public static readonly SaveFileInfo None = new(false, false, false, 0, 0);

    /// <summary>
    /// A short payload is a console built against revision 1.9: it knows nothing about transfers,
    /// so the three fields it never sent read as zero and every field it did send is still right.
    /// </summary>
    public static SaveFileInfo Parse(ReadOnlySpan<byte> payload)
    {
        var r = new SpanReader(payload);
        bool supported = r.ReadU8() != 0;
        bool installed = r.ReadU8() != 0;
        bool running = r.ReadU8() != 0;
        byte pending = r.ReadU8();
        uint size = r.ReadU32();

        if (payload.Length < WireSize) return new SaveFileInfo(supported, installed, running, pending, size);

        uint done = r.ReadU32();
        uint total = r.ReadU32();
        var error = (SaveFileError)r.ReadU8();
        return new SaveFileInfo(supported, installed, running, pending, size, done, total, error);
    }

    public byte[] ToBytes()
    {
        var bytes = new byte[WireSize];
        var w = new SpanWriter(bytes);
        w.WriteU8((byte)(Supported ? 1 : 0));
        w.WriteU8((byte)(Installed ? 1 : 0));
        w.WriteU8((byte)(Running ? 1 : 0));
        w.WriteU8(Pending);
        w.WriteU32(Size);
        w.WriteU32(Done);
        w.WriteU32(Total);
        w.WriteU8((byte)Error);
        w.WriteZeros(3);
        return bytes;
    }
}

/// <summary>
/// One row of SAVEFILE_LIST, section 5.13: a save on the console, its size and the CRC32 the
/// console keeps beside it. <see cref="Name"/> is the file name, <c>.sav</c> and all.
/// </summary>
public readonly record struct ConsoleSaveFile(string Name, uint Size, uint Crc)
{
    public const int WireSize = 40;

    public const int NameLength = 32;

    public static ConsoleSaveFile[] ParseList(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1) return Array.Empty<ConsoleSaveFile>();

        int count = payload[0];
        var rows = new List<ConsoleSaveFile>(count);
        var r = new SpanReader(payload[1..]);

        for (int i = 0; i < count && payload.Length >= 1 + (i + 1) * WireSize; i++)
        {
            string name = r.ReadFixedString(NameLength);
            uint size = r.ReadU32();
            uint crc = r.ReadU32();
            rows.Add(new ConsoleSaveFile(name, size, crc));
        }

        return rows.ToArray();
    }

    /// <summary>The other direction, for the fake console the tests drive.</summary>
    public static byte[] EncodeList(IReadOnlyList<ConsoleSaveFile> files)
    {
        var bytes = new byte[1 + files.Count * WireSize];
        var w = new SpanWriter(bytes);
        w.WriteU8((byte)files.Count);
        foreach (var file in files)
        {
            w.WriteFixedString(file.Name, NameLength);
            w.WriteU32(file.Size);
            w.WriteU32(file.Crc);
        }

        return bytes;
    }
}
