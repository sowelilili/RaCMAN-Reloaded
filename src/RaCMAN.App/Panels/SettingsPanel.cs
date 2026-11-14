using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The Settings page: four tabs of their own in the side nav (<see cref="SettingsNav"/>), each a
/// few tables of rows. A table has three columns: the label, the control, and a grey line saying
/// what the control's current value does, so within a table every such line starts at the same
/// place, after its widest control. A setting that is not obvious carries a tooltip saying what it
/// does; an obvious one has neither. Which rows a tab has is <see cref="SettingsLayout"/>'s, so the
/// page draws exactly what the tests check.
/// <para>
/// RaCMAN is this client's own: theme, updates, save files, its folders and the import from the old
/// RaCMAN. IPC is the two servers on this PC it talks to. Console is qwark's own settings, read from
/// the session and written by ops, and then how the client reaches the console: the connection
/// mode, the boot plugin install under Standalone, and the table refresh. Advanced is debug
/// information, the game layout and the qwark.sprx path.
/// </para>
/// </summary>
public static class SettingsPanel
{
    private static string _importPath = string.Empty;
    private static string _parsedPath = string.Empty;
    private static LegacyConfig? _parsed;
    private static string? _parseError;
    private static bool _dialogOpen;
    private static bool _importing;

    /// <summary>The two libraries beside the config.txt, counted when the path last changed.</summary>
    private static LegacyLibraryImport.Survey _found = LegacyLibraryImport.Survey.Nothing;

    /// <summary>
    /// The saved positions the file holds for the running game, counted when the file or the game
    /// last changed: that walks every planet the game has, which is not a once-a-frame job.
    /// </summary>
    private static IReadOnlyList<LegacyLibraryImport.LegacyPosition> _positionsFound =
        Array.Empty<LegacyLibraryImport.LegacyPosition>();

    /// <summary>What was counted for, so the count above happens once.</summary>
    private static (string Path, GameId Game) _positionsCountedFor;

    /// <summary>
    /// What pressing Import would do to the planet the console has loaded. Worked out every frame,
    /// because it is ten lookups for one planet and it has to follow the slots filling up.
    /// </summary>
    private static LegacyLibraryImport.PositionPlan _positionPlan = LegacyLibraryImport.PositionPlan.Nothing;

    /// <summary>The LiveSplit endpoint while it is being typed; committed when the boxes are left.</summary>
    private static string _liveSplitHost = string.Empty;
    private static int _liveSplitPort;
    private static bool _portsRead;

    /// <summary>The qwark.sprx path while it is being typed; committed when the box is left.</summary>
    private static string _sprxPath = string.Empty;
    private static bool _sprxPathRead;
    private static bool _sprxDialogOpen;

    /// <summary>The frame the Console tab was last drawn on, so opening it is told from staying on it.</summary>
    private static int _consoleDrawnFrame = int.MinValue;

    /// <summary>The Input display rate row's memory of the console's telemetry rate, fed every frame.</summary>
    public static InputRateMemory InputRate { get; } = new();

    /// <summary>The window's per-frame look at the session, so the rate row knows a console it has seen.</summary>
    public static void Observe(AppState state) => InputRate.Observe(state.Session, state.Connected);

    /// <summary>The context a tab's rows are worked out in.</summary>
    public static SettingsContext Context(AppState state) =>
        new(state.Session, state.Connected, InputRate, state.Settings.Ps3.StandaloneConnection);

    /// <summary>The page's heading for a tab.</summary>
    public static string Title(SettingsTab tab) => $"{SettingsNav.Name(tab)} settings";

    /// <summary>
    /// Whether the Console tab is being opened on <paramref name="frame"/>, rather than drawn again
    /// on the frame after the last one: the moment its boot_plugins.txt look runs.
    /// </summary>
    public static bool ConsoleTabOpened(SettingsTab tab, int frame)
    {
        if (tab != SettingsTab.Console) return false;
        bool opened = frame != _consoleDrawnFrame + 1 && frame != _consoleDrawnFrame;
        _consoleDrawnFrame = frame;
        return opened;
    }

    public static void Draw(AppState state, SettingsTab tab)
    {
        Ui.Heading(Title(tab));

        if (tab == SettingsTab.Console)
        {
            if (ConsoleTabOpened(tab, ImGui.GetFrameCount())) state.CheckBootPlugins();

            Ui.Hint(QwarkSettings.ConsoleHeading);
            if (state.Connected && QwarkSettings.PerUserNote(state.Session, state.Profile) is { } note) Ui.Hint(note);
            if (!state.Connected) Ui.Hint("Connect to change the settings qwark keeps on your console.");
            ImGui.Spacing();
        }

        var rows = SettingsLayout.Rows(tab, Context(state));
        float labelColumn = LabelColumnWidth(state, rows);
        string? group = null;
        bool started = false;
        bool tableOpen = false;

        foreach (var row in rows)
        {
            string? rowGroup = SettingsLayout.Group(row);
            if (!started || rowGroup != group)
            {
                if (tableOpen) EndList();
                if (started) ImGui.Spacing();
                if (rowGroup is not null) Ui.Text(Ui.Grey, rowGroup);

                tableOpen = BeginList($"settings-{tab}-{rowGroup}", labelColumn, wide: row == SettingRow.ImportFromRaCMAN);
                group = rowGroup;
                started = true;
            }

            if (!tableOpen) continue;

            ImGui.PushID((int)row);
            DrawRow(state, row);
            ImGui.PopID();
        }

        if (tableOpen) EndList();
    }

    // ---------------------------------------------------------------- the list

    /// <summary>How much taller than ImGui's own a row of the settings tables is, top and bottom.</summary>
    private const float RowPaddingY = 4f;

    /// <summary>
    /// One list, three columns: the labels' as wide as the widest label of the whole tab, the
    /// controls' as wide as this table's widest control (ImGui fits a fixed column with no width to
    /// what was drawn in it), and the hints' the rest. So every hint of a table starts at one x.
    /// <paramref name="wide"/> is the import's table, whose one row is a box and what the file
    /// holds, lines of text with no hint beside them: its middle column takes the width instead.
    /// </summary>
    private static bool BeginList(string id, float labelColumn, bool wide = false)
    {
        var padding = ImGui.GetStyle().CellPadding;
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(padding.X, padding.Y + RowPaddingY));

        // No alternating row colour: an unticked checkbox's frame all but vanished on the light one.
        const ImGuiTableFlags flags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersOuter;
        if (!ImGui.BeginTable(id, 3, flags))
        {
            ImGui.PopStyleVar();
            return false;
        }

        ImGui.TableSetupColumn("Setting", ImGuiTableColumnFlags.WidthFixed, labelColumn);
        ImGui.TableSetupColumn("Value", wide ? ImGuiTableColumnFlags.WidthStretch : ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("Hint", wide ? ImGuiTableColumnFlags.WidthFixed : ImGuiTableColumnFlags.WidthStretch, wide ? 1f : 0f);
        return true;
    }

