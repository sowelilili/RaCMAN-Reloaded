namespace RaCMAN.Protocol;

/// <summary>Request opcodes, section 5 of PROTOCOL.md.</summary>
public enum Opcode : ushort
{
    // 5.1 Session
    Hello = 0x0001,
    Heartbeat = 0x0002,
    Notify = 0x0003,
    PreviousList = 0x0004,
    PreviousReapply = 0x0005,
    PreviousDismiss = 0x0006,

    /// <summary>
    /// Revision 1.22 (build 54): how much memory the VSH has free, as qwark last read it, and the
    /// lowest it has read since the module started. No payload. A control op, so it is answered in
    /// every session state, BOOTING and QUITTING included. UNSUPPORTED where the platform cannot
    /// read the figure, and UNKNOWN_OP from a module before it, which is why the client asks only
    /// while <see cref="SessionFlags.VshMemory"/> is set.
    /// </summary>
    VshMemory = 0x0007,

    /// <summary>
    /// Revision 1.26 (build 57): the running game's module and the memory gate in front of it, 40
    /// bytes (<see cref="GameModuleInfo"/>). No payload. A control op, answered in every session
    /// state. A test build whose games are modules (qwark's PROTOCOL.md 9.11) is the only module
    /// worth asking: a release answers state LINKED, and a module from before the op UNKNOWN_OP,
    /// which is why the client asks only while <see cref="SessionFlags2.GameModuleBad"/> or
    /// <see cref="SessionFlags2.GameWaiting"/> is set.
    /// </summary>
    GameModule = 0x0008,

    // 5.2 Telemetry
    Subscribe = 0x0010,
    Unsubscribe = 0x0011,
    GetState = 0x0012,

    // 5.3 Features
    Describe = 0x0020,
    FeatureSet = 0x0021,
    FeatureTrigger = 0x0022,
    FeatureSetAuto = 0x0023,
    FeatureOptions = 0x0024,

    /// <summary>
    /// Revision 1.17: the running game's code switches as address and value words, for a platform
    /// that cannot patch code. Each instruction a WRITES_CODE feature patches becomes a branch to a
    /// trampoline that reads a flag byte of that feature, so with the switches in the game those
    /// features are toggled by writing data. The reply is laid out exactly as SAVEFILE_PATCH's,
    /// and the client puts it into the same RPCS3 patch file.
    /// </summary>
    SwitchPatch = 0x0025,

    // 5.4 Memory
    MemRead = 0x0030,
    MemWrite = 0x0031,
    WatchAdd = 0x0032,
    WatchRemove = 0x0033,
    WatchList = 0x0034,
    FreezeAdd = 0x0035,
    FreezeRemove = 0x0036,
    FreezeList = 0x0037,
    PatchApply = 0x0038,
    PatchRevert = 0x0039,
    PatchList = 0x003A,
    ClearClient = 0x003B,

    // 5.5 Positions and planets
    PosSelect = 0x0040,
    PosSave = 0x0041,
    PosLoad = 0x0042,
    PosList = 0x0043,
    PosClear = 0x0044,
    PlanetList = 0x0045,
    PlanetSelect = 0x0046,
    PlanetLoad = 0x0047,
    Die = 0x0048,
    MobyTable = 0x0049,
    PosEdit = 0x004A,    // revision 1.13
    PosStore = 0x004B,   // revision 1.13

    // 5.6 Unlocks and level flags
    UnlockList = 0x0050,
    UnlockSet = 0x0051,
    LevelFlagsGet = 0x0053,
    LevelFlagsReset = 0x0054,
    LevelFlagsSet = 0x0055,
    LevelFlagsBit = 0x0056,     // revision 1.28
    LevelFlagsNames = 0x0057,   // revision 1.28

    // 5.7 Mods
    ModList = 0x0060,
    ModLoad = 0x0061,
    ModUnload = 0x0062,
    ModSetAuto = 0x0063,
    ModRescan = 0x0064,
    ModInfo = 0x0065,

    /// <summary>
    /// Revision 1.16: one mod as address and value words, for a platform that cannot write it into
    /// the game itself. The reply is laid out exactly as SAVEFILE_PATCH's, and the client puts it
    /// into an RPCS3 patch the same way.
    /// </summary>
    ModPatch = 0x0066,

    /// <summary>
    /// Revision 1.24: the mods on the console's USB drives, available rather than installed, each
    /// with how it stands against the console's own copy. Asked only while
    /// <see cref="SessionFlags2.UsbMods"/> is set.
    /// </summary>
    ModUsbList = 0x0067,

    // 5.8 Files
    FileOpen = 0x0070,
    FileWrite = 0x0071,
    FileRead = 0x0072,
    FileClose = 0x0073,
    FileDelete = 0x0074,
    DirList = 0x0075,
    DirCreate = 0x0076,
    DirDelete = 0x0077,
    UserId = 0x0078,

    /// <summary>
    /// Revision 1.10. It sits at the end of the file block rather than at 0x0075, where a rename
    /// would naturally have gone: that number has been DIR_LIST since revision 1, and an opcode is
    /// never renumbered.
    /// </summary>
    FileRename = 0x0079,

    // 5.9 Combos
    ComboSet = 0x0080,
    ComboList = 0x0081,
    ComboSuspend = 0x0082,   // revision 1.8
    ComboEnable = 0x0083,    // revision 1.12

    // 5.10 Config
    ConfigReload = 0x0090,
    ConfigSave = 0x0091,

