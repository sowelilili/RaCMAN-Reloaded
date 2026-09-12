using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RaCMAN.Protocol.Testing;

/// <summary>
/// Enough of PROTOCOL.md to drive QwarkClient end to end in-process: HELLO, HEARTBEAT,
/// SUBSCRIBE with real UDP telemetry, GET_STATE, DESCRIBE, FEATURE_SET, the watch, freeze and
/// memory primitives, POS_LIST, PLANET_LIST, MOBY_TABLE, UNLOCK_LIST/SET, the LEVELFLAGS ops,
/// MOD_LIST, the file ops including FILE_RENAME, COMBO_SET/LIST/SUSPEND, the two AUTOSPLIT ops
/// with their UDP push, the three SAVEFILE ops over an in-memory aside buffer and the five library
/// ops of revision 1.10 over the same in-memory filesystem the file ops use. Everything else
/// answers UNKNOWN_OP.
/// </summary>
public sealed class FakeQwarkServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly object _gate = new();

    private SessionInfo _session;
    private IPEndPoint? _telemetryTarget;
    private Task? _telemetryTask;

    public FakeQwarkServer(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _session = SessionInfo.Empty with
        {
            ProtocolVersion = 1,
            QwarkVersion = 12,
            State = SessionState.Ingame,
            Game = GameId.Rac1,
            Generation = 1,
            TitleId = "NPEA00385",
            CurrentPlanet = 2,
            SelectedSlot = 0,
            PosX = 100f,
            PosY = 200f,
            PosZ = 300f,
            Readout = BuildReadouts(),
        };

        Memory = new byte[0x2000];
        for (int i = 0; i < Memory.Length; i++) Memory[i] = (byte)(i & 0xFF);
        BuildMobyTable();

        Describe = new DescribeResult(
            GameId.Rac1,
            new[] { "Cheats", "Movement" },
            new[] { "Bolts", "Health", "Ghost mode", "Armor colour", "QE offset" },
            new[]
            {
                new Feature(0, FeatureKind.Toggle, 0, 0, FeatureFlags.None, 0xFF, 0, 0, "Infinite ammo"),
                new Feature(1, FeatureKind.Toggle, 0, 0, FeatureFlags.Auto, 0xFF, 0, 0, "Fast loads"),
                new Feature(2, FeatureKind.Action, 1, 0, FeatureFlags.None, 0xFF, 0, 0, "Die"),
                new Feature(3, FeatureKind.Value, 0, 0, FeatureFlags.None, 0, 0, 99999, "Bolts"),
                new Feature(4, FeatureKind.Enum, 1, 3, FeatureFlags.None, 2, 0, 2, "Ghost mode"),
                new Feature(5, FeatureKind.Color, 1, 0, FeatureFlags.None, 3, 0, 0, "Armor colour"),

                // Revision 1.2: the two flagged ACTIONs the savefile manager drives.
                new Feature(6, FeatureKind.Action, 0, 0, FeatureFlags.SaveAside, 0xFF, 0, 0, "Set aside file"),
                new Feature(7, FeatureKind.Action, 0, 0, FeatureFlags.LoadAside, 0xFF, 0, 0, "Load set-aside file"),

                // Revision 1.3: a TOGGLE that is a game-memory byte qwark polls. No "on boot".
                new Feature(8, FeatureKind.Toggle, 0, 0, FeatureFlags.Live, 0xFF, 0, 0, "Goodies menu"),

                // A TOGGLE that patches instructions, so a platform that refuses code patches
                // (flags.NO_CODE_PATCHES) has something the panels must grey out.
                new Feature(9, FeatureKind.Toggle, 0, 0, FeatureFlags.WritesCode, 0xFF, 0, 0, "Infinite jump"),

                // Revision 1.7: a VALUE whose field is a signed halfword, unbounded because the
                // width is the range. Its readout carries the raw 0xFFFF the game holds for -1.
                new Feature(10, FeatureKind.Value, 0, 0, FeatureFlags.Signed, 4, 0, 0, "QE offset", 16),
            });

        Mods = new List<ModEntry>
        {
            new(0, ModFlags.None, 0x12345678, "crash-patch", "Crash patches", "1.0.0", "someone"),
            new(1, ModFlags.NeedsLua, 0, "flight", "Flight", "2.1.0", "someone else"),
        };

        UnlockCategories = new[] { "Weapons", "Gadgets" };

        // What the four value slots mean for this "game", as revision 1.3 has the console say.
        UnlockFields = new[]
        {
            new UnlockField("Owned", UnlockFieldKind.Flag, 0),
            new UnlockField("Level", UnlockFieldKind.Number, 8),
            new UnlockField("XP", UnlockFieldKind.Number, 0),
            new UnlockField("Ammo", UnlockFieldKind.Number, 0),
        };

        Unlocks = new List<Unlock>
        {
            // fields: bit f set = slot f is meaningful, named by UnlockFields[f].
            new(0, 0, 0b1011, new uint[] { 1, 0, 0, 40 }, "Bomb Glove"),
            new(1, 0, 0b1111, new uint[] { 1, 3, 1200, 200 }, "Blaster"),
            new(2, 0, 0b1011, new uint[] { 0, 0, 0, 0 }, "RYNO"),
            new(3, 1, 0b0001, new uint[] { 1, 0, 0, 0 }, "Heli-Pack"),
            new(4, 1, 0b0001, new uint[] { 0, 0, 0, 0 }, "Swingshot"),
        };

        for (byte planet = 0; planet < Planets.Length; planet++)
        {
            var flags = new byte[64];
            for (int i = 0; i < flags.Length; i++) flags[i] = (byte)((planet * 16 + i) & 0xFF);
            LevelFlags[planet] = flags;
        }
    }

    private static uint[] BuildReadouts()
    {
        var readouts = new uint[SessionInfo.ReadoutCount];
        readouts[0] = 1234;      // Bolts, mirrored by the VALUE feature
        readouts[1] = 40;        // Health
        readouts[2] = 1;         // Ghost mode, mirrored by the ENUM feature
        readouts[3] = 0x00CC4400; // Armor colour, mirrored by the COLOR feature
        readouts[4] = 0xFFFF;     // QE offset: the raw halfword a signed VALUE reads as -1
        return readouts;
    }

    /// <summary>
    /// Lays a small RAC1-shaped moby array into the fake process, plus the two pointer words
    /// MOBY_TABLE names. Offsets come from the layout file the client ships, not from here.
    /// </summary>
    private void BuildMobyTable()
    {
        uint start = MemoryBase + MobyTableOffset;
        uint end = start + (uint)(MobyCount * MobyStride);

        var w = new SpanWriter(Memory.AsSpan((int)MobyPointerOffset, 8));
        w.WriteU32(start);
        w.WriteU32(end);

        for (int i = 0; i < MobyCount; i++)
        {
            var entry = Memory.AsSpan((int)MobyTableOffset + i * MobyStride, MobyStride);
            entry.Clear();

            var pos = new SpanWriter(entry[0x10..]);
            pos.WriteF32(100f + i);
            pos.WriteF32(200f + i);
            pos.WriteF32(300f + i);

            entry[0x20] = (byte)(i % 5);                                    // state
            new SpanWriter(entry[0xA6..]).WriteU16((ushort)(5000 + i * 7));  // oClass
            new SpanWriter(entry[0xB2..]).WriteU16((ushort)(1 + i));         // UID
        }
    }

    /// <summary>Where the two MOBY_TABLE pointer words live inside <see cref="Memory"/>.</summary>
    public const uint MobyPointerOffset = 0x1000;

    /// <summary>Where the moby array itself starts inside <see cref="Memory"/>.</summary>
    public const uint MobyTableOffset = 0x1100;

    public const int MobyStride = 0x100;

    public const int MobyCount = 8;

    public int Port { get; }

    public byte[] Memory { get; }

    public uint MemoryBase { get; set; } = 0x300000;

    public string[] UnlockCategories { get; set; }

    /// <summary>The four value-slot descriptors UNLOCK_LIST reports, one per slot.</summary>
    public UnlockField[] UnlockFields { get; set; }

    public List<Unlock> Unlocks { get; }

    /// <summary>The concatenated flag region per planet index, as LEVELFLAGS_GET returns it.</summary>
    public Dictionary<byte, byte[]> LevelFlags { get; } = new();

    public int LevelFlagsResetCount { get; private set; }

    public DescribeResult Describe { get; set; }

    public List<ModEntry> Mods { get; }

    /// <summary>Options per ENUM feature id, answered by FEATURE_OPTIONS.</summary>
    public Dictionary<byte, string[]> EnumOptions { get; } = new()
    {
        [4] = new[] { "Off", "Ghost", "Ghost and no clip" },
    };

    public string[] Planets { get; set; } = { "Veldin", "Novalis", "Aridia", "Kerwan" };

    /// <summary>
    /// What AUTOSPLIT_DESCRIBE reports. Empty means the running game has no watcher, which the op
    /// answers UNSUPPORTED, exactly as a game qwark has no autosplitter for would.
    /// </summary>
    public List<AutosplitEventDesc> AutosplitDescriptors { get; set; } = new()
    {
        new AutosplitEventDesc(1, AutosplitKind.Split,
            AutosplitEventFlags.EnabledByDefault | AutosplitEventFlags.PlanetRoute, 0, "Planet entered"),

        // A split that is also a flat correction, the shape of RaC2's Protopet: seven frames of
        // cutscene come off game time just before the split goes out.
        new AutosplitEventDesc(2, AutosplitKind.Split,
            AutosplitEventFlags.EnabledByDefault | AutosplitEventFlags.Flat, 116667, "Boss defeated"),

        new AutosplitEventDesc(3, AutosplitKind.Split, AutosplitEventFlags.None, 0, "Arena entered"),

        // The two normalised pairs: RaC1's 7.56 s load timer and Deadlocked's 14.8 s quit.
        new AutosplitEventDesc(8, AutosplitKind.LoadStart, AutosplitEventFlags.Normalise, 7_560_000, "Level load"),
        new AutosplitEventDesc(9, AutosplitKind.Pause, AutosplitEventFlags.Normalise, 14_800_000, "Quit to XMB"),
    };

    /// <summary>The event ring AUTOSPLIT_EVENTS reads from, oldest first.</summary>
    public List<AutosplitEvent> AutosplitRing { get; } = new();

    /// <summary>
    /// Off makes both autosplit ops answer UNKNOWN_OP, which is what a module from before
    /// revision 1.4 does and what the client has to stop asking about.
    /// </summary>
    /// <remarks>
    /// The rows and events themselves are revision 1.5: a 32-byte descriptor with a parameter, and
    /// an event carrying milliseconds rather than ticks.</remarks>
    public bool AutosplitSupported { get; set; } = true;

    /// <summary>
    /// Whether <see cref="EmitAutosplitEvent"/> also sends the UDP push. Off lets a test put an
    /// event in the ring without announcing it, which is how a lost datagram is staged.
    /// </summary>
    public bool AutosplitPush { get; set; } = true;

    /// <summary>How many UDP copies of one event go out, one per telemetry tick (section 5.11).</summary>
    public const int AutosplitPushCopies = 3;

    private readonly List<(AutosplitEvent Event, int Left)> _autosplitPushes = new();
    private uint _autosplitSeq;

    /// <summary>
    /// The module's millisecond clock, which revision 1.5 events carry instead of a tick count.
    /// It runs from the moment the fake server was made, exactly as qwark's runs from module load.
    /// </summary>
    public uint AutosplitClockMs => (uint)_clock.ElapsedMilliseconds;

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>
    /// Appends one event to the ring the way the console's watcher would, and queues its three
    /// datagram copies when the push is on. Returns the event, sequence number and all.
    /// <para>
    /// <paramref name="timeMs"/> overrides the module clock, which is how a test or a fake script
    /// stages a load or a pause of an exact length without waiting for one.
    /// </para>
    /// </summary>
    public AutosplitEvent EmitAutosplitEvent(AutosplitKind kind, byte code = 0, uint arg = 0, uint? timeMs = null)
    {
        lock (_gate)
        {
            var ev = new AutosplitEvent(++_autosplitSeq, timeMs ?? AutosplitClockMs, kind, code, arg);
            AutosplitRing.Add(ev);
            if (AutosplitRing.Count > AutosplitEventsReply.MaxEvents) AutosplitRing.RemoveAt(0);
            if (AutosplitPush) _autosplitPushes.Add((ev, AutosplitPushCopies));
            return ev;
        }
    }

    public List<WatchEntry> Watches { get; } = new();

    public List<FreezeEntry> Freezes { get; } = new();

    public Dictionary<ComboAction, uint> Combos { get; } = new();

    /// <summary>
    /// The last COMBO_SUSPEND the client sent: true while it is holding the combos off, which is
    /// what the Combos panel does for the length of a capture. Null until one arrives, so a test
    /// can tell "never asked" from "asked and resumed".
    /// </summary>
    public bool? CombosSuspended { get; private set; }

    /// <summary>The console's filesystem, as far as the file ops are concerned.</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public List<string> Directories { get; } = new();

    /// <summary>Every FEATURE_TRIGGER id, in order, so a test can see which action a flow fired.</summary>
    public List<byte> Triggered { get; } = new();

    /// <summary>
    /// The savefile helper's aside buffer, revision 1.9. SAVEFILE_READ and SAVEFILE_WRITE move
    /// bytes in and out of this; the SAVE_ASIDE action fills it with
    /// <see cref="SaveAsideContent"/>, as the game's own helper would.
    /// <para>
    /// Small on purpose: a console's is one or two megabytes, and a test that moved that much
    /// through a loopback socket for every case would be slow for no gain. It is more than one
    /// 64 KB chunk, which is what the chunking needs to be exercised.
    /// </para>
    /// </summary>
    public byte[] SaveFileBuffer { get; private set; } = new byte[200 * 1024];

    /// <summary>What the game "sets aside" when the SAVE_ASIDE action fires.</summary>
    public byte[] SaveAsideContent { get; set; } = BuildSaveAsideContent(200 * 1024);

    /// <summary>False makes SAVEFILE_INFO answer OK with `supported` 0: a game with no helper.</summary>
    public bool SaveFileSupported { get; set; } = true;

    /// <summary>
    /// True makes all three savefile ops answer UNSUPPORTED, which is what a platform that
    /// cannot patch code does (section 5.12).
    /// </summary>
    public bool SaveFileUnsupported { get; set; }

    /// <summary>What SAVEFILE_INFO reports for the helper's own byte.</summary>
    public bool SaveFileRunning { get; set; } = true;

    /// <summary>
    /// True makes every savefile op answer BUSY (revision 1.11): qwark waiting for a starting
    /// game to finish loading its modules before it writes the 600-byte helper into it. It is
    /// what a console answers for the second or two after a game appears, and it is temporary,
    /// so a client must read it as "not ready", not as a failure.
    /// </summary>
    public bool SaveFileHelperPending { get; set; }

    /// <summary>
    /// How many SAVEFILE_INFO polls a request stays outstanding for, standing in for the frame
    /// or two a console's helper takes to notice it. 1 is answered on the first poll.
    /// </summary>
    public int SaveFilePendingPolls { get; set; } = 1;

    /// <summary>The bytes the aside buffer held when the LOAD_ASIDE action last fired.</summary>
    public byte[]? LoadedSaveFile { get; private set; }

    /// <summary>
    /// Every savefile step the client took, in the order it took them: <c>write &lt;offset&gt;</c>
    /// for each SAVEFILE_WRITE and <c>set-aside</c> or <c>load</c> for each flagged ACTION. A test
    /// reads this to check that a load fills the buffer front to back and only then asks the game
    /// to take it.
    /// </summary>
    public List<string> SaveFileOrder { get; } = new();

    /// <summary>
    /// When set, SAVEFILE_READ answers BUSY from this offset on: the console that stops answering
    /// part way through a save, which is what a torn .sav used to come from.
    /// </summary>
    public uint? SaveFileReadFailsFrom { get; set; }

    private int _setAsidePending;
    private int _loadPending;

    // ------------------------------------------- 5.13, the library on the console

    /// <summary>
    /// How many SAVEFILE_INFO polls a STORE or a RESTORE copy takes. The console moves 128 KB a
    /// tick; here it moves a fraction of the file per poll, which is enough for a client's
    /// progress reporting and its wait loop to be exercised. Two means one poll of partway.
    /// </summary>
    public int SaveFileTransferPolls { get; set; } = 2;

    /// <summary>
    /// Forces the console's next transfer to end with this error instead of doing its work, which
    /// is how the client's error surface is checked without breaking anything else.
    /// </summary>
    public SaveFileError SaveFileTransferError { get; set; } = SaveFileError.None;

    /// <summary>Every STORE and RESTORE the console was asked for, in order.</summary>
    public List<string> SaveFileLibraryOrder { get; } = new();

    /// <summary>Where the console keeps the library for the running title.</summary>
    public string SaveFileRoot => $"{QwarkClient.SaveFileConsoleRoot}/{Session.TitleId}";

    private int _transferPolls;
    private int _transferTotalPolls;
    private uint _transferDone;
    private uint _transferTotal;
    private SaveFileError _transferError;
    private Action? _transferEffect;

    private bool TransferRunning => _transferPolls > 0 || _setAsidePendingForTransfer;

    private bool _setAsidePendingForTransfer;

    /// <summary>
    /// The console's own copy, one poll at a time. A STORE waits for the game's helper first, so
    /// the set-aside is counted down before a byte of the copy moves.
    /// </summary>
    private void AdvanceTransfer()
    {
        if (_setAsidePendingForTransfer)
        {
            if (_setAsidePending > 0) return;
            _setAsidePendingForTransfer = false;
        }

        if (_transferPolls <= 0) return;

        _transferPolls--;
        _transferDone = _transferTotalPolls <= 0
            ? _transferTotal
            : (uint)((long)_transferTotal * (_transferTotalPolls - _transferPolls) / _transferTotalPolls);

        if (_transferPolls > 0) return;

        if (SaveFileTransferError != SaveFileError.None)
        {
            _transferError = SaveFileTransferError;
            SaveFileTransferError = SaveFileError.None;
            return;
        }

        _transferDone = _transferTotal;
        _transferEffect?.Invoke();
        _transferEffect = null;
    }

    private void BeginTransfer(uint total, bool waitForSetAside, Action effect)
    {
        _transferTotalPolls = Math.Max(1, SaveFileTransferPolls);
        _transferPolls = _transferTotalPolls;
        _transferDone = 0;
        _transferTotal = total;
        _transferError = SaveFileError.None;
        _transferEffect = effect;
        _setAsidePendingForTransfer = waitForSetAside;
        if (waitForSetAside) _setAsidePending = Math.Max(1, SaveFilePendingPolls);
    }

    private string SaveFilePath(string category, string name) => $"{SaveFileRoot}/{category}/{name}";

    /// <summary>The categories: every folder directly under the title, however it came to exist.</summary>
    private string[] SaveFileCategories()
    {
        var prefix = SaveFileRoot + "/";
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directories)
        {
            if (!directory.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = directory[prefix.Length..];
            if (rest.Length == 0 || rest.Contains('/')) continue;
            names.Add(rest);
        }

        foreach (var file in Files.Keys)
        {
            if (!file.StartsWith(prefix, StringComparison.Ordinal)) continue;
            int slash = file[prefix.Length..].IndexOf('/');
            if (slash > 0) names.Add(file[prefix.Length..][..slash]);
        }

        return names.ToArray();
    }

    /// <summary>
    /// The saves in one category. Only <c>.sav</c> files are listed; the CRC comes from the
    /// <c>.sum</c> sidecar when there is one and is computed and written out when there is not,
    /// exactly as qwark does for a file the client uploaded.
    /// </summary>
    private ConsoleSaveFile[] SaveFileList(string category)
    {
        var prefix = $"{SaveFileRoot}/{category}/";
        var rows = new List<ConsoleSaveFile>();

        foreach (var (path, content) in Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var name = path[prefix.Length..];
            if (name.Contains('/') || !name.EndsWith(SaveFileLibrary.Extension, StringComparison.Ordinal)) continue;

            uint crc;
            if (Files.TryGetValue(path + QwarkClient.SaveFileSumExtension, out var sum)
                && Crc32.TryParseSum(Encoding.ASCII.GetString(sum), out var parsed))
            {
                crc = parsed;
            }
            else
            {
                crc = Crc32.Compute(content);
                Files[path + QwarkClient.SaveFileSumExtension] =
                    Encoding.ASCII.GetBytes(Crc32.ToSumText(crc));
            }

            rows.Add(new ConsoleSaveFile(name, (uint)content.Length, crc));
        }

        return rows.ToArray();
    }

    private static byte[] BuildSaveAsideContent(int size)
    {
        var bytes = new byte[size];
        for (int i = 0; i < size; i++) bytes[i] = (byte)(i * 31 + 7);
        return bytes;
    }

    public int ModRescanCount { get; private set; }

    public int HeartbeatCount { get; private set; }

    public int HelloCount { get; private set; }

    /// <summary>
    /// How many times DESCRIBE was asked. The client keeps what a game described for as long as
    /// the same game keeps coming back, so this is how a test tells a re-read from a reuse.
    /// </summary>
    public int DescribeCount { get; private set; }

    public int SubscribeCount { get; private set; }

    public TimeSpan TelemetryInterval { get; set; } = TimeSpan.FromMilliseconds(33);

    /// <summary>Walks pad_mask through the buttons and sweeps the sticks, so the input display has something to draw.</summary>
    public bool AnimateInput { get; set; } = true;

    public SessionInfo Session
    {
        get { lock (_gate) return _session; }
        set { lock (_gate) _session = value; }
    }

    /// <summary>
    /// flags.EMULATOR: the fake console reports that it is running under an emulator. Only a
    /// readout, but it is half of what the RPCS3 build of qwark says about itself.
    /// </summary>
    public bool Emulator
    {
        get => (Session.Flags & SessionFlags.Emulator) != 0;
        set => SetSessionFlag(SessionFlags.Emulator, value);
    }

    /// <summary>
    /// flags.NO_CODE_PATCHES, and the behaviour behind it: with this on, FEATURE_SET on a
    /// WRITES_CODE feature, MOD_LOAD and PATCH_APPLY all answer UNSUPPORTED, exactly as qwark
    /// does on a platform whose instruction memory it cannot write.
    /// </summary>
    public bool NoCodePatches
    {
        get => (Session.Flags & SessionFlags.NoCodePatches) != 0;
        set => SetSessionFlag(SessionFlags.NoCodePatches, value);
    }

    /// <summary>
    /// How many packets carrying flags.TELEMETRY_QUIET go out before the silence starts. qwark
    /// sends about five, which is what gives a client the announcement it then keeps.
    /// </summary>
    public int QuietWarningPackets { get; set; } = 5;

    private int _quietWarningsLeft;

    /// <summary>
    /// flags.TELEMETRY_QUIET and the <c>quiet_ms</c> beside it (revision 1.11): the console is
    /// starting a game and is about to stop sending. A handful of packets carry the announcement
    /// and then the telemetry loop says nothing at all, which is the half of the behaviour a
    /// client's poll suppression has to survive.
    /// </summary>
    public void GoQuiet(ushort quietMs)
    {
        lock (_gate)
        {
            _quietWarningsLeft = Math.Max(1, QuietWarningPackets);
            _session = _session with
            {
                State = SessionState.Booting,
                Flags = _session.Flags | SessionFlags.TelemetryQuiet,
                QuietMs = quietMs,
            };
        }
    }

    /// <summary>The game is up: the flag goes, <c>quiet_ms</c> goes back to 0 and telemetry resumes.</summary>
    public void GoLoud(SessionState state = SessionState.Ingame)
    {
        lock (_gate)
        {
            _quietWarningsLeft = 0;
            _session = _session with
            {
                State = state,
                Flags = _session.Flags & ~SessionFlags.TelemetryQuiet,
                QuietMs = 0,
            };
        }
    }

    /// <summary>True while the fake console is keeping the silence it announced.</summary>
    public bool Quiet
    {
        get { lock (_gate) return (_session.Flags & SessionFlags.TelemetryQuiet) != 0; }
    }

    /// <summary>Every GET_STATE the client asked for, which is what a quiet window must not add to.</summary>
    public int GetStateCount { get; private set; }

    private void SetSessionFlag(SessionFlags flag, bool on)
    {
        lock (_gate)
        {
            _session = _session with { Flags = on ? _session.Flags | flag : _session.Flags & ~flag };
        }
    }

    /// <summary>The ops a platform without writable instruction memory refuses outright.</summary>
    private bool RefusedWithoutCodePatches(Opcode opcode, byte[] payload)
    {
        if (!NoCodePatches) return false;
        if (opcode is Opcode.PatchApply or Opcode.ModLoad) return true;

        // A FEATURE_SET only fails for the features that patch instructions; a data cheat is fine.
        if (opcode != Opcode.FeatureSet || payload.Length < 1) return false;
        return Array.Find(Describe.Features, f => f.Id == payload[0])?.WritesCode ?? false;
    }

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    /// <summary>Kills every accepted connection, the way a console crash or a reboot would.</summary>
    public void DropClients()
    {
        foreach (var client in _clients.Keys)
        {
            _clients.TryRemove(client, out _);
            try { client.Close(); } catch { /* already gone */ }
        }

        lock (_gate) _telemetryTarget = null;
    }

    /// <summary>Stops accepting, so a reconnect attempt fails until <see cref="Reopen"/>.</summary>
    public void StopListening() => _listener.Stop();

    public void Reopen() => _listener.Start();

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(10);
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                // Listener stopped; wait for Reopen.
                await Task.Delay(10);
                continue;
            }

            _clients.TryAdd(client, 0);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var header = new byte[Frame.HeaderSize];

                while (!_cts.IsCancellationRequested)
                {
                    await stream.ReadExactlyAsync(header, _cts.Token);
                    var (length, seq, code) = Frame.DecodeHeader(header);
                    var payload = new byte[length];
                    if (length > 0) await stream.ReadExactlyAsync(payload, _cts.Token);

                    var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
                    var (status, reply) = Handle((Opcode)code, payload, remote);
                    var frame = Frame.Reply(status, seq, reply);
                    await stream.WriteAsync(frame.Encode(), _cts.Token);
                }
            }
        }
        catch
        {
            // Client went away.
        }
        finally
        {
            _clients.TryRemove(client, out _);
        }
    }

    /// <summary>
    /// Answer NOT_INGAME to everything that touches game memory while the session is not INGAME,
    /// as sections 5.4 to 5.6 require. Off only for a test that wants the raw op.
    /// </summary>
    public bool EnforceIngame { get; set; } = true;

    /// <summary>The savefile ops, every one of which needs the helper a starting game has not got.</summary>
    private static bool NeedsSaveFileHelper(Opcode opcode) => opcode is
        Opcode.SaveFileInfo or Opcode.SaveFileRead or Opcode.SaveFileWrite
        or Opcode.SaveFileCategories or Opcode.SaveFileList or Opcode.SaveFileStore
        or Opcode.SaveFileRestore or Opcode.SaveFileCategory;

    /// <summary>The ops a real qwark refuses outside INGAME because they read or write the process.</summary>
    private static bool TouchesGameMemory(Opcode opcode) => opcode is
        Opcode.MemRead or Opcode.MemWrite or Opcode.MobyTable
        or Opcode.PosList or Opcode.PosSave or Opcode.PosLoad or Opcode.PosClear
        or Opcode.PlanetLoad or Opcode.Die
        or Opcode.UnlockList or Opcode.UnlockSet
        or Opcode.LevelFlagsGet or Opcode.LevelFlagsSet or Opcode.LevelFlagsReset
        or Opcode.FeatureSet or Opcode.FeatureTrigger;

    private (Status, byte[]?) Handle(Opcode opcode, byte[] payload, IPEndPoint remote)
    {
        lock (_gate)
        {
            if (EnforceIngame && _session.State != SessionState.Ingame && TouchesGameMemory(opcode))
            {
                return (Status.NotIngame, null);
            }

            if (RefusedWithoutCodePatches(opcode, payload)) return (Status.Unsupported, null);

            // Revision 1.11: the helper is not in the game yet, so everything that needs it waits.
            if (SaveFileHelperPending && NeedsSaveFileHelper(opcode)) return (Status.Busy, null);

            switch (opcode)
            {
                case Opcode.Hello:
                    if (payload.Length < 1) return (Status.BadArg, null);
                    HelloCount++;
                    return (Status.Ok, _session.ToBytes());

                case Opcode.Heartbeat:
                    HeartbeatCount++;
                    return (Status.Ok, null);

                case Opcode.Notify:
                    return (Status.Ok, null);

                case Opcode.Subscribe:
                {
                    if (payload.Length < 2) return (Status.BadArg, null);
                    ushort port = (ushort)((payload[0] << 8) | payload[1]);
                    SubscribeCount++;
                    _telemetryTarget = new IPEndPoint(remote.Address, port);
                    _telemetryTask ??= Task.Run(TelemetryLoopAsync);
                    return (Status.Ok, null);
                }

                case Opcode.Unsubscribe:
                    _telemetryTarget = null;
                    return (Status.Ok, null);

                case Opcode.GetState:
                    GetStateCount++;
                    return (Status.Ok, BuildTelemetry().ToBytes());

                case Opcode.Describe:
                    DescribeCount++;
                    return (Status.Ok, EncodeDescribe(Describe));

                case Opcode.FeatureSet:
                {
                    if (payload.Length < 5) return (Status.BadArg, null);
                    byte id = payload[0];
                    uint value = (uint)((payload[1] << 24) | (payload[2] << 16) | (payload[3] << 8) | payload[4]);

                    // Every kind but TOGGLE reports its state through the readout it names, so the
                    // fake console has to move that readout for the client to see the change.
                    var feature = Array.Find(Describe.Features, f => f.Id == id);
                    if (feature?.MirrorReadout is { } mirror)
                    {
                        var readouts = (uint[])_session.Readout.Clone();
                        readouts[mirror] = value;
                        _session = _session with { Readout = readouts };
                        return (Status.Ok, null);
                    }

                    ulong bit = 1UL << id;
                    _session = value != 0
                        ? _session with { ToggleState = _session.ToggleState | bit }
                        : _session with { ToggleState = _session.ToggleState & ~bit };
                    return (Status.Ok, null);
                }

                case Opcode.FeatureSetAuto:
                {
                    if (payload.Length < 2) return (Status.BadArg, null);

                    // A live toggle is the game's own byte: there is nothing to apply on boot.
                    var target = Array.Find(Describe.Features, f => f.Id == payload[0]);
                    if (target is not null && target.IsLive) return (Status.Unsupported, null);

                    ulong bit = 1UL << payload[0];
                    _session = payload[1] != 0
                        ? _session with { ToggleAuto = _session.ToggleAuto | bit }
                        : _session with { ToggleAuto = _session.ToggleAuto & ~bit };
                    return (Status.Ok, null);
                }

                case Opcode.FeatureTrigger:
                {
                    if (payload.Length < 1) return (Status.BadArg, null);
                    byte id = payload[0];
                    var action = Array.Find(Describe.Features, f => f.Id == id);
                    if (action is null) return (Status.NotFound, null);

                    Triggered.Add(id);

                    // The savefile helper's two halves are the only triggers with a side effect
                    // the client can observe: one fills the aside buffer, the other consumes it.
                    // Both stay "pending" for a poll or two, as the console's helper does.
                    if (action.SavesAside)
                    {
                        if (SaveFileUnsupported) return (Status.Unsupported, null);
                        SaveFileBuffer = (byte[])SaveAsideContent.Clone();
                        _setAsidePending = SaveFilePendingPolls;
                        SaveFileOrder.Add("set-aside");
                    }
                    else if (action.LoadsAside)
                    {
                        if (SaveFileUnsupported) return (Status.Unsupported, null);
                        LoadedSaveFile = (byte[])SaveFileBuffer.Clone();
                        _loadPending = SaveFilePendingPolls;
                        SaveFileOrder.Add("load");
                    }

                    return (Status.Ok, null);
                }

                case Opcode.FeatureOptions:
                {
                    if (payload.Length < 1) return (Status.BadArg, null);
                    if (!EnumOptions.TryGetValue(payload[0], out var options)) return (Status.Unsupported, null);

                    var buffer = new byte[1 + options.Length * 24];
                    var w = new SpanWriter(buffer);
                    w.WriteU8((byte)options.Length);
                    foreach (var option in options) w.WriteFixedString(option, 24);
                    return (Status.Ok, buffer);
                }

                case Opcode.MemRead:
                {
                    if (payload.Length < 8) return (Status.BadArg, null);
                    var r = new SpanReader(payload);
                    uint addr = r.ReadU32();
                    uint len = r.ReadU32();
                    if (!InRange(addr, len)) return (Status.BadArg, null);
                    return (Status.Ok, Memory.AsSpan((int)(addr - MemoryBase), (int)len).ToArray());
                }

                case Opcode.MemWrite:
                {
                    if (payload.Length < 4) return (Status.BadArg, null);
                    var r = new SpanReader(payload);
                    uint addr = r.ReadU32();
                    var data = r.ReadRest();
                    if (!InRange(addr, (uint)data.Length)) return (Status.BadArg, null);
                    data.CopyTo(Memory.AsSpan((int)(addr - MemoryBase)));
                    return (Status.Ok, null);
                }

                case Opcode.WatchAdd:
                {
                    if (payload.Length < 5) return (Status.BadArg, null);
                    var r = new SpanReader(payload);
                    uint addr = r.ReadU32();
                    byte size = r.ReadU8();
                    var existing = Watches.FirstOrDefault(w => w.Address == addr && w.Size == size);
                    if (existing.Size != 0) return (Status.Ok, new[] { existing.Id });

                    byte id = (byte)Watches.Count;
                    Watches.Add(new WatchEntry(id, size, addr));
                    return (Status.Ok, new[] { id });
                }

                case Opcode.WatchRemove:
                    if (payload.Length < 1) return (Status.BadArg, null);
                    Watches.RemoveAll(w => w.Id == payload[0]);
                    return (Status.Ok, null);

                case Opcode.FreezeAdd:
                {
                    if (payload.Length < 16) return (Status.BadArg, null);
                    var r = new SpanReader(payload);
                    uint addr = r.ReadU32();
                    byte size = r.ReadU8();
                    r.Skip(3);
                    ulong value = r.ReadU64();

                    byte id = (byte)Freezes.Count;
                    Freezes.Add(new FreezeEntry(id, size, addr, value));
                    _session = _session with { FreezeActive = _session.FreezeActive | (1UL << id) };
                    return (Status.Ok, new[] { id });
                }

                case Opcode.FreezeRemove:
                {
                    if (payload.Length < 1) return (Status.BadArg, null);
                    Freezes.RemoveAll(f => f.Id == payload[0]);
                    _session = _session with { FreezeActive = _session.FreezeActive & ~(1UL << payload[0]) };
                    return (Status.Ok, null);
                }

                case Opcode.WatchList:
                {
                    var buffer = new byte[1 + Watches.Count * 8];
                    var w = new SpanWriter(buffer);
                    w.WriteU8((byte)Watches.Count);
                    foreach (var watch in Watches)
                    {
                        w.WriteU8(watch.Id);
                        w.WriteU8(watch.Size);
                        w.WriteZeros(2);
                        w.WriteU32(watch.Address);
                    }

                    return (Status.Ok, buffer);
                }

                case Opcode.PosList:
                {
                    var buffer = new byte[2 + 8 * 16];
                    var w = new SpanWriter(buffer);
                    w.WriteU8(_session.CurrentPlanet);
                    w.WriteU8(8);
                    for (int i = 0; i < 8; i++)
                    {
                        w.WriteU8(i < 2 ? (byte)1 : (byte)0);
                        w.WriteZeros(3);
                        w.WriteF32(i * 10f);
                        w.WriteF32(i * 20f);
                        w.WriteF32(i * 30f);
                    }

                    return (Status.Ok, buffer);
                }

                case Opcode.FreezeList:
                {
                    var buffer = new byte[1 + Freezes.Count * 16];
                    var w = new SpanWriter(buffer);
                    w.WriteU8((byte)Freezes.Count);
                    foreach (var freeze in Freezes)
                    {
                        w.WriteU8(freeze.Id);
                        w.WriteU8(freeze.Size);
                        w.WriteZeros(2);
                        w.WriteU32(freeze.Address);
                        w.WriteU64(freeze.Value);
                    }

                    return (Status.Ok, buffer);
                }

                case Opcode.PatchList:
                    return (Status.Ok, new byte[] { 0 });

                case Opcode.MobyTable:
                {
                    var buffer = new byte[12];
                    var w = new SpanWriter(buffer);
                    w.WriteU32(MemoryBase + MobyPointerOffset);
                    w.WriteU32(MemoryBase + MobyPointerOffset + 4);
                    w.WriteU16(MobyStride);
                    w.WriteU16(0);
                    return (Status.Ok, buffer);
                }

                case Opcode.UnlockList:
                {
                    var buffer = new byte[1 + UnlockCategories.Length * 24
                        + UnlockList.SlotCount * UnlockField.Size + 1 + Unlocks.Count * Unlock.Size];
                    var w = new SpanWriter(buffer);
                    w.WriteU8((byte)UnlockCategories.Length);
                    foreach (var category in UnlockCategories) w.WriteFixedString(category, 24);

                    for (int slot = 0; slot < UnlockList.SlotCount; slot++)
                    {
                        var field = slot < UnlockFields.Length ? UnlockFields[slot] : UnlockField.None;
                        w.WriteBytes(field.ToBytes());
                    }

                    w.WriteU8((byte)Unlocks.Count);
                    foreach (var unlock in Unlocks)
                    {
                        w.WriteU8(unlock.Id);
                        w.WriteU8(unlock.Category);
                        w.WriteU8(unlock.Fields);
                        w.WriteU8(0);
                        for (int i = 0; i < 4; i++) w.WriteU32(i < unlock.Values.Length ? unlock.Values[i] : 0u);
                        w.WriteFixedString(unlock.Name, 24);
                    }

                    return (Status.Ok, buffer);
                }

                case Opcode.UnlockSet:
                {
                    if (payload.Length < 8) return (Status.BadArg, null);
                    var r = new SpanReader(payload);
                    byte id = r.ReadU8();
                    byte field = r.ReadU8();
                    r.Skip(2);
                    uint value = r.ReadU32();
                    if (field > 3) return (Status.BadArg, null);

                    int index = Unlocks.FindIndex(u => u.Id == id);
                    if (index < 0) return (Status.NotFound, null);
                    if (!Unlocks[index].HasField(field)) return (Status.BadArg, null);

                    var values = (uint[])Unlocks[index].Values.Clone();
                    values[field] = value;
                    Unlocks[index] = Unlocks[index] with { Values = values };
                    return (Status.Ok, null);
                }

                case Opcode.LevelFlagsGet:
                {
                    if (payload.Length < 1) return (Status.BadArg, null);
                    if (!LevelFlags.TryGetValue(payload[0], out var flags)) return (Status.Unsupported, null);

                    var buffer = new byte[2 + flags.Length];
                    var w = new SpanWriter(buffer);
                    w.WriteU16((ushort)flags.Length);
                    w.WriteBytes(flags);
                    return (Status.Ok, buffer);
                }

                case Opcode.LevelFlagsSet:
                {
                    if (payload.Length < 4) return (Status.BadArg, null);
                    var r = new SpanReader(payload);
                    byte planet = r.ReadU8();
                    byte value = r.ReadU8();
                    ushort offset = r.ReadU16();
                    if (!LevelFlags.TryGetValue(planet, out var flags)) return (Status.Unsupported, null);
                    if (offset >= flags.Length) return (Status.BadArg, null);
                    flags[offset] = value;
                    return (Status.Ok, null);
                }

                case Opcode.LevelFlagsReset:
                {
                    if (payload.Length < 1) return (Status.BadArg, null);
                    if (!LevelFlags.TryGetValue(payload[0], out var flags)) return (Status.Unsupported, null);
                    Array.Clear(flags);
                    LevelFlagsResetCount++;
                    return (Status.Ok, null);
                }

                case Opcode.PlanetList:
                {
                    var buffer = new byte[1 + Planets.Length * 24];
                    var w = new SpanWriter(buffer);
                    w.WriteU8((byte)Planets.Length);
                    foreach (var planet in Planets) w.WriteFixedString(planet, 24);
                    return (Status.Ok, buffer);
                }

                case Opcode.ModList:
                {
                    var buffer = new byte[1 + Mods.Count * ModEntry.Size];
                    buffer[0] = (byte)Mods.Count;
                    for (int i = 0; i < Mods.Count; i++)
                    {
                        Mods[i].ToBytes().CopyTo(buffer, 1 + i * ModEntry.Size);
                    }

                    return (Status.Ok, buffer);
                }

                case Opcode.ModRescan:
                    ModRescanCount++;
                    return (Status.Ok, null);

                case Opcode.AutosplitEvents when AutosplitSupported:
                {
                    if (payload.Length < 4) return (Status.BadArg, null);
                    uint since = new SpanReader(payload).ReadU32();

                    var events = AutosplitRing.Where(e => e.Seq > since).Take(AutosplitEventsReply.MaxEvents).ToArray();
                    uint latest = AutosplitRing.Count == 0 ? _autosplitSeq : AutosplitRing[^1].Seq;
                    return (Status.Ok, new AutosplitEventsReply(latest, events).ToBytes());
                }

                case Opcode.AutosplitDescribe when AutosplitSupported:
                {
                    if (AutosplitDescriptors.Count == 0) return (Status.Unsupported, null);
                    return (Status.Ok, AutosplitEventDesc.EncodeList(AutosplitDescriptors));
                }

                case Opcode.ComboSet:
                {
                    if (payload.Length < 8) return (Status.BadArg, null);
                    var r = new SpanReader(payload);
                    var action = (ComboAction)r.ReadU8();
                    r.Skip(3);
                    Combos[action] = r.ReadU32();
                    return (Status.Ok, null);
                }

                case Opcode.ComboList:
                {
                    var buffer = new byte[1 + Combos.Count * 8];
                    var w = new SpanWriter(buffer);
                    w.WriteU8((byte)Combos.Count);
                    foreach (var (action, mask) in Combos)
                    {
                        w.WriteU8((byte)action);
                        w.WriteZeros(3);
                        w.WriteU32(mask);
                    }

                    return (Status.Ok, buffer);
                }

                case Opcode.ComboSuspend:
                {
                    // `u8 suspend` and nothing else, so a client that pads the request out or
                    // sends an empty one is caught here rather than passing quietly.
                    if (payload.Length != 1) return (Status.BadArg, null);
                    CombosSuspended = payload[0] != 0;
                    return (Status.Ok, null);
                }

                case Opcode.DirCreate:
                {
                    string path = Encoding.UTF8.GetString(payload).TrimEnd('/');
                    if (path.Length == 0) return (Status.BadArg, null);
                    if (!Directories.Contains(path, StringComparer.Ordinal)) Directories.Add(path);
                    return (Status.Ok, null);
                }

                case Opcode.DirDelete:
                {
                    string path = Encoding.UTF8.GetString(payload).TrimEnd('/');
                    Directories.RemoveAll(d => d == path || d.StartsWith(path + "/", StringComparison.Ordinal));
                    foreach (var key in Files.Keys.Where(k => k.StartsWith(path + "/", StringComparison.Ordinal)).ToArray())
                    {
                        Files.Remove(key);
                    }

                    return (Status.Ok, null);
                }

                case Opcode.FileOpen:
                {
                    if (payload.Length < 2) return (Status.BadArg, null);
                    var mode = (FileMode)payload[0];
                    string path = Encoding.UTF8.GetString(payload, 1, payload.Length - 1);

                    if (mode == FileMode.Read)
                    {
                        if (!Files.ContainsKey(path)) return (Status.NotFound, null);
                    }
                    else
                    {
                        Files[path] = Array.Empty<byte>();
                    }

                    uint handle = _nextHandle++;
                    _openFiles[handle] = new OpenFile(path, mode);
                    var reply = new byte[4];
                    new SpanWriter(reply).WriteU32(handle);
                    return (Status.Ok, reply);
                }

                case Opcode.FileWrite:
                {
                    if (payload.Length < 4) return (Status.BadArg, null);
                    var r = new SpanReader(payload);
                    uint handle = r.ReadU32();
                    if (!_openFiles.TryGetValue(handle, out var file)) return (Status.NotFound, null);
                    if (file.Mode != FileMode.WriteTruncate) return (Status.BadArg, null);

                    var chunk = r.ReadRest();
                    if (chunk.Length > 65536) return (Status.BadArg, null);
                    Files[file.Path] = Files[file.Path].Concat(chunk.ToArray()).ToArray();
                    file.Position += chunk.Length;
                    return (Status.Ok, null);
                }

                case Opcode.FileRead:
                {
                    if (payload.Length < 8) return (Status.BadArg, null);
                    var r = new SpanReader(payload);
                    uint handle = r.ReadU32();
                    uint length = r.ReadU32();
                    if (!_openFiles.TryGetValue(handle, out var file)) return (Status.NotFound, null);
                    if (file.Mode != FileMode.Read) return (Status.BadArg, null);
                    if (length > 65536) return (Status.BadArg, null);

                    var content = Files.TryGetValue(file.Path, out var bytes) ? bytes : Array.Empty<byte>();
                    int available = Math.Max(0, content.Length - file.Position);
                    int take = (int)Math.Min(length, (uint)available);
                    var chunk = content.AsSpan(file.Position, take).ToArray();
                    file.Position += take;
                    return (Status.Ok, chunk);
                }

                case Opcode.FileClose:
                {
                    if (payload.Length < 4) return (Status.BadArg, null);
                    uint handle = new SpanReader(payload).ReadU32();
                    if (!_openFiles.Remove(handle)) return (Status.NotFound, null);
                    return (Status.Ok, null);
                }

                case Opcode.FileDelete:
                {
                    string path = Encoding.UTF8.GetString(payload);
                    return Files.Remove(path) ? (Status.Ok, null) : (Status.NotFound, null);
                }

                case Opcode.DirList:
                {
                    string path = Encoding.UTF8.GetString(payload).TrimEnd('/');
                    if (path.Length == 0) return (Status.BadArg, null);

                    string prefix = path + "/";
                    var entries = new List<(bool IsDir, uint Size, string Name)>();

                    foreach (var directory in Directories)
                    {
                        if (!directory.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        var rest = directory[prefix.Length..];
                        if (rest.Length == 0 || rest.Contains('/')) continue;
                        entries.Add((true, 0, rest));
                    }

                    foreach (var (file, content) in Files)
                    {
                        if (!file.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        var rest = file[prefix.Length..];
                        int slash = rest.IndexOf('/');
                        if (slash < 0) entries.Add((false, (uint)content.Length, rest));
                        else
                        {
                            var child = rest[..slash];
                            if (!entries.Any(e => e.IsDir && e.Name == child)) entries.Add((true, 0, child));
                        }
                    }

                    int size = 2 + entries.Sum(e => 6 + Encoding.UTF8.GetByteCount(e.Name));
                    var buffer = new byte[size];
                    var w = new SpanWriter(buffer);
                    w.WriteU16((ushort)entries.Count);
                    foreach (var entry in entries)
                    {
                        var name = Encoding.UTF8.GetBytes(entry.Name);
                        w.WriteU8(entry.IsDir ? (byte)1 : (byte)0);
                        w.WriteU8((byte)name.Length);
                        w.WriteU32(entry.Size);
                        w.WriteBytes(name);
                    }

                    return (Status.Ok, buffer);
                }

                // ------------------------------------------- 5.12 save files, revision 1.9

                case Opcode.SaveFileInfo:
                {
                    // A platform that cannot patch code refuses the block outright; a game with
                    // no helper is an OK answer with `supported` 0.
                    if (SaveFileUnsupported) return (Status.Unsupported, null);
                    if (!SaveFileSupported) return (Status.Ok, SaveFileInfo.None.ToBytes());

                    byte pending = 0;
                    if (_setAsidePending > 0) pending |= SaveFileInfo.PendingSetAside;
                    if (_loadPending > 0) pending |= SaveFileInfo.PendingLoad;
                    if (TransferRunning) pending |= SaveFileInfo.PendingTransfer;

                    // The helper clears its own request byte a frame or two later; counting the
                    // polls down stands in for that, so a client's wait loop is exercised.
                    if (_setAsidePending > 0) _setAsidePending--;
                    if (_loadPending > 0) _loadPending--;
                    AdvanceTransfer();

                    var info = new SaveFileInfo(true, true, SaveFileRunning, pending,
                                                (uint)SaveFileBuffer.Length,
                                                _transferDone, _transferTotal, _transferError);
                    return (Status.Ok, info.ToBytes());
                }

                case Opcode.SaveFileRead:
                {
                    if (SaveFileUnsupported || !SaveFileSupported) return (Status.Unsupported, null);
                    if (payload.Length < 8) return (Status.BadArg, null);

                    var r = new SpanReader(payload);
                    uint offset = r.ReadU32();
                    uint length = r.ReadU32();

                    if (length == 0 || length > QwarkClient.SaveFileChunkSize) return (Status.BadArg, null);
                    if (offset >= (uint)SaveFileBuffer.Length) return (Status.BadArg, null);
                    if (SaveFileReadFailsFrom is uint fails && offset >= fails)
                    {
                        return (Status.Busy, null);
                    }

                    // A read that runs off the end is trimmed, as section 5.12 has it.
                    uint left = (uint)SaveFileBuffer.Length - offset;
                    if (length > left) length = left;

                    return (Status.Ok, SaveFileBuffer.AsSpan((int)offset, (int)length).ToArray());
                }

                case Opcode.SaveFileWrite:
                {
                    if (SaveFileUnsupported || !SaveFileSupported) return (Status.Unsupported, null);
                    if (payload.Length < 5) return (Status.BadArg, null);

                    var r = new SpanReader(payload);
                    uint offset = r.ReadU32();
                    var data = r.ReadRest();

                    if (data.Length > QwarkClient.SaveFileChunkSize) return (Status.BadArg, null);
                    if (offset >= (uint)SaveFileBuffer.Length) return (Status.BadArg, null);
                    // Unlike a read, a write past the end is refused rather than trimmed.
                    if (offset + (uint)data.Length > (uint)SaveFileBuffer.Length) return (Status.BadArg, null);

                    data.CopyTo(SaveFileBuffer.AsSpan((int)offset));
                    SaveFileOrder.Add($"write {offset}");
                    return (Status.Ok, null);
                }

                // ------------------------------------ 5.13 the library, revision 1.10

                case Opcode.SaveFileCategories:
                {
                    if (SaveFileUnsupported || !SaveFileSupported) return (Status.Unsupported, null);
                    if (TransferRunning) return (Status.Busy, null);

                    var categories = SaveFileCategories();
                    var buffer = new byte[1 + categories.Length * ConsoleSaveFile.NameLength];
                    var w = new SpanWriter(buffer);
                    w.WriteU8((byte)categories.Length);
                    foreach (var category in categories) w.WriteFixedString(category, ConsoleSaveFile.NameLength);
                    return (Status.Ok, buffer);
                }

                case Opcode.SaveFileList:
                {
                    if (SaveFileUnsupported || !SaveFileSupported) return (Status.Unsupported, null);
                    if (payload.Length < ConsoleSaveFile.NameLength) return (Status.BadArg, null);
                    if (TransferRunning) return (Status.Busy, null);

                    var category = new SpanReader(payload).ReadFixedString(ConsoleSaveFile.NameLength);
                    if (!SaveFileCategories().Contains(category, StringComparer.OrdinalIgnoreCase))
                    {
                        return (Status.NotFound, null);
                    }

                    return (Status.Ok, ConsoleSaveFile.EncodeList(SaveFileList(category)));
                }

                case Opcode.SaveFileStore:
                case Opcode.SaveFileRestore:
                {
                    if (SaveFileUnsupported || !SaveFileSupported) return (Status.Unsupported, null);
                    if (payload.Length < 2 * ConsoleSaveFile.NameLength) return (Status.BadArg, null);

                    var r = new SpanReader(payload);
                    var category = r.ReadFixedString(ConsoleSaveFile.NameLength);
                    var name = r.ReadFixedString(ConsoleSaveFile.NameLength);

                    if (!name.EndsWith(SaveFileLibrary.Extension, StringComparison.Ordinal))
                    {
                        return (Status.BadArg, null);
                    }

                    // One aside buffer, so one transfer: a second is refused, not queued.
                    if (TransferRunning || _loadPending > 0 || _setAsidePending > 0)
                    {
                        _transferError = SaveFileError.BufferBusy;
                        return (Status.Busy, null);
                    }

                    var path = SaveFilePath(category, name);

                    if (opcode == Opcode.SaveFileStore)
                    {
                        SaveFileLibraryOrder.Add($"store {category}/{name}");

                        // The game fills the buffer first, then the console copies it out.
                        SaveFileBuffer = (byte[])SaveAsideContent.Clone();
                        var stored = (byte[])SaveFileBuffer.Clone();
                        BeginTransfer((uint)stored.Length, waitForSetAside: true, () =>
                        {
                            Files[path] = stored;
                            Files[path + QwarkClient.SaveFileSumExtension] =
                                Encoding.ASCII.GetBytes(Crc32.ToSumText(Crc32.Compute(stored)));
                        });

                        return (Status.Ok, null);
                    }

                    SaveFileLibraryOrder.Add($"restore {category}/{name}");

                    if (!Files.TryGetValue(path, out var content))
                    {
                        _transferError = SaveFileError.FileMissing;
                        return (Status.NotFound, null);
                    }

                    if (content.Length != SaveFileBuffer.Length)
                    {
                        _transferError = SaveFileError.ShortFile;
                        return (Status.BadArg, null);
                    }

                    BeginTransfer((uint)content.Length, waitForSetAside: false, () =>
                    {
                        SaveFileBuffer = (byte[])content.Clone();
                        LoadedSaveFile = (byte[])content.Clone();
                        _loadPending = Math.Max(1, SaveFilePendingPolls);
                    });

                    return (Status.Ok, null);
                }

                case Opcode.SaveFileCategory:
                {
                    if (SaveFileUnsupported || !SaveFileSupported) return (Status.Unsupported, null);
                    if (payload.Length < 1 + ConsoleSaveFile.NameLength) return (Status.BadArg, null);
                    if (TransferRunning) return (Status.Busy, null);

                    var op = (SaveFileCategoryOp)payload[0];
                    var name = new SpanReader(payload[1..]).ReadFixedString(ConsoleSaveFile.NameLength);
                    if (name.Length == 0 || name.Contains('/') || name.StartsWith('.'))
                    {
                        return (Status.BadArg, null);
                    }

                    var folder = $"{SaveFileRoot}/{name}";

                    if (op == SaveFileCategoryOp.Create)
                    {
                        if (!Directories.Contains(folder, StringComparer.Ordinal)) Directories.Add(folder);
                        return (Status.Ok, null);
                    }

                    if (op != SaveFileCategoryOp.Delete) return (Status.BadArg, null);
                    if (!SaveFileCategories().Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        return (Status.NotFound, null);
                    }

                    // The sidecars are swept up; a save that is still there refuses the delete.
                    var prefix = folder + "/";
                    var left = Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                    foreach (var key in left.Where(k => k.EndsWith(QwarkClient.SaveFileSumExtension, StringComparison.Ordinal)))
                    {
                        Files.Remove(key);
                    }

                    if (Files.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        return (Status.IoError, null);
                    }

                    Directories.RemoveAll(d => d == folder);
                    return (Status.Ok, null);
                }

                case Opcode.FileRename:
                {
                    if (payload.Length < 3) return (Status.BadArg, null);

                    int fromLength = (payload[0] << 8) | payload[1];
                    if (fromLength <= 0 || payload.Length <= 2 + fromLength) return (Status.BadArg, null);

                    string from = Encoding.UTF8.GetString(payload, 2, fromLength);
                    string to = Encoding.UTF8.GetString(payload, 2 + fromLength, payload.Length - 2 - fromLength);

                    if (!Files.TryGetValue(from, out var moving)) return (Status.NotFound, null);
                    if (Files.ContainsKey(to)) return (Status.BadArg, null);

                    Files.Remove(from);
                    Files[to] = moving;
                    return (Status.Ok, null);
                }

                default:
                    return (Status.UnknownOp, null);
            }
        }
    }

    private sealed class OpenFile
    {
        public OpenFile(string path, FileMode mode)
        {
            Path = path;
            Mode = mode;
        }

        public string Path { get; }

        public FileMode Mode { get; }

        public int Position { get; set; }
    }

    private readonly Dictionary<uint, OpenFile> _openFiles = new();
    private uint _nextHandle = 1;

    private bool InRange(uint address, uint length) =>
        address >= MemoryBase && address + length <= MemoryBase + (uint)Memory.Length;

    private TelemetryPacket BuildTelemetry()
    {
        var watches = Watches.Select(w =>
        {
            bool valid = InRange(w.Address, w.Size) && _session.State == SessionState.Ingame;
            ulong value = 0;
            if (valid)
            {
                int offset = (int)(w.Address - MemoryBase);
                for (int i = 0; i < w.Size; i++) value = (value << 8) | Memory[offset + i];
            }

            return new WatchValue(w.Id, w.Size, valid, value);
        }).ToArray();

        return new TelemetryPacket(_session, watches);
    }

    private async Task TelemetryLoopAsync()
    {
        using var udp = new UdpClient();
        while (!_cts.IsCancellationRequested)
        {
            IPEndPoint? target;
            byte[] bytes;
            List<byte[]>? pushes = null;
            lock (_gate)
            {
                // The tick thread runs at 120 Hz and telemetry goes out every fourth tick.
                _session = _session with { Tick = _session.Tick + 4 };
                if (AnimateInput) _session = _session with
                {
                    PadMask = (uint)PadButtons.All[(_session.Tick / 60) % PadButtons.All.Length].Button,
                    Analog = new[]
                    {
                        MathF.Sin(_session.Tick / 40f),
                        MathF.Cos(_session.Tick / 40f),
                        MathF.Sin(_session.Tick / 25f),
                        MathF.Cos(_session.Tick / 25f),
                    },
                };

                // Revision 1.11: the announcement goes out a handful of times and then the module
                // says nothing at all until the game is up.
                if ((_session.Flags & SessionFlags.TelemetryQuiet) != 0 && _quietWarningsLeft <= 0)
                {
                    target = null;
                }
                else
                {
                    if (_quietWarningsLeft > 0) _quietWarningsLeft--;
                    target = _telemetryTarget;
                }

                bytes = target is null ? Array.Empty<byte>() : BuildTelemetry().ToBytes();

                // One copy of each pending autosplit event per tick, on the telemetry socket.
                if (target is not null && _autosplitPushes.Count > 0)
                {
                    pushes = _autosplitPushes.Select(p => AutosplitDatagram.Build(p.Event)).ToList();
                    for (int i = _autosplitPushes.Count - 1; i >= 0; i--)
                    {
                        var (ev, left) = _autosplitPushes[i];
                        if (left <= 1) _autosplitPushes.RemoveAt(i);
                        else _autosplitPushes[i] = (ev, left - 1);
                    }
                }
            }

            if (target is not null)
            {
                try
                {
                    await udp.SendAsync(bytes, bytes.Length, target);
                    foreach (var push in pushes ?? Enumerable.Empty<byte[]>())
                    {
                        await udp.SendAsync(push, push.Length, target);
                    }
                }
                catch (SocketException)
                {
                    // The subscriber went away.
                }
            }

            try
            {
                await Task.Delay(TelemetryInterval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public static byte[] EncodeDescribe(DescribeResult describe)
    {
        int size = 1 + 1 + describe.Groups.Length * 24 + 1 + describe.Readouts.Length * 24 + 1
                   + describe.Features.Length * Feature.Size;
        var buffer = new byte[size];
        var w = new SpanWriter(buffer);
        w.WriteU8((byte)describe.Game);
        w.WriteU8((byte)describe.Groups.Length);
        foreach (var group in describe.Groups) w.WriteFixedString(group, 24);
        w.WriteU8((byte)describe.Readouts.Length);
        foreach (var readout in describe.Readouts) w.WriteFixedString(readout, 24);
        w.WriteU8((byte)describe.Features.Length);
        foreach (var feature in describe.Features) w.WriteBytes(feature.ToBytes());
        return buffer;
    }

    public void Dispose()
    {
        _cts.Cancel();
        DropClients();
        try { _listener.Stop(); } catch { /* already stopped */ }
        _cts.Dispose();
    }
}
