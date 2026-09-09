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

    public static void Draw(AppState state)
    {
        var settings = state.Settings;

        Ui.Heading("Settings");

        ImGui.TextUnformatted("Theme");
        Ui.Hint("Applies straight away and is remembered.");
        ImGui.Spacing();

        bool light = settings.LightTheme;
        if (ImGui.RadioButton("Light", light) && !light) SetTheme(state, "light");
        ImGui.SameLine();
        if (ImGui.RadioButton("Dark", !light) && light) SetTheme(state, "dark");

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

        Ui.Hint("Shows the wire-level detail: qwark and protocol versions, the reboot and tick "
                + "counters, request names in error messages, frame rate, raw readouts and internal addresses.");

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Layout");

        if (ImGui.Button("Reload gamelayout.json"))
        {
            GameLayout.Invalidate();

            // Touching the layout re-reads the file now, so a broken edit is reported here
            // instead of on the next visit to the Game page.
            _ = GameLayout.SideSections;
            state.AddToast(
                GameLayout.Problems.Count == 0 ? "Game layout reloaded" : "Game layout reloaded with problems",
                GameLayout.Problems.Count == 0 ? ToastKind.Success : ToastKind.Error);
        }

        foreach (var problem in GameLayout.Problems) Ui.Warning(problem);
        Ui.Hint(GameLayout.DefaultPath);

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Import from RaCMAN");
        DrawImport(state);

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Files");

        Path("Settings file", string.IsNullOrEmpty(settings.Path) ? Settings.DefaultPath : settings.Path);
        Path("Mods folder", state.Mods.RootPath);
        Path("Save files folder", state.SaveFiles.RootPath);
    }

    // ---------------------------------------------------------------- import

    /// <summary>
    /// Point it at the old RaCMAN's config.txt, see what it holds, import. The IP goes into this
    /// client's settings; the combos and the mod auto-apply list go to the console, so those two
    /// need a connection (and the mod list needs the matching game running, since the console
    /// keeps that list per title).
    /// </summary>
    private static void DrawImport(AppState state)
    {
        Ui.Hint("Reads the old RaCMAN's config.txt (beside racman.exe): the console IP, the controller combos and "
                + "the auto-apply mod list. Saved positions and chargeboot colour slots are not imported.");

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
        if (!config.HasAnything)
        {
            Ui.Hint("That file has nothing this client can use.");
            return;
        }

        ImGui.Spacing();
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

        if (!state.Connected)
        {
            Ui.Warning("Not connected: only the IP will be imported now. Connect to import the combos and mod flags.");
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
        if (path.Length == 0 || !File.Exists(path)) return;

        try
        {
            _parsed = LegacyConfig.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _parseError = $"Could not read it: {ex.Message}";
        }
    }

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

        if (!state.Connected)
        {
            state.AddToast(done.Count > 0 ? $"Imported {string.Join(", ", done)}; connect to import the rest" : "Nothing imported: not connected",
                done.Count > 0 ? ToastKind.Success : ToastKind.Error);
            return;
        }

        var combos = config.Combos;
        string title = state.Session.TitleId ?? string.Empty;
        var wanted = title.Length > 0 && config.ModAutoByTitle.TryGetValue(title, out var list) ? list : Array.Empty<string>();
        var known = state.ConsoleMods.Select(m => m.DirName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var flagged = wanted.Where(known.Contains).ToArray();
        var missing = wanted.Where(f => !known.Contains(f)).ToArray();

        _importing = true;
        state.Run(async () =>
        {
            try
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
                    done.Add($"{combos.Count} combos");
                    if (flagged.Length > 0) done.Add($"{flagged.Length} mod auto flag(s)");
                    state.RefreshCombos();
                    state.RefreshMods();
                    state.AddToast($"Imported {string.Join(", ", done)}", ToastKind.Success);
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

    // ---------------------------------------------------------------- helpers

    private static void SetTheme(AppState state, string theme)
    {
        state.Settings.Theme = theme;
        state.Settings.Save();
        state.ThemeDirty = true;
    }

    private static void Path(string label, string value)
    {
        ImGui.TextUnformatted(label);
        Ui.Hint(value);
    }
}