    /// <summary>
    /// Revision 1.23 (qwark build 55): <c>u8 on</c>, whether the console menu's Trainer row takes
    /// the place of the game's Help row in its pause menu (config.txt's <c>menu.help</c>, 1 out
    /// of the box). A config write, answered in every session state; the console swaps the row's
    /// words at the next moment the pause menu cannot be reading them.
    /// <see cref="SessionFlags.MenuHelpOff"/> is where the setting comes back. UNSUPPORTED under
    /// RPCS3, which has no console menu, and UNKNOWN_OP from a module before it.
    /// </summary>
    MenuHelp = 0x0092,

    /// <summary>
    /// Revision 1.24 (qwark build 56): no payload; 64 bytes back, <see cref="Protocol.ProfileInfo"/>.
    /// Who is signed in on the console and whose settings are in use. Answered in every session
    /// state; with <see cref="ProfileFlags.Supported"/> clear (RPCS3) it answers OK with zeros.
    /// The client sends PROFILE_INFO, PROFILE_ENABLE and COMBO_OVERRIDE only while SessionInfo
    /// byte 29 bit1 (<see cref="ProfileFlags.Supported"/>) is set: a module before the revision
    /// answers them UNKNOWN_OP.
    /// </summary>
    ProfileInfo = 0x0093,

    /// <summary>
    /// Revision 1.24: <c>u8 on, u8 setup</c>. Writes the console-wide <c>profiles.enabled</c>
    /// and, with <see cref="ProfileSetup.Copy"/> or <see cref="ProfileSetup.Fresh"/>, sets up the
    /// signed-in user's profile if it has never been set up. No reply. BAD_ARG for on &gt; 1 or
    /// setup &gt; 2, UNSUPPORTED where byte 29 bit1 is clear.
    /// </summary>
    ProfileEnable = 0x0094,

    /// <summary>
    /// Revision 1.24: <c>u8 action, u8 set, u8 pad[2], u32 mask</c>. <c>set</c> 1 stores the mask
    /// (0 allowed: the combo is off for this user) as the active profile's override of that one
    /// combo; 0 removes the override, so the shared mask is in force again (removing one that is
    /// not there is OK). No reply. BAD_ARG for an action past the last combo or set &gt; 1,
    /// UNSUPPORTED where byte 29 bit1 is clear, NOT_FOUND when no profile is active. COMBO_LIST flags
    /// an overridden row with <see cref="ComboEntryFlags.Overridden"/>.
    /// </summary>
    ComboOverride = 0x0095,

    // 5.11 Autosplitting (revision 1.5)
    AutosplitEvents = 0x00A0,
    AutosplitDescribe = 0x00A1,

    // 5.12 Save files (revision 1.9)
    SaveFileInfo = 0x00B0,
    SaveFileRead = 0x00B1,
    SaveFileWrite = 0x00B2,

    // 5.13 The savefile library on the console (revision 1.10)
    SaveFileCategories = 0x00B3,
    SaveFileList = 0x00B4,
    SaveFileStore = 0x00B5,
    SaveFileRestore = 0x00B6,
    SaveFileCategory = 0x00B7,

    /// <summary>
    /// Revision 1.15: the savefile helper as address and value words, for a platform that cannot
    /// write it into the game itself. RPCS3 applies patch files when it loads the executable, before
    /// it recompiles anything, so the client puts these words into an RPCS3 patch.
    /// </summary>
    SaveFilePatch = 0x00B8,

    // 5.14 Manip categories (revision 1.21, build 53). All four answer UNSUPPORTED for a game with
    // no groups and outside a game, and NOT_INGAME where they need the game's state.

    /// <summary>
    /// The game's category types, the selection, the two options, the last event and the headings
    /// the manips area draws with the features listed under each.
    /// </summary>
    ManipList = 0x00D0,

    /// <summary><c>u8 group</c>, 0 for None. BAD_ARG for an id the game does not have.</summary>
    ManipSelect = 0x00D1,

    /// <summary><c>u8 auto_reset, u8 remember</c>. qwark keeps both in its config.</summary>
    ManipOptions = 0x00D2,

    /// <summary>
    /// Applies the selected group's setup and per-run writes now, behind the challenge-mode check.
    /// A refusal is BUSY, and the reason is the event MANIP_LIST reports next.
    /// </summary>
    ManipApply = 0x00D3,

    // 5.15 Lua (revision 1.27): Tools of Destruction runs Lua 5.0 scripts, and qwark carries a small
    // helper that runs a chunk of source in the game's own lua_State between two gameplay frames.

    /// <summary>
    /// <c>u16 len, u16 flags (0), char src[len]</c>, len 1..8192. Answered once the game has run the
    /// chunk or qwark gave up on it, at most about 1.6 s later; the reply is <see cref="LuaExecResult"/>.
    /// On a console the first one of a boot writes the helper into the game, so only a user's
    /// action ever sends it.
    /// </summary>
    LuaExec = 0x00E0,

    /// <summary>No request. Where the helper stands and the game's last script error: <see cref="LuaStatusInfo"/>.</summary>
    LuaStatus = 0x00E1,

    /// <summary>
    /// The Lua helper as words for an RPCS3 patch (emulator builds only; the PS3 module answers
    /// UNSUPPORTED). Laid out exactly as SAVEFILE_PATCH's reply, with no single bytes.
    /// </summary>
    LuaPatch = 0x00E2,
}

/// <summary>
/// LUA_EXEC's <c>lua_status</c> (revision 1.27): 0..5 are Lua 5.0's own results for the chunk, and
/// the 0x1xx and 0x2xx values are qwark's, for a chunk that never ran or never finished.
/// </summary>
public enum LuaExecStatus
{
    Ok = 0,
    RuntimeError = 1,
    FileError = 2,
    SyntaxError = 3,
    OutOfMemory = 4,
    ErrorInErrorHandler = 5,
    NoState = 0x100,
    BadLength = 0x101,
    Cancelled = 0x102,

