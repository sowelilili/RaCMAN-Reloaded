namespace RaCMAN.Protocol.Testing;

/// <summary>
/// Tools of Destruction (game 5, revision 1.27) and its Lua ops: a ToD session with the feature
/// table, unlock categories and planets qwark describes for it, LUA_STATUS, LUA_EXEC answering
/// <c>Lua 5.0.2</c> to a chunk that asks for <c>_VERSION</c>, and LUA_PATCH with a few words. None
/// of it is the game's real data: only the labels and the shapes are, which is what the client
/// draws from.
/// </summary>
public sealed partial class FakeQwarkServer
{
    /// <summary>ToD's title id, PSN EU v01.00.</summary>
    public const string TodTitleId = "NPEA00452";

    /// <summary>What a chunk that returns <c>_VERSION</c> answers.</summary>
    public const string LuaVersion = "Lua 5.0.2";

    /// <summary>The game has Lua: LUA_STATUS and LUA_EXEC answer. Off answers both UNSUPPORTED, as every other game does.</summary>
    public bool HasLua
    {
        get { lock (_gate) return _hasLua; }
        set { lock (_gate) _hasLua = value; }
    }

    private bool _hasLua;

    /// <summary>
    /// Under RPCS3 (<see cref="NoCodePatches"/>): the Lua helper's RPCS3 patch was applied at this
    /// boot. On a console the helper goes in with the first LUA_EXEC of a boot instead.
    /// </summary>
    public bool LuaPatchInstalled
    {
        get { lock (_gate) return _luaPatchInstalled; }
        set { lock (_gate) _luaPatchInstalled = value; }
    }

    private bool _luaPatchInstalled;

    /// <summary>LUA_STATUS answers <c>installed</c> 2: qwark is still looking for the patch (RPCS3).</summary>
    public bool LuaChecking { get; set; }

    /// <summary>NOT_FOUND to LUA_EXEC: no lua_State, the main menu or a load.</summary>
    public bool LuaNoState { get; set; }

    /// <summary>The game's own last script error, as LUA_STATUS reports it.</summary>
    public uint LuaErrorSeq { get; set; }

    public string LuaErrorScript { get; set; } = string.Empty;

    public string LuaError { get; set; } = string.Empty;

    /// <summary>The words LUA_PATCH hands out: a cave and a hook, laid out as SAVEFILE_PATCH's.</summary>
    public PatchWord[] LuaPatchWords { get; set; } =
    {
        new(0x00F10000, 0x7C0802A6),
        new(0x00F10004, 0x9421FF80),
        new(0x00F10008, 0x4E800020),
        new(0x00412340, 0x48AFDCC1),
    };

    /// <summary>Every chunk LUA_EXEC was sent, oldest first.</summary>
    public List<string> LuaSources { get; } = new();

    /// <summary>When set, what LUA_EXEC answers in place of the fake's own rule.</summary>
    public Func<string, LuaExecResult>? LuaExecReply { get; set; }

    /// <summary>The sequence number each LUA_EXEC reply carries.</summary>
    private uint _luaSeq;

    /// <summary>Gameplay frames, stood in for by LUA_STATUS polls.</summary>
    private uint _luaHeartbeat;

    /// <summary>The boot the console-side install belongs to: a LUA_EXEC in it put the helper in.</summary>
    private uint? _luaInstalledGeneration;

    /// <summary>Whether the helper is in the running game: the RPCS3 patch, or a LUA_EXEC this boot.</summary>
    private bool LuaInstalledNow =>
        NoCodePatches ? _luaPatchInstalled : _luaInstalledGeneration == _session.Generation;

    /// <summary>ToD's 19 planets, the game's LEVEL_ enum, as qwark's PLANET_LIST names them.</summary>
    public static readonly string[] TodPlanets =
    {
        "Metropolis", "Cobalia", "Stratus City", "Fastoon", "Space Combat I", "Imperial Fight Fest",
        "Apogee Space Station", "Pirate Base", "Space Combat II", "Rykan V", "Sargasso", "Iris",
        "Zordoom Prison", "Space Combat III", "Kerchu City", "Slag's Fleet", "Cragmite Ruins",
        "Meridian City", "Fastoon II",
    };

