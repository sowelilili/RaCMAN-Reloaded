using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The console's own settings, the Settings page's Qwark tab: In-game menus (MENU_HELP), Run
/// warnings (RUN_WARN, hidden) and Per-user settings (PROFILE_ENABLE). Each is drawn from the
/// session, so it always says what the console says, and each click sends one request; the next
/// packet moves the control. They were the Combos panel's "Qwark settings" section until the
/// Settings page had tabs. The rules live here, apart from the drawing, so they can be tested.
/// </summary>
public static class QwarkSettings
{
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
    /// Whether the Run warnings box is shown at all. After build 57 (the user's call) it is not:
    /// the console's switch, RUN_WARN and SessionInfo <c>flags3</c> stay as they are, and the row
    /// stays on the Qwark tab, so showing it again is this one word.
    /// </summary>
    public const bool RunWarningsSwitchShown = false;

    /// <summary>
    /// Whether the Run warnings box is drawn: <see cref="RunWarningsSwitchShown"/>, and SessionInfo
    /// <c>flags3</c> bit0 (qwark revision 1.30), the module's capability bit, and not the build
    /// number, so a test build of the same number without it shows nothing. RPCS3 has the warnings
    /// too: they go to qwark-rpcs3's log there.
    /// </summary>
    public static bool ShowsRunWarnings(SessionInfo session) => RunWarningsSwitchShown && session.HasRunWarnings;

    /// <summary>What the Run warnings box says when the pointer rests on it.</summary>
    public const string RunWarningsTooltip =
        "When enabled, your console shows a notification as a speedrun starts if cheats are on, or mods "
        + "that are not allowed in runs or whose authors haven't said. Unknown mods are "
        + "mentioned once.";

    /// <summary>What the Run warnings box says beside itself, from the session alone.</summary>
    public static string RunWarningsStatus(SessionInfo session) =>
        session.RunWarningsOff ? "no warnings" : "warns about cheats and mods as a run starts";

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
