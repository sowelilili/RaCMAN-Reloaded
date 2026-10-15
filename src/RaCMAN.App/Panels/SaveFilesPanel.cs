using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The savefile manager. The library of record is the console's, under
/// <c>/dev_hdd0/qwark/savefiles/&lt;TITLEID&gt;/&lt;category&gt;/</c>, and this PC keeps a mirror of it:
/// saving asks the console to write its own file and then copies it down, and loading asks the
/// console to read its own file back, uploading it first only when this PC has bytes the console
/// does not. A save is up to 2 MB, and until qwark build 12 every single load streamed one from
/// here.
/// <para>
/// No address, no game knowledge and no savefile format ever reaches this client: what a save is
/// is whatever the game put in the helper's aside buffer.
/// </para>
/// </summary>
public static class SaveFilesPanel
{
    private static string _title = string.Empty;
    private static string[] _categories = { SaveFileLibrary.DefaultCategory };
    private static int _categoryIndex;
    private static string _newCategory = string.Empty;
    private static SaveFileEntry[] _entries = Array.Empty<SaveFileEntry>();
    private static int _fileIndex = -1;
    private static string _name = string.Empty;

    private static bool _busy;
    private static bool _listing;
    private static string _status = string.Empty;
    private static long _transferred;

    /// <summary>
    /// A save picked from a row's menu, with the title and category it was listed under, so the
    /// answer acts on the file that was asked about even if the list has moved on meanwhile.
    /// </summary>
    private sealed record FileQuestion(SaveFileEntry Entry, string Title, string Category);

    /// <summary>The rename the user picked from a row's menu, waiting for a new name.</summary>
    private static FileQuestion? _pendingRename;

    /// <summary>The question the rename dialog was last opened for, so it is opened once per question.</summary>
    private static FileQuestion? _shownRename;

    /// <summary>What the rename dialog's box holds: the new name, without the .sav suffix.</summary>
    private static string _renameTo = string.Empty;

    /// <summary>The delete the user picked from a row's menu, waiting for the answer to its question.</summary>
    private static FileQuestion? _pendingDelete;

    /// <summary>The question the delete dialog was last opened for, so it is opened once per question.</summary>
    private static FileQuestion? _shownDelete;

    private const string RenameId = "###savefiles-rename";

    private const string DeleteId = "###savefiles-delete";

    /// <summary>What a row's tooltip adds, since nothing else on the row says the menu is there.</summary>
    private const string RowMenuHint = "Right-click it to rename or delete it.\nF2 and Delete do the same for the selected save.";

    /// <summary>Seconds since SAVEFILE_INFO was last re-read while it said the helper had not run.</summary>
    private static float _sinceInfo;

    /// <summary>How often the panel asks again whether the helper has run a frame.</summary>
    private const float NotRunningPollSeconds = 1f;

    /// <summary>Category and file count, for the smoke-run summary.</summary>
    public static string Summary => $"{_categories.Length}/{_entries.Length}";

    /// <summary>
    /// Whether the savefile helper can be asked to do anything this session. On a console qwark
    /// writes the helper in on first use, so it always can. Under RPCS3 (a platform that refuses
    /// code patches) it cannot, and the helper is in the game only when the RPCS3 patch put it there
    /// at this boot, which is what SAVEFILE_INFO's <c>installed</c> says there; without it saving and
    /// loading are refused. This panel, the Game page's set-aside buttons and the Combos panel's
    /// set-aside combo all go by this.
    /// </summary>
    public static bool HelperIn(SessionInfo session, SaveFileInfo info) => !session.CodePatchesUnsupported || info.Installed;

    /// <summary>
    /// The library changed under the panel — an import put files in it — so read it again on the
    /// next frame. Render thread only, like everything else here.
    /// </summary>
    public static void Invalidate() => _title = string.Empty;

    /// <summary>
    /// True while a save is moving between this PC and the console. Anything else that would send
    /// megabytes down the same link — the standalone module update — waits for it.
    /// </summary>
    public static bool Busy => _busy;

    public static void Reset()
    {
        _title = string.Empty;
        _categories = new[] { SaveFileLibrary.DefaultCategory };
        _categoryIndex = 0;
        _newCategory = string.Empty;
        _entries = Array.Empty<SaveFileEntry>();
        _fileIndex = -1;
        _name = string.Empty;
        ClearTransfer();
    }

    /// <summary>
    /// The progress readout describes a transfer against a session that is now gone, and a rename or
    /// delete still waiting for its answer was asked about a list that is now gone with it.
    /// </summary>
    public static void ClearTransfer()
    {
        _status = string.Empty;
        _transferred = 0;
        DropQuestions();
        _busy = false;
        _listing = false;
    }

