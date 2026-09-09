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

    // 5.6 Unlocks and level flags
    UnlockList = 0x0050,
    UnlockSet = 0x0051,
    LevelFlagsGet = 0x0053,
    LevelFlagsReset = 0x0054,
    LevelFlagsSet = 0x0055,

    // 5.7 Mods
    ModList = 0x0060,
    ModLoad = 0x0061,
    ModUnload = 0x0062,
    ModSetAuto = 0x0063,
    ModRescan = 0x0064,
    ModInfo = 0x0065,

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

    // 5.9 Combos
    ComboSet = 0x0080,
    ComboList = 0x0081,

    // 5.10 Config
    ConfigReload = 0x0090,
    ConfigSave = 0x0091,

    // 5.11 Autosplitting (revision 1.5)
    AutosplitEvents = 0x00A0,
    AutosplitDescribe = 0x00A1,
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

public enum SessionState : byte
{
    Xmb = 0,
    Booting = 1,
    Ingame = 2,
    Quitting = 3,
}

public enum GameId : byte
{
    None = 0,
    Rac1 = 1,
    Rac2 = 2,
    Rac3 = 3,
    Rac4 = 4,
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

    /// <summary>This ACTION makes the game write its save to <c>USRDIR/tempsave</c>.</summary>
    SaveAside = 1 << 2,

    /// <summary>This ACTION makes the game load <c>USRDIR/tempsave</c>.</summary>
    LoadAside = 1 << 3,

    /// <summary>
    /// This TOGGLE is a plain game-memory byte that qwark polls, so its bit in
    /// <c>toggle_state</c> follows the game rather than what the client last sent. There is
    /// nothing to auto-apply on boot and FEATURE_SET_AUTO is refused for it (revision 1.3).
    /// </summary>
    Live = 1 << 4,
}

[Flags]
public enum ModFlags : byte
{
    None = 0,
    Loaded = 1 << 0,
    Auto = 1 << 1,
    NeedsLua = 1 << 2,
    Previous = 1 << 3,
    ParseError = 1 << 4,
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