    /// <summary>
    /// ToD's AUTOSPLIT_DESCRIBE as qwark lays it out (revision 1.29): the planet split, "Tachyon
    /// defeated", code 7, the fade to black after Tachyon's last hit (the final split, on), the
    /// credits and every mission, the old ToD autosplitter's long load held (HOLD: game time stands
    /// still while it lasts) and the quit to the XMB at the old script's 19.95 s. Codes 2 and 8 are
    /// retired.
    /// </summary>
    public static AutosplitEventDesc[] TodAutosplitDescriptors() => new[]
    {
        new AutosplitEventDesc(1, AutosplitKind.Split,
            AutosplitEventFlags.EnabledByDefault | AutosplitEventFlags.PlanetRoute, 0, "Planet entered"),
        new AutosplitEventDesc(7, AutosplitKind.Split, AutosplitEventFlags.EnabledByDefault, 0, "Tachyon defeated"),
        new AutosplitEventDesc(5, AutosplitKind.Split, AutosplitEventFlags.None, 0, "Credits"),
        new AutosplitEventDesc(6, AutosplitKind.Split, AutosplitEventFlags.None, 0, "Mission completed"),
        new AutosplitEventDesc(4, AutosplitKind.LoadStart,
            AutosplitEventFlags.EnabledByDefault | AutosplitEventFlags.Hold, 0, "Long load"),
        new AutosplitEventDesc(3, AutosplitKind.Pause,
            AutosplitEventFlags.EnabledByDefault | AutosplitEventFlags.Normalise, 19_950_000, "Quit to XMB"),
    };

    /// <summary>
    /// ToD's DESCRIBE as qwark lays it out since the first hardware test (2026-10-27): the labels
    /// and kinds qwark gives, no addresses. The RYNO plans' buttons are gone, id 9 is Infinite
    /// health, and God Ratchet (the old trainer's) and Fast loads are new. Revision 1.28:
    /// the savefile manager's two flagged actions, 40 and 41, in a fourth group. Revision 1.29:
    /// Fast loads is a code patch, WRITES_CODE, with a code switch under RPCS3, and Infinite ammo
    /// and Infinite health are no longer LIVE, so they have an "on boot" box; God Ratchet still is.
    /// Revision 1.29: the cutscene cache, Cache cutscenes (19, naming readout 3 as its status
    /// readout, with qwark's id, which the client's status line keys on) and its two toggles.
    /// </summary>
    public static DescribeResult TodDescribe() => new(
        GameId.Tod,
        new[] { "Cheats", "Player", "Progress", "Savefile" },
        new[] { "Bolts", "Raritanium", "Challenge mode", "Cutscene cache" },
        new[]
        {
            new Feature(17, FeatureKind.Toggle, 0, 0, FeatureFlags.WritesCode, 0xFF, 0, 0, "Fast loads"),
            new Feature(18, FeatureKind.Toggle, 0, 0, FeatureFlags.None, 0xFF, 0, 0, "Remove cutscenes"),
            new Feature(0, FeatureKind.Toggle, 0, 0, FeatureFlags.None, 0xFF, 0, 0, "Infinite ammo"),
            new Feature(1, FeatureKind.Toggle, 0, 0, FeatureFlags.None, 0xFF, 0, 0, "Infinite health"),
            new Feature(16, FeatureKind.Toggle, 0, 0, FeatureFlags.Live, 0xFF, 0, 0, "God Ratchet"),
            new Feature(19, FeatureKind.Action, 0, 0, FeatureFlags.None, 3, 0, 0, "Cache cutscenes"),
            new Feature(20, FeatureKind.Toggle, 0, 0, FeatureFlags.None, 0xFF, 0, 0, "Cache cutscenes on reset"),
            new Feature(21, FeatureKind.Toggle, 0, 0, FeatureFlags.None, 0xFF, 0, 0, "Disable cutscene caching"),
            new Feature(2, FeatureKind.Action, 1, 0, FeatureFlags.None, 0xFF, 0, 0, "Die"),
            new Feature(3, FeatureKind.Value, 1, 0, FeatureFlags.None, 0, 0, 0, "Bolts"),
            new Feature(4, FeatureKind.Value, 1, 0, FeatureFlags.None, 1, 0, 0, "Raritanium"),
            new Feature(5, FeatureKind.Value, 1, 0, FeatureFlags.None, 2, 0, 255, "Challenge mode"),
            new Feature(6, FeatureKind.Action, 2, 0, FeatureFlags.None, 0xFF, 0, 0, "Unlock all skill points"),
            new Feature(7, FeatureKind.Action, 2, 0, FeatureFlags.None, 0xFF, 0, 0, "Reset skill points"),
            new Feature(8, FeatureKind.Action, 2, 0, FeatureFlags.None, 0xFF, 0, 0, "Unlock all gold bolts"),
            new Feature(9, FeatureKind.Action, 2, 0, FeatureFlags.None, 0xFF, 0, 0, "Reset gold bolts"),
            new Feature(40, FeatureKind.Action, 3, 0, FeatureFlags.SaveAside, 0xFF, 0, 0, "Set aside file"),
            new Feature(41, FeatureKind.Action, 3, 0, FeatureFlags.LoadAside, 0xFF, 0, 0, "Load set-aside file"),
        });