    private static void DropQuestions()
    {
        _pendingRename = null;
        _pendingDelete = null;
    }

    private static string Category =>
        _categories.Length == 0 ? SaveFileLibrary.DefaultCategory
        : _categories[Math.Clamp(_categoryIndex, 0, _categories.Length - 1)];

    private static SaveFileEntry? Selected =>
        _fileIndex >= 0 && _fileIndex < _entries.Length ? _entries[_fileIndex] : null;

    public static void Draw(AppState state)
    {
        Ui.Heading("Save files");

        var session = state.Session;
        string title = session.TitleId;

        // A rename or a delete still waiting for its answer goes with the list it was asked about.
        if (!state.Connected)
        {
            DropQuestions();
            Ui.Hint("Connect to a console to manage save files.");
            return;
        }

        if (string.IsNullOrEmpty(title))
        {
            DropQuestions();
            Ui.Hint("No game is running, so there is no savefile library to show.");
            return;
        }

        var save = state.Describe.SaveAsideAction;
        var load = state.Describe.LoadAsideAction;
        var info = state.SaveFile;

        // The console is the authority on whether it has a helper for this game: the flagged
        // ACTIONs say a game asks for one, SAVEFILE_INFO says whether there is one to ask.
        bool hasHelper = info.Supported && save is not null && load is not null;

        // Under RPCS3 the helper is in the game only when the RPCS3 patch put it there at this
        // boot; without it saving and loading are refused, so they are greyed out.
        bool helperIn = HelperIn(session, info);
        bool enabled = state.Ingame && hasHelper && helperIn && !_busy;

        // INFO only observes the helper. An action installs it on demand; transfers poll INFO
        // themselves without updating this snapshot. Refresh until the helper is running so
        // an action's installation and first game frame are reflected here too. A helper that
        // RPCS3 has not put in cannot arrive without the game starting again, and that re-reads
        // INFO by itself.
        if (hasHelper && helperIn && state.Ingame && !info.Running && !_busy)
        {
            _sinceInfo += ImGui.GetIO().DeltaTime;
            if (_sinceInfo >= NotRunningPollSeconds)
            {
                _sinceInfo = 0;
                state.RefreshSaveFileInfo();
            }
        }
        else
        {
            _sinceInfo = 0;
        }

        if (!hasHelper)
        {
            Ui.Warning("This game has no savefile helper, so saving and loading are not available.");
            ImGui.Spacing();
        }
        else if (!state.Ingame)
        {
            Ui.Warning($"Saving and loading need INGAME (state is {session.State.DisplayName()}).");
            ImGui.Spacing();
        }
        else if (!helperIn)
        {
            Ui.Warning(Ui.NeedsSaveFileHelper);
            ImGui.Spacing();
        }
        else if (info.Installed && !info.Running)
        {
            Ui.Hint("The helper is installed but has not run a frame yet. It runs while the game " +
                    "is in play, so a save started on a loading screen or in a menu may wait.");
            ImGui.Spacing();
        }

        if (!string.Equals(_title, title, StringComparison.Ordinal)) Rescan(state, title);


        // The category's own folder on this PC, since that is where a mirrored file lands; its
        // path is on the button's tooltip rather than printed, being absolute and long enough
        // to wrap.
        Ui.OpenFolderButton(state, state.SaveFiles.CategoryFolder(title, Category));
        ImGui.SameLine();

        ImGui.BeginDisabled(_busy);
        if (ImGui.SmallButton("Rescan")) Rescan(state, title);

        ImGui.SameLine();
        Ui.Text(Ui.Grey, state.Settings.MirrorSaveFiles
            ? "Saves are stored on the console and mirrored here."
            : "Saves are stored on the console. Mirroring to this PC is off.");

        if (hasHelper)
        {
            Ui.DebugHint($"Console: {info.Size} bytes in the helper's aside buffer, " +
                         $"installed={info.Installed} running={info.Running}");
            Ui.DebugHint($"Actions: '{save!.Label}' (id {save.Id}, SAVE_ASIDE), '{load!.Label}' (id {load.Id}, LOAD_ASIDE)");
            Ui.DebugHint($"Console library: {QwarkClient.SaveFileConsoleFolder(title, Category)}");
        }

        ImGui.Spacing();
        DrawCategoryRow(state, title);

        ImGui.Spacing();
        DrawFileList(title);

        ImGui.Spacing();
        DrawActions(state, title, save, load, enabled);

        if (_status.Length > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(_busy ? Ui.Yellow : Ui.Grey, _status);
            if (_transferred > 0) Ui.Hint($"{_transferred} bytes");
        }

        DrawRenameQuestion(state);
        DrawDeleteQuestion(state);
    }

