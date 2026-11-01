namespace RaCMAN.Protocol;

/// <summary>Where Tools of Destruction's Cache cutscenes is, by the state in its status word (PROTOCOL.md section 5.17).</summary>
public enum CutsceneCacheState : byte
{
    /// <summary>No run this boot. A boot of the game comes back to it, as the game clears its cache.</summary>
    None = 0,

    /// <summary>Asked for, waiting for gameplay or for the game's own caching of the level to finish.</summary>
    Waiting = 1,

    Caching = 2,

    Done = 3,

    Failed = 4,
}

/// <summary>What a waiting request waits for.</summary>
public enum CutsceneCacheWait : byte
{
    None = 0,

    /// <summary>The game is not in gameplay (the title, a load, the pause).</summary>
    Gameplay = 1,

    /// <summary>The game's level warmer is still reading the level's own files.</summary>
    Warmer = 2,
}

/// <summary>Why a request stopped.</summary>
public enum CutsceneCacheFailure : byte
{
    None = 0,

    /// <summary>A level load, or the title, came before it finished.</summary>
    LevelChanged = 1,

    /// <summary>The game's level table could not be read.</summary>
    NoTable = 2,

    /// <summary>The game's warmer stopped reading.</summary>
    Stalled = 3,

    /// <summary>Disable cutscene caching is on.</summary>
    Disabled = 4,

    /// <summary>The game's memory could not be read or written.</summary>
    Unreadable = 5,

    /// <summary>The game closed.</summary>
    GameClosed = 6,
}

/// <summary>
/// Tools of Destruction's Cache cutscenes status, the word its row names as its status readout
/// (revision 1.29, PROTOCOL.md section 5.17):
/// <code>
/// bits 28..31  state    CutsceneCacheState
/// bits 24..27  detail   Waiting: CutsceneCacheWait. Caching: the planet, 1 Fastoon, 2 Stratus City.
///                       Failed: CutsceneCacheFailure
/// bits 16..23  percent  Caching: of that planet. Done: 100
/// bits 8..15   seconds  since the request, or how long it ran, at most 255
/// bits 0..7    seq      the requests that have ended since the game started, modulo 256
/// </code>
/// </summary>
public readonly record struct CutsceneCacheStatus(
    CutsceneCacheState State,
    byte Detail,
    byte Percent,
    byte Seconds,
    byte Seq)
{
    /// <summary>The feature whose row names this word: Cache cutscenes, ToD's id 19.</summary>
    public const byte FeatureId = 19;

    /// <summary>What a waiting request waits for, or None for every other state.</summary>
    public CutsceneCacheWait Wait => State == CutsceneCacheState.Waiting ? (CutsceneCacheWait)Detail : CutsceneCacheWait.None;

    /// <summary>Why a failed request stopped, or None for every other state.</summary>
    public CutsceneCacheFailure Failure =>
        State == CutsceneCacheState.Failed ? (CutsceneCacheFailure)Detail : CutsceneCacheFailure.None;

    /// <summary>The planet being cached, or null when none is.</summary>
    public string? Planet => State != CutsceneCacheState.Caching ? null : Detail switch
    {
        1 => "Fastoon",
        2 => "Stratus City",
        _ => null,
    };

    public static CutsceneCacheStatus Parse(uint word) => new(
        (CutsceneCacheState)(word >> 28),
        (byte)((word >> 24) & 0xF),
        (byte)(word >> 16),
        (byte)(word >> 8),
        (byte)word);

    /// <summary>The other direction, for the fake console the tests drive.</summary>
    public uint ToWord() =>
        ((uint)State & 0xF) << 28
        | ((uint)Detail & 0xF) << 24
        | (uint)Percent << 16
        | (uint)Seconds << 8
        | Seq;
}