    /// <summary>The size of a ToD save, the GAME.SAV image (revision 1.28).</summary>
    public const int TodSaveSize = 0x906F0;

    /// <summary>Remove cutscenes' (id 18) auto bit, which qwark sets with no config.txt key for it (revision 1.29).</summary>
    public const ulong TodRemoveCutscenesAutoBit = 1UL << 18;

    /// <summary>
    /// Makes this fake a ToD console: a new boot of NPEA00452 INGAME on Metropolis, ToD's feature
    /// table, unlock categories and planets, Lua, its level flags (one 40-byte region for every
    /// planet, with names, revision 1.28), its autosplitter (revision 1.28), its
    /// savefile manager (revision 1.28: the game's own save system, so installed on every
    /// platform and no patch to hand out), and none of the manips or code switches, which ToD does
    /// not have.
    /// </summary>
    public void UseTod()
    {
        lock (_gate)
        {
            Describe = TodDescribe();
            Planets = (string[])TodPlanets.Clone();
            PlanetOrder = null;
            LevelFlags.Clear();
            var todFlags = new byte[40];
            todFlags[3] = 0x80;   // flag 39, LVL_COBALIA_BRIDGE_EXTENDED
            for (byte planet = 0; planet < TodPlanets.Length; planet++) LevelFlags[planet] = todFlags;
            LevelFlagNames = TodLevelFlagNames();
            UnlockCategories = new[] { "Weapons", "Devices", "Gadgets", "Items" };
            UnlockFields = new[]
            {
                new UnlockField("Owned", UnlockFieldKind.Flag, 0),
                new UnlockField("Level", UnlockFieldKind.Number, 10),
                new UnlockField(string.Empty, UnlockFieldKind.Flag, 0),
                new UnlockField("Ammo", UnlockFieldKind.Number, 0),
            };
            Unlocks.Clear();
            Unlocks.AddRange(new[]
            {
                new Unlock(0, 0, 0b1011, new uint[] { 1, 2, 0, 120 }, "Combuster"),
                new Unlock(1, 0, 0b1011, new uint[] { 0, 0, 0, 0 }, "Fusion Grenade"),
                new Unlock(2, 1, 0b0001, new uint[] { 1, 0, 0, 0 }, "Groovitron"),
                new Unlock(3, 2, 0b0001, new uint[] { 1, 0, 0, 0 }, "Swingshot"),
                new Unlock(4, 3, 0b0001, new uint[] { 0, 0, 0, 0 }, "Heli-Pack"),
            });

            _hasLua = true;
            SaveFileSupported = true;
            SaveFileOwnSystem = true;
            SaveAsideContent = BuildSaveAsideContent(TodSaveSize);
            SaveFileBuffer = new byte[TodSaveSize];
            AutosplitSupported = true;
            AutosplitDescriptors = new List<AutosplitEventDesc>(TodAutosplitDescriptors());
            ManipsSupported = false;
            CodeSwitchesSupported = false;

        }

        var session = Session;
        Session = session with
        {
            State = SessionState.Ingame,
            Game = GameId.Tod,
            TitleId = TodTitleId,
            CurrentPlanet = 0,
            Generation = session.Generation + 1,
            Readout = TodReadouts(),

            // qwark revision 1.29 (2026-10-31): Remove cutscenes ships auto-flagged.
            ToggleAuto = session.ToggleAuto | TodRemoveCutscenesAutoBit,
        };
    }

    /// <summary>A few of ToD's flags, as LEVELFLAGS_NAMES names them: flag n is byte (n >> 6) * 8 + 7 - ((n &amp; 63) >> 3), bit n &amp; 7.</summary>
    public static LevelFlagNames TodLevelFlagNames()
    {
        static LevelFlagName Flag(int n, byte group, string name) =>
            new((ushort)((((n >> 6) * 8) + 7 - ((n & 63) >> 3)) * 8 + (n & 7)), (ushort)n, group, name);

        var groups = TodPlanets.Select((name, i) => new LevelFlagGroup((byte)i, name))
            .Append(new LevelFlagGroup(LevelFlagGroup.NoPlanet, "Other")).ToArray();
        return new LevelFlagNames(groups, new[]
        {
            Flag(38, 0, "LVL_METROPOLIS_GRINDRAIL_USED"),
            Flag(183, 0, "MOVIE_METROPOLIS_INTRO"),
            Flag(39, 1, "LVL_COBALIA_BRIDGE_EXTENDED"),
            Flag(40, 1, "LVL_COBALIA_GEL_FACTORY_ACTIVE"),
            Flag(186, 1, "MOVIE_COBALIA_INTRO"),
            Flag(104, 2, "LVL_STRATUS_CITY_LEFT_LEVEL"),
            Flag(0, (byte)TodPlanets.Length, "GLOBAL_FLAG_TEST"),
            Flag(33, (byte)TodPlanets.Length, "RYNO_HAS1"),
            Flag(287, (byte)TodPlanets.Length, string.Empty),
        });
    }

