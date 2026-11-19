using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The console's own settings, the Settings page's Console tab's first group: Per-user settings
/// (PROFILE_ENABLE), In-game menus (MENU_HELP), Cheat warnings (RUN_WARN) and Apply toggles on game
/// reboot (REBOOT_APPLY). Each is drawn from the session (Apply toggles on game reboot from its own
/// read, which no SessionInfo bit carries), so it always says what the console says, and each pick
/// sends one request. The rules live here, apart from the drawing, so they can be tested.
/// </summary>
public static class QwarkSettings
{
    /// <summary>The line at the top of the Console tab, the user's own words (2026-11-14).</summary>
    public const string ConsoleHeading =
        "Editing console settings. These are saved persistently on your console and apply whenever qwark is loaded.";

    /// <summary>
    /// The Qwark table's heading: "Qwark", and "Qwark (User 1)" while per-user settings are on and
    /// someone is signed in, so the table says whose settings it holds. The user is named as the
    /// Combos panel names them (<see cref="CombosPanel.UserName"/>): a name if the console sends one,
    /// else "User n". Plain "Qwark" until PROFILE_INFO has said who is signed in.
    /// </summary>
    public static string QwarkHeading(SessionInfo session, ProfileInfo profile) =>
        session.ProfilesOn && session.ProfileActive && profile.SignedIn
            ? $"{SettingsLayout.QwarkGroup} ({CombosPanel.UserName(profile)})"
            : SettingsLayout.QwarkGroup;
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
    /// What the running game's module says of its Trainer (qwark revision 1.34, DESCRIBE's
    /// extension), or null: outside a game, before DESCRIBE has answered for this game, or from a
    /// module before the revision.
    /// </summary>
    private static DescribeExtension? MenuFacts(SessionInfo session, DescribeResult describe) =>
        session.Game != GameId.None && describe.Game == session.Game ? describe.Extension : null;

    /// <summary>
    /// Whether the In-game menus box is greyed: the module says whether its Trainer can come up in
    /// this game on this platform (a game whose Trainer is its own pause menu can under RPCS3, the
    /// others cannot). Outside a game, or against a module that does not say, it is greyed where code
    /// cannot be patched, as the four original games' menus need: a module before revision 1.34 under
    /// RPCS3 reads as having none, ToD's included.
    /// </summary>
    public static bool InGameMenusBlocked(SessionInfo session, DescribeResult describe) =>
        MenuFacts(session, describe) is { } facts
            ? !facts.MenuHere
            : session.IsEmulator || session.CodePatchesUnsupported;

    /// <summary>What the In-game menus box says when the pointer rests on it.</summary>
    public static string InGameMenusTooltip(SessionInfo session, DescribeResult describe) =>
        InGameMenusBlocked(session, describe)
            ? "The console menu is not available under RPCS3."
            : $"When enabled, shows the in-game Trainer menu in place of the {MenuRow(session, describe)} menu. "
              + "When disabled, the Trainer menu is only accessible with its button combo.";

    /// <summary>The pause-menu row the Trainer takes, as the module names it; Help when it does not.</summary>
    public static string MenuRow(SessionInfo session, DescribeResult describe) =>
        MenuFacts(session, describe) is { MenuRow.Length: > 0 } facts ? facts.MenuRow : "Help";

    /// <summary>
    /// What the In-game menus box says beside itself: the row the Trainer takes in the pause menu,
    /// as the running game's module names it (Inventory in Tools of Destruction, Help elsewhere).
    /// </summary>
    public static string InGameMenusStatus(SessionInfo session, DescribeResult describe) =>
        InGameMenusBlocked(session, describe) ? "not under RPCS3"
        : session.MenuHelpOff ? "the game's default pause menu"
        : $"Trainer replaces {MenuRow(session, describe)} in the pause menu";
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

    /// <summary>Whether the Apply toggles on game reboot dropdown is drawn: flags3 bit5, qwark revision 1.33.</summary>
    public static bool ShowsRebootApply(SessionInfo session) => session.HasSettings133;

    /// <summary>The dropdown's choices, in its order.</summary>
    public static readonly IReadOnlyList<(RebootApplyMode Mode, string Label)> RebootApplyChoices = new[]
    {
        (RebootApplyMode.Always, "Always"),
        (RebootApplyMode.Ask, "Ask"),
        (RebootApplyMode.Never, "Never"),
    };

    /// <summary>What the Apply toggles on game reboot dropdown says when the pointer rests on it.</summary>
    public const string RebootApplyTooltip =
        "Whether the toggles and mods you had on come back when your game reboots. Ask asks in RaCMAN, "
        + "and needs RaCMAN connected at the reboot. Toggles and mods set to apply on boot always come back.";

    /// <summary>What the Apply toggles on game reboot dropdown says beside itself.</summary>
    public static string RebootApplyHint(RebootApplyMode mode) => mode switch
    {
        RebootApplyMode.Always => "re-applies your last session's toggles after a reboot",
        RebootApplyMode.Never => "never re-applies them",
        _ => "asks in RaCMAN after a reboot",
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
    /// The line beside the box: what the switch does as it stands. Whose settings the table holds is
    /// its heading's (<see cref="QwarkHeading"/>), so this names nobody.
    /// </summary>
    public static string ProfileStatus(SessionInfo session) =>
        !session.ProfilesOn ? "settings shared by every user"
        : !session.ProfileActive ? "waiting for a user to sign in"
        : "different settings for each user";
}
