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

    /// <summary>The question the RPCS3 dialog was last opened for, so it is opened once per question.</summary>
    private static ModsConfirmation? _shownConfirmation;

    private const string ConfirmId = "###rpcs3-mods-confirm";

    /// <summary>The frame the panel was last drawn on, so a frame that does not follow it is the panel opening.</summary>
    private static int _lastDrawnFrame = int.MinValue;

    /// <summary>The uninstall the user picked from a row's menu, waiting for the answer to its question.</summary>
    private static UninstallPlan? _pendingUninstall;

    /// <summary>The plan the uninstall dialog was last opened for, so it is opened once per question.</summary>
    private static UninstallPlan? _shownUninstall;

    /// <summary>True while a confirmed uninstall is running; no other starts meanwhile.</summary>
    private static bool _uninstalling;

    private const string UninstallId = "###mods-uninstall";

    /// <summary>The rename the user picked from a row's menu, and the name it would take.</summary>
    private static (LocalMod Mod, string NewName)? _pendingRename;

    private static bool _renameShown;

    private const string RenameId = "Rename folder?###mods-rename";

    /// <summary>
    /// The console-only rows as they were last drawn, folder and status, for the summary line of a
    /// headless run: empty unless the run ended on this panel.
    /// </summary>
    public static string ConsoleOnlySummary { get; private set; } = string.Empty;

    /// <summary>
    /// Whether the panel is drawn for RPCS3, where a mod is an RPCS3 patch the user switches on,
    /// rather than for a console, where it is loaded into the running game. What the connected
    /// qwark says decides it; between connections it is the target the Connection panel is set to.
    /// </summary>
    public static bool UnderRpcs3(AppState state) =>
        state.Connected ? state.CodePatchesUnsupported : state.Settings.Rpcs3Target;

    public static void Draw(AppState state)
    {
        Ui.Heading("Mods");

        // Every mod is patch words, code caves or both, which qwark cannot write into a game RPCS3
        // has recompiled. Under RPCS3 the mods therefore go in as RPCS3 patches, which RPCS3 only
        // ever applies when the game boots.
        bool rpcs3 = UnderRpcs3(state);
        if (rpcs3)
        {
            state.Rpcs3Mods.Update();
            Ui.Warning(Ui.ModsAreRpcs3Patches);
        }

        // The described title: between sessions the library is still the one whose game just quit,
        // and the buttons that need a console are disabled below whether or not one is running.
        string title = string.IsNullOrEmpty(state.Session.TitleId) ? state.DescribedTitle : state.Session.TitleId;
        if (string.IsNullOrEmpty(title))
        {
            Ui.Hint("No game is running, so there is no mod library to show.");
            return;
        }

        // The panel has just been opened: the console's list and its mod folder are read again, so
        // a mod that only the console has shows up without anybody pressing anything. Once, not
        // once a frame.
        int frame = ImGui.GetFrameCount();
        if (frame != _lastDrawnFrame + 1) state.RefreshMods(quiet: true);
        _lastDrawnFrame = frame;

        if (rpcs3) DrawRpcs3Problem(state);

        // The library is a button that opens the folder rather than the path printed out: the path
        // is absolute and long enough to need wrapping, and reading it was never the point.
        if (ImGui.SmallButton("Rescan library"))
        {
            state.RescanLocalMods();
            state.RefreshConsoleFolders(quiet: true);
        }

        // Under RPCS3 there is no console, and the folder qwark-rpcs3 keeps its copies in is one the
        // client uploads to and rescans by itself whenever a mod is enabled.
        if (!rpcs3)
        {
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
        }

        ImGui.SameLine();
        Ui.OpenFolderButton(state, Path.Combine(state.Mods.RootPath, title));

        if (rpcs3)
        {
            // RPCS3's Patch Manager can change the same switches, so what the files say is only as
            // fresh as the last look; this is the look again.
            ImGui.SameLine();
            ImGui.BeginDisabled(state.Rpcs3Mods.Writing);
            if (ImGui.SmallButton("Check again")) state.Rpcs3Mods.Recheck();
            ImGui.EndDisabled();
        }

        ImGui.Spacing();

        // The library's mods first, then every mod only the console has, found by pairing MOD_LIST
        // with the folder names DIR_LIST gives whole (ModTable).
        var rows = state.ModRows();
        bool busy = state.ConsoleBusy || _uninstalling;
        ConsoleOnlySummary = string.Join(" ", rows.Where(row => row.ConsoleOnly)
            .Select(row => $"{row.FolderName}=\"{ModTable.ConsoleOnlyStatus(row, withAuto: true).Text}\""));

        if (rows.Count == 0)
        {
            Ui.Hint("No mods found for this title. Install one from a ZIP below.");
        }
        else if (rpcs3)
        {
            DrawRpcs3Table(state, title, rows, busy);
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

            foreach (var row in rows)
            {
                var actions = ModTable.ActionsFor(row, rpcs3: false, state.Connected, busy);
                ImGui.TableNextRow();
                ImGui.PushID(row.Key);

                DrawNameAndVersion(state, title, row, actions, rpcs3: false);

                ImGui.TableNextColumn();
                var (text, tone) = ModTable.ConsoleStatus(row);
                Ui.Text(ColourOf(tone), text);
                if (row.ConsoleOnly) Ui.Tooltip(row.FolderNameTooLong ? $"{ConsoleOnlyTooltip} qwark cannot load it: {ModFolderName.TooLongFact(row.FolderName)}" : ConsoleOnlyTooltip);
                else if (row.FolderNameTooLong) Ui.Tooltip($"This mod cannot be uploaded or loaded: {ModFolderName.TooLongReason(row.FolderName)}");

                ImGui.TableNextColumn();
                bool auto = row.Console?.Auto ?? false;
                ImGui.BeginDisabled(!actions.AutoEnabled);
                if (ImGui.Checkbox("##auto", ref auto) && row.ListedName is { } dir)
                {
                    bool wanted = auto;
                    state.Run(async () =>
                    {
                        await state.Client.ModSetAutoAsync(dir, wanted);
                        state.Post(() => state.RefreshMods());
                    });
                }

                ImGui.EndDisabled();

                ImGui.TableNextColumn();
                DrawActions(state, title, row, actions);

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        DrawDetails(state, title, rows, rpcs3);
        DrawZipInstall(state, title);
        if (rpcs3) DrawConfirmation(state);
        DrawUninstallQuestion(state);
        DrawRenameQuestion(state, title);
    }

    private const string ConsoleOnlyTooltip =
        "Only the console has this mod: no mods folder on this PC holds it. Right-click it to uninstall it from the console.";

    private static Vector4 ColourOf(ModTone tone) => tone switch
    {
        ModTone.Good => Ui.Green,
        ModTone.Bad => Ui.Red,
        _ => Ui.Grey,
    };

    /// <summary>
    /// The first two cells of a row, the same in both layouts: the name, which selects the row,
    /// carries the author and the full name on its tooltip and the row's menu on a right-click, and
    /// the version. A mod only the console has is drawn in grey, as "not uploaded" is.
    /// </summary>
    private static void DrawNameAndVersion(AppState state, string title, ModRow row, ModRowActions actions, bool rpcs3)
    {
        ImGui.TableNextColumn();

        // A name too long for the column takes a second line and the row grows for it,
        // which is the difference between reading "Incremental RNG" and reading
        // "Incremental R". A third line is where a table stops being a table, so the
        // second one ends in an ellipsis and the tooltip has the rest.
        string label = WrapName(row.Name, ImGui.GetContentRegionAvail().X, MeasureText);

        // SpanAllColumns makes the whole row select, but without AllowOverlap the selectable
        // sits on top of the checkbox and the buttons and eats their clicks. The row's own ID is
        // pushed around all of it, so wrapping the name differently as the window is resized does
        // not lose the selection or the menu.
        if (row.ConsoleOnly) ImGui.PushStyleColor(ImGuiCol.Text, Ui.Grey);
        bool clicked = ImGui.Selectable($"{label}##name", _selected == row.Key,
            ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap);
        if (row.ConsoleOnly) ImGui.PopStyleColor();
        if (clicked) _selected = row.Key;

        // The name is also where the author is read now, and where a name too long for the
        // column can be read in full. The row's selectable spans every column, so the
        // tooltip waits for the pointer to settle rather than following it across the row.
        // Built by hand rather than through SetTooltip, which is printf underneath: a mod
        // called "100% Speed" would otherwise lose the per cent and what follows it.
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(TooltipFor(row));
            ImGui.EndTooltip();
        }

        DrawRowMenu(state, title, row, actions, rpcs3);

        ImGui.TableNextColumn();
        string version = string.IsNullOrEmpty(row.Version) ? "-" : row.Version;
        if (row.ConsoleOnly) Ui.Text(Ui.Grey, version);
        else ImGui.TextUnformatted(version);
    }

    /// <summary>
    /// A row's right-click menu: Uninstall, which removes the mod from the console and from this
    /// PC, and for a mod of the user's whose folder name qwark cannot use, Rename folder. Both only
    /// ask their question here; nothing is changed until the user answers it.
    /// </summary>
    private static void DrawRowMenu(AppState state, string title, ModRow row, ModRowActions actions, bool rpcs3)
    {
        if (!ImGui.BeginPopupContextItem("row-menu")) return;

        if (ImGui.MenuItem("Uninstall...", string.Empty, false, actions.UninstallEnabled))
        {
            _pendingUninstall = PlanUninstall(state, title, row, rpcs3);
        }

        if (!actions.UninstallEnabled) Ui.Tooltip(actions.UninstallReason);

        if (actions.Rename && row.Local is { } mod && ImGui.MenuItem("Rename folder..."))
        {
            _pendingRename = (mod, state.Mods.SuggestFolderName(title, mod));
        }

        ImGui.EndPopup();
    }

    /// <summary>
    /// The uninstall of one row, as it would go now. Under RPCS3 RaCMAN's patch file is read here,
    /// once, so the question can say whether the mod has an RPCS3 patch to take out.
    /// </summary>
    private static UninstallPlan PlanUninstall(AppState state, string title, ModRow row, bool rpcs3)
    {
        Rpcs3UninstallLook? look = null;
        if (rpcs3)
        {
            var disk = state.Rpcs3Mods.Disk;
            var folder = disk?.Folder.Folder;
            var file = folder is null || !Rpcs3Patches.IsTitleId(title) ? null : Rpcs3Patches.ReadPatchFile(folder.PatchFile(title));
            look = new Rpcs3UninstallLook(folder, file, disk?.Folder.Problem ?? "RPCS3's folder has not been looked at yet");
        }

        bool shippedBehind = row.Local is { Shipped: false } mine && state.Mods.ReadShipped(title, mine.DirName) is not null;

        return ModUninstall.Plan(row, title, rpcs3, look, shippedBehind);
    }

    /// <summary>
    /// The question before an uninstall, listing each step it will take in order, and what it will
    /// not do and why. Cancel is the easy answer, as with every question this client asks.
    /// </summary>
    private static void DrawUninstallQuestion(AppState state)
    {
        var plan = _pendingUninstall;
        if (plan is null)
        {
            _shownUninstall = null;
            return;
        }

        string label = $"Uninstall {plan.ModName}?{UninstallId}";
        if (!ReferenceEquals(plan, _shownUninstall))
        {
            _shownUninstall = plan;
            ImGui.OpenPopup(label);
        }
        else if (!ImGui.IsPopupOpen(label))
        {
            _pendingUninstall = null;
            _shownUninstall = null;
            return;
        }

        var centre = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(560, 0), ImGuiCond.Appearing);

        bool open = true;
        if (!ImGui.BeginPopupModal(label, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) _pendingUninstall = null;
            return;
        }

        if (plan.NothingToDo)
        {
            Ui.Paragraph($"There is nothing to remove for {plan.ModName}.");
        }
        else
        {
            Ui.Paragraph($"Uninstalling {plan.ModName} does this, in this order:");
            for (int i = 0; i < plan.Steps.Count; i++) Ui.Paragraph($"{i + 1}. {plan.Steps[i].Text}");
        }

        foreach (var note in plan.Notes)
        {
            ImGui.Spacing();
            Ui.Hint(note);
        }

        if (!plan.NothingToDo)
        {
            ImGui.Spacing();
            Ui.Hint((plan.Rpcs3 ? "qwark-rpcs3" : "The console") + " then rescans its mod folder. If a step fails, the steps after it are not done.");
            if (plan.Rpcs3 && plan.Steps.Any(step => step.Kind == UninstallStepKind.RemoveRpcs3Patch))
            {
                Ui.Hint("RPCS3 applies patches when the game boots, so a mod in the game now stays there until the game is restarted in RPCS3.");
            }
        }

        ImGui.Separator();

        ImGui.BeginDisabled(plan.NothingToDo || _uninstalling);
        bool confirm = ImGui.Button("Uninstall", new Vector2(140, 0));
        ImGui.EndDisabled();

        ImGui.SameLine();
        bool cancel = ImGui.Button(plan.NothingToDo ? "Close" : "Cancel", new Vector2(120, 0));

        // Enter on a dialog that has just opened answers no.
        ImGui.SetItemDefaultFocus();

        if (confirm)
        {
            _uninstalling = true;
            _pendingUninstall = null;
            ModUninstall.Start(state, plan, () => _uninstalling = false);
            ImGui.CloseCurrentPopup();
        }
        else if (cancel || !open)
        {
            _pendingUninstall = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    /// <summary>
    /// The question before a user mod's folder is renamed to a name qwark can use. Only the folder
    /// on this PC moves; a copy the console has under the old name stays there, and shows up in the
    /// list as a console-only mod for Uninstall to take away.
    /// </summary>
    private static void DrawRenameQuestion(AppState state, string title)
    {
        if (_pendingRename is not { } rename)
        {
            _renameShown = false;
            return;
        }

        if (!_renameShown)
        {
            _renameShown = true;
            ImGui.OpenPopup(RenameId);
        }
        else if (!ImGui.IsPopupOpen(RenameId))
        {
            _pendingRename = null;
            _renameShown = false;
            return;
        }

        var centre = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(560, 0), ImGuiCond.Appearing);

        bool open = true;
        if (!ImGui.BeginPopupModal(RenameId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) _pendingRename = null;
            return;
        }

        var (mod, newName) = rename;
        Ui.Paragraph($"{mod.Name} is in a folder called {mod.DirName}, which is {ModFolderName.ByteCount(mod.DirName)} bytes long. "
                     + $"qwark can only use folder names of up to {ModFolderName.MaxBytes} bytes, so this mod cannot be uploaded or loaded.");
        ImGui.Spacing();
        Ui.Paragraph($"Rename the folder to {newName}?");
        ImGui.Spacing();
        Ui.Hint("Only the folder in your mods folder is renamed. A copy the console already has under the old name stays "
                + "there; it shows in this list as a console-only mod, and Uninstall removes it.");
        ImGui.Separator();

        bool confirm = ImGui.Button("Rename", new Vector2(140, 0));
        ImGui.SameLine();
        bool cancel = ImGui.Button("Cancel", new Vector2(120, 0));
        ImGui.SetItemDefaultFocus();

        if (confirm)
        {
            try
            {
                var renamed = state.Mods.RenameFolder(title, mod, newName);
                state.AddToast($"{renamed.Name}'s folder is now {renamed.DirName}", ToastKind.Success);
                _selected = renamed.DirName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.AddToast($"{mod.Name}'s folder was not renamed: {ex.Message}", ToastKind.Error);
            }

            state.RescanLocalMods();
            state.RefreshConsoleFolders(quiet: true);
            _pendingRename = null;
            ImGui.CloseCurrentPopup();
        }
        else if (cancel || !open)
        {
            _pendingRename = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- under RPCS3

    /// <summary>
    /// Why RPCS3's folder cannot say which mods are enabled, when it cannot: RPCS3 not found, no
    /// game in its log, somebody else's patch file, a patch_config.yml it cannot read.
    /// </summary>
    private static void DrawRpcs3Problem(AppState state)
    {
        var mods = state.Rpcs3Mods;
        string problem = mods.DiskProblem;
        if (problem.Length == 0)
        {
            if (Ui.Debug && mods.Disk is { Hash.Hash: { } hash, File: { } file })
            {
                Ui.DebugHint($"Executable {hash}, patch file {file.Path}");
            }

            return;
        }

        Ui.Error(problem);
        if (mods.FolderProblem) Ui.Hint("Set RPCS3's folder on the Connection panel, then press Check again.");
    }

    /// <summary>What a row's Status cell says while its write is under way.</summary>
    private const string WritingText = "Writing...";

    private const int Rpcs3Columns = 5;

    /// <summary>
    /// The RPCS3 table: Status in place of the console column, Enabled in place of Auto, and no
    /// Load or Unload, since RPCS3 is what loads a mod, when the game boots. Auto has no meaning
    /// here: an enabled mod goes in at every boot. A mod only qwark-rpcs3's folder has is listed
    /// after the library's, in grey, with nothing to enable: its row's menu uninstalls it.
    /// <para>
    /// Status is the column that stretches, since its texts are the long ones ("Disabled - restart
    /// needed"); Mod is as wide as the longest name in it (<see cref="Rpcs3ModColumnWidth"/>) and
    /// the rest are as wide as what they hold, so no band of empty name column is left over.
    /// </para>
    /// </summary>
    private static void DrawRpcs3Table(AppState state, string title, IReadOnlyList<ModRow> rows, bool busy)
    {
        var controller = state.Rpcs3Mods;

        // Decided before the table, so the Status column's longest text is known when the columns
        // are set up; each row is still decided once a frame, as it was.
        var statuses = new ModRpcs3Status?[rows.Count];
        var texts = new string[rows.Count];
        var writing = new bool[rows.Count];
        float statusNeeds = MeasureText("Status");
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Local is { } mod)
            {
                statuses[i] = controller.StatusFor(mod);
                writing[i] = controller.IsBusy(mod);
                texts[i] = writing[i] ? WritingText : statuses[i]!.Text;
            }
            else
            {
                texts[i] = ModTable.ConsoleOnlyStatus(rows[i], withAuto: true).Text;
            }

            statusNeeds = MathF.Max(statusNeeds, MeasureText(texts[i]));
        }

        // The buttons column holds Update, and in debug Upload beside it, and nothing else.
        var style = ImGui.GetStyle();
        float actions = MeasureText("Update") + (style.FramePadding.X * 2f);
        if (Ui.Debug) actions += style.ItemSpacing.X + MeasureText("Upload") + (style.FramePadding.X * 2f);
        actions = MathF.Ceiling(actions);

        const float version = 60f;
        const float enabled = 55f;

        // What Mod and Status share: the table's width, less the fixed columns and what the table
        // puts around every column (cell padding each side, a border between two, one down each edge).
        float overhead = (style.CellPadding.X * 2f * Rpcs3Columns) + (Rpcs3Columns - 1) + 2f;
        float room = ImGui.GetContentRegionAvail().X - overhead - version - enabled - actions;
        float name = Rpcs3ModColumnWidth(rows.Select(row => row.Name), room, statusNeeds, MeasureText);

        if (!ImGui.BeginTable("mods-rpcs3", Rpcs3Columns, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        // A fixed width the table is not resizable over is applied afresh every frame, so the
        // column follows a new mod, a new font size and a resized window.
        ImGui.TableSetupColumn("Mod", ImGuiTableColumnFlags.WidthFixed, name);
        ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthFixed, version);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed, enabled);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, actions);
        ImGui.TableHeadersRow();

        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var rowActions = ModTable.ActionsFor(row, rpcs3: true, state.Connected, busy || controller.Writing);

            ImGui.TableNextRow();
            ImGui.PushID(row.Key);

            DrawNameAndVersion(state, title, row, rowActions, rpcs3: true);

            if (row.Local is not { } mod || statuses[i] is not { } status)
            {
                DrawRpcs3ConsoleOnlyCells(row, texts[i]);
                ImGui.PopID();
                continue;
            }

            bool rowBusy = writing[i];

            ImGui.TableNextColumn();
            var colour = status.Tone switch
            {
                ModRpcs3Tone.Good => Ui.Green,
                ModRpcs3Tone.Bad => Ui.Red,
                ModRpcs3Tone.Pending => Ui.Yellow,
                _ => Ui.Grey,
            };
            Ui.Text(rowBusy ? Ui.Yellow : colour, texts[i]);
            Ui.Tooltip(rowBusy ? "Writing to RPCS3's folder..." : status.Tooltip);

            ImGui.TableNextColumn();
            bool ticked = status.Enabled;
            ImGui.BeginDisabled(!status.CanToggle || rowBusy || controller.Writing || _uninstalling);
            if (ImGui.Checkbox("##enabled", ref ticked)) controller.Toggle(mod, ticked);
            ImGui.EndDisabled();
            if (!status.CanToggle && status.ToggleReason.Length > 0) Ui.Tooltip(status.ToggleReason);

            ImGui.TableNextColumn();
            if (status.CanUpdate)
            {
                bool answering = state.Connected && !state.ConsoleBusy;
                ImGui.BeginDisabled(controller.Writing || !answering);
                if (ImGui.SmallButton("Update")) controller.RequestUpdate(mod);
                ImGui.EndDisabled();
                Ui.Tooltip(answering
                    ? "Rewrite this mod's RPCS3 patch from the library's copy. Asks first."
                    : "qwark-rpcs3 hands out the mod's words: connect to it, and wait while a game is starting or stopping.");
                if (Ui.Debug) ImGui.SameLine();
            }

            if (Ui.Debug) DrawUploadButton(state, title, mod);

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// The Status, Enabled and button cells of a mod only qwark-rpcs3's folder has: what it is, and
    /// an Enabled box that cannot be ticked, since there is no library copy to make a patch of.
    /// </summary>
    private static void DrawRpcs3ConsoleOnlyCells(ModRow row, string text)
    {
        ImGui.TableNextColumn();
        var (_, tone) = ModTable.ConsoleOnlyStatus(row, withAuto: true);
        Ui.Text(ColourOf(tone), text);
        Ui.Tooltip(row.FolderNameTooLong
            ? $"Only qwark-rpcs3's folder has this mod, and qwark cannot use it: {ModFolderName.TooLongFact(row.FolderName)} Right-click it to uninstall it."
            : "Only qwark-rpcs3's folder has this mod: no mods folder on this PC holds it. Right-click it to uninstall it.");

        ImGui.TableNextColumn();
        bool ticked = false;
        ImGui.BeginDisabled();
        ImGui.Checkbox("##enabled", ref ticked);
        ImGui.EndDisabled();
        Ui.Tooltip("Not in your mods library, so there is nothing to enable. Right-click it to uninstall it.");

        ImGui.TableNextColumn();
    }

    /// <summary>
    /// The question before a change the user did not click directly: a dependency, a dependant, an
    /// update. No is the easy answer, as with every such question this client asks.
    /// </summary>
    private static void DrawConfirmation(AppState state)
    {
        var controller = state.Rpcs3Mods;
        var pending = controller.Pending;
        if (pending is null)
        {
            _shownConfirmation = null;
            return;
        }

        string label = pending.Title + ConfirmId;
        if (!ReferenceEquals(pending, _shownConfirmation))
        {
            _shownConfirmation = pending;
            ImGui.OpenPopup(label);
        }
        else if (!ImGui.IsPopupOpen(label))
        {
            // Closed some other way than the two buttons: that is a no.
            controller.Cancel();
            _shownConfirmation = null;
            return;
        }

        var centre = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(520, 0), ImGuiCond.Appearing);

        bool open = true;
        if (!ImGui.BeginPopupModal(label, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) controller.Cancel();
            return;
        }

        Ui.Paragraph(pending.Message);
        ImGui.Spacing();
        Ui.Hint("RPCS3 applies mods when the game boots, so restart the game in RPCS3 afterwards.");
        ImGui.Separator();

        if (ImGui.Button(pending.ConfirmLabel, new Vector2(140, 0)))
        {
            controller.Confirm();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        bool cancel = ImGui.Button("Cancel", new Vector2(120, 0));

        // Enter on a dialog that has just opened answers no.
        ImGui.SetItemDefaultFocus();
        if (cancel || !open)
        {
            controller.Cancel();
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    /// <summary>How wide a piece of text is on screen. The measure WrapName uses in the panel.</summary>
    private static float MeasureText(string text) => ImGui.CalcTextSize(text).X;

    /// <summary>
    /// How wide the RPCS3 table's Mod column is: its longest entry, header included, so no name is
    /// wrapped while there is room for it and the Status column beside it gets everything else.
    /// <paramref name="room"/> is what Mod and Status share, and <paramref name="statusNeeds"/> is
    /// Status's own longest entry. Where the two do not both fit, Status keeps its longest text
    /// and the names wrap, as <see cref="WrapName"/> does in either table, but Mod never drops
    /// below half of the room.
    /// <para>
    /// Rounded up, since the table hands a column whole pixels and a name a fraction of a pixel too
    /// wide for its column is a name that wraps.
    /// </para>
    /// </summary>
    public static float Rpcs3ModColumnWidth(IEnumerable<string?> names, float room, float statusNeeds, Func<string, float> measure)
    {
        float longest = measure("Mod");
        foreach (string? name in names) longest = MathF.Max(longest, measure((name ?? string.Empty).Trim()));

        float width = MathF.Min(longest, MathF.Max(room - statusNeeds, room / 2f));
        return MathF.Max(1f, MathF.Ceiling(width));
    }

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

    /// <summary>The same for any row: a mod only the console has says so, and names its folder there.</summary>
    public static string TooltipFor(ModRow row)
    {
        if (row.Local is { } mod) return TooltipFor(mod);

        string text = string.IsNullOrWhiteSpace(row.Author) ? row.Name : $"{row.Name}\nby {row.Author}";
        return text + $"\nOnly on the console, in {row.FolderName}";
    }

    private static void DrawActions(AppState state, string title, ModRow row, ModRowActions actions)
    {
        if (actions.Unload)
        {
            ImGui.BeginDisabled(!state.Connected || row.ListedName is null);
            if (ImGui.SmallButton("Unload") && row.ListedName is { } dir)
            {
                state.Run(async () =>
                {
                    await state.Client.ModUnloadAsync(dir);
                    state.Post(() => state.RefreshMods());
                }, $"{row.Name} unloaded");
            }

            ImGui.EndDisabled();
        }
        else if (actions.Load)
        {
            // MOD_LOAD is answered UNSUPPORTED for a mod with patch words or caves on a platform
            // without code patches, which is every mod: the button is greyed out rather than left
            // to produce an error toast.
            bool blocked = state.CodePatchesUnsupported;
            ImGui.BeginDisabled(blocked || !actions.LoadEnabled);
            bool load = ImGui.SmallButton("Load");
            ImGui.EndDisabled();
            if (!actions.LoadEnabled && actions.LoadReason.Length > 0) Ui.Tooltip(actions.LoadReason);
            else if (blocked) Ui.Tooltip(Ui.NoCodePatches);

            if (load && row.Local is { } mod)
            {
                var library = state.Mods;
                uint consoleHash = row.Console?.Hash ?? 0;
                state.Run(async () =>
                {
                    await library.EnsureUploadedAsync(state.Client, title, mod, consoleHash,
                        new Progress<string>(message => state.Post(() => state.AddToast(message))));
                    await state.Client.ModLoadAsync(mod.DirName);
                    state.Post(() => state.RefreshMods());
                }, $"{mod.Name} loaded");
            }
            else if (load && row.ListedName is { } dir)
            {
                // A mod only the console has is loaded from the console's own copy: there is
                // nothing on this PC to upload first.
                state.Run(async () =>
                {
                    await state.Client.ModLoadAsync(dir);
                    state.Post(() => state.RefreshMods());
                }, $"{row.Name} loaded");
            }
        }

        // Load uploads on its own when the console's copy is missing or stale, so a manual
        // upload is only a debugging tool; it stays behind the debug switch.
        if (!Ui.Debug || row.Local is not { } local) return;

        ImGui.SameLine();
        DrawUploadButton(state, title, local);
    }

    /// <summary>The debug-only Upload, in both layouts: the console's copy is otherwise sent only on the way to a load or an enable.</summary>
    private static void DrawUploadButton(AppState state, string title, LocalMod mod)
    {
        ImGui.BeginDisabled(!state.Connected || mod.FolderNameTooLong);
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
        if (mod.FolderNameTooLong) Ui.Tooltip($"It cannot be uploaded: {ModFolderName.TooLongReason(mod.DirName)}");
    }

    private static void DrawDetails(AppState state, string title, IReadOnlyList<ModRow> rows, bool rpcs3)
    {
        var row = rows.FirstOrDefault(r => r.Key == _selected);
        if (row is null) return;

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading(row.Name);

        if (row.Local is not { } mod)
        {
            DrawConsoleOnlyDetails(state, title, row, rpcs3);
            return;
        }

        ImGui.TextUnformatted($"Folder: {mod.DirName}  ({(mod.Shipped ? "ships with RaCMAN Reloaded" : "yours")})");
        ImGui.TextUnformatted($"Version: {(string.IsNullOrEmpty(mod.Version) ? "-" : mod.Version)}    Author: {(string.IsNullOrEmpty(mod.Author) ? "-" : mod.Author)}");

        if (mod.FolderNameTooLong)
        {
            Ui.Error($"This mod cannot be uploaded or loaded: {ModFolderName.TooLongReason(mod.DirName)}");
        }

        // Word counts, code caves and checksums say nothing to someone who only wants the mod on;
        // they are what you look at when a mod misbehaves, which is what the debug switch is for.
        // The console column already says whether the console's copy is stale.
        if (Ui.Debug)
        {
            ImGui.TextUnformatted($"Patch words: {mod.PatchWordCount}    Code caves: {mod.BinFiles.Count}    CRC32: {Crc32.ToSumText(mod.Hash)}");

            if (row.Console is { } remote)
            {
                ImGui.TextUnformatted($"Console hash: {Crc32.ToSumText(remote.Hash)}{(remote.Hash == mod.Hash ? " (current)" : " (differs, will re-upload)")}");
            }
        }

        if (mod.NeedsLua)
        {
            Ui.Warning(rpcs3
                ? "This mod has a Lua automation, which qwark cannot run, so it cannot go in as an RPCS3 patch."
                : "This mod has a Lua automation; only its patches are applied.");
        }
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

        DrawReadDescription(state, row);
    }

    /// <summary>What there is to say about a mod only the console has: where it is, what qwark makes of it, and what to do about it.</summary>
    private static void DrawConsoleOnlyDetails(AppState state, string title, ModRow row, bool rpcs3)
    {
        string where = rpcs3 ? "qwark-rpcs3's folder" : "the console";
        ImGui.TextUnformatted(row.ConsoleFolder is { } folder
            ? $"Folder: {ModLibrary.ConsoleFolder(title, folder)}  (only on {where})"
            : $"qwark lists it as {row.ListedName}, and its folder is not in {where}'s mod folder any more.");
        ImGui.TextUnformatted($"Version: {(string.IsNullOrEmpty(row.Version) ? "-" : row.Version)}    Author: {(string.IsNullOrEmpty(row.Author) ? "-" : row.Author)}");

        if (row.FolderNameTooLong)
        {
            Ui.Error($"qwark cannot load this mod: {ModFolderName.TooLongFact(row.FolderName)}");
        }

        if (row.Console is { Auto: true })
        {
            Ui.Warning(rpcs3
                ? "It is flagged to load by itself when the game boots."
                : "It loads by itself whenever the game boots.");
        }

        Ui.Hint($"No mods folder on this PC holds this mod. Right-click it and pick Uninstall... to remove it from {where}.");

        if (!Ui.Debug || row.ListedName is null) return;
        DrawReadDescription(state, row);
    }

    private static void DrawReadDescription(AppState state, ModRow row)
    {
        ImGui.Spacing();
        ImGui.BeginDisabled(!state.Connected || row.ListedName is null);
        if (ImGui.Button("Read description from console") && row.ListedName is { } dir)
        {
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
        bool install = Ui.SubmitTextWithHint("##zip", "C:\\downloads\\some-mod.zip", ref _zipPath, 512);

        ImGui.SameLine();
        ImGui.BeginDisabled(!FileDialog.IsSupported || _dialogOpen);
        if (ImGui.Button("Browse...")) Browse(state, title);
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Install ZIP") || install) InstallZip(state, title);

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
                state.RefreshConsoleFolders(quiet: true);
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
                state.RefreshConsoleFolders(quiet: true);
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