    /// <summary>Frames ran and the game did not answer; the request was withdrawn and will not run.</summary>
    Timeout = 0x200,

    /// <summary>No gameplay frame ran (paused, a menu, a load); the request was withdrawn.</summary>
    NotRunning = 0x201,

    /// <summary>The game hung inside the chunk.</summary>
    Hung = 0x202,
}

/// <summary>Lua 5.0's <c>lua_type</c> of LUA_EXEC's first result, with 0xFF for none.</summary>
public enum LuaType : byte
{
    Nil = 0,
    Boolean = 1,
    LightUserdata = 2,
    Number = 3,
    String = 4,
    Table = 5,
    Function = 6,
    Userdata = 7,
    Thread = 8,
    None = 0xFF,
}

/// <summary>LUA_EXEC's <c>out_flags</c>.</summary>
[Flags]
public enum LuaOutFlags : byte
{
    None = 0,

    /// <summary>The text was cut to fit the reply.</summary>
    Truncated = 1 << 0,

    /// <summary>The first result is a boolean, and it is true.</summary>
    True = 1 << 1,
}

/// <summary>LUA_STATUS's <c>installed</c>.</summary>
public enum LuaInstalled : byte
{
    No = 0,
    Yes = 1,

    /// <summary>qwark is still looking for the helper in the game (RPCS3).</summary>
    Checking = 2,
}

/// <summary>LUA_STATUS's <c>phase</c>: what the helper is doing with a request.</summary>
public enum LuaPhase : byte
{
    Idle = 0,
    Waiting = 1,
    Withdrawing = 2,
    Hung = 3,
}

public static class LuaExtensions
{
    /// <summary>The name to show a human for a LUA_EXEC status, with its number for one not known here.</summary>
    public static string DisplayName(this LuaExecStatus status) => status switch
    {
        LuaExecStatus.Ok => "OK",
        LuaExecStatus.RuntimeError => "runtime error",
        LuaExecStatus.FileError => "file error",
        LuaExecStatus.SyntaxError => "syntax error",
        LuaExecStatus.OutOfMemory => "out of memory",
        LuaExecStatus.ErrorInErrorHandler => "error in error handler",
        LuaExecStatus.NoState => "NO_STATE",
        LuaExecStatus.BadLength => "BAD_LEN",
        LuaExecStatus.Cancelled => "CANCELLED",
        LuaExecStatus.Timeout => "TIMEOUT",
        LuaExecStatus.NotRunning => "NOT_RUNNING",
        LuaExecStatus.Hung => "HUNG",
        _ => $"status 0x{(int)status:X}",
    };

    /// <summary>Lua's own name for a type ("nil", "number", "string"), and "none" for no result.</summary>
    public static string DisplayName(this LuaType type) => type switch
    {
        LuaType.Nil => "nil",
        LuaType.Boolean => "boolean",
        LuaType.LightUserdata => "lightuserdata",
        LuaType.Number => "number",
        LuaType.String => "string",
        LuaType.Table => "table",
        LuaType.Function => "function",
        LuaType.Userdata => "userdata",
        LuaType.Thread => "thread",
        LuaType.None => "none",
        _ => $"type {(byte)type}",
    };
}

/// <summary>
/// MANIP_LIST group flags, section 5.14 of PROTOCOL.md (revision 1.21). The client only reads
/// them to draw the dropdown and the auto-reset warning; what each group writes is qwark's alone.
/// </summary>
[Flags]
public enum ManipGroupFlags : byte
{
    None = 0,

    /// <summary>bit0: a challenge-mode (NG+) group. Clear means a new-game, any%-style group.</summary>
    NgPlus = 1 << 0,

    /// <summary>bit1: a category extension, drawn with less emphasis.</summary>
    Extension = 1 << 1,

    /// <summary>bit2: the group has writes that have to be made again on every file load.</summary>
    PerRun = 1 << 2,

    /// <summary>bit3: the group skips the challenge-mode check (RaC2's NG+ No Specials).</summary>
    NoChallengeCheck = 1 << 3,
}

/// <summary>
/// What MANIP_LIST's last event says happened, section 5.14. <c>event_seq</c> moves on with every
/// new one, which is how the client shows each of them once.
/// </summary>
public enum ManipEventKind : byte
{
    None = 0,

    /// <summary>
    /// The group's first application this boot, whatever it wrote: its setup, or its per-run
    /// writes alone for a group that has no setup (build 53).
    /// </summary>
    Activated = 1,

    /// <summary>
    /// A later application of the group: its per-run writes at a run's start, or its setup put in
    /// again after the group was announced this boot (Apply now, or the group picked again). The
    /// name is the first of the two; the event says only that the group's manips went in.
    /// </summary>
    PerRunApplied = 2,

    /// <summary>Refused: an NG+ group, and the file is not a challenge-mode file.</summary>
    RefusedNotChallengeFile = 3,

    /// <summary>Refused: a new-game group, and the file is a challenge-mode file.</summary>
    RefusedChallengeFile = 4,

    /// <summary>Refused: Apply now with no file loaded (the title screen or the menus).</summary>
    RefusedNoFile = 5,
}

/// <summary>
/// SAVEFILE_CATEGORY's <c>op</c>, section 5.13. A delete removes the folder and any orphaned
/// <c>.sum</c> sidecars in it, and is refused while a save is still there.
/// </summary>
public enum SaveFileCategoryOp : byte
{
    Create = 0,
    Delete = 1,
}