    private static void EndList()
    {
        ImGui.EndTable();
        ImGui.PopStyleVar();
    }

    /// <summary>
    /// The width of the labels' column for a tab: its widest label, so every list on the tab puts
    /// its controls at the same place.
    /// </summary>
    private static float LabelColumnWidth(AppState state, IReadOnlyList<SettingRow> rows)
    {
        float widest = 0f;
        foreach (var row in rows)
        {
            if (row == SettingRow.Folders)
            {
                foreach (var folder in Folders(state)) widest = MathF.Max(widest, ImGui.CalcTextSize(folder.Label).X);
            }
            else
            {
                widest = MathF.Max(widest, ImGui.CalcTextSize(SettingsLayout.Label(row)).X);
            }
        }

        return MathF.Ceiling(widest + (ImGui.GetStyle().CellPadding.X * 2f) + 12f);
    }

    /// <summary>A new row: its label, with the setting's tooltip on it, and the cursor in the control's cell.</summary>
    private static void Label(string label, string? tooltip = null)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        Ui.TableLabel(label);
        if (tooltip is not null) Ui.Tooltip(tooltip);
        ImGui.TableSetColumnIndex(1);
    }

    /// <summary>The setting's tooltip on the control just drawn as well, so it is on the whole row.</summary>
    private static void ControlTooltip(string? tooltip)
    {
        if (tooltip is not null) Ui.Tooltip(tooltip);
    }

    /// <summary>The cursor into the row's third cell, level with the control's text.</summary>
    private static void HintCell()
    {
        ImGui.TableSetColumnIndex(2);
        ImGui.AlignTextToFramePadding();
    }

    /// <summary>The grey line in the third column: what the control's current value does.</summary>
    private static void InlineHint(string text) => InlineText(Ui.Grey, text);

    private static void InlineText(Vector4 colour, string text)
    {
        HintCell();
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    private static void DrawRow(AppState state, SettingRow row)
    {
        switch (row)
        {
            case SettingRow.Theme: DrawTheme(state); break;
            case SettingRow.DebugInformation: DrawDebug(state); break;
            case SettingRow.GameLayout: DrawGameLayout(state); break;
            case SettingRow.Version: DrawVersion(state); break;
            case SettingRow.CheckForUpdates: DrawCheckForUpdates(state); break;
            case SettingRow.Folders: DrawFolders(state); break;
            case SettingRow.MirrorSaves: DrawMirrorSaves(state); break;
            case SettingRow.SortSaveFiles: DrawSortSaveFiles(state); break;
            case SettingRow.ImportFromRaCMAN: DrawImportRow(state); break;
            case SettingRow.Rpcs3Port: DrawRpcs3Port(state); break;
            case SettingRow.LiveSplit: DrawLiveSplit(state); break;
            case SettingRow.ConnectionMode: DrawConnectionMode(state); break;
            case SettingRow.Installation: DrawInstallation(state); break;
            case SettingRow.TableRefresh: DrawTableRefresh(state); break;
            case SettingRow.InputDisplayRate: DrawInputDisplayRate(state); break;
            case SettingRow.InGameMenus: DrawInGameMenus(state); break;
            case SettingRow.CheatWarnings: DrawCheatWarnings(state); break;
            case SettingRow.BootApply: DrawBootApply(state); break;
            case SettingRow.PerUserSettings: DrawPerUserSettings(state); break;
            case SettingRow.SprxPath: DrawSprxPath(state); break;
        }
    }

    // ---------------------------------------------------------------- RaCMAN: general

    private static readonly string[] Themes = { "Light", "Dark" };

    private static void DrawTheme(AppState state)
    {
        Label(SettingsLayout.Label(SettingRow.Theme));

        int theme = state.Settings.LightTheme ? 0 : 1;
        if (Ui.Dropdown("##theme", Themes, ref theme)) SetTheme(state, theme == 0 ? "light" : "dark");
    }

    // ---------------------------------------------------------------- RaCMAN: updates

    /// <summary>
    /// Which version this is and the buttons for a newer one. A copy the installer did not put here
    /// cannot replace itself, so it says that instead of offering a check that could only end in a
    /// download link.
    /// </summary>
    private static void DrawVersion(AppState state)
    {
        var updates = state.Updates;
        Label(SettingsLayout.Label(SettingRow.Version));

        Ui.TableLabel(AppVersion.Current);
        ImGui.SameLine();

        ImGui.BeginDisabled(updates.IsInert || updates.Busy);
        if (ImGui.Button("Check now")) updates.CheckNow();
        ImGui.EndDisabled();

        if (updates.Stage == UpdateStage.Available)
        {
            ImGui.SameLine();
            if (ImGui.Button("Download")) updates.Download();
        }
        else if (updates.Stage == UpdateStage.Ready)
        {
            ImGui.SameLine();
            if (ImGui.Button("Restart to update")) updates.RestartAndApply();
        }

        string status = updates.InertReason ?? updates.StatusLine();
        if (updates.InertReason is null && updates.Stage != UpdateStage.Idle && updates.LastCheckUtc is { } last)
        {
            status += $" Last checked {last.ToLocalTime():yyyy-MM-dd HH:mm}.";
        }

        InlineText(updates.InertReason is null && updates.Stage == UpdateStage.Failed ? Ui.Red : Ui.Grey, status);

        Ui.DebugHint(UpdateService.RepositoryUrl);
    }

    private static void DrawCheckForUpdates(AppState state)
    {
        Label(SettingsLayout.Label(SettingRow.CheckForUpdates));

        bool check = state.Settings.Updates.Check;
        if (ImGui.Checkbox("##check-updates", ref check))
        {
            state.Settings.Updates.Check = check;
            state.Settings.Save();
        }
    }

    // ---------------------------------------------------------------- RaCMAN: save files

    public const string MirrorSavesTooltip =
        "Save files are kept on your console. When enabled, every save you take is also copied to this PC.";

    /// <summary>What the Mirror saves box says beside itself.</summary>
    public static string MirrorSavesHint(bool on) =>
        on ? "every save is also copied to this PC" : "saves are kept on your console only";

    private static void DrawMirrorSaves(AppState state)
    {
        var settings = state.Settings;
        Label(SettingsLayout.Label(SettingRow.MirrorSaves), MirrorSavesTooltip);

        // The savefile library lives on the console since qwark build 12; this is the second copy.
        bool mirror = settings.MirrorSaveFiles;
        if (ImGui.Checkbox("##mirror", ref mirror))
        {
            settings.MirrorSaveFiles = mirror;
            settings.Save();
        }

        ControlTooltip(MirrorSavesTooltip);
        InlineHint(MirrorSavesHint(settings.MirrorSaveFiles));
    }

    private static readonly string[] SaveSorts = { "By planet", "Alphabetical" };

    /// <summary>
    /// What the Sort save files dropdown says on hover. The By planet sentence is the user's own
    /// wording for the option (2026-11-14), which began "When enabled" while it was a pair of radio
    /// buttons; as a dropdown it names the choice it is about instead.
    /// </summary>
    public const string SortSaveFilesTooltip =
        "How each category of save files is ordered. By planet uses the planet ID of each stored save "
        + "to determine its order. Alphabetical orders the saves by name.";

    /// <summary>What the Sort save files dropdown says beside itself.</summary>
    public static string SortSaveFilesHint(SaveFileSort sort) =>
        sort == SaveFileSort.ByPlanet
            ? "uses the planet ID of each stored save to determine its order"
            : "orders the saves by name";

    private static void DrawSortSaveFiles(AppState state)
    {
        var settings = state.Settings;
        Label(SettingsLayout.Label(SettingRow.SortSaveFiles), SortSaveFilesTooltip);

        int sort = settings.SaveFileSort == SaveFileSort.Alphabetical ? 1 : 0;
        if (Ui.Dropdown("##sort", SaveSorts, ref sort))
        {
            settings.SaveFileSort = sort == 1 ? SaveFileSort.Alphabetical : SaveFileSort.ByPlanet;
            settings.Save();
        }

        ControlTooltip(SortSaveFilesTooltip);
        InlineHint(SortSaveFilesHint(settings.SaveFileSort));
    }

    // ---------------------------------------------------------------- RaCMAN: files

    /// <summary>
    /// The client's places on disk, a row each with a button that opens it. What is in the data
    /// folder is the user's and survives an update; what is beside the executable is the release's.
    /// </summary>
    private static IEnumerable<(string Label, string Folder, string? Tooltip)> Folders(AppState state)
    {
        var settings = state.Settings;
        string settingsFile = string.IsNullOrEmpty(settings.Path) ? Settings.DefaultPath : settings.Path;

        yield return ("Data folder", AppPaths.Root, null);
        yield return ("Settings file", FolderOf(settingsFile), settingsFile);
        yield return ("Mods you installed", state.Mods.RootPath, null);
        yield return ("Save files", state.SaveFiles.RootPath, null);
        yield return ("Colour presets", state.ColourPresets.Folder, null);
        yield return ("Watchlists", state.Watchlists.Folder, null);
        yield return ("Lua scripts", state.LuaScripts.Folder, null);
    }

    private static void DrawFolders(AppState state)
    {
        foreach (var folder in Folders(state))
        {
            Label(folder.Label);
            Ui.OpenFolderButton(state, folder.Folder, folder.Tooltip);
        }
    }

    // ---------------------------------------------------------------- RaCMAN: import

    public const string ImportTooltip =
        "Imports settings, save files and mods from previous versions of RaCMAN: point it at the old "
        + "RaCMAN's config.txt.";

    /// <summary>The import's path box: a fixed width, as every control in a table's middle column has.</summary>
    private const float ImportPathWidth = 420f;

    private static void DrawImportRow(AppState state)
    {
        Label(SettingsLayout.Label(SettingRow.ImportFromRaCMAN), ImportTooltip);
        DrawImport(state);
    }

    // ---------------------------------------------------------------- IPC

    public const string Rpcs3PortTooltip =
        "Where RaCMAN looks for RPCS3's IPC server. Change this to match RPCS3's IPC settings.";

    /// <summary>What the IPC rows' button under each box says.</summary>
    public const string ResetToDefault = "Reset to default";

    /// <summary>Whether RPCS3's port is RPCS3's own default, 28012, which greys the reset.</summary>
    public static bool Rpcs3PortIsDefault(Settings settings) => settings.Rpcs3.PinePort == Rpcs3Host.DefaultPinePort;

    /// <summary>The reset under the RPCS3 port: back to 28012, saved as a typed edit is.</summary>
    public static void ResetRpcs3Port(AppState state)
    {
        state.Settings.Rpcs3.PinePort = Rpcs3Host.DefaultPinePort;
        state.Settings.Save();
    }

    private static void DrawRpcs3Port(AppState state)
    {
        var settings = state.Settings;
        Label(SettingsLayout.Label(SettingRow.Rpcs3Port), Rpcs3PortTooltip);

        int pine = settings.Rpcs3.PinePort;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("##pine", ref pine))
        {
            settings.Rpcs3.PinePort = Math.Clamp(pine, 1, 65535);
            settings.Save();
        }

        ControlTooltip(Rpcs3PortTooltip);

        ImGui.BeginDisabled(Rpcs3PortIsDefault(settings));
        if (ImGui.SmallButton($"{ResetToDefault}##pine")) ResetRpcs3Port(state);
        ImGui.EndDisabled();
    }

    public const string LiveSplitTooltip =
        "Where RaCMAN looks for LiveSplit's server. Change this to match LiveSplit's server settings.";

    /// <summary>Whether LiveSplit's endpoint is LiveSplit's own default, 127.0.0.1:16834, which greys the reset.</summary>
    public static bool LiveSplitIsDefault(AutosplitSettings autosplit) =>
        string.Equals(autosplit.Host, LiveSplitClient.DefaultHost, StringComparison.OrdinalIgnoreCase)
        && autosplit.Port == LiveSplitClient.DefaultPort;

    /// <summary>
    /// The reset under the LiveSplit boxes: 127.0.0.1:16834 into both boxes and saved the way a
    /// typed edit is (<see cref="ApplyLiveSplit"/>), a connection pointed elsewhere moved with it.
    /// </summary>
    public static void ResetLiveSplit(AppState state)
    {
        _liveSplitHost = LiveSplitClient.DefaultHost;
        _liveSplitPort = LiveSplitClient.DefaultPort;
        _portsRead = true;
        ApplyLiveSplit(state, state.Settings.Autosplit);
    }

    /// <summary>
    /// LiveSplit's TCP server, host and port. Committed the moment both boxes are left alone: a
    /// half-typed host would otherwise send a connection attempt somewhere nobody asked for, and a
    /// popup after it.
    /// </summary>
    private static void DrawLiveSplit(AppState state)
    {
        var autosplit = state.Settings.Autosplit;
        Label(SettingsLayout.Label(SettingRow.LiveSplit), LiveSplitTooltip);

        if (!_portsRead)
        {
            _liveSplitHost = autosplit.Host;
            _liveSplitPort = autosplit.Port;
            _portsRead = true;
        }

        ImGui.SetNextItemWidth(180);
        ImGui.InputText("##livesplit-host", ref _liveSplitHost, 64);
        bool editing = ImGui.IsItemActive();
        ControlTooltip(LiveSplitTooltip);

        ImGui.SameLine();
        Ui.TableLabel(":");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("##livesplit-port", ref _liveSplitPort))
        {
            _liveSplitPort = Math.Clamp(_liveSplitPort, 1, 65535);
        }

        editing |= ImGui.IsItemActive();
        ControlTooltip(LiveSplitTooltip);

        bool changed = !string.Equals(_liveSplitHost.Trim(), autosplit.Host, StringComparison.OrdinalIgnoreCase)
                       || _liveSplitPort != autosplit.Port;
        if (changed && !editing) ApplyLiveSplit(state, autosplit);

        ImGui.BeginDisabled(LiveSplitIsDefault(autosplit) && !changed);
        if (ImGui.SmallButton($"{ResetToDefault}##livesplit")) ResetLiveSplit(state);
        ImGui.EndDisabled();
    }

    /// <summary>Saves the LiveSplit endpoint and moves a connection that is pointed at the old one.</summary>
    private static void ApplyLiveSplit(AppState state, AutosplitSettings autosplit)
    {
        string host = _liveSplitHost.Trim();
        autosplit.Host = host.Length == 0 ? LiveSplitClient.DefaultHost : host;
        autosplit.Port = _liveSplitPort;
        _liveSplitHost = autosplit.Host;
        state.Settings.Save();

        // Nothing is pointed anywhere, so there is nothing to move: the automatic probe looks at
        // the new endpoint on its own, and the Autosplitter panel's Connect button is there for a
        // user who has that switched off.
        if (!state.LiveSplit.IsConnected && !state.LiveSplit.Enabled) return;

        // Start only restarts a connection that is now pointed somewhere else, and a failure there
        // is one the user asked for, so it earns the popup. One attempt, like the button's.
        LiveSplitModal.ArmForAttempt();
        state.LiveSplit.Start(autosplit.Host, autosplit.Port);
    }

    // ---------------------------------------------------------------- Console: connection

    private static readonly string[] ConnectionModes = { "webMAN", "Standalone" };

    public const string ConnectionModeTooltip = "How RaCMAN gets qwark onto your console when you connect.";

    /// <summary>What the Connection mode dropdown says beside itself.</summary>
    public static string ConnectionModeHint(bool standalone) =>
        standalone
            ? "qwark loads automatically on console boot"
            : "loads qwark through webMAN when connecting";

    /// <summary>The warning on the Connection mode row when a standalone console's boot list has no qwark.</summary>
    public const string NotInstalledWarning = "qwark is not installed";

    /// <summary>
    /// What the Connection mode row says under its hint about the boot plugin list, in standalone
    /// mode only: the warning when the list was read and names no qwark.sprx, a grey line when it
    /// could not be read, and nothing while the look is under way or found qwark.
    /// </summary>
    public static (string Text, bool Warning)? BootPluginLine(bool standalone, BootPluginCheck check) =>
        !standalone ? null
        : check.State switch
        {
            BootPluginState.NotListed => (NotInstalledWarning, true),
            BootPluginState.Failed => ($"couldn't check whether qwark is installed: {check.Problem}", false),
            _ => null,
        };

    /// <summary>The console's mode: RPCS3 has nothing to load and never asks webMAN anything.</summary>
    private static void DrawConnectionMode(AppState state)
    {
        var settings = state.Settings;
        Label(SettingsLayout.Label(SettingRow.ConnectionMode), ConnectionModeTooltip);

        int mode = settings.Ps3.StandaloneConnection ? 1 : 0;
        if (Ui.Dropdown("##mode", ConnectionModes, ref mode))
        {
            settings.Ps3.StandaloneConnection = mode == 1;
            settings.Save();

            // Picking Standalone is the moment to look whether the console boots qwark.
            state.CheckBootPlugins();
        }

        ControlTooltip(ConnectionModeTooltip);
        InlineHint(ConnectionModeHint(settings.Ps3.StandaloneConnection));

        if (BootPluginLine(settings.Ps3.StandaloneConnection, state.BootPlugins) is { } line)
        {
            if (line.Warning) Ui.Warning(line.Text);
            else Ui.Hint(line.Text);
        }
    }

    public const string InstallationTooltip =
        "Installs qwark as a boot plugin, so your console loads it every time it starts. Requires webMAN "
        + "for installation. Restart your console after installing.";

    /// <summary>
    /// The boot plugin install, under Connection mode while Standalone is picked: the Connection
    /// panel's Installation section until the second round, with the same two buttons, the same
    /// toasts (the HEN six-line note among them) and the debug-only update button. Either button,
    /// once done, looks at boot_plugins.txt again.
    /// </summary>
    private static void DrawInstallation(AppState state)
    {
        Label(SettingsLayout.Label(SettingRow.Installation), InstallationTooltip);

        ConnectionPanel.DrawBootInstallButtons(state, () => state.CheckBootPlugins());

        HintCell();
        ConnectionPanel.DrawBootInstallNotes(Ps3Connect.ResolveSprx(state.Settings.Ps3.SprxPath));
    }

    public const string TableRefreshTooltip =
        "How often the Unlocks and Level flags tables update. On a slow "
        + "connection, raise it. Set to 0 for manual refresh.";

    /// <summary>What the Table refresh box says beside itself.</summary>
    public static string TableRefreshHint(float seconds) =>
        seconds > 0f
            ? $"read again every {seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s"
            : "read only when you press Refresh";

    /// <summary>
    /// How often the tables that watch live game state read themselves back. The panels read this
    /// every frame, so a change here is live in the table you can see. It is a period in seconds,
    /// <c>tableRefreshSeconds</c>, which the box was labelled in Hz until the Settings tabs.
    /// </summary>
    private static void DrawTableRefresh(AppState state)
    {
        var settings = state.Settings;
        Label(SettingsLayout.Label(SettingRow.TableRefresh), TableRefreshTooltip);

        float seconds = settings.TableRefreshSeconds;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputFloat("##refresh", ref seconds, 0.1f, 1f, "%.1f s"))
        {
            // The property clamps to 0..10, so a typed 99 or a typed -1 is still a period the
            // panels can use.
            settings.TableRefreshSeconds = seconds;
            settings.Save();
        }

        ControlTooltip(TableRefreshTooltip);
        InlineHint(TableRefreshHint(settings.TableRefreshSeconds));
    }

    // ---------------------------------------------------------------- Console: qwark

    private static readonly string[] InputRates = SessionInfo.TelemetryRates.Select(hz => $"{hz} Hz").ToArray();

    public const string InputDisplayRateTooltip =
        "How often your console sends the controller and the rest of the live state to RaCMAN. Higher "
        + "rates make the input display and the OBS overlay smoother, and use more network traffic. "
        + "It is your console's setting, so it applies to every PC connected to it.";

    /// <summary>What the Input display rate dropdown says beside itself.</summary>
    public static string InputDisplayRateHint(int hz) => hz switch
    {
        >= 120 => "smoothest input display, the most network traffic",
        >= 60 => "smoother input display",
        _ => "the least network traffic",
    };

    /// <summary>
    /// The console's telemetry rate (TELEMETRY_RATE, qwark revision 1.32). Drawn from the session
    /// like the other qwark rows, so it always says what the console says; a pick sends the op and
    /// the next packet moves it. Only for a module with the switch, and greyed without a console.
    /// </summary>
    private static void DrawInputDisplayRate(AppState state)
    {
        bool enabled = InputRateMemory.Enabled(state.Connected);
        string tooltip = enabled ? InputDisplayRateTooltip : $"{InputDisplayRateTooltip} Connect to change it.";
        Label(SettingsLayout.Label(SettingRow.InputDisplayRate), tooltip);

        int shown = InputRate.Shown(state.Session, state.Connected);
        int index = Math.Max(0, SessionInfo.TelemetryRates.ToList().IndexOf(shown));

        ImGui.BeginDisabled(!enabled);
        if (Ui.Dropdown("##input-rate", InputRates, ref index)) SetInputRate(state, SessionInfo.TelemetryRates[index]);
        ImGui.EndDisabled();

        ControlTooltip(tooltip);
        InlineHint(InputDisplayRateHint(shown));
    }

    /// <summary>
    /// The dropdown's pick: one TELEMETRY_RATE, and only to a console that has the switch. False
    /// when there was nobody to send it to.
    /// </summary>
    public static bool SetInputRate(AppState state, int hz)
    {
        if (!state.Connected || !state.Session.HasTelemetryRate) return false;

        state.Run(() => state.Client.TelemetryRateAsync(hz));
        return true;
    }

    /// <summary>
    /// The console's In-game menus switch, MENU_HELP of revision 1.23: whether the Trainer menu
    /// takes the place of Help in the game's pause menu. Greyed under RPCS3, which has no console
    /// menu (but in ToD, whose pause menu is its console menu there too, qwark revision 1.29).
    /// </summary>
    private static void DrawInGameMenus(AppState state)
    {
        var session = state.Session;
        string tooltip = QwarkSettings.InGameMenusTooltip(session);
        Label(SettingsLayout.Label(SettingRow.InGameMenus), tooltip);

        bool blocked = QwarkSettings.InGameMenusBlocked(session);
        bool on = !session.MenuHelpOff;

        ImGui.BeginDisabled(blocked);
        if (ImGui.Checkbox("##in-game-menus", ref on))
        {
            bool want = on;
            state.Run(() => state.Client.MenuHelpAsync(want));
        }

        ImGui.EndDisabled();
        ControlTooltip(tooltip);
        InlineHint(QwarkSettings.InGameMenusStatus(session));
    }

    /// <summary>
    /// The console's Cheat warnings, RUN_WARN of qwark revision 1.30 with revision 1.33's third
    /// value: a dropdown drawn from flags3 bits 1 and 6, whose Cheats only is offered only to a
    /// module that takes it (flags3 bit5).
    /// </summary>
    private static void DrawCheatWarnings(AppState state)
    {
        var session = state.Session;
        Label(SettingsLayout.Label(SettingRow.CheatWarnings), QwarkSettings.CheatWarningsTooltip);

        var choices = QwarkSettings.CheatWarningChoices(session);
        var labels = choices.Select(c => c.Label).ToArray();
        var mode = session.RunWarnings;
        int index = Math.Max(0, choices.ToList().FindIndex(c => c.Mode == mode));

        if (Ui.Dropdown("##cheat-warnings", labels, ref index)) QwarkSettings.SetCheatWarnings(state, choices[index].Mode);

        ControlTooltip(QwarkSettings.CheatWarningsTooltip);
        InlineHint(QwarkSettings.CheatWarningsHint(mode));
    }

    /// <summary>
    /// The console's Apply toggles on game boot, BOOT_APPLY of qwark revision 1.33: no SessionInfo
    /// bit carries it, so it is drawn from the client's read (once per connection and profile), and
    /// greyed until that read is in.
    /// </summary>
    private static void DrawBootApply(AppState state)
    {
        Label(SettingsLayout.Label(SettingRow.BootApply), QwarkSettings.BootApplyTooltip);

        var choices = QwarkSettings.BootApplyChoices;
        var labels = choices.Select(c => c.Label).ToArray();
        var mode = state.BootApply;
        int index = mode is { } known ? Math.Max(0, choices.ToList().FindIndex(c => c.Mode == known)) : 0;

        ImGui.BeginDisabled(mode is null);
        if (Ui.Dropdown("##boot-apply", labels, ref index)) state.SetBootApply(choices[index].Mode);
        ImGui.EndDisabled();

        ControlTooltip(QwarkSettings.BootApplyTooltip);
        InlineHint(mode is { } shown ? QwarkSettings.BootApplyHint(shown) : "reading your console's setting...");
    }

    /// <summary>
    /// The console-wide per-user settings switch, drawn from SessionInfo byte 29 bit2. Ticking it
    /// reads PROFILE_INFO and either asks the copy-or-defaults question or turns the switch on
    /// (<see cref="AppState.TurnProfilesOn"/>); unticking sends PROFILE_ENABLE(0, 0).
    /// </summary>
    private static void DrawPerUserSettings(AppState state)
    {
        var session = state.Session;
        Label(SettingsLayout.Label(SettingRow.PerUserSettings), QwarkSettings.ProfilesTooltip);

        bool on = session.ProfilesOn;
        ImGui.BeginDisabled(state.ProfileQuestion is not null);
        if (ImGui.Checkbox("##per-user", ref on))
        {
            if (on) state.TurnProfilesOn();
            else state.TurnProfilesOff();
        }

        ImGui.EndDisabled();
        ControlTooltip(QwarkSettings.ProfilesTooltip);
        InlineHint(QwarkSettings.ProfileStatus(session, state.Profile));
    }

    // ---------------------------------------------------------------- Advanced

    public const string DebugInformationTooltip =
        "Shows extra technical detail across RaCMAN: qwark and protocol versions, reboot and tick "
        + "counters, request names in error messages, frame rate, raw readouts and internal addresses.";

    /// <summary>What the Debug information box says beside itself.</summary>
    public static string DebugInformationHint(bool on) =>
        on ? "technical detail shown on every panel" : "technical detail hidden";

    private static void DrawDebug(AppState state)
    {
        var settings = state.Settings;
        Label(SettingsLayout.Label(SettingRow.DebugInformation), DebugInformationTooltip);

        bool debug = settings.DebugInfo;
        if (ImGui.Checkbox("##debug", ref debug))
        {
            settings.DebugInfo = debug;
            settings.Save();
            state.ThemeDirty = true;
        }

        ControlTooltip(DebugInformationTooltip);
        InlineHint(DebugInformationHint(settings.DebugInfo));
    }

    public const string GameLayoutTooltip =
        "gamelayout.json decides where the Game page and its sub-pages put each of the game's features. "
        + "To edit it, copy it into the data folder, then reload it.";

    private static void DrawGameLayout(AppState state)
    {
        Label(SettingsLayout.Label(SettingRow.GameLayout), GameLayoutTooltip);

        if (ImGui.Button("Reload gamelayout.json"))
        {
            GameLayout.Invalidate();

            // The layout decides where the console's features land, so this is also one of the two
            // places the user can throw the cached description away and have it read back: the
            // page is rebuilt from a fresh DESCRIBE rather than from what was on screen.
            state.ForceRefresh();

            // Touching the layout re-reads the file now, so a broken edit is reported here
            // instead of on the next visit to the Game page.
            _ = GameLayout.SectionsDrawnElsewhere;
            state.AddToast(
                GameLayout.Problems.Count == 0 ? "Game layout reloaded" : "Game layout reloaded with problems",
                GameLayout.Problems.Count == 0 ? ToastKind.Success : ToastKind.Error);
        }

        ControlTooltip(GameLayoutTooltip);
        ImGui.SameLine();
        Ui.OpenFolderButton(state, FolderOf(GameLayout.DefaultPath), GameLayout.DefaultPath);

        InlineHint(GameLayout.UsingOverride ? "using your own copy" : "using the default");

        foreach (var problem in GameLayout.Problems) Ui.Warning(problem);
    }

    public const string SprxPathTooltip =
        "Which qwark.sprx RaCMAN sends to your console. Leave it empty to use the one beside RaCMAN, "
        + "which is the one this release ships.";

    /// <summary>
    /// Whether a stored path is detection: empty, or the bare <c>qwark.sprx</c> the settings start
    /// with, both of which <see cref="Ps3Connect.ResolveSprx"/> finds beside the executable.
    /// </summary>
    public static bool SprxPathDetected(string configured)
    {
        string trimmed = configured.Trim().Trim('"');
        return trimmed.Length == 0 || string.Equals(trimmed, Ps3Settings.DefaultSprxPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the box shows for a stored path: nothing for detection, so its "detected automatically" shows.</summary>
    public static string SprxPathShown(string configured) => SprxPathDetected(configured) ? string.Empty : configured;

    /// <summary>
    /// What the qwark.sprx path row says beside its box: which file is in use. Empty is detection
    /// (<see cref="Ps3Connect.ResolveSprx"/>, beside the executable); anything typed is the user's.
    /// </summary>
    public static (string Text, bool Warning) SprxPathHint(string configured, bool exists)
    {
        bool detected = SprxPathDetected(configured);
        return (detected, exists) switch
        {
            (true, true) => ("detected beside RaCMAN", false),
            (true, false) => ("no qwark.sprx beside RaCMAN; enter the path to one", true),
            (false, true) => ("using your file", false),
            _ => ("your file isn't there; empty the box to use the one beside RaCMAN", true),
        };
    }

    /// <summary>
    /// The qwark.sprx the client sends: empty for the one beside it, which nearly everyone wants,
    /// or a path of the user's. Committed when the box is left or Enter is pressed, and by the
    /// Browse button's pick; the Connection panel, Connect and the boot install all read it.
    /// </summary>
    private static void DrawSprxPath(AppState state)
    {
        var ps3 = state.Settings.Ps3;
        Label(SettingsLayout.Label(SettingRow.SprxPath), SprxPathTooltip);

        if (!_sprxPathRead)
        {
            _sprxPath = SprxPathShown(ps3.SprxPath);
            _sprxPathRead = true;
        }

        ImGui.SetNextItemWidth(240);
        bool enter = Ui.SubmitTextWithHint("##sprx-path", "detected automatically", ref _sprxPath, 512);
        bool left = ImGui.IsItemDeactivatedAfterEdit();
        ControlTooltip(SprxPathTooltip);
        if ((enter || left) && _sprxPath.Trim() != SprxPathShown(ps3.SprxPath)) SetSprxPath(state, _sprxPath);

        // Under the box, as the IPC rows' resets are, so the hint column keeps its room.
        ImGui.BeginDisabled(!FileDialog.IsSupported || _sprxDialogOpen);
        if (ImGui.SmallButton("Browse...##sprx")) BrowseSprx(state);
        ImGui.EndDisabled();

        var (text, warning) = SprxPathHint(ps3.SprxPath, File.Exists(Ps3Connect.ResolveSprx(ps3.SprxPath)));
        InlineText(warning ? Ui.Yellow : Ui.Grey, text);
        Ui.DebugHint(Ps3Connect.ResolveSprx(ps3.SprxPath));
    }

    /// <summary>Saves the qwark.sprx path; empty, or only spaces, is detection again (the bare name the settings start with).</summary>
    public static void SetSprxPath(AppState state, string path)
    {
        string trimmed = path.Trim();
        state.Settings.Ps3.SprxPath = trimmed.Length == 0 ? Ps3Settings.DefaultSprxPath : trimmed;
        state.Settings.Save();
        _sprxPath = SprxPathShown(state.Settings.Ps3.SprxPath);
    }

    private static void BrowseSprx(AppState state)
    {
        _sprxDialogOpen = true;
        _ = Task.Run(async () =>
        {
            try
            {
                string? picked = await FileDialog.OpenAsync("qwark.sprx", "PS3 plugins", "sprx").ConfigureAwait(false);
                state.Post(() =>
                {
                    _sprxDialogOpen = false;
                    if (picked is not null) SetSprxPath(state, picked);
                });
            }
            catch (Exception ex)
            {
                state.Post(() =>
                {
                    _sprxDialogOpen = false;
                    state.AddToast($"File dialog failed: {ex.Message}", ToastKind.Error);
                });
            }
        });
    }

    // ---------------------------------------------------------------- import

    /// <summary>
    /// Point it at the old RaCMAN's config.txt, see what it holds, import. The IP goes into this
    /// client's settings; the combos, the mod auto-apply list and the saved positions go to the
    /// console, so those three need a connection (the mod list needs the matching game running,
    /// since the console keeps that list per title, and the positions need it running and standing
    /// on the planet they were saved on, since POS_STORE writes the current planet's slots).
    /// <para>
    /// The file's own folder is the old RaCMAN folder, and the old client kept its save files and
    /// its mods in it, so those come across at the same time. They are files on this PC and need
    /// nothing plugged in.
    /// </para>
    /// </summary>
    private static void DrawImport(AppState state)
    {
        ImGui.SetNextItemWidth(ImportPathWidth);

        // Enter in the path box is the Import button at the bottom of this section, which is the
        // only thing the panel does with a path. Every return between here and it is a file that
        // holds nothing to import, so a press on one of those is a press on a button that is not
        // drawn: nothing.
        bool import = Ui.SubmitTextWithHint("##legacy-config", "C:\\RaCMAN\\config.txt", ref _importPath, 512);
        ImGui.SameLine();
        ImGui.BeginDisabled(!FileDialog.IsSupported || _dialogOpen);
        if (ImGui.Button("Browse...")) Browse(state);
        ImGui.EndDisabled();

        ParseIfChanged();

        if (_parseError is not null)
        {
            Ui.Error(_parseError);
            return;
        }

        if (_parsed is null)
        {
            if (_importPath.Trim().Length > 0) Ui.Hint("No such file.");
            return;
        }

        var config = _parsed;
        CountPositions(state, config);

        if (!config.HasAnything && !config.HasSavedPositions && !_found.Anything)
        {
            Ui.Hint("That file has nothing this client can use, and there are no save files or mods beside it.");
            return;
        }

        ImGui.Spacing();

        if (config.HasAnything)
        {
            ImGui.TextUnformatted(config.Ip is { } ip ? $"Console IP: {ip}" : "No console IP in the file.");

            ImGui.TextUnformatted(config.HasComboKeys ? "Combos:" : "Combos (the file has none set; these are the old RaCMAN defaults):");
            ImGui.Indent();
            foreach (var combo in config.Combos)
            {
                string note = combo.WasDefault && config.HasComboKeys ? "  (RaCMAN default)" : string.Empty;
                Ui.Hint($"{CombosPanel.Label(combo.Action)} = {PadButtons.Describe(combo.Mask)}{note}");
            }
            ImGui.Unindent();

            var mods = config.ModAutoByTitle;
            string title = state.Session.TitleId ?? string.Empty;
            if (mods.Count > 0)
            {
                if (title.Length > 0 && mods.TryGetValue(title, out var here))
                {
                    ImGui.TextUnformatted($"Auto-apply mods for {title}: {string.Join(", ", here)}");
                }

                int others = mods.Count - (title.Length > 0 && mods.ContainsKey(title) ? 1 : 0);
                if (others > 0)
                {
                    Ui.Hint($"Auto-apply mod lists for {others} other title(s) are only imported while that game is running; "
                            + "start it and import again.");
                }
            }

            var slots = config.ColourSlots;
            if (slots.Count > 0)
            {
                ImGui.TextUnformatted($"{slots.Count} chargeboot colour slot(s)");
                Ui.Hint("Saved as colour presets named \"RaCMAN slot N\" for both RaC2 and RaC3, since the old picker was "
                        + "shared by the two games.");
            }
        }

        DrawPositionSurvey(state, config);

        // What is in the folder the file sits in, which is the old RaCMAN folder itself.
        if (_found.Anything || _found.Excluded > 0)
        {
            ImGui.TextUnformatted($"In that folder: {_found.Describe()}");

            if (LegacyModExclusions.Shipped.Problem is { } problem) Ui.DebugHint(problem);
        }

        if (!state.Connected && (config.HasAnything || config.HasSavedPositions))
        {
            Ui.Warning("Not connected: the IP, the colour slots, the save files and the mods are imported now. "
                       + "Connect to import the combos, the mod flags and the saved positions.");
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(_importing);
        if (ImGui.Button("Import") || (import && !_importing)) Import(state, config);
        ImGui.EndDisabled();
    }

    /// <summary>
    /// Counts the saved positions in the file, which is a walk of every planet the running game
    /// has, and works out what a press would do to the planet the console is on, which is not.
    /// </summary>
    private static void CountPositions(AppState state, LegacyConfig config)
    {
        var game = state.DescribedGame;
        var counted = (_parsedPath, game);
        if (counted != _positionsCountedFor)
        {
            _positionsCountedFor = counted;
            _positionsFound = game == GameId.None
                ? Array.Empty<LegacyLibraryImport.LegacyPosition>()
                : LegacyLibraryImport.Positions(config, game);
        }

        _positionPlan = game == GameId.None
            ? LegacyLibraryImport.PositionPlan.Nothing
            : LegacyLibraryImport.PlanPositions(config, game, state.Positions);
    }

    /// <summary>
    /// The saved positions, which were in the config.txt itself rather than beside it. They belong
    /// on the console now, and POS_STORE writes the slots of the planet the console has loaded and
    /// carries no planet of its own, so this half needs a connection, the game the positions belong
    /// to, and that planet: the rest of the file waits for the planet it was saved on.
    /// </summary>
    private static void DrawPositionSurvey(AppState state, LegacyConfig config)
    {
        if (!config.HasSavedPositions) return;

        var game = state.DescribedGame;
        if (game == GameId.None)
        {
            Ui.Hint("Saved positions: start one of the four games to see what the file holds for it.");
            return;
        }

        int planets = _positionsFound.Select(p => p.Planet).Distinct().Count();
        ImGui.TextUnformatted($"Saved positions for {game.DisplayName()}: {_positionsFound.Count} "
                              + $"on {planets} planet{(planets == 1 ? string.Empty : "s")}");

        Ui.Hint(state.Ingame && state.Positions.Slots.Length > 0
            ? "Only the planet your console has loaded is imported, and a slot that already holds a "
              + $"position is left alone. {_positionPlan.Describe()}. Load another planet and import "
              + "again for the rest."
            : "They are imported into the planet your console has loaded, so connect, start the game "
              + "and go to the planet they were saved on.");
    }

    private static void ParseIfChanged()
    {
        string path = _importPath.Trim().Trim('"');
        if (path == _parsedPath) return;

        _parsedPath = path;
        _parsed = null;
        _parseError = null;
        _found = LegacyLibraryImport.Survey.Nothing;
        if (path.Length == 0 || !File.Exists(path)) return;

        try
        {
            _parsed = LegacyConfig.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _parseError = $"Could not read it: {ex.Message}";
        }

        // Counting the two libraries walks the old folder, so it happens here, with the parse, and
        // not on every frame the section is drawn.
        _found = LegacyLibraryImport.Look(OldFolder(), LegacyModExclusions.Shipped);
    }

    /// <summary>The folder the config.txt sits in, which is the folder the old racman.exe sat in.</summary>
    private static string OldFolder() =>
        System.IO.Path.GetDirectoryName(_parsedPath) is { Length: > 0 } folder ? folder : string.Empty;

    private static void Browse(AppState state)
    {
        _dialogOpen = true;
        _ = Task.Run(async () =>
        {
            try
            {
                string? picked = await FileDialog.OpenAsync("The old RaCMAN config.txt", "Text files", "txt")
                    .ConfigureAwait(false);
                state.Post(() =>
                {
                    _dialogOpen = false;
                    if (picked is not null) _importPath = picked;
                });
            }
            catch (Exception ex)
            {
                state.Post(() =>
                {
                    _dialogOpen = false;
                    state.AddToast($"File dialog failed: {ex.Message}", ToastKind.Error);
                });
            }
        });
    }

    /// <summary>
    /// Everything the file and its folder hold, in one go. The IP and the colour slots are this
    /// client's own files and are written here and now; the two libraries are a file copy and go
    /// off the render thread, because a savefile library is megabytes; only the combos and the mod
    /// flags need a console, and they are the one part a disconnected import leaves for later.
    /// </summary>
    private static void Import(AppState state, LegacyConfig config)
    {
        var settings = state.Settings;
        var done = new List<string>();

        if (config.Ip is { } ip)
        {
            settings.Ps3.Host = ip;
            settings.Save();
            ConnectionPanel.UseHost(ip);
            done.Add($"IP {ip}");
        }

        int presets = ImportColourSlots(state, config);
        if (presets > 0) done.Add($"{presets} colour preset(s) for RaC2 and RaC3");

        string folder = OldFolder();
        bool connected = state.Connected;

        // A file with nothing in it at all is not an instruction to set the old defaults on the
        // console; it is a config.txt that happens to sit beside a library worth importing.
        var combos = config.HasAnything ? config.Combos : Array.Empty<LegacyConfig.ComboImport>();

        string title = state.Session.TitleId ?? string.Empty;
        var wanted = connected && title.Length > 0 && config.ModAutoByTitle.TryGetValue(title, out var list)
            ? list
            : Array.Empty<string>();
        var known = state.ConsoleMods.Select(m => m.DirName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var flagged = wanted.Where(known.Contains).ToArray();
        var missing = wanted.Where(f => !known.Contains(f)).ToArray();

        // The planet the console is on, as the last POS_LIST described it. Without a game running
        // there is no planet to write into and the positions stay in the file for next time.
        var positions = connected && state.Ingame
            ? _positionPlan
            : LegacyLibraryImport.PositionPlan.Nothing;

        _importing = true;
        state.Run(async () =>
        {
            try
            {
                var copied = await Task.Run(() => LegacyLibraryImport.Run(
                        folder, AppPaths.SaveFiles, AppPaths.Mods, AppPaths.ShippedMods,
                        LegacyModExclusions.Shipped))
                    .ConfigureAwait(false);

                state.Post(() =>
                {
                    done.AddRange(copied.Lines);
                    if (copied.Mods > 0) state.RescanLocalMods();
                    if (copied.Saves > 0) SaveFilesPanel.Invalidate();
                });

                if (connected)
                {
                    foreach (var combo in combos)
                    {
                        await state.Client.ComboSetAsync(combo.Action, combo.Mask).ConfigureAwait(false);
                    }

                    foreach (var dir in flagged)
                    {
                        await state.Client.ModSetAutoAsync(dir, true).ConfigureAwait(false);
                    }

                    // One slot at a time, and a refusal is counted rather than thrown: a blob the
                    // running game will not take says nothing about the next slot, and the file
                    // keyed its positions by planet name alone, so another game's Orxon can land
                    // in here and be refused for its length.
                    int stored = 0;
                    Status? refused = null;
                    foreach (var position in positions.Store)
                    {
                        try
                        {
                            await state.Client.PosStoreAsync(position.Slot, position.Blob).ConfigureAwait(false);
                            stored++;
                        }
                        catch (QwarkStatusException ex)
                        {
                            refused ??= ex.Status;
                        }
                    }

                    state.Post(() =>
                    {
                        if (combos.Count > 0) done.Add($"{combos.Count} combos");
                        if (flagged.Length > 0) done.Add($"{flagged.Length} mod auto flag(s)");
                        if (stored > 0)
                        {
                            done.Add($"{stored} saved position(s) into {positions.PlanetName}");
                            state.RefreshPositions();
                        }

                        int left = positions.Store.Count - stored;
                        if (left > 0)
                        {
                            state.AddToast(
                                $"Your console refused {left} position(s) for {positions.PlanetName}: {refused}",
                                ToastKind.Error);
                        }

                        state.RefreshCombos();
                        state.RefreshMods();
                    });
                }

                state.Post(() =>
                {
                    if (done.Count == 0)
                    {
                        state.AddToast("Nothing imported: nothing this client can use", ToastKind.Error);
                        return;
                    }

                    // Only the combos and the mod flags are left for a console; a folder whose
                    // config.txt held nothing has nothing waiting on one.
                    state.AddToast(!connected && combos.Count > 0
                        ? $"Imported {string.Join(", ", done)}; connect for combos and mod flags"
                        : $"Imported {string.Join(", ", done)}",
                        ToastKind.Success);

                    if (missing.Length > 0)
                    {
                        state.AddToast($"Not flagged, not on your console yet: {string.Join(", ", missing)}");
                    }
                });
            }
            finally
            {
                state.Post(() => _importing = false);
            }
        });
    }

    /// <summary>
    /// The old chargeboot picker's slots, saved as named presets for RaC2 and RaC3 both: one picker
    /// drove the two games, and the presets are keyed by game, so a slot has to land in each file.
    /// Returns how many slots were written; a file that cannot be written is a toast, not a throw.
    /// </summary>
    private static int ImportColourSlots(AppState state, LegacyConfig config)
    {
        var slots = config.ColourSlots;
        if (slots.Count == 0) return 0;

        int written = 0;
        foreach (var slot in slots)
        {
            var colours = new[]
            {
                new KeyValuePair<string, uint>(ColourPresetStore.ChargebootFront, slot.Front),
                new KeyValuePair<string, uint>(ColourPresetStore.ChargebootBack, slot.Back),
                new KeyValuePair<string, uint>(ColourPresetStore.ChargebootTint, slot.Tint),
            };

            try
            {
                state.ColourPresets.Save(GameId.Rac2, $"RaCMAN slot {slot.Slot}", colours);
                state.ColourPresets.Save(GameId.Rac3, $"RaCMAN slot {slot.Slot}", colours);
                written++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                state.AddToast($"Colour slot {slot.Slot} not saved: {ex.Message}", ToastKind.Error);
            }
        }

        // The Game panel lists the presets once per game and section, so make it list them again.
        if (written > 0) GamePanel.InvalidatePresets();

        return written;
    }

    // ---------------------------------------------------------------- helpers

    private static void SetTheme(AppState state, string theme)
    {
        state.Settings.Theme = theme;
        state.Settings.Save();
        state.ThemeDirty = true;
    }

    /// <summary>The folder a file sits in, for the buttons that lead to a file rather than a folder.</summary>
    private static string FolderOf(string file) =>
        System.IO.Path.GetDirectoryName(file) is { Length: > 0 } folder ? folder : AppPaths.Root;
}
