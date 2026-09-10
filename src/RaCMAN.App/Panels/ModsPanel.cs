using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

public static class ModsPanel
{
    private static string _zipPath = string.Empty;
    private static ZipCandidate? _pendingZip;
    private static string _selected = string.Empty;

    /// <summary>True while a native file dialog is up, so Browse cannot open a second one.</summary>
    private static bool _dialogOpen;

    public static void Draw(AppState state)
    {
        Ui.Heading("Mods");

        // Every mod is patch words, code caves or both, so on a platform that refuses code patches
        // the whole panel is a library you can keep up to date and not load. Uploading still
        // works, which is what keeps the console's copy current for the next PS3 session.
        if (state.CodePatchesUnsupported) Ui.Warning(Ui.ModsAreCodePatches);

        // The described title: between sessions the library is still the one whose game just quit,
        // and the buttons that need a console are disabled below whether or not one is running.
        string title = string.IsNullOrEmpty(state.Session.TitleId) ? state.DescribedTitle : state.Session.TitleId;
        if (string.IsNullOrEmpty(title))
        {
            Ui.Hint("No game is running, so there is no mod library to show.");
            return;
        }

        // The library is a button that opens the folder rather than the path printed out: the path
        // is absolute and long enough to need wrapping, and reading it was never the point.
        if (ImGui.SmallButton("Rescan library")) state.RescanLocalMods();
        ImGui.SameLine();
        ImGui.BeginDisabled(!state.Connected);
        if (ImGui.SmallButton("Rescan console folder"))
        {
            state.Run(async () =>
            {
                await state.Client.ModRescanAsync();
                state.Post(() => state.RefreshMods());
            }, "Console rescanned its mod folder");
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        Ui.OpenFolderButton(state, Path.Combine(state.Mods.RootPath, title));

        ImGui.Spacing();

        var console = state.ConsoleMods.ToDictionary(m => m.DirName, StringComparer.OrdinalIgnoreCase);
        var locals = state.LocalMods;

        if (locals.Count == 0)
        {
            Ui.Hint("Neither your mods folder nor the one that ships with RaCMAN Reloaded has a mod for this "
                    + "title. Install one from a ZIP below.");
        }
        else if (ImGui.BeginTable("mods", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            // Mod is the only stretching column, so every pixel the fixed ones do not need is a
            // pixel of mod name that survives; at the default window width they are the difference
            // between "Incremental RNG" and "Incremental R". The author had a column of its own and
            // it cost the name 90 of those pixels, so it moved to the name's tooltip and to the
            // details below, which have room for it.
            ImGui.TableSetupColumn("Mod");
            ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Console", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("Auto", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableHeadersRow();

            foreach (var mod in locals)
            {
                console.TryGetValue(mod.DirName, out var remote);
                ImGui.TableNextRow();
                ImGui.PushID(mod.DirName);

                ImGui.TableNextColumn();

                // A name too long for the column takes a second line and the row grows for it,
                // which is the difference between reading "Incremental RNG" and reading
                // "Incremental R". A third line is where a table stops being a table, so the
                // second one ends in an ellipsis and the tooltip has the rest.
                string label = WrapName(mod.Name, ImGui.GetContentRegionAvail().X, MeasureText);

                // SpanAllColumns makes the whole row select, but without AllowOverlap the selectable
                // sits on top of the Auto checkbox and the Load/Upload buttons and eats their clicks.
                // The id after the ## is the mod's, so wrapping the name differently as the window
                // is resized does not make it a different item.
                if (ImGui.Selectable($"{label}##name", _selected == mod.DirName,
                        ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap))
                {
                    _selected = mod.DirName;
                }

                // The name is also where the author is read now, and where a name too long for the
                // column can be read in full. The row's selectable spans every column, so the
                // tooltip waits for the pointer to settle rather than following it across the row.
                // Built by hand rather than through SetTooltip, which is printf underneath: a mod
                // called "100% Speed" would otherwise lose the per cent and what follows it.
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(TooltipFor(mod));
                    ImGui.EndTooltip();
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrEmpty(mod.Version) ? "-" : mod.Version);

                ImGui.TableNextColumn();
                DrawConsoleState(mod, remote);

                ImGui.TableNextColumn();
                bool auto = remote?.Auto ?? false;
                ImGui.BeginDisabled(remote is null || !state.Connected);
                if (ImGui.Checkbox("##auto", ref auto))
                {
                    string dir = mod.DirName;
                    bool wanted = auto;
                    state.Run(async () =>
                    {
                        await state.Client.ModSetAutoAsync(dir, wanted);
                        state.Post(() => state.RefreshMods());
                    });
                }

                ImGui.EndDisabled();

                ImGui.TableNextColumn();
                DrawActions(state, title, mod, remote);

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        DrawDetails(state, locals, console);
        DrawZipInstall(state, title);
    }

    /// <summary>How wide a piece of text is on screen. The measure WrapName uses in the panel.</summary>
    private static float MeasureText(string text) => ImGui.CalcTextSize(text).X;

    /// <summary>
    /// A mod name as the table draws it: one line while it fits, two when it does not, and never a
    /// third. The break is at a space where there is one, and what will not fit on the second line
    /// ends in an ellipsis; the row's tooltip carries the name in full either way.
    /// <para>
    /// <paramref name="measure"/> is how wide a string is, which is ImGui.CalcTextSize in the panel
    /// and a stand-in in the tests, so the wrapping itself can be checked without a window.
    /// </para>
    /// </summary>
    public static string WrapName(string? name, float width, Func<string, float> measure)
    {
        string text = (name ?? string.Empty).Trim();
        if (text.Length == 0 || width <= 0f || measure(text) <= width) return text;

        int broke = BreakAt(text, width, measure);
        string first = text[..broke].TrimEnd();
        string rest = text[broke..].TrimStart();

        if (rest.Length == 0) return first;
        if (measure(rest) <= width) return first + "\n" + rest;

        // The second line is the last one, so what is left of the name is cut to fit an ellipsis.
        int keep = Fits(rest, width, measure, "...");
        return first + "\n" + rest[..keep].TrimEnd() + "...";
    }

    /// <summary>Where the first line ends: on the last space that fits, or mid-word when there is none.</summary>
    private static int BreakAt(string text, float width, Func<string, float> measure)
    {
        int fits = Fits(text, width, measure, string.Empty);
        if (fits >= text.Length) return text.Length;

        int space = text.LastIndexOf(' ', fits);
        return space > 0 ? space : Math.Max(1, fits);
    }

    /// <summary>
    /// How many characters of <paramref name="text"/> fit in <paramref name="width"/> with
    /// <paramref name="suffix"/> after them. A binary search, so a long name costs a handful of
    /// measurements rather than one per character.
    /// </summary>
    private static int Fits(string text, float width, Func<string, float> measure, string suffix)
    {
        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (measure(text[..mid] + suffix) <= width) low = mid;
            else high = mid - 1;
        }

        return low;
    }

    /// <summary>
    /// What hovering a row's name says: the full name, which the column may have clipped, the
    /// author, which no longer has a column of its own, and which library it came out of. An
    /// unnamed author is left out rather than spelled "by -".
    /// </summary>
    public static string TooltipFor(LocalMod mod)
    {
        string text = string.IsNullOrWhiteSpace(mod.Author) ? mod.Name : $"{mod.Name}\nby {mod.Author}";
        return text + (mod.Shipped ? "\nShips with RaCMAN Reloaded" : "\nIn your mods folder");
    }

    private static void DrawConsoleState(LocalMod mod, ModEntry? remote)
    {
        if (remote is null)
        {
            ImGui.TextColored(Ui.Grey, "not uploaded");
            return;
        }

        var parts = new List<string>();
        if (remote.Loaded) parts.Add("loaded");
        if (remote.Previous) parts.Add("previous");
        if (remote.NeedsLua) parts.Add("needs Lua");
        if (remote.ParseError) parts.Add("parse error");
        if (remote.Hash != mod.Hash) parts.Add("stale");

        var colour = remote.Loaded ? Ui.Green : remote.ParseError ? Ui.Red : Ui.Grey;
        ImGui.TextColored(colour, parts.Count == 0 ? "ready" : string.Join(", ", parts));
    }

    private static void DrawActions(AppState state, string title, LocalMod mod, ModEntry? remote)
    {
        ImGui.BeginDisabled(!state.Connected);

        bool loaded = remote?.Loaded ?? false;
        if (!loaded)
        {
            // MOD_LOAD is answered UNSUPPORTED for a mod with patch words or caves on a platform
            // without code patches, which is every mod: the button is greyed out rather than left
            // to produce an error toast.
            bool blocked = state.CodePatchesUnsupported;
            ImGui.BeginDisabled(blocked);
            bool load = ImGui.SmallButton("Load");
            ImGui.EndDisabled();
            if (blocked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Ui.NoCodePatches);

            if (load)
            {
                var library = state.Mods;
                var target = mod;
                uint consoleHash = remote?.Hash ?? 0;
                state.Run(async () =>
                {
                    await library.EnsureUploadedAsync(state.Client, title, target, consoleHash,
                        new Progress<string>(message => state.Post(() => state.AddToast(message))));
                    await state.Client.ModLoadAsync(target.DirName);
                    state.Post(() => state.RefreshMods());
                }, $"{mod.Name} loaded");
            }
        }
        else if (ImGui.SmallButton("Unload"))
        {
            string dir = mod.DirName;
            state.Run(async () =>
            {
                await state.Client.ModUnloadAsync(dir);
                state.Post(() => state.RefreshMods());
            }, $"{mod.Name} unloaded");
        }

        // Load uploads on its own when the console's copy is missing or stale, so a manual
        // upload is only a debugging tool; it stays behind the debug switch.
        if (!Ui.Debug)
        {
            ImGui.EndDisabled();
            return;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Upload"))
        {
            var library = state.Mods;
            var target = mod;
            state.Run(async () =>
            {
                bool changed = await library.EnsureUploadedAsync(state.Client, title, target, 0,
                    new Progress<string>(message => state.Post(() => state.AddToast(message))));
                state.Post(() =>
                {
                    state.AddToast(changed ? $"{target.Name} uploaded" : $"{target.Name} was already current");
                    state.RefreshMods();
                });
            });
        }

        ImGui.EndDisabled();
    }

    private static void DrawDetails(AppState state, IReadOnlyList<LocalMod> locals, Dictionary<string, ModEntry> console)
    {
        var mod = locals.FirstOrDefault(m => m.DirName == _selected);
        if (mod is null) return;

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading(mod.Name);

        ImGui.Text($"Folder: {mod.DirName}  ({(mod.Shipped ? "ships with RaCMAN Reloaded" : "yours")})");
        ImGui.Text($"Version: {(string.IsNullOrEmpty(mod.Version) ? "-" : mod.Version)}    Author: {(string.IsNullOrEmpty(mod.Author) ? "-" : mod.Author)}");

        // Word counts, code caves and checksums say nothing to someone who only wants the mod on;
        // they are what you look at when a mod misbehaves, which is what the debug switch is for.
        // The console column already says whether the console's copy is stale.
        if (Ui.Debug)
        {
            ImGui.Text($"Patch words: {mod.PatchWordCount}    Code caves: {mod.BinFiles.Count}    CRC32: {Crc32.ToSumText(mod.Hash)}");

            if (console.TryGetValue(mod.DirName, out var remote))
            {
                ImGui.Text($"Console hash: {Crc32.ToSumText(remote.Hash)}{(remote.Hash == mod.Hash ? " (current)" : " (differs, will re-upload)")}");
            }
        }

        if (mod.NeedsLua) Ui.Warning("This mod has a Lua automation; only its patches are applied.");
        if (!string.IsNullOrEmpty(mod.Link)) Ui.Hint(mod.Link);

        if (!string.IsNullOrEmpty(mod.Description))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(mod.Description);
        }

        // Every mod in this table is installed on this PC, so its description is the one already
        // shown above, read out of the local patch.txt. MOD_INFO only answers what the console
        // parsed out of its own copy: a wire check, not something to offer while playing.
        if (!Ui.Debug) return;

        ImGui.Spacing();
        ImGui.BeginDisabled(!state.Connected);
        if (ImGui.Button("Read description from console"))
        {
            string dir = mod.DirName;
            state.Run(() => state.Client.ModInfoAsync(dir), info => state.AddToast(string.IsNullOrWhiteSpace(info) ? "(no description)" : info));
        }

        ImGui.EndDisabled();
    }

    /// <summary>
    /// The quotes a file manager's "copy as path" leaves around a path, and the spaces around
    /// them, are not part of the path. An empty result means nothing was picked, which is the one
    /// case the install must refuse: ZipFile would throw ArgumentException at it.
    /// </summary>
    public static string NormalizeZipPath(string? text) => (text ?? string.Empty).Trim().Trim('"').Trim();

    private static void DrawZipInstall(AppState state, string title)
    {
        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Install from ZIP");

        ImGui.SetNextItemWidth(-250);
        Ui.InputTextWithHint("##zip", "C:\\downloads\\some-mod.zip", ref _zipPath, 512);

        ImGui.SameLine();
        ImGui.BeginDisabled(!FileDialog.IsSupported || _dialogOpen);
        if (ImGui.Button("Browse...")) Browse(state, title);
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Install ZIP")) InstallZip(state, title);

        if (!FileDialog.IsSupported) Ui.Hint("No file dialog available; paste the path.");

        if (_pendingZip is null) return;

        ImGui.OpenPopup("Replace mod?");
        var centre = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        bool open = true;
        if (!ImGui.BeginPopupModal("Replace mod?", ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;

        var candidate = _pendingZip;
        ImGui.Text(candidate.Kind == ZipInstallKind.Downgrade
            ? $"The installed {candidate.Installed?.Name} is version {candidate.Installed?.Version}, newer than the ZIP's {candidate.Mod.Version}."
            : $"{candidate.Mod.Name} {candidate.Mod.Version} is already installed.");

        ImGui.Spacing();
        if (ImGui.Button(candidate.Kind == ZipInstallKind.Downgrade ? "Downgrade" : "Replace"))
        {
            try
            {
                var installed = state.Mods.CommitZip(candidate, title);
                state.AddToast($"{installed.Name} {installed.Version} installed", ToastKind.Success);
                state.RescanLocalMods();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                state.AddToast($"ZIP install failed: {ex.Message}", ToastKind.Error);
            }

            candidate.Dispose();
            _pendingZip = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            candidate.Dispose();
            _pendingZip = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    /// <summary>
    /// Extracts what the path box points at, asking first when the mod is already installed. Both
    /// the Install button and a successful Browse land here, so picking a file installs it.
    /// </summary>
    private static void InstallZip(AppState state, string title)
    {
        string path = NormalizeZipPath(_zipPath);
        if (path.Length == 0)
        {
            state.AddToast("Pick a ZIP file first", ToastKind.Error);
            return;
        }

        state.Settings.LastZipPath = _zipPath;
        state.Settings.Save();

        try
        {
            _pendingZip?.Dispose();
            _pendingZip = state.Mods.OpenZip(path, title);

            if (!_pendingZip.NeedsConfirmation)
            {
                var installed = state.Mods.CommitZip(_pendingZip, title);
                state.AddToast($"{installed.Name} {installed.Version} installed", ToastKind.Success);
                _pendingZip.Dispose();
                _pendingZip = null;
                state.RescanLocalMods();
            }
        }
        // ArgumentException and NotSupportedException are what Path and ZipFile throw at a path
        // that is not one; they used to reach the top of the render loop and close the window.
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            state.AddToast($"ZIP install failed: {ex.Message}", ToastKind.Error);
            _pendingZip?.Dispose();
            _pendingZip = null;
        }
    }

    /// <summary>
    /// Opens the native picker. The dialog runs off the render thread and the answer comes back
    /// through <see cref="AppState.Post"/>, so the path box and the install only ever move on the
    /// render thread; <see cref="_dialogOpen"/> keeps a second dialog behind the first.
    /// </summary>
    private static void Browse(AppState state, string title)
    {
        if (!FileDialog.IsSupported)
        {
            state.AddToast("No file dialog available; paste the path", ToastKind.Error);
            return;
        }

        _dialogOpen = true;
        _ = Task.Run(async () =>
        {
            try
            {
                string? picked = await FileDialog.OpenAsync("Install a mod from a ZIP", "ZIP files", "zip")
                    .ConfigureAwait(false);

                state.Post(() =>
                {
                    _dialogOpen = false;
                    if (picked is null) return;

                    _zipPath = picked;
                    InstallZip(state, title);
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
}