/// <summary>
/// SAVEFILE_INFO's <c>error</c>, section 5.13: why the console's last copy between a file and the
/// aside buffer stopped. It survives until the next STORE or RESTORE, so a client that polls once
/// more after the transfer bit clears still learns how it ended.
/// </summary>
public enum SaveFileError : byte
{
    None = 0,

    /// <summary>A RESTORE named a file the console does not have.</summary>
    FileMissing = 1,

    /// <summary>A read, a write or an open failed on the console.</summary>
    IoError = 2,

    /// <summary>The file is not exactly the size of the aside buffer, so no game could take it.</summary>
    ShortFile = 3,

    /// <summary>The game went away underneath the copy, taking the helper with it.</summary>
    HelperMissing = 4,

    /// <summary>The aside buffer was already spoken for by another transfer or request.</summary>
    BufferBusy = 5,

    /// <summary>
    /// The game was not somewhere a save can be taken or put back (build 43). Deadlocked since
    /// build 43: anything but ordinary play or its pause, or a mode change queued (a menu, a lobby,
    /// a mission starting). RaC1, RaC2 and UYA since build 53, for a load only: the helper pauses
    /// the game from almost anywhere, so what is left is a planet load under way, a RaC1 movie,
    /// RaC2's title screen, space, debug mode, multiplayer or a lobby, and the helper's ten-second
    /// wait for the pause running out. The console refuses before touching the game or a file, or
    /// the helper drops the request on the frame it would act.
    /// </summary>
    NotIdle = 6,

    /// <summary>
    /// There was no save to act on (build 53, revision 1.20). A set-aside found the game holding no
    /// save yet: RaC1, RaC2 and UYA have none until they have saved or loaded once since the game
    /// started, which on a new game is the first autosave. A load found nothing set aside that is
    /// a save of this game and no save of the game's own to fill it from, and a RESTORE found that
    /// the file it copied in is not a save of the running game, so it loaded nothing.
    /// </summary>
    NoSave = 7,

    /// <summary>
    /// The console could not put the savefile helper into the game because its
    /// <c>/dev_hdd0/qwark/ui.pak</c>, which carries the helper since build 56, is missing or from
    /// another build (SessionInfo <see cref="SessionFlags.UiPakBad"/> says the same). Nothing was
    /// raised at the game. Sending the pak from the Connection panel fixes it in the same session.
    /// </summary>
    NoUiPak = 8,
}

/// <summary>
/// Which of the savefile helper's requests an error came back from. Most errors mean the same thing
/// for all three; "no save" (<see cref="SaveFileError.NoSave"/>) does not.
/// </summary>
public enum SaveFileRequest
{
    /// <summary>Taking a save: the set-aside action, or the set-aside a SAVEFILE_STORE raises.</summary>
    Store,

    /// <summary>The load action: putting back what is set aside.</summary>
    Load,

    /// <summary>SAVEFILE_RESTORE: loading a file of the console's library.</summary>
    Restore,
}

public static class SaveFileErrorExtensions
{
    /// <summary>
    /// What to put in a toast. Plain words: the user did not ask about aside buffers.
    /// <paramref name="request"/> is what was asked for, which only "no save" needs.
    /// </summary>
    public static string Describe(this SaveFileError error, SaveFileRequest request = SaveFileRequest.Store) => error switch
    {
        SaveFileError.None => "no error",
        SaveFileError.FileMissing => "your console no longer has that file",
        SaveFileError.IoError => "your console could not read or write the file",
        SaveFileError.ShortFile => "the file is not the size this game's save has to be",
        SaveFileError.HelperMissing => "the game stopped while your console was copying",
        SaveFileError.BufferBusy => "your console was already busy with another save",
        // Not "leave the menu": the RaC1-3 helper pauses the game from almost anywhere since build
        // 53, so what is left is a load under way, space, a screen that cannot pause and
        // multiplayer. A set-aside is refused only by Deadlocked, for the same kinds of moment.
        SaveFileError.NotIdle => request == SaveFileRequest.Store
            ? "the game can't set a save aside right now (a load, a cutscene or a mode change is under way, "
              + "or it's in multiplayer). Try again in a moment"
            : "the game can't load a save right now (a planet load is under way, or it's in space, "
              + "a menu that can't pause, or multiplayer). Try again in a moment",
        SaveFileError.NoSave => request switch
        {
            SaveFileRequest.Restore => "that file is not a save of this game, so nothing was loaded",
            SaveFileRequest.Load =>
                "there is no save to load yet: wait for an autosave (or use Force autosave in RaC1), or set a save aside first",
            _ => "the game has no save yet: wait for an autosave (or use Force autosave in RaC1), then try again",
        },
        SaveFileError.NoUiPak =>
            "your console's ui.pak is missing or from another build, and save files need it. "
            + "Send it from the Connection panel, then try again",
        _ => $"error {(byte)error}",
    };

    /// <summary>
    /// The errors a BUSY answer to a set-aside, a load, a STORE or a RESTORE carries: why the
    /// console refused it outright rather than why a copy stopped.
    /// </summary>
    public static bool IsRefusal(this SaveFileError error) =>
        error is SaveFileError.BufferBusy or SaveFileError.NotIdle or SaveFileError.NoSave or SaveFileError.NoUiPak;
}

