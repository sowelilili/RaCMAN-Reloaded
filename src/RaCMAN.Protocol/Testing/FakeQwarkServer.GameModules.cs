namespace RaCMAN.Protocol.Testing;

/// <summary>
/// Game modules (revision 1.26, qwark's PROTOCOL.md 9.11): a test build whose games are SPRX files
/// of their own, <c>/dev_hdd0/qwark/rac1.sprx</c> .. <c>rac4.sprx</c>, in the same in-memory filesystem the
/// file ops use. With <see cref="GameModules"/> on, a session in one of the four games reports
/// <c>game</c> 0 and flags2 bit5 or bit6 until its module is in, answers every game op UNSUPPORTED
/// meanwhile, and answers GAME_MODULE with why. Off, the default, is a module whose games are linked
/// in: no bit is ever set and GAME_MODULE answers LINKED, as a release does.
/// </summary>
public sealed partial class FakeQwarkServer
{
    /// <summary>The module's games are modules of their own (a test build of revision 1.26).</summary>
    public bool GameModules
    {
        get { lock (_gate) return _gameModules; }
        set { lock (_gate) _gameModules = value; }
    }

    private bool _gameModules;

    /// <summary>
    /// The memory gate holds a good module back: the session waits (flags2 bit6) with the figures
    /// below until this goes off, and then the game arrives.
    /// </summary>
    public bool WaitsForMemory
    {
        get { lock (_gate) return _waitsForMemory; }
        set { lock (_gate) _waitsForMemory = value; }
    }

    private bool _waitsForMemory;

    /// <summary>GAME_MODULE's lowest_kb, the lowest free reading over the window.</summary>
    public uint GateLowestKb { get; set; } = 212;

    /// <summary>GAME_MODULE's now_kb.</summary>
    public uint GateNowKb { get; set; } = 660;

    /// <summary>GAME_MODULE's need_kb: one page and the 64 KB transient.</summary>
    public uint GateNeedKb { get; set; } = 128;

    /// <summary>GAME_MODULE's floor_kb, config.txt's game.floor_kb.</summary>
    public uint GateFloorKb { get; set; } = 128;

    /// <summary>How many GAME_MODULE requests arrived.</summary>
    public int GameModuleCount { get; private set; }

    /// <summary>
    /// A module this fake counts as good for <paramref name="build"/> and <paramref name="game"/>:
    /// a SELF's magic, then the build and the game at bytes 8 and 9, and filler. The real core
    /// checks the module's build number, build ID and table; the fake reads its two bytes.
    /// </summary>
    public static byte[] GameModuleFor(byte build, GameId game, int length = 4096)
    {
        var module = new byte[Math.Max(length, 128)];
        for (int i = 16; i < module.Length; i++) module[i] = (byte)(i * 7 + (int)game);
        GameModule.SelfMagic.CopyTo(module);
        module[8] = build;
        module[9] = (byte)game;
        return module;
    }

    /// <summary>Puts a good module for this fake's build, or for <paramref name="build"/>, where the module reads it.</summary>
    public void PutGameModule(GameId game, byte? build = null)
    {
        lock (_gate) Files[GameModule.ConsolePath(game)] = GameModuleFor(build ?? _session.QwarkVersion, game);
    }

    /// <summary>Under the gate: what the module makes of the running game's module now.</summary>
    private (GameModuleState State, uint Error, uint ModuleId) GameModuleLook()
    {
        var game = _session.Game;
        if (_session.State != SessionState.Ingame || _unknownGame || !GameModule.HasModule(game))
        {
            return (GameModuleState.None, 0, 0);
        }

        if (!_gameModules) return (GameModuleState.Linked, 0, 0);

        if (!Files.TryGetValue(GameModule.ConsolePath(game), out var file)) return (GameModuleState.Missing, 0, 0);
        if (!GameModule.LooksLikeSprx(file)) return (GameModuleState.Failed, 0, 0);
        if (file[9] != (byte)game) return (GameModuleState.Failed, 0x80000100u, 0);
        if (file[8] != _session.QwarkVersion) return (GameModuleState.Other, GameModuleInfo.OtherBuildNumber, file[8]);

        return (_waitsForMemory ? GameModuleState.Waiting : GameModuleState.Loaded, 0, 0);
    }

    /// <summary>Under the gate: the session's game is not in, so it reads as game 0.</summary>
    private SessionFlags2 GameModuleBits()
    {
        return GameModuleLook().State switch
        {
            GameModuleState.Waiting => SessionFlags2.GameWaiting,
            GameModuleState.Missing or GameModuleState.Failed or GameModuleState.Other => SessionFlags2.GameModuleBad,
            _ => SessionFlags2.None,
        };
    }

    /// <summary>
    /// Under the gate, as the game boots (a new generation): a staged <c>rac1.sprx.new</c> (..) is
    /// put in place of the game's module, as the core does.
    /// </summary>
    private void AdoptStagedGameModule()
    {
        if (!_gameModules || !GameModule.HasModule(_session.Game)) return;

        string staged = GameModule.StagedPath(_session.Game);
        if (!Files.Remove(staged, out var module)) return;
        Files[GameModule.ConsolePath(_session.Game)] = module;
    }

    /// <summary>Under the gate: GAME_MODULE's reply, from the look above.</summary>
    private GameModuleInfo GameModuleReply()
    {
        GameModuleCount++;
        var (state, error, id) = GameModuleLook();
        bool waiting = state == GameModuleState.Waiting;
        var flags = (_gameModules ? GameModuleFlags.Modules : GameModuleFlags.None)
                    | (state == GameModuleState.Loaded ? GameModuleFlags.Resident : GameModuleFlags.None);

        return new GameModuleInfo(
            state,
            state == GameModuleState.None ? GameId.None : _session.Game,
            Pages: _gameModules && state != GameModuleState.Missing ? (byte)1 : (byte)0,
            flags,
            LowestKb: GateLowestKb,
            NowKb: GateNowKb,
            NeedKb: GateNeedKb,
            FloorKb: GateFloorKb,
            WindowMs: 3000,
            WaitedMs: waiting ? 3010u : 0u,
            Error: error,
            LoadMicroseconds: state == GameModuleState.Loaded ? 12_000u : 0u,
            ModuleId: id);
    }
}
