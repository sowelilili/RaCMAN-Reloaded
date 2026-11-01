namespace RaCMAN.Protocol;

/// <summary>Where Tools of Destruction's Cache cutscenes is, by the state in its status word (PROTOCOL.md section 5.17).</summary>
public enum CutsceneCacheState : byte
{
    /// <summary>No run this boot. A boot of the game comes back to it, as the game clears its cache.</summary>
    None = 0,

    /// <summary>Asked for, waiting for gameplay or for the game's level warmer to have a file of its own to go ahead of.</summary>
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

    /// <summary>The game's level warmer has no file of its own in hand to go ahead of yet (revision 1.29: the request goes first).</summary>
    Warmer = 2,
}

/// <summary>Why a request stopped.</summary>
public enum CutsceneCacheFailure : byte
{
    None = 0,

    /// <summary>The title came before it finished. A level load no longer stops it: what is left goes on on the next level.</summary>
    TitleFirst = 1,

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

    /// <summary>A load into Stratus City came before its cutscene sound was in.</summary>
    TooLateStratusCity = 7,

    /// <summary>A load into Fastoon came before its cutscene sound was in.</summary>
    TooLateFastoon = 8,
}

/// <summary>
/// Tools of Destruction's Cache cutscenes status, the word its row names as its status readout
/// (revision 1.29, PROTOCOL.md section 5.17):
/// <code>
/// bits 28..31  state    CutsceneCacheState
/// bits 24..27  detail   Waiting: CutsceneCacheWait. Caching: the planet, 1 Fastoon, 2 Stratus City.
///                       Failed: CutsceneCacheFailure
/// bits 16..23  percent  Caching: of that planet. Done: the read rate in tenths of a MiB/s, 0 when
///                       everything was cached already
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

    /// <summary>
    /// How fast a done run read, in MiB/s, or null: not done, or nothing needed reading because the
    /// cache had both planets' sound already. At most 25.5.
    /// </summary>
    public double? MiBPerSecond => State == CutsceneCacheState.Done && Percent != 0 ? Percent / 10.0 : null;

    /// <summary>A done run that read nothing: the cache had both planets' sound already.</summary>
    public bool AlreadyCached => State == CutsceneCacheState.Done && Percent == 0;

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