/// <summary>
/// What an <see cref="AutosplitEvent"/> says happened, section 5.11 of PROTOCOL.md. qwark reports
/// the event; which of them become LiveSplit commands is entirely the client's decision.
/// <para>
/// Revision 1.5 added the two load kinds. A load and a pause are the same shape — something began
/// and later ended — and both are timing, never a split.
/// </para>
/// </summary>
public enum AutosplitKind : byte
{
    None = 0,
    Start = 1,
    Split = 2,
    Reset = 3,
    Pause = 4,
    Resume = 5,
    LoadStart = 6,
    LoadEnd = 7,
}

/// <summary>
/// AutosplitEventDesc.flags. Bit0 is the state the client's checkbox takes when the settings file
/// has nothing to say about the label; bit1 marks the one code the planet route applies to; bits 2
/// and 3 mark the two game-time corrections of revision 1.5, which the client applies whether or
/// not any checkbox is ticked.
/// </summary>
[Flags]
public enum AutosplitEventFlags : byte
{
    None = 0,

    /// <summary>The user's checkbox for this code starts ticked.</summary>
    EnabledByDefault = 1 << 0,

    /// <summary>
    /// This code carries a planet index in <c>arg</c>, so the "split by planet route" setting
    /// applies to it. Only ever set on code 1.
    /// </summary>
    PlanetRoute = 1 << 1,

    /// <summary>
    /// A fixed correction: the row's <c>param_us</c> comes off game time every time the event
    /// happens. The old scripts' constant subtractions — RaC2's three loading screens and its
    /// seven frames before the Protopet cutscene, RaC3's one second of long load.
    /// </summary>
    Flat = 1 << 2,

    /// <summary>
    /// A normalised pair: the row is a LOAD_START (or PAUSE), its LOAD_END (or RESUME) carries the
    /// same code, and everything the pair lasted beyond <c>param_us</c> comes off game time. The
    /// old scripts' <c>isLoading</c> blocks — RaC1's 7.56 s load timer, Deadlocked's 14.8 s quit.
    /// </summary>
    Normalise = 1 << 3,
}

/// <summary>Reply status codes, section 2 of PROTOCOL.md.</summary>
public enum Status : ushort
{
    Ok = 0,
    NotIngame = 1,
    Unsupported = 2,
    BadArg = 3,
    IoError = 4,
    Full = 5,
    UnknownOp = 6,
    NotFound = 7,
    Busy = 8,
}

public static class StatusExtensions
{
    /// <summary>The status as PROTOCOL.md spells it: NOT_INGAME, UNSUPPORTED, BUSY, and the number of one it does not name.</summary>
    public static string WireName(this Status status) => status switch
    {
        Status.Ok => "OK",
        Status.NotIngame => "NOT_INGAME",
        Status.Unsupported => "UNSUPPORTED",
        Status.BadArg => "BAD_ARG",
        Status.IoError => "IO_ERROR",
        Status.Full => "FULL",
        Status.UnknownOp => "UNKNOWN_OP",
        Status.NotFound => "NOT_FOUND",
        Status.Busy => "BUSY",
        _ => $"status {(ushort)status}",
    };
}

public enum SessionState : byte
{
    Xmb = 0,
    Booting = 1,
    Ingame = 2,
    Quitting = 3,
}

public static class SessionStateExtensions
{
    /// <summary>
    /// The name to show a human: INGAME, XMB, BOOTING, QUITTING. The protocol and the panels have
    /// always spelled these in upper case ("needs INGAME"), so the status line spells them the same
    /// way rather than showing the C# "Ingame" beside a sentence about INGAME.
    /// </summary>
    public static string DisplayName(this SessionState state) => state switch
    {
        SessionState.Xmb => "XMB",
        SessionState.Booting => "BOOTING",
        SessionState.Ingame => "INGAME",
        SessionState.Quitting => "QUITTING",
        _ => state.ToString().ToUpperInvariant(),
    };
}

public enum GameId : byte
{
    None = 0,
    Rac1 = 1,
    Rac2 = 2,
    Rac3 = 3,
    Rac4 = 4,

    /// <summary>Tools of Destruction (revision 1.27), NPEA00452.</summary>
    Tod = 5,
}

public static class GameIdExtensions
{
    /// <summary>The name to show a human: "RaC1", "Deadlocked", etc., not the C# "Rac1".</summary>
    public static string DisplayName(this GameId game) => game switch
    {
        GameId.Rac1 => "RaC1",
        GameId.Rac2 => "RaC2",
        GameId.Rac3 => "RaC3",
        GameId.Rac4 => "Deadlocked",
        GameId.Tod => "ToD",
        _ => "no game",
    };
}

public enum FeatureKind : byte
{
    Toggle = 0,
    Action = 1,
    Value = 2,
    Enum = 3,
    Color = 4,
}

public enum ComboAction : byte
{
    SavePosition = 0,
    LoadPosition = 1,
    Die = 2,
    LoadPlanet = 3,
    LoadSetAsideFile = 4,

    /// <summary>
    /// Revision 1.22 (qwark build 54): opens the console menu on the TV, or closes it. The one
    /// combo the console gives a mask out of the box, <see cref="ComboDefaults.OpenConsoleMenu"/>.
    /// Build 54 did not let the combo switch hold it off; since build 55 (revision 1.23) the switch
    /// holds it like every other combo. COMBO_LIST reports it as a sixth entry.
    /// </summary>
    OpenConsoleMenu = 5,

    /// <summary>
    /// Revision 1.23 (qwark build 55): turns the running game's fast loads on or off, exactly as
    /// the Fast loads box does, and the console says which. No mask out of the box. Under RPCS3 it
    /// is refused like the box until qwark's patches are in.
    /// </summary>
    ToggleFastLoads = 6,
}

