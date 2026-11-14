using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The console's own settings, the Settings page's Console tab's first group: In-game menus
/// (MENU_HELP), Cheat warnings (RUN_WARN), Apply toggles on game boot (BOOT_APPLY) and Per-user
/// settings (PROFILE_ENABLE). Each is drawn from the session (Apply toggles on game boot from its
/// own read, which no SessionInfo bit carries), so it always says what the console says, and each
/// pick sends one request. The rules live here, apart from the drawing, so they can be tested.
/// </summary>
public static class QwarkSettings
{
    /// <summary>The line at the top of the Console tab, the user's own words (2026-11-14).</summary>
    public const string ConsoleHeading =
        "Editing console settings. These are saved persistently on your console and apply whenever qwark is loaded.";

    /// <summary>
    /// The line under <see cref="ConsoleHeading"/> while per-user settings are on, or null: which of
    /// the rows are the signed-in user's and which stay the console's, so the heading's "your
    /// console" and the Per-user row's "Editing settings for ..." do not read as two answers to one
    /// question.
    /// </summary>
    public static string? PerUserNote(SessionInfo session, ProfileInfo profile)
    {
        if (!session.ProfilesOn) return null;
        const string rows = "In-game menus, Cheat warnings and Apply toggles on game boot";
        if (!session.ProfileActive) return $"Per-user settings are on: {rows} will be kept for each user once one signs in.";

        // Named as the Per-user settings row names them.
        string who = !profile.SignedIn ? "the signed-in user"
            : profile.Name.Length > 0 ? $"{profile.Name} (user {profile.UserId})"
            : $"user {profile.UserId}";
        return $"Per-user settings are on: {rows} are kept for {who}; the rest are shared by every user.";
    }
    /// <summary>
    /// The first qwark build with the In-game menus switch (MENU_HELP), revision 1.23, the same
    /// build as the fast-loads combo.
    /// </summary>
    public const byte InGameMenusBuild = CombosPanel.FastLoadsComboBuild;

    /// <summary>
    /// Whether the In-game menus box is drawn: only against a module that has MENU_HELP, which an
    /// older one answers UNKNOWN_OP.
    /// </summary>
    public static bool ShowsInGameMenus(byte qwarkBuild) => qwarkBuild >= InGameMenusBuild;

    /// <summary>
    /// The same, for the session: not in a game with no console menu (<c>flags2</c> bit7 NO_MENU),
    /// which has no Help row for the switch to give to the Trainer menu.
    /// </summary>
    public static bool ShowsInGameMenus(SessionInfo session) =>
        ShowsInGameMenus(session.QwarkVersion) && !session.NoConsoleMenu;

    /// <summary>
    /// Whether the In-game menus box is greyed: under RPCS3 there is no console menu, and qwark
    /// answers MENU_HELP UNSUPPORTED. Qwark revision 1.29: but Tools of Destruction's, which is the
    /// game's own pause menu and works under RPCS3 with the Lua helper's patch, so there the box
    /// is live.
    /// </summary>
    public static bool InGameMenusBlocked(SessionInfo session) =>
        (session.IsEmulator || session.CodePatchesUnsupported) && session.Game != GameId.Tod;

    /// <summary>What the In-game menus box says when the pointer rests on it.</summary>
    public static string InGameMenusTooltip(SessionInfo session) =>
        InGameMenusBlocked(session)
            ? "The console menu is not available under RPCS3."
            : "When enabled, shows the in-game Trainer menu in place of the Help menu. "
              + "When disabled, the Trainer menu is only accessible with its button combo.";

    /// <summary>
    /// What the In-game menus box says beside itself, from the session alone. In Tools of Destruction
    /// the Trainer takes the pause list's Inventory row (qwark revision 1.29), elsewhere Help's.
    /// </summary>
    public static string InGameMenusStatus(SessionInfo session) =>
        InGameMenusBlocked(session) ? "not under RPCS3"
        : session.MenuHelpOff ? "the game's default pause menu"
        : session.Game == GameId.Tod ? "Trainer replaces Inventory in the pause menu"
        : "Trainer replaces Help in the pause menu";

    /// <summary>
    /// Whether the Cheat warnings dropdown is drawn: SessionInfo <c>flags3</c> bit0 (qwark revision
    /// 1.30), the module's capability bit, and not the build number, so a test build of the same
    /// number without it shows nothing. RPCS3 has the warnings too: they go to qwark-rpcs3's log
    /// there. It was a hidden checkbox, Run warnings, from build 57 until the second Settings round.
    /// </summary>
    public static bool ShowsCheatWarnings(SessionInfo session) => session.HasRunWarnings;

