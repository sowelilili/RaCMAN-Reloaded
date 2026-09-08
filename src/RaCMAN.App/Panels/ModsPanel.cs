using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

public static class ModsPanel
{
    private static string _zipPath = string.Empty;
    private static ZipCandidate? _pendingZip;
    private static string _selected = string.Empty;

    public static void Draw(AppState state)
    {
        Ui.Heading("Mods");

        string title = state.Session.TitleId;
        if (string.IsNullOrEmpty(title))
        {
            Ui.Hint("No game is running, so there is no mod library to show.");
            return;
        }

        ImGui.Text($"Library: {Path.Combine(state.Mods.RootPath, title)}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Rescan library")) state.RescanLocalMods();
        ImGui.SameLine();
        ImGui.BeginDisabled(!state.Connected);
        if (ImGui.SmallButton("Rescan console folder"))
        {
            state.Run(async () =>
            {
                await state.Client.ModRescanAsync();
                state.Post(state.RefreshMods);
            }, "Console rescanned its mod folder");
        }

        ImGui.EndDisabled();

        ImGui.Spacing();

        var console = state.ConsoleMods.ToDictionary(m => m.DirName, StringComparer.OrdinalIgnoreCase);
        var locals = state.LocalMods;

        if (locals.Count == 0)
        {
            Ui.Hint("The local library has no mods for this title. Install one from a ZIP below.");
        }
        else if (ImGui.BeginTable("mods", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Mod");
            ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Author", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Console", ImGuiTableColumnFlags.WidthFixed, 160);
            ImGui.TableSetupColumn("Auto", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 170);
            ImGui.TableHeadersRow();

            foreach (var mod in locals)
            {
                console.TryGetValue(mod.DirName, out var remote);
                ImGui.TableNextRow();
                ImGui.PushID(mod.DirName);

                ImGui.TableNextColumn();
                // SpanAllColumns makes the whole row select, but without AllowOverlap the selectable
                // sits on top of the Auto checkbox and the Load/Upload buttons and eats their clicks.
                if (ImGui.Selectable(mod.Name, _selected == mod.DirName,
                        ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap))
                {
                    _selected = mod.DirName;
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrEmpty(mod.Version) ? "-" : mod.Version);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrEmpty(mod.Author) ? "-" : mod.Author);

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
                        state.Post(state.RefreshMods);
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
            if (ImGui.SmallButton("Load"))
            {
                var library = state.Mods;
                var target = mod;
                uint consoleHash = remote?.Hash ?? 0;
                state.Run(async () =>
                {
                    await library.EnsureUploadedAsync(state.Client, title, target, consoleHash,
                        new Progress<string>(message => state.Post(() => state.AddToast(message))));
                    await state.Client.ModLoadAsync(target.DirName);
                    state.Post(state.RefreshMods);
                }, $"{mod.Name} loaded");
            }
        }
        else if (ImGui.SmallButton("Unload"))
        {
            string dir = mod.DirName;
            state.Run(async () =>
            {
                await state.Client.ModUnloadAsync(dir);
                state.Post(state.RefreshMods);
            }, $"{mod.Name} unloaded");
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

        ImGui.Text($"Folder: {mod.DirName}");
        ImGui.Text($"Version: {(string.IsNullOrEmpty(mod.Version) ? "-" : mod.Version)}    Author: {(string.IsNullOrEmpty(mod.Author) ? "-" : mod.Author)}");
        ImGui.Text($"Patch words: {mod.PatchWordCount}    Code caves: {mod.BinFiles.Count}    CRC32: {Crc32.ToSumText(mod.Hash)}");

        if (console.TryGetValue(mod.DirName, out var remote))
        {
            ImGui.Text($"Console hash: {Crc32.ToSumText(remote.Hash)}{(remote.Hash == mod.Hash ? " (current)" : " (differs, will re-upload)")}");
        }

        if (mod.NeedsLua) ImGui.TextColored(Ui.Yellow, "This mod has a Lua automation; only its patches are applied.");
        if (!string.IsNullOrEmpty(mod.Link)) ImGui.TextColored(Ui.Grey, mod.Link);

        if (!string.IsNullOrEmpty(mod.Description))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(mod.Description);
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(!state.Connected);
        if (ImGui.Button("Read description from console"))
        {
            string dir = mod.DirName;
            state.Run(() => state.Client.ModInfoAsync(dir), info => state.AddToast(string.IsNullOrWhiteSpace(info) ? "(no description)" : info));
        }

        ImGui.EndDisabled();
    }

    private static void DrawZipInstall(AppState state, string title)
    {
        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Install from ZIP");
        Ui.Hint("Paste a path: a portable file dialog is a dependency this client does not carry.");

        ImGui.SetNextItemWidth(-160);
        Ui.InputTextWithHint("##zip", "C:\\downloads\\some-mod.zip", ref _zipPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("Install ZIP"))
        {
            state.Settings.LastZipPath = _zipPath;
            state.Settings.Save();
            try
            {
                _pendingZip?.Dispose();
                _pendingZip = state.Mods.OpenZip(_zipPath.Trim('"'), title);

                if (!_pendingZip.NeedsConfirmation)
                {
                    var installed = state.Mods.CommitZip(_pendingZip, title);
                    state.AddToast($"{installed.Name} {installed.Version} installed", ToastKind.Success);
                    _pendingZip.Dispose();
                    _pendingZip = null;
                    state.RescanLocalMods();
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                state.AddToast($"ZIP install failed: {ex.Message}", ToastKind.Error);
                _pendingZip?.Dispose();
                _pendingZip = null;
            }
        }

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
}