/// <summary>The masks a combo has on a console that was never told otherwise.</summary>
public static class ComboDefaults
{
    /// <summary>L1 + R1 + L2 + R2 + L3 + R3, qwark's COMBO_MENU_DEFAULT.</summary>
    public const uint OpenConsoleMenu = 0x060F;
}

/// <summary>
/// How an unlock value slot is edited, from the UNLOCK_LIST descriptors of revision 1.3. Before
/// that the four slots were fixed as Owned/Gold/Level/Ammo, which only RaC1 laid out that way:
/// RaC3 keeps a weapon's version in slot 1 and its XP in slot 2, so a client that assumed the old
/// names drew a checkbox over a number.
/// </summary>
public enum UnlockFieldKind : byte
{
    /// <summary>A checkbox; only 0 and 1 are ever written.</summary>
    Flag = 0,

    /// <summary>A number box, clamped to the descriptor's max when it names one.</summary>
    Number = 1,
}

public enum PatchKind : byte
{
    Client = 0,
    Feature = 1,
    Mod = 2,
}

public enum FileMode : byte
{
    Read = 0,
    WriteTruncate = 1,
}

/// <summary>
/// SessionInfo.flags, section 3 of PROTOCOL.md. EMULATOR and NO_CODE_PATCHES arrived with the
/// RPCS3 build of qwark: the module says up front what the platform it runs on cannot do, so the
/// client can grey a control out rather than let the user press it and read UNSUPPORTED.
/// </summary>
[Flags]
public enum SessionFlags : byte
{
    None = 0,
    PreviousPending = 1 << 0,

    /// <summary>qwark is running against an emulator rather than on a console.</summary>
    Emulator = 1 << 1,

    /// <summary>
    /// Code patches are refused on this platform: qwark answers UNSUPPORTED to FEATURE_SET on a
    /// WRITES_CODE feature, to MOD_LOAD of a mod that carries patch words or code caves, and to
    /// PATCH_ADD. Everything that only reads and writes data still works.
    /// </summary>
    NoCodePatches = 1 << 2,

    /// <summary>
    /// The console's combo switch is off (revision 1.12): every combo is held off until a
    /// COMBO_ENABLE with 1 turns them back on. The switch lives in the console's config, so it
    /// survives a client restart and a reboot, and this is the only place the client reads it
    /// from. The capture-time hold of COMBO_SUSPEND is a different thing and does not show here.
    /// </summary>
    CombosOff = 1 << 3,

    /// <summary>
    /// The running game's code switches are in memory (revision 1.17): the RPCS3 patch SWITCH_PATCH
    /// describes was applied when the game booted, and qwark-rpcs3 has found it there, byte for byte.
    /// It looks once per session. With this set every WRITES_CODE feature works under
    /// <see cref="NoCodePatches"/> exactly as any other one does. Never set on a console, where qwark
    /// patches the instructions itself.
    /// </summary>
    CodeSwitches = 1 << 4,

    /// <summary>
    /// The module can read the VSH's free memory (revision 1.22): its first reading went through,
    /// so VSH_MEMORY answers with figures. The PS3 module sets it from module start; qwark-rpcs3
    /// never does, and neither does a module from before the op, which would answer UNKNOWN_OP.
    /// It is the only thing the client asks VSH_MEMORY on.
    /// </summary>
    VshMemory = 1 << 5,

    /// <summary>
    /// The console's <c>menu.help</c> is 0 (revision 1.23): its menu leaves the game's Help row
    /// alone, so Trainer is reached through the menu combo only. Clear on every earlier module,
    /// whose menu always took the row, so it reads right against those too.
    /// </summary>
    MenuHelpOff = 1 << 6,

    /// <summary>
    /// The console's <c>/dev_hdd0/qwark/ui.pak</c> is missing, damaged or not the one built with
    /// the running module (revision 1.23, build 55), so the console menu cannot go into a game.
    /// The module checks it at start and again just after a file op on that path. Never set by
    /// qwark-rpcs3, and never by a module before build 55, where the bit was always 0.
    /// </summary>
    UiPakBad = 1 << 7,
}

/// <summary>SessionInfo byte 29, <c>flags2</c> (revision 1.24): a pad byte, always 0, before it.</summary>
[Flags]
public enum SessionFlags2 : byte
{
    None = 0,

    /// <summary>
    /// The module reads mods off the console's USB drives and answers MOD_USB_LIST (revision 1.24,
    /// build 56). Never set by qwark-rpcs3, where a mod is an RPCS3 patch, nor by an older module.
    /// </summary>
    UsbMods = 1 << 0,

    /// <summary>
    /// bit4 DEV (revision 1.24, test builds): the module is a test build, told from another build of
    /// its number by the build ID after HELLO's SessionInfo (<see cref="SessionInfo.BuildId"/>). A
    /// release module never sets it, and neither does qwark-rpcs3. PROTOCOL.md 3.1.1.
    /// </summary>
    Dev = 1 << 4,

    /// <summary>
    /// bit5 GAME_MODULE_BAD (revision 1.26, a test build whose games are modules): the running
    /// game's module, <c>/dev_hdd0/qwark/rac1.sprx</c> .. <c>rac4.sprx</c>, is missing, is no module, would
    /// not load or start, or is another build's, so the session has no game features until a
    /// client writes the file or the game boots again. INGAME only, and <c>game</c> reads 0 while
    /// it is set. Never set by a module whose games are linked in (a release, qwark-rpcs3).
    /// </summary>
    GameModuleBad = 1 << 5,