    private static uint[] TodReadouts()
    {
        var readouts = new uint[SessionInfo.ReadoutCount];
        readouts[0] = 15000; // Bolts
        readouts[1] = 12;    // Raritanium
        readouts[2] = 0;     // Challenge mode
        return readouts;
    }

    /// <summary>LUA_EXEC, LUA_STATUS and LUA_PATCH, under the gate.</summary>
    private (Status, byte[]?) HandleLua(Opcode opcode, byte[] payload)
    {
        switch (opcode)
        {
            case Opcode.LuaStatus:
            {
                if (!_hasLua) return (Status.Unsupported, null);
                if (EnforceIngame && _session.State != SessionState.Ingame) return (Status.NotIngame, null);

                var installed = LuaChecking ? LuaInstalled.Checking
                    : LuaInstalledNow ? LuaInstalled.Yes
                    : LuaInstalled.No;
                bool yes = installed == LuaInstalled.Yes;
                if (yes) _luaHeartbeat++;

                var info = yes
                    ? new LuaStatusInfo(installed, LuaPhase.Idle, _luaHeartbeat, LuaNoState ? 0u : 0x102043D0u,
                        LuaErrorSeq, 0, LuaErrorScript, LuaError)
                    : new LuaStatusInfo(installed, LuaPhase.Idle, 0, 0, 0, 0, string.Empty, string.Empty);
                return (Status.Ok, info.ToBytes());
            }

            case Opcode.LuaExec:
            {
                if (!_hasLua) return (Status.Unsupported, null);
                if (EnforceIngame && _session.State != SessionState.Ingame) return (Status.NotIngame, null);

                // qwark-rpcs3 is still looking for the patch: try again. Then, under RPCS3, the
                // helper is the patch's, and qwark cannot put it in itself.
                if (LuaChecking) return (Status.Busy, null);
                if (NoCodePatches && !_luaPatchInstalled) return (Status.Unsupported, null);

                string? source = LuaExecRequest.Decode(payload);
                if (source is null) return (Status.BadArg, null);
                if (LuaNoState) return (Status.NotFound, null);

                LuaSources.Add(source);
                if (!NoCodePatches) _luaInstalledGeneration = _session.Generation;

                uint seq = ++_luaSeq;
                var reply = LuaExecReply?.Invoke(source) ?? Answer(source);
                return (Status.Ok, (reply with { Seq = seq }).ToBytes());
            }

            case Opcode.LuaPatch:
            {
                // The PS3 module writes the helper itself, so only an emulator build has a patch.
                if (!_hasLua || !NoCodePatches) return (Status.Unsupported, null);
                if (EnforceIngame && _session.State != SessionState.Ingame) return (Status.NotIngame, null);

                var words = LuaPatchWords;
                var bytes = Array.Empty<PatchByte>();
                return (Status.Ok, new PatchReply(PatchReply.StampFor(words, bytes), words, bytes).ToBytes());
            }

            default:
                return (Status.UnknownOp, null);
        }
    }

    /// <summary>
    /// The fake's Lua: a chunk that mentions <c>_VERSION</c> returns "Lua 5.0.2", one that calls
    /// <c>error(</c> is a runtime error, one that does not parse (an unclosed bracket) is a syntax
    /// error, and anything else returns nothing.
    /// </summary>
    private static LuaExecResult Answer(string source)
    {
        if (source.Count(c => c == '(') != source.Count(c => c == ')'))
        {
            return new LuaExecResult(LuaExecStatus.SyntaxError, 0, LuaType.String, LuaOutFlags.None,
                "[string \"chunk\"]:1: `)' expected near `<eof>'");
        }

        if (source.Contains("error(", StringComparison.Ordinal))
        {
            return new LuaExecResult(LuaExecStatus.RuntimeError, 0, LuaType.String, LuaOutFlags.None,
                "[string \"chunk\"]:1: error raised by the chunk");
        }

        if (source.Contains("_VERSION", StringComparison.Ordinal))
        {
            return new LuaExecResult(LuaExecStatus.Ok, 0, LuaType.String, LuaOutFlags.None, LuaVersion);
        }

        return new LuaExecResult(LuaExecStatus.Ok, 0, LuaType.None, LuaOutFlags.None, string.Empty);
    }
}
