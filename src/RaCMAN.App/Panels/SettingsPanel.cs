using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// Local PC preferences only: how this client looks and where it keeps its files. Anything the
/// console owns (auto-reconnect aside, which is a client habit) stays on the Connection panel.
/// The one exception is the import from the old RaCMAN, which pushes combos and mod flags to the
/// console because that is where they live now.
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

    /// <summary>The LiveSplit endpoint while it is being typed; committed when the boxes are left.</summary>
    private static string _liveSplitHost = string.Empty;
    private static int _liveSplitPort;
    private static bool _portsRead;

    public static void Draw(AppState state)
    {
        var settings = state.Settings;

        Ui.Heading("Settings");

        ImGui.TextUnformatted("Theme");

        bool light = settings.LightTheme;
        if (ImGui.RadioButton("Light", light) && !light) SetTheme(state, "light");
        ImGui.SameLine();
        if (ImGui.RadioButton("Dark", !light) && light) SetTheme(state, "dark");

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Updates");
        DrawUpdates(state);

        // Then the import, because it is the one thing on this panel that is done once, on the
        // first run, and never looked at again: it belongs where somebody arriving from the old
        // RaCMAN will see it rather than below the settings they have come to change.
        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Import from RaCMAN");
        DrawImport(state);

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Connection");
        DrawConnection(settings);

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Ports");
        DrawPorts(state);

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Layout");

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

        ImGui.SameLine();
        Ui.OpenFolderButton(state, FolderOf(GameLayout.DefaultPath), GameLayout.DefaultPath);

        Ui.Hint(GameLayout.UsingOverride
            ? "Using a custom gamelayout.json override."
            : "Using the default gamelayout.json. Copy to the data folder to edit it.");

        foreach (var problem in GameLayout.Problems) Ui.Warning(problem);

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Files");
        DrawFiles(state, settings);

        // Last, because it is the one switch here that is about this client's own workings rather
        // than about the user's files, and because everything it reveals is elsewhere.
        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Debug");

        bool debug = settings.DebugInfo;
        if (ImGui.Checkbox("Show debug information", ref debug))
        {
            settings.DebugInfo = debug;
            settings.Save();
            state.ThemeDirty = true;
        }

        Ui.Hint("Shows debug information: qwark and protocol versions, reboot and tick "
                + "counters, request names in error messages, frame rate, raw readouts and internal addresses.");
    }

    // ---------------------------------------------------------------- connection

    /// <summary>
    /// How this client gets qwark onto a console, and how often the panels that watch live state
    /// read it back. Two settings about the link, in the order they are met: the mode decides what
    /// pressing Connect does at all, and the refresh rate is what a slow link is turned down for.
    /// </summary>
    private static void DrawConnection(Settings settings)
    {
        int mode = settings.StandaloneConnection ? 1 : 0;
        int chosen = mode;

        ImGui.TextUnformatted("Mode");
        ImGui.SameLine();
        ImGui.RadioButton("webMAN", ref chosen, 0);
        ImGui.SameLine();
        ImGui.RadioButton("Standalone", ref chosen, 1);

        if (chosen != mode)
        {
            settings.StandaloneConnection = chosen == 1;
            settings.Save();
        }

        Ui.Hint(chosen == 0
            ? "webMAN: automatically re-send and load the qwark SPRX using webMAN when connecting."
            : "Standalone: load the qwark SPRX manually, or install it to run on boot.");

        ImGui.Spacing();

        // The panels read this every frame, so a change here is live in the table you can see.
        float seconds = settings.TableRefreshSeconds;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputFloat("Refresh rate (Hz)", ref seconds, 0.1f, 1f, "%.1f"))
        {
            // The property clamps to 0..10, so a typed 99 or a typed -1 is still a period the
            // panels can use.
            settings.TableRefreshSeconds = seconds;
            settings.Save();
        }

        Ui.Hint("How often to refresh data tables. If you're using a slower connection, decrease this. Set to 0 for manual refresh.");
    }

    // ---------------------------------------------------------------- updates

    /// <summary>
    /// Which version this is, whether it looks for a newer one, and the button for asking now. A
    /// copy the installer did not put here cannot replace itself, so it says that instead of
    /// offering a check that could only end in a download link.
    /// </summary>
    private static void DrawUpdates(AppState state)
    {
        var updates = state.Updates;

        ImGui.TextUnformatted($"Version {AppVersion.Current}");

        bool check = state.Settings.Updates.Check;
        if (ImGui.Checkbox("Check for updates on start", ref check))
        {
            state.Settings.Updates.Check = check;
            state.Settings.Save();
        }

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

        if (updates.InertReason is { } reason) Ui.Hint(reason);
        else if (updates.Stage == UpdateStage.Failed) Ui.Error(updates.StatusLine());
        else Ui.Hint(updates.StatusLine());

        if (updates.Stage != UpdateStage.Idle && updates.LastCheckUtc is { } last)
        {
            Ui.Hint($"Last checked {last.ToLocalTime():yyyy-MM-dd HH:mm}.");
        }

        Ui.DebugHint(UpdateService.RepositoryUrl);
    }

    // ---------------------------------------------------------------- files

    /// <summary>
    /// The two folders and the difference between them, because it is the difference that matters:
    /// what is in the data folder is the user's and survives an update, and what is beside the
    /// executable is the release's and does not.
    /// </summary>
    /// <summary>The gap between the longest label and the column the buttons share.</summary>
    private const float FolderButtonGap = 16f;

    private static void DrawFiles(AppState state, Settings settings)
    {
        string settingsFile = string.IsNullOrEmpty(settings.Path) ? Settings.DefaultPath : settings.Path;

        // The labels are one column and the buttons another, at a single offset measured from the
        // longest label of the lot: a button per row at the end of its own label would leave the
        // section a ragged edge of nine Open folder buttons, and there is nothing to read down.
        var rows = new List<(string Label, string Folder, string? Tooltip)>
        {
            ("Data folder", AppPaths.Root, null),
            ("Settings file", FolderOf(settingsFile), settingsFile),
            ("Mods you installed", state.Mods.RootPath, null),
            ("Save files", state.SaveFiles.RootPath, null),
            ("Colour presets", state.ColourPresets.Folder, null),
            ("Watchlists", state.Watchlists.Folder, null),
        };

        float column = 0;
        foreach (var row in rows) column = Math.Max(column, ImGui.CalcTextSize(row.Label).X);
        column += FolderButtonGap;

        foreach (var row in rows) Folder(state, row.Label, row.Folder, column, row.Tooltip);

        ImGui.Spacing();

        // The savefile library lives on the console since qwark build 12; this is the second copy.
        bool mirror = settings.MirrorSaveFiles;
        if (ImGui.Checkbox("Mirror saves to this PC", ref mirror))
        {
            settings.MirrorSaveFiles = mirror;
            settings.Save();
        }

        Ui.Hint("Save files are kept on your console. With this on, every save you take is also copied "
                + "to this PC.");
    }

    // ---------------------------------------------------------------- ports

    /// <summary>
    /// The two ports nobody should have to think about: RPCS3's IPC server, which the helper is
    /// pointed at, and LiveSplit's TCP server. Both are typed once, if ever, so they live here and
    /// the panels that use them only name the value they are using.
    /// </summary>
    private static void DrawPorts(AppState state)
    {
        var settings = state.Settings;
        var autosplit = settings.Autosplit;

        int pine = settings.Rpcs3PinePort;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("RPCS3 IPC port", ref pine))
        {
            settings.Rpcs3PinePort = Math.Clamp(pine, 1, 65535);
            settings.Save();
        }

        Ui.Hint($"Where to look for RPCS3 (default: {Rpcs3Host.DefaultPinePort})");

        ImGui.Spacing();

        if (!_portsRead)
        {
            _liveSplitHost = autosplit.Host;
            _liveSplitPort = autosplit.Port;
            _portsRead = true;
        }

        ImGui.SetNextItemWidth(180);
        ImGui.InputText("LiveSplit host", ref _liveSplitHost, 64);
        bool editing = ImGui.IsItemActive();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("LiveSplit port", ref _liveSplitPort))
        {
            _liveSplitPort = Math.Clamp(_liveSplitPort, 1, 65535);
        }

        editing |= ImGui.IsItemActive();

        // Committed the moment both boxes are left alone: a half-typed host would otherwise send
        // the reconnect loop somewhere nobody asked for.
        bool changed = !string.Equals(_liveSplitHost.Trim(), autosplit.Host, StringComparison.OrdinalIgnoreCase)
                       || _liveSplitPort != autosplit.Port;
        if (changed && !editing) ApplyLiveSplit(state, autosplit);

        Ui.Hint($"Where to look for LiveSplit server (default: {LiveSplitClient.DefaultHost}:{LiveSplitClient.DefaultPort})");
    }

    /// <summary>Saves the LiveSplit endpoint and, while the autosplitter is on, points it at the new one.</summary>
    private static void ApplyLiveSplit(AppState state, AutosplitSettings autosplit)
    {
        string host = _liveSplitHost.Trim();
        autosplit.Host = host.Length == 0 ? LiveSplitClient.DefaultHost : host;
        autosplit.Port = _liveSplitPort;
        _liveSplitHost = autosplit.Host;
        state.Settings.Save();

        if (!autosplit.Enabled) return;

        // Start only restarts a connection that is now pointed somewhere else, and a failure there
        // is one the user asked for, so it earns the popup.
        LiveSplitModal.ArmForAttempt();
        state.LiveSplit.Start(autosplit.Host, autosplit.Port);
    }

    // ---------------------------------------------------------------- import

    /// <summary>
    /// Point it at the old RaCMAN's config.txt, see what it holds, import. The IP goes into this
    /// client's settings; the combos and the mod auto-apply list go to the console, so those two
    /// need a connection (and the mod list needs the matching game running, since the console
    /// keeps that list per title).
    /// <para>
    /// The file's own folder is the old RaCMAN folder, and the old client kept its save files and
    /// its mods in it, so those come across at the same time. They are files on this PC and need
    /// nothing plugged in.
    /// </para>
    /// </summary>
    private static void DrawImport(AppState state)
    {
        Ui.Hint("Import settings, save files and mods from previous versions of RaCMAN");

        ImGui.SetNextItemWidth(-110);
        Ui.InputTextWithHint("##legacy-config", "C:\\RaCMAN\\config.txt", ref _importPath, 512);
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
        if (!config.HasAnything && !_found.Anything)
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

        // What is in the folder the file sits in, which is the old RaCMAN folder itself.
        if (_found.Anything || _found.Excluded > 0)
        {
            ImGui.TextUnformatted($"In that folder: {_found.Describe()}");
            Ui.Hint("Copied into this client's own save file and mod folders. Nothing already here is replaced: a save "
                    + "you already have is left alone, and a mod folder of the same name is kept beside yours.");

            if (_found.Excluded > 0)
            {
                Ui.Hint("The excluded mods are the old defaults this client does not carry: the savefile helpers, which "
                        + "qwark does itself now, and the mods that drive a Lua script, which it cannot run. The import "
                        + "names each one and why.");
            }

            if (LegacyModExclusions.Shipped.Problem is { } problem) Ui.DebugHint(problem);
        }

        if (!state.Connected && config.HasAnything)
        {
            Ui.Warning("Not connected: the IP, the colour slots, the save files and the mods are imported now. "
                       + "Connect to import the combos and mod flags.");
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(_importing);
        if (ImGui.Button("Import")) Import(state, config);
        ImGui.EndDisabled();
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
            settings.LastHost = ip;
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

                    state.Post(() =>
                    {
                        if (combos.Count > 0) done.Add($"{combos.Count} combos");
                        if (flagged.Length > 0) done.Add($"{flagged.Length} mod auto flag(s)");
                        state.RefreshCombos();
                        state.RefreshMods();
                    });
                }

                state.Post(() =>
                {
                    if (done.Count == 0)
                    {
                        state.AddToast("Nothing imported: there was nothing this client can use", ToastKind.Error);
                        return;
                    }

                    // Only the combos and the mod flags are left for a console; a folder whose
                    // config.txt held nothing has nothing waiting on one.
                    state.AddToast(!connected && combos.Count > 0
                        ? $"Imported {string.Join(", ", done)}; connect to import the combos and mod flags"
                        : $"Imported {string.Join(", ", done)}",
                        ToastKind.Success);

                    if (missing.Length > 0)
                    {
                        state.AddToast($"Not on the console yet, so not flagged: {string.Join(", ", missing)}. Load each once, then import again.");
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

    /// <summary>
    /// One of this client's own places on disk: what it is, and a button that opens it. The path
    /// is on the button's tooltip, where a file has room to be named in full. The button goes at
    /// <paramref name="column"/>, an offset from the start of the panel's content, so every row in
    /// the section puts its button in the same place.
    /// </summary>
    private static void Folder(AppState state, string label, string folder, float column, string? tooltip = null)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(column);
        Ui.OpenFolderButton(state, folder, tooltip);
    }

    /// <summary>The folder a file sits in, for the buttons that lead to a file rather than a folder.</summary>
    private static string FolderOf(string file) =>
        System.IO.Path.GetDirectoryName(file) is { Length: > 0 } folder ? folder : AppPaths.Root;
}