    /// <summary>
    /// bit6 GAME_WAITING (revision 1.26): the running game's module is there and waits until the
    /// VSH has the memory for it; it loads by itself when it has. INGAME only, <c>game</c> 0 while
    /// it is set. Never set by a release or by qwark-rpcs3. PROTOCOL.md 9.11.
    /// </summary>
    GameWaiting = 1 << 6,

    /// <summary>
    /// bit7 NO_MENU (revision 1.28): the running game has no console menu (Tools of
    /// Destruction), so the menu's combo opens nothing and the In-game menus switch has no Help
    /// row to take. INGAME with the game's code in only; clear in every earlier build.
    /// </summary>
    NoMenu = 1 << 7,
}

/// <summary>GAME_MODULE's <c>state</c>, section 5.1.2 of PROTOCOL.md (revision 1.26).</summary>
public enum GameModuleState : byte
{
    /// <summary>No game: the XMB, BOOTING, a title no game claims.</summary>
    None = 0,

    /// <summary>The game is linked into the module (a release, qwark-rpcs3).</summary>
    Linked = 1,

    /// <summary>A module is wanted and its file not yet read (a tick at most).</summary>
    Checking = 2,

    /// <summary>The module is there and the memory gate holds it back (flags2 bit6).</summary>
    Waiting = 3,

    /// <summary>Loaded and started.</summary>
    Loaded = 4,

    /// <summary>No module file (<c>rac1.sprx</c> ..) on the console (flags2 bit5).</summary>
    Missing = 5,

    /// <summary>No module in the file, or lv2 would not load or start it (flags2 bit5).</summary>
    Failed = 6,

    /// <summary>The module is another build's (flags2 bit5).</summary>
    Other = 7,
}

/// <summary>GAME_MODULE's <c>flags</c>, section 5.1.2 of PROTOCOL.md (revision 1.26).</summary>
[Flags]
public enum GameModuleFlags : byte
{
    None = 0,

    /// <summary>config.txt's <c>game.keep</c> is 1: the module stays loaded over the XMB.</summary>
    Keep = 1 << 0,

    /// <summary>This wait's 30-second notification has gone to the TV.</summary>
    Notified = 1 << 1,

    /// <summary>This module's games are modules (0 for a release).</summary>
    Modules = 1 << 2,

    /// <summary>A module is loaded now, a kept one included.</summary>
    Resident = 1 << 3,
}

/// <summary>
/// The per-user settings bits of SessionInfo byte 29, <c>flags2</c> (revision 1.24, build 56),
/// and of the first byte of PROFILE_INFO, which adds <see cref="HasProfile"/>. Byte 29 bit0 is
/// USB_MODS, which is not a profile bit (<see cref="SessionInfo.ProfileBits"/>). Per-user
/// settings: with the console-wide switch on, each console user's position slots, auto-load and
/// auto-apply choices, manip and planet choices and <c>menu.help</c> come from that user's
/// profile; each combo stays shared unless the profile overrides it (COMBO_OVERRIDE).
/// </summary>
[Flags]
public enum ProfileFlags : byte
{
    None = 0,

    /// <summary>
    /// bit1: the module has per-user settings and the console has users to have them for. Set by
    /// the PS3 module from build 56 and by qwark-host; never by qwark-rpcs3 (RPCS3 has one user)
    /// or an earlier build. The client's capability bit: everything about profiles hides without it.
    /// </summary>
    Supported = 1 << 1,

    /// <summary>bit2: the console-wide switch, config.txt's <c>profiles.enabled</c>, is on.</summary>
    On = 1 << 2,

    /// <summary>bit3: per-user settings come from a console user's profile now (switch on, a user signed in).</summary>
    Active = 1 << 3,

    /// <summary>
    /// bit5, PROFILE_INFO only: the signed-in user's profile folder exists (it was set up before),
    /// whether or not the switch is on. Never set in SessionInfo.
    /// </summary>
    HasProfile = 1 << 5,
}

/// <summary>
/// COMBO_LIST's per-entry flags, the byte after each action that was pad before revision 1.24.
/// </summary>
[Flags]
public enum ComboEntryFlags : byte
{
    None = 0,

    /// <summary>bit0: the mask is the active profile's override of this combo, not the shared one.</summary>
    Overridden = 1 << 0,
}

/// <summary>PROFILE_INFO's <c>kind</c>: whose per-user settings are in use.</summary>
public enum ProfileKind : byte
{
    /// <summary>The console's shared settings (the switch is off, or nobody is signed in).</summary>
    Shared = 0,

    /// <summary>A console user's profile.</summary>
    User = 1,

    /// <summary>Reserved for named profiles, a later revision.</summary>
    Named = 2,
}

/// <summary>PROFILE_ENABLE's <c>setup</c>: how a profile that has never been set up starts.</summary>
public enum ProfileSetup : byte
{
    /// <summary>Set nothing up.</summary>
    None = 0,

    /// <summary>A copy of the shared per-user settings and position files.</summary>
    Copy = 1,

    /// <summary>Empty: every per-user setting at its default.</summary>
    Fresh = 2,
}

/// <summary>
/// VSH_MEMORY's <c>lowest_at</c>, section 5.1.1 of PROTOCOL.md (revision 1.22): which of qwark's
/// readings of the VSH's free memory the lowest one was. A value not listed here is a reading a
/// later module takes, and is shown by its number.
/// </summary>
public enum VshMemoryReading : byte
{
    /// <summary>No lowest reading yet.</summary>
    None = 0,

    /// <summary>At module start, before the tick thread was created.</summary>
    Start = 1,

    /// <summary>Just after the tick thread was created.</summary>
    TickThread = 2,

    /// <summary>A game reached INGAME.</summary>
    Game = 3,