    /// <summary>The dropdown's choices, in its order, and what each is called.</summary>
    public static readonly IReadOnlyList<(RunWarningMode Mode, string Label)> AllCheatWarningChoices = new[]
    {
        (RunWarningMode.ModsAndCheats, "Mods and cheats"),
        (RunWarningMode.CheatsOnly, "Cheats only"),
        (RunWarningMode.Off, "Off"),
    };

    /// <summary>
    /// The choices a module takes: all three from qwark revision 1.33 (flags3 bit5), and without
    /// Cheats only before it, which answers RUN_WARN 2 BAD_ARG.
    /// </summary>
    public static IReadOnlyList<(RunWarningMode Mode, string Label)> CheatWarningChoices(SessionInfo session) =>
        session.HasSettings133
            ? AllCheatWarningChoices
            : AllCheatWarningChoices.Where(c => c.Mode != RunWarningMode.CheatsOnly).ToArray();

    /// <summary>What the Cheat warnings dropdown says when the pointer rests on it.</summary>
    public const string CheatWarningsTooltip =
        "What your console warns about in a notification as a speedrun starts: cheats that are on, and "
        + "mods that are not allowed in runs or whose authors haven't said. Unknown mods are mentioned once.";

    /// <summary>What the Cheat warnings dropdown says beside itself.</summary>
    public static string CheatWarningsHint(RunWarningMode mode) => mode switch
    {
        RunWarningMode.Off => "no warnings",
        RunWarningMode.CheatsOnly => "warns about cheats only",
        _ => "warns about cheats and unknown or illegal mods when a run starts",
    };

    /// <summary>
    /// The dropdown's pick: one RUN_WARN, and only to a console that has the switch, and Cheats only
    /// only to one that takes it. False when there was nothing to send.
    /// </summary>
    public static bool SetCheatWarnings(AppState state, RunWarningMode mode)
    {
        var session = state.Session;
        if (!state.Connected || !session.HasRunWarnings) return false;
        if (mode == RunWarningMode.CheatsOnly && !session.HasSettings133) return false;

        state.Run(() => state.Client.RunWarnAsync(mode));
        return true;
    }

    /// <summary>Whether the Apply toggles on game boot dropdown is drawn: flags3 bit5, qwark revision 1.33.</summary>
    public static bool ShowsBootApply(SessionInfo session) => session.HasSettings133;

    /// <summary>The dropdown's choices, in its order.</summary>
    public static readonly IReadOnlyList<(BootApplyMode Mode, string Label)> BootApplyChoices = new[]
    {
        (BootApplyMode.Always, "Always"),
        (BootApplyMode.Ask, "Ask"),
        (BootApplyMode.Never, "Never"),
    };

    /// <summary>What the Apply toggles on game boot dropdown says when the pointer rests on it.</summary>
    public const string BootApplyTooltip =
        "Whether the toggles and mods you set to apply on boot are applied when a game boots. Ask asks "
        + "in RaCMAN while it is connected, and applies nothing when it isn't. Your on-boot choices are "
        + "kept either way.";

    /// <summary>What the Apply toggles on game boot dropdown says beside itself.</summary>
    public static string BootApplyHint(BootApplyMode mode) => mode switch
    {
        BootApplyMode.Ask => "asks in RaCMAN when a game boots; nothing applied without RaCMAN connected",
        BootApplyMode.Never => "nothing is applied when a game boots",
        _ => "applies your on-boot toggles and mods when a game boots",
    };

    /// <summary>What the Per-user settings box says when the pointer rests on it.</summary>
    public const string ProfilesTooltip =
        "When enabled, each console user keeps its own saved positions and settings. " +
        "Combos are shared by default, but can be overridden manually. " +
        "When disabled, positions and settings are shared between all users.";

    /// <summary>
    /// Whether the Per-user settings box is drawn: SessionInfo byte 29 bit1, the capability bit,
    /// and never under RPCS3, which has one user (<see cref="CombosPanel.ShowsProfiles"/>, which the
    /// Combos panel's overrides follow too).
    /// </summary>
    public static bool ShowsPerUserSettings(SessionInfo session) => CombosPanel.ShowsProfiles(session);

    /// <summary>
    /// The line beside the box: whose settings the panels are editing, from the session's bits
    /// and the last PROFILE_INFO.
    /// </summary>
    public static string ProfileStatus(SessionInfo session, ProfileInfo profile)
    {
        if (!session.ProfilesOn) return "Editing your console's shared settings";
        if (!session.ProfileActive) return "Per-user settings are on; waiting for a user to sign in";
        if (!profile.SignedIn) return "Editing the signed-in user's settings";

        string who = profile.Name.Length > 0 ? $"{profile.Name} (user {profile.UserId})" : $"User {profile.UserId}";
        return $"Editing settings for {who}";
    }
}
