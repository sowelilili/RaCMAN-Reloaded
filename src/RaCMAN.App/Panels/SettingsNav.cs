using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>The Settings page's own side tabs, in the order the nav lists them.</summary>
public enum SettingsTab
{
    /// <summary>This client: theme, updates, save files, its files (the old RaCMAN's import is hidden for now).</summary>
    RaCMAN,

    /// <summary>The two servers on this PC the client talks to: RPCS3's IPC server and LiveSplit's.</summary>
    Ipc,

    /// <summary>
    /// The console: qwark's own settings, kept in its config.txt, then how the client reaches the
    /// console and how often it reads it back. The Connection and Qwark tabs until the second round.
    /// </summary>
    Console,

    /// <summary>What most users never touch: debug information, the game layout, and with debug information on the qwark.sprx path.</summary>
    Advanced,
}

/// <summary>
/// Every row the Settings page draws. A row is one setting, or one thing on the page that is drawn
/// as a row of the list (the version and its buttons, a folder and its button, the import, the
/// boot plugin install).
/// </summary>
public enum SettingRow
{
    Theme,
    Version,
    CheckForUpdates,
    MirrorSaves,
    SortSaveFiles,
    Folders,
    ImportFromRaCMAN,
    Rpcs3Port,
    LiveSplit,
    InGameMenus,
    CheatWarnings,
    RebootApply,
    PerUserSettings,
    InputDisplayRate,
    ConnectionMode,
    Installation,
    TableRefresh,
    DebugInformation,
    GameLayout,
    SprxPath,
}

/// <summary>
/// What the Settings page shows on a tab, and the side nav's state while it is open. Pressing
/// Settings puts the page's own tabs where the window's panels were, under a Back entry; Back, or
/// Esc, puts the panels back on the one that was open before. Kept apart from the window so the
/// rules can be tested without one.
/// </summary>
public sealed class SettingsNav
{
    /// <summary>The tabs in the order the nav lists them.</summary>
    public static readonly SettingsTab[] Tabs = { SettingsTab.RaCMAN, SettingsTab.Ipc, SettingsTab.Console, SettingsTab.Advanced };

    /// <summary>What the nav calls a tab.</summary>
    public static string Name(SettingsTab tab) => tab switch
    {
        SettingsTab.RaCMAN => "RaCMAN",
        SettingsTab.Ipc => "IPC",
        SettingsTab.Console => "Console",
        SettingsTab.Advanced => "Advanced",
        _ => tab.ToString(),
    };

    /// <summary>What the Back entry at the top of the Settings nav says.</summary>
    public const string BackLabel = "< Back";

    /// <summary>True while the nav shows the Settings tabs in place of the window's panels.</summary>
    public bool Open { get; private set; }

    /// <summary>The tab the page is showing. Kept while the page is closed, so it reopens where it was left.</summary>
    public SettingsTab Tab { get; private set; } = SettingsTab.RaCMAN;

    /// <summary>The panel Back returns to: the one that was open when Settings was pressed.</summary>
    public int ReturnPanel { get; private set; } = PanelNav.Connection;

    /// <summary>
    /// Settings was pressed while <paramref name="fromPanel"/> was showing. Returns the panel the
    /// window shows now, which is Settings.
    /// </summary>
    public int Enter(int fromPanel)
    {
        if (fromPanel != PanelNav.Settings && fromPanel >= 0 && fromPanel < PanelNav.Count) ReturnPanel = fromPanel;
        Open = true;
        return PanelNav.Settings;
    }

    /// <summary>A tab was picked in the Settings nav.</summary>
    public void Select(SettingsTab tab) => Tab = tab;

    /// <summary>Back was pressed: the nav shows the panels again. Returns the panel to show.</summary>
    public int Back()
    {
        Open = false;
        return ReturnPanel;
    }

    /// <summary>
    /// Keeps the nav in step with the window's panel when something other than the nav moves it:
    /// a panel that has gone away sends the window elsewhere, and <c>--panel</c> can start it on
    /// Settings. Returns the panel unchanged.
    /// </summary>
    public int Follow(int panel)
    {
        if (panel == PanelNav.Settings) Open = true;
        else if (Open) Open = false;
        return panel;
    }

    /// <summary>
    /// Whether an Esc press is the nav's Back: only while the Settings tabs are showing, and not
    /// while a box is being typed into or a popup or modal is open, which take Esc for themselves.
    /// </summary>
    public bool EscapeGoesBack(bool escapePressed, bool typing, bool popupOpen) =>
        Open && escapePressed && !typing && !popupOpen;