    /// <summary>The console menu's renderer went into the game.</summary>
    Menu = 4,

    /// <summary>The reading qwark takes every ten seconds.</summary>
    Periodic = 5,
}

/// <summary>
/// Feature.flags, section 5.3 of PROTOCOL.md. SAVE_ASIDE and LOAD_ASIDE arrived with revision
/// 1.2: they mark the two ACTIONs that drive the savefile helper, so the client can find them
/// without matching labels.
/// </summary>
[Flags]
public enum FeatureFlags : byte
{
    None = 0,
    Auto = 1 << 0,
    WritesCode = 1 << 1,

    /// <summary>
    /// This ACTION asks the game to copy its current save into the savefile helper's aside
    /// buffer, which SAVEFILE_READ then streams (revision 1.9). Before that revision it wrote
    /// a <c>USRDIR/tempsave</c> file the client pulled over the FILE ops.
    /// </summary>
    SaveAside = 1 << 2,

    /// <summary>
    /// This ACTION asks the game to load whatever is in the aside buffer, which SAVEFILE_WRITE
    /// has just filled (revision 1.9).
    /// </summary>
    LoadAside = 1 << 3,

    /// <summary>
    /// This TOGGLE is a plain game-memory byte that qwark polls, so its bit in
    /// <c>toggle_state</c> follows the game rather than what the client last sent. There is
    /// nothing to auto-apply on boot and FEATURE_SET_AUTO is refused for it (revision 1.3).
    /// </summary>
    Live = 1 << 4,

    /// <summary>
    /// The field behind this VALUE is two's complement in <see cref="Feature.Bits"/> bits, so the
    /// readout mirroring it carries the raw field and the client sign-extends it before showing it.
    /// FEATURE_SET still takes a <c>u32</c>: the low <c>Bits</c> bits of the number the user typed
    /// (revision 1.7).
    /// </summary>
    Signed = 1 << 5,

    /// <summary>
    /// This COLOR is a shade qwark works out from the COLOR whose id is <see cref="Feature.Aux"/>,
    /// until the user overrides it. Its readout carries <see cref="Feature.ColorAuto"/> while it is
    /// worked out, and a FEATURE_SET of that bit makes it automatic again (revision 1.24).
    /// </summary>
    Shade = 1 << 6,

    /// <summary>
    /// This COLOR has a colour of the game's own to go back to. Its readout carries
    /// <see cref="Feature.ColorAuto"/> while it is the game's own, and a FEATURE_SET of that bit puts
    /// it back, with every shade that follows it automatic again (revision 1.24, PROTOCOL.md 5.3.4).
    /// </summary>
    Stock = 1 << 7,
}

/// <summary>
/// Mod.flags, section 5.7 of PROTOCOL.md. On a platform that refuses code patches (SessionInfo
/// NO_CODE_PATCHES) LOADED means something else since revision 1.16: qwark-rpcs3 has found the
/// mod's words and caves in game memory this session, put there by an RPCS3 patch, and CHECKING
/// says it has not looked yet.
/// </summary>
[Flags]
public enum ModFlags : byte
{
    None = 0,
    Loaded = 1 << 0,
    Auto = 1 << 1,
    NeedsLua = 1 << 2,
    Previous = 1 << 3,
    ParseError = 1 << 4,

    /// <summary>Revision 1.16, only where code cannot be patched: whether the mod is in the game is not known yet this session.</summary>
    Checking = 1 << 5,
}

[Flags]
public enum PlanetFlags : byte
{
    None = 0,
    ResetLevelFlags = 1 << 0,
    ResetSpecialBolts = 1 << 1,
}

[Flags]
public enum PreviousCategories : byte
{
    None = 0,
    Toggles = 1 << 0,
    Mods = 1 << 1,
    Freezes = 1 << 2,
    Patches = 1 << 3,
    All = Toggles | Mods | Freezes | Patches,
}

/// <summary>The OG pad-mask layout, section 6 of PROTOCOL.md. Shared by all four games.</summary>
[Flags]
public enum PadButton : uint
{
    None = 0,
    L2 = 0x1,
    R2 = 0x2,
    L1 = 0x4,
    R1 = 0x8,
    Triangle = 0x10,
    Circle = 0x20,
    Cross = 0x40,
    Square = 0x80,
    Select = 0x100,
    L3 = 0x200,
    R3 = 0x400,
    Start = 0x800,
    Up = 0x1000,
    Right = 0x2000,
    Down = 0x4000,
    Left = 0x8000,
}

public static class PadButtons
{
    /// <summary>Buttons in mask order, with the display names the old client used.</summary>
    public static readonly (PadButton Button, string Name)[] All =
    {
        (PadButton.L2, "L2"),
        (PadButton.R2, "R2"),
        (PadButton.L1, "L1"),
        (PadButton.R1, "R1"),
        (PadButton.Triangle, "Triangle"),
        (PadButton.Circle, "Circle"),
        (PadButton.Cross, "Cross"),
        (PadButton.Square, "Square"),
        (PadButton.Select, "Select"),
        (PadButton.L3, "L3"),
        (PadButton.R3, "R3"),
        (PadButton.Start, "Start"),
        (PadButton.Up, "Up"),
        (PadButton.Right, "Right"),
        (PadButton.Down, "Down"),
        (PadButton.Left, "Left"),
    };

    public static IEnumerable<string> DecodeNames(uint mask)
    {
        foreach (var (button, name) in All)
        {
            if ((mask & (uint)button) != 0) yield return name;
        }
    }

    public static string Describe(uint mask) =>
        mask == 0 ? "None" : string.Join(" + ", DecodeNames(mask));
}