    private static void DrawCategoryRow(AppState state, string title)
    {
        ImGui.SetNextItemWidth(220);
        int index = Math.Clamp(_categoryIndex, 0, Math.Max(0, _categories.Length - 1));
        if (ImGui.Combo("Category", ref index, _categories, _categories.Length))
        {
            _categoryIndex = index;
            _fileIndex = -1;
            Remember(state, title);
            RescanFiles(state, title);
        }

        ImGui.EndDisabled();

        if (_listing)
        {
            ImGui.SameLine();
            Ui.Text(Ui.Grey, "reading the console...");
        }

        ImGui.SetNextItemWidth(220);
        bool create = Ui.SubmitTextWithHint("##newcategory", "New category", ref _newCategory, 64);
        ImGui.SameLine();

        // The box stays live while the button beside it is greyed out, so Enter in it asks the same
        // two questions the button does.
        bool canCreate = !string.IsNullOrWhiteSpace(_newCategory) && !_busy;
        ImGui.BeginDisabled(!canCreate);
        if (ImGui.Button("Create category") || (create && canCreate)) CreateCategory(state, title);

        ImGui.EndDisabled();
    }

    /// <summary>A category is made on both sides, so the next save has somewhere to go either way.</summary>
    private static void CreateCategory(AppState state, string title)
    {
        string wanted = SaveFileLibrary.Sanitise(_newCategory, SaveFileLibrary.DefaultCategory);

        try
        {
            state.SaveFiles.EnsureCategory(title, wanted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.AddToast($"Could not create the category: {ex.Message}", ToastKind.Error);
            return;
        }

        _newCategory = string.Empty;

        state.Run(async () =>
        {
            await state.Client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, wanted).ConfigureAwait(false);
            state.Post(() =>
            {
                Rescan(state, title);
                Select(_categories, wanted);
                Remember(state, title);
                RescanFiles(state, title);
                state.AddToast($"Category '{wanted}' created", ToastKind.Success);
            });
        });
    }