    /// <summary>
    /// A tab named on the command line (<c>--settings-tab</c>), by its name or its number; null for
    /// none. The first round's Connection and Qwark name the Console tab, which holds both.
    /// </summary>
    public static SettingsTab? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (int.TryParse(text, out int number)) return number >= 0 && number < Tabs.Length ? Tabs[number] : null;
        foreach (var tab in Tabs)
        {
            if (string.Equals(Name(tab), text, StringComparison.OrdinalIgnoreCase)) return tab;
        }

        if (string.Equals(text, "connection", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "qwark", StringComparison.OrdinalIgnoreCase))
        {
            return SettingsTab.Console;
        }

        return null;
    }
}

/// <summary>
/// What a tab's rows depend on: the session, whether it is live, what the client remembers of it,
/// whether the console is set up as standalone (the Installation row is that mode's), and whether
/// debug information is on (the qwark.sprx path is shown only then).
/// </summary>
public readonly record struct SettingsContext(
    SessionInfo Session, bool Connected, InputRateMemory InputRate, bool Standalone = false, bool Debug = false);

/// <summary>Which rows each tab of the Settings page has, and what each is called.</summary>
public static class SettingsLayout
{
    /// <summary>
    /// The Console tab's first group: the settings qwark keeps on the console. Its heading names the
    /// signed-in user while per-user settings are on (<see cref="QwarkSettings.QwarkHeading"/>).
    /// </summary>
    public const string QwarkGroup = "Qwark";

    /// <summary>
    /// The Console tab's second group: how this client reaches the console and how often it hears
    /// from it, the console-wide Input display rate among them, which no user has their own of.
    /// </summary>
    public const string ConnectionGroup = "Connection";

    /// <summary>
    /// Rows the page knows and draws on no tab for now. Import from RaCMAN waits for a rework: its
    /// Import button is drawn only once the box names an old config.txt that holds something, so the
    /// row showed a box and Browse and nothing to press, and since the Settings tabs the line that
    /// said what to put in the box is only a tooltip. Its code stays.
    /// </summary>
    public static bool Hidden(SettingRow row) => row == SettingRow.ImportFromRaCMAN;

    /// <summary>The rows of a tab, in the order they are drawn. The page draws exactly these.</summary>
    public static IReadOnlyList<SettingRow> Rows(SettingsTab tab, SettingsContext context)
    {
        var rows = new List<SettingRow>();
        switch (tab)
        {
            case SettingsTab.RaCMAN:
                rows.AddRange(new[]
                {
                    SettingRow.Theme,
                    SettingRow.Version, SettingRow.CheckForUpdates,
                    SettingRow.MirrorSaves, SettingRow.SortSaveFiles,
                    SettingRow.Folders,
                });
                break;

            case SettingsTab.Ipc:
                rows.Add(SettingRow.Rpcs3Port);
                rows.Add(SettingRow.LiveSplit);
                break;

            case SettingsTab.Console:
                // qwark's settings first, Per-user settings at the top since it decides whose the
                // rest are. They are read from the session and written by ops, so without a
                // console there is nothing to show and nothing to send.
                if (context.Connected)
                {
                    if (QwarkSettings.ShowsPerUserSettings(context.Session)) rows.Add(SettingRow.PerUserSettings);
                    if (QwarkSettings.ShowsInGameMenus(context.Session)) rows.Add(SettingRow.InGameMenus);
                    if (QwarkSettings.ShowsCheatWarnings(context.Session)) rows.Add(SettingRow.CheatWarnings);
                    if (QwarkSettings.ShowsRebootApply(context.Session)) rows.Add(SettingRow.RebootApply);
                }

                // Then the connection: the boot plugin install is the standalone mode's setup step,
                // so it sits under the mode while that mode is picked. The input display rate is
                // the console's for every user and PC, so it is here rather than under Qwark, and
                // stays without a console, greyed at the last rate seen (InputRateMemory).
                rows.Add(SettingRow.ConnectionMode);
                if (context.Standalone) rows.Add(SettingRow.Installation);
                if (context.InputRate.Shows(context.Session, context.Connected)) rows.Add(SettingRow.InputDisplayRate);
                rows.Add(SettingRow.TableRefresh);
                break;

            case SettingsTab.Advanced:
                rows.Add(SettingRow.DebugInformation);
                rows.Add(SettingRow.GameLayout);

                // Empty is detection, which is what nearly everyone wants, so the box is debug
                // information's; the setting is read and kept whether it shows or not.
                if (context.Debug) rows.Add(SettingRow.SprxPath);
                break;
        }

        return rows;
    }

