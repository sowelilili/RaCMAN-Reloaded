namespace RaCMAN.Protocol;

/// <summary>Where UYA's trophy reset is, by the state in its status word (PROTOCOL.md section 5.14).</summary>
public enum TrophyResetState : byte
{
    /// <summary>No reset this boot. A boot, a reboot of the same title included, comes back to it.</summary>
    None = 0,

    Running = 1,

    /// <summary>The game has its trophies back, and they can be won.</summary>
    Ready = 2,

    Failed = 3,
}

/// <summary>The step a running reset is at.</summary>
public enum TrophyResetStep : byte
{
    None = 0,

    /// <summary>The game is asked, and lets go of its trophies.</summary>
    Starting = 1,

    /// <summary>qwark deletes the console's trophy data for the game.</summary>
    Deleting = 2,

    /// <summary>The game registers its trophies again; the status word says which registration.</summary>
    Registering = 3,
}

/// <summary>Why a reset failed, every one a run whose trophies will not pop.</summary>
public enum TrophyResetFailure : byte
{
    None = 0,

    /// <summary>A trophy file would not go, so the trophies won before are still won.</summary>
    DeleteStopped = 1,

    /// <summary>Every registration failed: the game has no trophies to award.</summary>
    Unregistered = 2,

    /// <summary>The console had not answered the game in ten minutes.</summary>
    NoAnswer = 3,

    /// <summary>The auto reset's load into Veldin ended before the game took the request; nothing was done.</summary>
    LoadEnded = 4,

    /// <summary>The game's side refused the request.</summary>
    GameRefused = 5,

    /// <summary>The game never picked the request up.</summary>
    NotTaken = 6,

    /// <summary>qwark refused the reset before doing anything; the status it answered is in the word.</summary>
    Refused = 7,

    /// <summary>The game changed under the reset.</summary>
    Stopped = 8,
}

/// <summary>
/// UYA's trophy reset status, the word Reset trophies' row names as its status readout (revision
/// 1.21, PROTOCOL.md sections 5.3 and 5.14):
/// <code>
/// bits 28..31  state    TrophyResetState
/// bits 24..27  detail   Running: the TrophyResetStep. Failed: the TrophyResetFailure
/// bits 20..23  arg      Registering: the registrations the game has started, 0..3.
///                       Refused: the Status the reset was refused with
/// bits 8..19   seconds  Running: since the request went out. Ready, Failed: how long it ran
/// bits 0..7    seq      the resets that have ended since the module started, modulo 256
/// </code>
/// The client finds the word through the row, never by a readout index or name of its own, so a
/// module that names no status readout simply has none to show.
/// </summary>
public readonly record struct TrophyResetStatus(
    TrophyResetState State,
    byte Detail,
    byte Arg,
    ushort Seconds,
    byte Seq)
{
    /// <summary>The step a running reset is at, or None for every other state.</summary>
    public TrophyResetStep Step => State == TrophyResetState.Running ? (TrophyResetStep)Detail : TrophyResetStep.None;

    /// <summary>Why a failed reset failed, or None for every other state.</summary>
    public TrophyResetFailure Failure =>
        State == TrophyResetState.Failed ? (TrophyResetFailure)Detail : TrophyResetFailure.None;

    /// <summary>The registrations the game has started, while it registers; 0 otherwise.</summary>
    public int Registrations => Step == TrophyResetStep.Registering ? Arg : 0;

    /// <summary>The status a refused reset was answered with, or OK for anything else.</summary>
    public Status RefusedWith => Failure == TrophyResetFailure.Refused ? (Status)Arg : Status.Ok;

    public static TrophyResetStatus Parse(uint word) => new(
        (TrophyResetState)(word >> 28),
        (byte)((word >> 24) & 0xF),
        (byte)((word >> 20) & 0xF),
        (ushort)((word >> 8) & 0xFFF),
        (byte)word);

    /// <summary>The other direction, for the fake console the tests drive. Each field is cut to its width.</summary>
    public uint ToWord() =>
        ((uint)State & 0xF) << 28
        | ((uint)Detail & 0xF) << 24
        | ((uint)Arg & 0xF) << 20
        | ((uint)Seconds & 0xFFF) << 8
        | Seq;

    public static TrophyResetStatus Running(TrophyResetStep step, int registrations = 0, int seconds = 0, byte seq = 0) =>
        new(TrophyResetState.Running, (byte)step, (byte)registrations, (ushort)seconds, seq);

    public static TrophyResetStatus Ready(int seconds, byte seq = 0) =>
        new(TrophyResetState.Ready, 0, 0, (ushort)seconds, seq);

    public static TrophyResetStatus Failed(TrophyResetFailure failure, Status refusedWith = Status.Ok, int seconds = 0, byte seq = 0) =>
        new(TrophyResetState.Failed, (byte)failure, (byte)refusedWith, (ushort)seconds, seq);
}