    private static void DrawFileList(string title)
    {
        if (!ImGui.BeginChild("##savefiles", new Vector2(-1, 220), ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        if (_entries.Length == 0)
        {
            Ui.Hint("No files in this category yet.");
        }
        // Every save in here is one game's whole file, so the byte count says nothing about which
        // one to load: it is wire detail, and only shown with debug information on.
        else if (ImGui.BeginTable("savefiles", Ui.Debug ? 3 : 2,
                     ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Save");
            ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthFixed, 90);
            if (Ui.Debug) ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableHeadersRow();

            for (int i = 0; i < _entries.Length; i++)
            {
                var entry = _entries[i];

                ImGui.TableNextRow();

                // The row's menu is "row-menu" on every row, so each row's id is pushed around it:
                // the file's own name, which keeps a menu on its file when a rescan reorders the list.
                ImGui.PushID(entry.Name);
                ImGui.TableNextColumn();

                // The row shows the name without the .sav every file in here ends in; the id after
                // the ## is the file itself, so two files that only differ by their suffix are
                // still two rows to ImGui.
                if (ImGui.Selectable($"{entry.DisplayName}##{entry.Name}", _fileIndex == i,
                        ImGuiSelectableFlags.SpanAllColumns))
                {
                    _fileIndex = i;
                }

                // A right-click selects the row it opens the menu on, as a file manager does, so the
                // highlighted row is always the one the menu is about.
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _fileIndex = i;

                // Read before the menu is drawn: the selectable spans the row, so this is the row's
                // tooltip wherever the pointer settles on it, and it says where the save lives as
                // well as where its menu is.
                bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal);
                bool menuOpen = DrawRowMenu(entry, title);
                if (hovered && !menuOpen)
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(Tooltip(entry));
                    ImGui.EndTooltip();
                }

                ImGui.TableNextColumn();

                // Where it lives, and whether the two copies are the same bytes. A row that says
                // "both, differ" is the one case where loading sends the file: this PC's copy is
                // the one the user picked, so it is the one that wins.
                Ui.Text(entry.Differs ? Ui.Yellow : Ui.Grey, entry.Where);

                if (Ui.Debug)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.Size > 0 ? $"{entry.Size / 1024} KB" : "-");
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        // F2 and Delete, the keys a file manager has for the same two things, while the list has
        // the keyboard: they ask the same questions the row's menu does, of the selected save.
        if (Selected is { } chosen && !_busy && ImGui.IsWindowFocused())
        {
            if (ImGui.IsKeyPressed(ImGuiKey.F2, false)) AskRename(chosen, title);
            else if (ImGui.IsKeyPressed(ImGuiKey.Delete, false)) AskDelete(chosen, title);
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// A row's right-click menu: Rename and Delete, the two things that used to be buttons under
    /// the list. Each only asks its question here; nothing changes until the user answers it.
    /// Returns whether the menu is open, so the row's tooltip stays out of its way.
    /// </summary>
    private static bool DrawRowMenu(SaveFileEntry entry, string title)
    {
        if (!ImGui.BeginPopupContextItem("row-menu")) return false;

        const string busyReason = "Wait for the save, load or change under way to finish.";

        if (ImGui.MenuItem("Rename...", "F2", false, !_busy)) AskRename(entry, title);
        if (_busy) Ui.Tooltip(busyReason);

        if (ImGui.MenuItem("Delete...", "Delete", false, !_busy)) AskDelete(entry, title);
        if (_busy) Ui.Tooltip(busyReason);

        ImGui.EndPopup();
        return true;
    }

    private static void AskRename(SaveFileEntry entry, string title)
    {
        _pendingDelete = null;
        _pendingRename = new FileQuestion(entry, title, Category);
        _renameTo = entry.DisplayName;
    }

    private static void AskDelete(SaveFileEntry entry, string title)
    {
        _pendingRename = null;
        _pendingDelete = new FileQuestion(entry, title, Category);
    }

    private static string Tooltip(SaveFileEntry entry) => (entry.Location switch
    {
        SaveFileLocation.Console =>
            $"{entry.Name}\nOn the console only. Loading it sends nothing.",
        SaveFileLocation.Pc =>
            $"{entry.Name}\nOn this PC only. Loading it uploads it to the console once.",
        _ when entry.Differs =>
            $"{entry.Name}\nBoth copies exist and hold different bytes "
            + $"(console {entry.ConsoleCrc:x8}, PC {entry.PcCrc:x8}).\n"
            + "Loading it sends this PC's copy and replaces the console's.",
        _ => $"{entry.Name}\nBoth copies exist and agree (CRC {entry.ConsoleCrc:x8}).",
    }) + "\n" + RowMenuHint;

    // ---------------------------------------------------------------- the two questions

    /// <summary>What the rename dialog makes of the name typed into it.</summary>
    public enum RenameCheck
    {
        /// <summary>A new name nothing else in the category has: Rename goes ahead.</summary>
        Ok,

        /// <summary>Nothing typed yet.</summary>
        Blank,

        /// <summary>The name the save already has.</summary>
        Unchanged,

        /// <summary>Another save in the category already has that name.</summary>
        Taken,
    }

    /// <summary>
    /// The file name a rename to <paramref name="typed"/> gives the save, the same way a save is
    /// named: kept inside the library and given the .sav suffix.
    /// </summary>
    public static string RenameTarget(string typed) =>
        SaveFileLibrary.EnsureExtension(SaveFileLibrary.Sanitise(typed, "savefile"));

    /// <summary>
    /// Whether <paramref name="typed"/> is a name <paramref name="entry"/> can be renamed to among
    /// the category's <paramref name="entries"/>. Another save's name is refused whatever its case,
    /// since one of the two sides may well ignore case; the save's own name in another case is a
    /// rename like any other.
    /// </summary>
    public static RenameCheck CheckRename(string typed, SaveFileEntry entry, IReadOnlyList<SaveFileEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(typed)) return RenameCheck.Blank;

        string target = RenameTarget(typed);
        if (string.Equals(target, entry.Name, StringComparison.Ordinal)) return RenameCheck.Unchanged;

        foreach (var other in entries)
        {
            if (string.Equals(other.Name, entry.Name, StringComparison.Ordinal)) continue;
            if (string.Equals(other.Name, target, StringComparison.OrdinalIgnoreCase)) return RenameCheck.Taken;
        }

        return RenameCheck.Ok;
    }

    /// <summary>What the rename dialog says will be renamed, by where the save lives.</summary>
    public static string RenameWhere(SaveFileEntry entry) => entry.Location switch
    {
        SaveFileLocation.Console => "Only the console has this save, so only its copy is renamed.",
        SaveFileLocation.Pc => "Only this PC has this save, so only its copy is renamed.",
        _ => "The console's copy and this PC's are both renamed.",
    };

    /// <summary>What the delete dialog says will be deleted, by where the save lives.</summary>
    public static string DeleteWhere(SaveFileEntry entry) => entry.Location switch
    {
        SaveFileLocation.Console => $"{entry.DisplayName} is deleted from the console. This PC has no copy of it.",
        SaveFileLocation.Pc => $"{entry.DisplayName} is deleted from this PC. The console has no copy of it.",
        _ => $"{entry.DisplayName} is deleted from the console and from this PC.",
    };

    /// <summary>
    /// The rename dialog: a box holding the new name, which has the keyboard when the dialog opens,
    /// so typing and Enter is the whole of a rename. Rename is greyed out until the name is a new
    /// one, and says why when another save already has it.
    /// </summary>
    private static void DrawRenameQuestion(AppState state)
    {
        var question = _pendingRename;
        if (question is null)
        {
            _shownRename = null;
            return;
        }

        string label = $"Rename {question.Entry.DisplayName}{RenameId}";
        if (!ReferenceEquals(question, _shownRename))
        {
            _shownRename = question;
            ImGui.OpenPopup(label);
        }
        else if (!ImGui.IsPopupOpen(label))
        {
            _pendingRename = null;
            _shownRename = null;
            return;
        }

        var centre = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(560, 0), ImGuiCond.Appearing);

        bool open = true;
        if (!ImGui.BeginPopupModal(label, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) _pendingRename = null;
            return;
        }

        Ui.Paragraph($"New name for {question.Entry.DisplayName}:");

        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(320);
        bool submitted = Ui.SubmitTextWithHint("##renameto", "New name", ref _renameTo, 64);

        var check = CheckRename(_renameTo, question.Entry, _entries);
        if (check == RenameCheck.Taken)
        {
            Ui.Warning($"A save called {SaveFileLibrary.DisplayName(RenameTarget(_renameTo))} is already in this category.");
        }

        ImGui.Spacing();
        Ui.Hint(RenameWhere(question.Entry));
        ImGui.Separator();

        bool canRename = check == RenameCheck.Ok && !_busy;
        ImGui.BeginDisabled(!canRename);
        bool confirm = ImGui.Button("Rename", new Vector2(140, 0)) || (submitted && canRename);
        ImGui.EndDisabled();

        ImGui.SameLine();
        bool cancel = ImGui.Button("Cancel", new Vector2(120, 0));

        if (confirm)
        {
            _pendingRename = null;
            StartRename(state, question.Title, question.Category, question.Entry, RenameTarget(_renameTo));
            ImGui.CloseCurrentPopup();
        }
        else if (cancel || !open)
        {
            _pendingRename = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    /// <summary>
    /// The question before a save is deleted, the way the Mods panel asks before an uninstall: what
    /// goes, from where, then Delete and Cancel, with Cancel the answer Enter gives.
    /// </summary>
    private static void DrawDeleteQuestion(AppState state)
    {
        var question = _pendingDelete;
        if (question is null)
        {
            _shownDelete = null;
            return;
        }

        string label = $"Delete {question.Entry.DisplayName}?{DeleteId}";
        if (!ReferenceEquals(question, _shownDelete))
        {
            _shownDelete = question;
            ImGui.OpenPopup(label);
        }
        else if (!ImGui.IsPopupOpen(label))
        {
            _pendingDelete = null;
            _shownDelete = null;
            return;
        }

        var centre = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(560, 0), ImGuiCond.Appearing);

        bool open = true;
        if (!ImGui.BeginPopupModal(label, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) _pendingDelete = null;
            return;
        }

        Ui.Paragraph(DeleteWhere(question.Entry));
        ImGui.Spacing();
        Ui.Hint("A deleted save cannot be brought back.");
        ImGui.Separator();

        ImGui.BeginDisabled(_busy);
        bool confirm = ImGui.Button("Delete", new Vector2(140, 0));
        ImGui.EndDisabled();

        ImGui.SameLine();
        bool cancel = ImGui.Button("Cancel", new Vector2(120, 0));

        // Enter on a dialog that has just opened answers no.
        ImGui.SetItemDefaultFocus();

        if (confirm)
        {
            _pendingDelete = null;
            StartDelete(state, question.Title, question.Category, question.Entry);
            ImGui.CloseCurrentPopup();
        }
        else if (cancel || !open)
        {
            _pendingDelete = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static void DrawActions(AppState state, string title, Feature? save, Feature? load, bool enabled)
    {
        var selected = Selected;

        ImGui.SetNextItemWidth(260);
        bool saveNow = Ui.SubmitTextWithHint("##name", "File name to save as", ref _name, 64);
        ImGui.SameLine();

        // The name box is typed in whether or not a save can be taken, so Enter in it asks what the
        // button's own greying out asks.
        bool canSave = enabled && save is not null && !string.IsNullOrWhiteSpace(_name);
        ImGui.BeginDisabled(!canSave);
        if (ImGui.Button("Save on console") || (saveNow && canSave)) StartSave(state, title);
        ImGui.EndDisabled();

        // Rename and Delete are on each row's right-click menu, which the row's tooltip points at.
        ImGui.SameLine();
        ImGui.BeginDisabled(!enabled || load is null || selected is null);
        if (ImGui.Button("Load into game")) StartLoad(state, title, selected!.Value);
        ImGui.EndDisabled();
    }

    /// <summary>
    /// The toast for a save or a load that did not happen: one the console refused outright says
    /// so, with its reason (the game busy, no save yet), and one that stopped on the way says it did
    /// not finish, with what stopped it.
    /// </summary>
    public static string FailureToast(bool load, Exception ex) =>
        $"The {(load ? "load" : "save")} {(ex is SaveFileTransfer.RefusedException ? "was refused" : "did not finish")}: {ex.Message}";

    /// <summary>
    /// SAVEFILE_STORE, poll SAVEFILE_INFO to completion, then mirror the file down when mirroring
    /// is on. The console writes its own file whole before it says it is finished, so a save that
    /// stops half way leaves the console's library exactly as it was.
    /// </summary>
    private static void StartSave(AppState state, string title)
    {
        string category = Category;
        string name = SaveFileLibrary.EnsureExtension(SaveFileLibrary.Sanitise(_name, "savefile"));
        var library = state.SaveFiles;
        bool mirror = state.Settings.MirrorSaveFiles;

        _busy = true;
        _transferred = 0;
        _status = "Asking the console to save...";

        var status = new Progress<string>(text => state.Post(() => _status = text));
        var bytes = new Progress<long>(count => state.Post(() => _transferred = count));

        state.Run(async () =>
        {
            try
            {
                var path = await SaveFileTransfer.StoreAsync(
                    state.Client, library, title, category, name, mirror,
                    timeout: null, status: status, bytes: bytes)
                    .ConfigureAwait(false);

                state.Post(() =>
                {
                    _status = path.Length > 0 ? $"Saved and uploaded successfully."
                                              : "Saved successfully.";
                    _name = string.Empty;
                    Rescan(state, title);
                    state.AddToast($"Saved '{SaveFileLibrary.DisplayName(name)}'", ToastKind.Success);
                });
            }
            catch (Exception ex) when (ex is SaveFileTransfer.NotAnsweredException
                                          or SaveFileTransfer.TransferFailedException
                                          or SaveFileTransfer.RefusedException
                                          or QwarkStatusException or ProtocolException
                                          or IOException or UnauthorizedAccessException)
            {
                state.Post(() =>
                {
                    _status = ex.Message;
                    state.AddToast(FailureToast(false, ex), ToastKind.Error);
                });
            }
            finally
            {
                state.Post(() => _busy = false);
            }
        });
    }

    /// <summary>
    /// SAVEFILE_RESTORE when the console already has these bytes, and an upload first when it does
    /// not. Which of the two it is comes out of the merged row, so the panel never guesses.
    /// </summary>
    private static void StartLoad(AppState state, string title, SaveFileEntry entry)
    {
        string category = Category;
        var library = state.SaveFiles;

        _busy = true;
        _transferred = 0;
        _status = SaveFileMerge.PlanLoad(entry) == SaveFileLoadPlan.UploadThenRestore
            ? $"Uploading {entry.DisplayName} to the console..."
            : $"Asking the console to load {entry.DisplayName}...";

        var status = new Progress<string>(text => state.Post(() => _status = text));
        var bytes = new Progress<long>(count => state.Post(() => _transferred = count));

        state.Run(async () =>
        {
            try
            {
                bool uploaded = await SaveFileTransfer.LoadAsync(
                    state.Client, library, title, category, entry,
                    timeout: null, status: status, bytes: bytes)
                    .ConfigureAwait(false);

                state.Post(() =>
                {
                    _status = uploaded
                        ? "Uploaded to the console and loaded into the game"
                        : "Loaded into the game from the console";
                    state.AddToast($"Loaded '{entry.DisplayName}'", ToastKind.Success);
                    if (uploaded) Rescan(state, title);
                });
            }
            catch (Exception ex) when (ex is SaveFileTransfer.NotAnsweredException
                                          or SaveFileTransfer.TransferFailedException
                                          or SaveFileTransfer.RefusedException
                                          or QwarkStatusException or ProtocolException
                                          or IOException or UnauthorizedAccessException)
            {
                state.Post(() =>
                {
                    _status = ex.Message;
                    state.AddToast(FailureToast(true, ex), ToastKind.Error);
                });
            }
            finally
            {
                state.Post(() => _busy = false);
            }
        });
    }

    /// <summary>
    /// Both copies, and the CRC sidecar the console keeps beside its own. <paramref name="renamed"/>
    /// is a whole file name, .sav and all (<see cref="RenameTarget"/>), so the file on both sides keeps it.
    /// </summary>
    private static void StartRename(AppState state, string title, string category, SaveFileEntry entry, string renamed)
    {
        var library = state.SaveFiles;

        _busy = true;
        state.Run(async () =>
        {
            try
            {
                await SaveFileTransfer.RenameAsync(state.Client, library, title, category, entry, renamed)
                    .ConfigureAwait(false);
                state.Post(() =>
                {
                    state.AddToast($"Renamed to {SaveFileLibrary.DisplayName(renamed)}", ToastKind.Success);
                    Rescan(state, title);
                });
            }
            catch (Exception ex) when (ex is QwarkStatusException or ProtocolException
                                          or IOException or UnauthorizedAccessException)
            {
                state.Post(() => state.AddToast($"Rename failed: {ex.Message}", ToastKind.Error));
            }
            finally
            {
                state.Post(() => _busy = false);
            }
        });
    }

    private static void StartDelete(AppState state, string title, string category, SaveFileEntry entry)
    {
        var library = state.SaveFiles;

        _busy = true;
        state.Run(async () =>
        {
            try
            {
                await SaveFileTransfer.DeleteAsync(state.Client, library, title, category, entry)
                    .ConfigureAwait(false);
                state.Post(() =>
                {
                    state.AddToast($"Deleted '{entry.DisplayName}'", ToastKind.Success);
                    _fileIndex = -1;
                    Rescan(state, title);
                });
            }
            catch (Exception ex) when (ex is QwarkStatusException or ProtocolException
                                          or IOException or UnauthorizedAccessException)
            {
                state.Post(() => state.AddToast($"Delete failed: {ex.Message}", ToastKind.Error));
            }
            finally
            {
                state.Post(() => _busy = false);
            }
        });
    }

    private static void Rescan(AppState state, string title)
    {
        // A new game means a new answer about the console's helper, and this panel is the only
        // thing that reads it, so a rescan is a good moment to make sure it is current.
        bool arrived = !string.Equals(_title, title, StringComparison.Ordinal);
        if (arrived) state.RefreshSaveFileInfo();

        // Coming to a title opens the combo where that title was last left; a rescan after a save
        // or a delete leaves it exactly where the user put it.
        string wanted = arrived ? state.Settings.SaveFileCategoryFor(title) ?? string.Empty : Category;
        _title = title;

        string[] local;
        try
        {
            local = state.SaveFiles.Categories(title);

            // A library copied out of the old RaCMAN is full of files with no extension at all, and
            // the console will not load one. Putting the suffix on is the library's own doing; this
            // is only where it gets said out loud, once, on the title's first scan.
            if (arrived && DescribeRenames(state.SaveFiles.EnsureExtensions(title)) is { Length: > 0 } renamed)
            {
                state.AddToast(renamed, ToastKind.Success);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            local = new[] { SaveFileLibrary.DefaultCategory };
            state.AddToast($"Savefile library: {ex.Message}", ToastKind.Error);
        }

        Select(Merge(local, Array.Empty<string>()), wanted);
        RescanFiles(state, title);

        // The console's categories are read on its own time, and the two lists are merged when the
        // answer comes back: the panel is drawn from whatever it has, never blocked on a request.
        if (!state.Ingame || !state.SaveFile.Supported) return;

        _listing = true;
        state.RunQuiet(async () =>
        {
            try
            {
                var categories = await state.Client.SaveFileCategoriesAsync().ConfigureAwait(false);
                state.Post(() =>
                {
                    string wanted = Category;
                    Select(Merge(_categories, categories), wanted);
                    RescanFiles(state, title);
                });
            }
            catch (QwarkStatusException ex) when (ex.Status is Status.Busy)
            {
                // The console is copying a save right now and will not read its own library
                // half way through writing it. The rescan after the transfer has the answer.
            }
            finally
            {
                state.Post(() => _listing = false);
            }
        });
    }

    /// <summary>
    /// The categories both sides know about, in one list with no repeats, and the only place the
    /// order of the combo is decided: alphabetical, except that the default category goes to the
    /// bottom. It is the one nobody named — every save lands in it until the user says otherwise —
    /// so it has no business sitting in the middle of the categories they did name.
    /// </summary>
    public static string[] Merge(IEnumerable<string>? local, IEnumerable<string>? console)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in (local ?? Enumerable.Empty<string>()).Concat(console ?? Enumerable.Empty<string>()))
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
            names.Add(name);
        }

        names.Sort(CompareCategories);
        return names.Count == 0 ? new[] { SaveFileLibrary.DefaultCategory } : names.ToArray();
    }

    /// <summary>Alphabetical, with <see cref="SaveFileLibrary.DefaultCategory"/> last of all.</summary>
    public static int CompareCategories(string? left, string? right)
    {
        bool leftDefault = IsDefault(left);
        if (leftDefault != IsDefault(right)) return leftDefault ? 1 : -1;

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDefault(string? category) =>
        string.Equals(category, SaveFileLibrary.DefaultCategory, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Takes the list and selects <paramref name="wanted"/>, or the first entry when that category
    /// is not there any more — a remembered one that has since been deleted, or one only the
    /// console had before a rescan.
    /// </summary>
    private static void Select(string[] categories, string? wanted)
    {
        _categories = categories;
        _categoryIndex = IndexOf(categories, wanted);
    }

    /// <summary>
    /// Where a category is in the list, and 0 — the first entry — when it is not in it at all,
    /// which is what a title remembered on a category that has since been deleted falls back to.
    /// </summary>
    public static int IndexOf(string[] categories, string? wanted)
    {
        int index = Array.FindIndex(categories, c => string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index;
    }

    /// <summary>Remembers the selected category for this title, so the combo opens on it next time.</summary>
    private static void Remember(AppState state, string title)
    {
        if (string.IsNullOrEmpty(title)) return;
        if (!state.Settings.SetSaveFileCategory(title, Category)) return;

        state.Settings.Save();
    }

    /// <summary>
    /// The one line the panel says when a library carried over from the old RaCMAN is put right.
    /// Empty when there was nothing to put right, which is every scan after the first.
    /// </summary>
    public static string DescribeRenames(IReadOnlyList<SaveFileRename> renames)
    {
        int dropped = 0;
        foreach (var rename in renames)
        {
            if (rename.Dropped) dropped++;
        }

        int renamed = renames.Count - dropped;
        var parts = new List<string>(2);
        if (renamed > 0)
        {
            parts.Add($"{renamed} old save{(renamed == 1 ? string.Empty : "s")} renamed to {SaveFileLibrary.Extension}");
        }

        if (dropped > 0) parts.Add($"{dropped} duplicate{(dropped == 1 ? string.Empty : "s")} dropped");

        return string.Join(", ", parts);
    }

    private static void RescanFiles(AppState state, string title)
    {
        string category = Category;
        LocalSaveFile[] pc;

        try
        {
            pc = state.SaveFiles.FilesWithCrc(title, category);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            pc = Array.Empty<LocalSaveFile>();
            state.AddToast($"Savefile library: {ex.Message}", ToastKind.Error);
        }

        _entries = SaveFileMerge.Build(null, pc);
        if (_fileIndex >= _entries.Length) _fileIndex = -1;

        if (!state.Ingame || !state.SaveFile.Supported) return;

        _listing = true;
        state.RunQuiet(async () =>
        {
            try
            {
                var console = await state.Client.SaveFileListAsync(category).ConfigureAwait(false);
                state.Post(() =>
                {
                    // The category may have moved on while the console was answering.
                    if (!string.Equals(category, Category, StringComparison.Ordinal)) return;

                    string? keep = Selected?.Name;
                    _entries = SaveFileMerge.Build(console, pc);
                    _fileIndex = keep is null ? -1 : Array.FindIndex(_entries, e => e.Name == keep);
                });
            }
            catch (QwarkStatusException ex) when (ex.Status is Status.NotFound or Status.Busy)
            {
                // NOT_FOUND is a category this PC has and the console does not, which is a normal
                // state of the world; BUSY is a listing asked for while a transfer is running,
                // and the rescan after it will have the answer.
            }
            finally
            {
                state.Post(() => _listing = false);
            }
        });
    }
}