    /// <summary>The tab a row is on.</summary>
    public static SettingsTab TabOf(SettingRow row) => row switch
    {
        SettingRow.Rpcs3Port or SettingRow.LiveSplit => SettingsTab.Ipc,
        SettingRow.InGameMenus or SettingRow.CheatWarnings or SettingRow.RebootApply or SettingRow.PerUserSettings
            or SettingRow.InputDisplayRate or SettingRow.ConnectionMode or SettingRow.Installation
            or SettingRow.TableRefresh => SettingsTab.Console,
        SettingRow.DebugInformation or SettingRow.GameLayout or SettingRow.SprxPath => SettingsTab.Advanced,
        _ => SettingsTab.RaCMAN,
    };

    /// <summary>
    /// The group heading a row sits under, each group its own table; IPC and Advanced are one list
    /// each, with no heading.
    /// </summary>
    public static string? Group(SettingRow row) => row switch
    {
        SettingRow.Theme => "General",
        SettingRow.Version or SettingRow.CheckForUpdates => "Updates",
        SettingRow.MirrorSaves or SettingRow.SortSaveFiles => "Save files",
        SettingRow.Folders => "Files",
        SettingRow.ImportFromRaCMAN => "Import",
        SettingRow.PerUserSettings or SettingRow.InGameMenus or SettingRow.CheatWarnings or SettingRow.RebootApply => QwarkGroup,
        SettingRow.ConnectionMode or SettingRow.Installation or SettingRow.InputDisplayRate
            or SettingRow.TableRefresh => ConnectionGroup,
        _ => null,
    };

    /// <summary>The row's label in the list's first column. The folders' rows are labelled one by one.</summary>
    public static string Label(SettingRow row) => row switch
    {
        SettingRow.Theme => "Theme",
        SettingRow.DebugInformation => "Debug information",
        SettingRow.GameLayout => "Game layout",
        SettingRow.Version => "Version",
        SettingRow.CheckForUpdates => "Check for updates on start",
        SettingRow.Folders => "Folders",
        SettingRow.MirrorSaves => "Mirror saves to this PC",
        SettingRow.SortSaveFiles => "Sort save files",
        SettingRow.ImportFromRaCMAN => "Import from RaCMAN",
        SettingRow.Rpcs3Port => "RPCS3 IPC port",
        SettingRow.LiveSplit => "LiveSplit server",
        SettingRow.ConnectionMode => "Connection mode",
        SettingRow.Installation => "Installation",
        SettingRow.TableRefresh => "Table refresh",
        SettingRow.InputDisplayRate => "Input display rate",
        SettingRow.InGameMenus => "In-game menus",
        SettingRow.CheatWarnings => "Cheat warnings",
        SettingRow.RebootApply => "Apply toggles on game reboot",
        SettingRow.PerUserSettings => "Per-user settings",
        SettingRow.SprxPath => "qwark.sprx path",
        _ => row.ToString(),
    };
}

/// <summary>
/// The Input display rate row's view of the console's telemetry rate (qwark revision 1.32). The
/// row is there only for a module that has the switch (SessionInfo flags3 bit2); while the client
/// is not connected it stays, greyed out at the last rate seen, once a module of this run has had
/// the switch, so the row does not come and go with every reconnect.
/// </summary>
public sealed class InputRateMemory
{
    /// <summary>A module with the switch has been connected to in this run.</summary>
    public bool Seen { get; private set; }

    /// <summary>The rate that module last reported; 60 before any, the default since qwark revision 1.33.</summary>
    public int LastHz { get; private set; } = 60;

    /// <summary>Takes in the session of a frame: a live session with the switch is remembered.</summary>
    public void Observe(SessionInfo session, bool connected)
    {
        if (!connected || session.QwarkVersion == 0) return;

        Seen = session.HasTelemetryRate;
        if (Seen) LastHz = session.TelemetryHz;
    }

    /// <summary>
    /// Whether the row is drawn: against a live module, exactly when it has the switch; with no
    /// console, when the last module of this run had it.
    /// </summary>
    public bool Shows(SessionInfo session, bool connected) =>
        connected && session.QwarkVersion != 0 ? session.HasTelemetryRate : Seen;

    /// <summary>Whether the dropdown takes a pick: only with a console to send it to.</summary>
    public static bool Enabled(bool connected) => connected;

    /// <summary>The rate the dropdown shows: the console's, or with none the last one seen.</summary>
    public int Shown(SessionInfo session, bool connected) =>
        connected && session.HasTelemetryRate ? session.TelemetryHz : LastHz;
}
