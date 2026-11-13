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
/// No address and no game knowledge reaches this client, and a save or a load moves whatever the
/// game put in the helper's aside buffer. The one thing read out of a save is the planet it was
/// made on (<see cref="SaveFilePlanet"/>), and only to list a category in planet order.
/// </para>
/// <para>
/// A game the release ships premade savefiles for gets one more category, always last:
/// <see cref="PremadeSaveFiles.Category"/>. Its files are read out of the application folder and
/// are read-only: they load like any other save, uploaded to the console's category of the same
/// name the first time, but nothing is stored into them, nothing in them is renamed or deleted,
/// and the row menu offers a copy into the user's own library instead.
/// </para>
/// </summary>
public static class SaveFilesPanel
{
    private static string _title = string.Empty;

    /// <summary>
    /// The game the list was read for. BCES01503 holds three under one title, each with its own
    /// helper and its own premade savefiles, so a new game under the same title is a new list.
    /// </summary>
    private static GameId _game;

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
    public sealed record FileQuestion(SaveFileEntry Entry, string Title, string Category);

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

    /// <summary>The override the user picked from a row's menu, waiting for the answer to its question.</summary>
    private static FileQuestion? _pendingOverride;

    /// <summary>The question the override dialog was last opened for, so it is opened once per question.</summary>
    private static FileQuestion? _shownOverride;

    private const string RenameId = "###savefiles-rename";

    private const string DeleteId = "###savefiles-delete";

    private const string OverrideId = "###savefiles-override";

    /// <summary>
    /// The planets of the saves listed so far, by game and CRC (<see cref="SaveFilePlanetCache"/>),
    /// for the order by planet. Kept across rescans, sessions and games, since the same bytes are
    /// always the same planet.
    /// </summary>
    public static SaveFilePlanetCache Planets { get; } = new();

    /// <summary>The console copies whose planet is being read right now, by game and CRC. Render thread only.</summary>
    private static readonly HashSet<(GameId Game, uint Crc)> _readingPlanets = new();

    /// <summary>The Settings order <see cref="_entries"/> was last sorted by, so a change there re-sorts.</summary>
    private static SaveFileSort _sortedBy;

    /// <summary>Seconds since SAVEFILE_INFO was last re-read while it said the helper had not run.</summary>
    private static float _sinceInfo;

    /// <summary>How often the panel asks again whether the helper has run a frame.</summary>
    private const float NotRunningPollSeconds = 1f;

    /// <summary>How many premade savefiles the release ships for the game on screen.</summary>
    private static int _premadeCount;

    /// <summary>
    /// Whether the last scan asked the console for its side. A scan made before SAVEFILE_INFO said
    /// the console has a library for this game did not, and the next frame that finds it readable
    /// scans again.
    /// </summary>
    private static bool _consoleListed;

    /// <summary>Category and file count, for the smoke-run summary.</summary>
    public static string Summary => $"{_categories.Length}/{_entries.Length}";

    /// <summary>
    /// The premade savefiles of the game on screen and whether the list is open on them, for the
    /// smoke-run summary: "2" or "2 shown".
    /// </summary>
    public static string PremadeSummary => _premadeCount + (IsPremade ? " shown" : string.Empty);

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
        _game = GameId.None;
        _categories = new[] { SaveFileLibrary.DefaultCategory };
        _categoryIndex = 0;
        _newCategory = string.Empty;
        _entries = Array.Empty<SaveFileEntry>();
        _fileIndex = -1;
        _name = string.Empty;
        _premadeCount = 0;
        _consoleListed = false;
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
        _pendingOverride = null;
    }

    private static string Category =>
        _categories.Length == 0 ? SaveFileLibrary.DefaultCategory
        : _categories[Math.Clamp(_categoryIndex, 0, _categories.Length - 1)];

    private static SaveFileEntry? Selected =>
        _fileIndex >= 0 && _fileIndex < _entries.Length ? _entries[_fileIndex] : null;

    /// <summary>Whether the list is open on the premade savefiles.</summary>
    private static bool IsPremade => IsReadOnly(Category);

    /// <summary>
    /// Whether a category's files are read-only: the premade savefiles are, every category of the
    /// user's is not. Rename, Delete and a save into it are offered only where this is false.
    /// </summary>
    public static bool IsReadOnly(string? category) => PremadeSaveFiles.IsCategory(category);

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

        bool hasHelper = HasHelper(state);
        bool helperIn = HelperIn(session, info);
        bool enabled = Ready(state);

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
        else if (state.UiPakNeeded && !info.Installed)
        {
            // Build 56: a console installs the helper out of ui.pak, so without it every save and
            // load is refused (SaveFileError.NoUiPak) until the pak is sent.
            Ui.Warning(AppState.UiPakSaveFilesWarning);
            ImGui.Spacing();
        }
        else if (info.Installed && !info.Running)
        {
            Ui.Hint("The helper is installed but has not run a frame yet. It runs while the game " +
                    "is in play, so a save started on a loading screen or in a menu may wait.");
            ImGui.Spacing();
        }

        // A new title or game, or the console's library becoming readable after the last scan left
        // it out. After a connect or a game switch SAVEFILE_INFO answers after the scan the new
        // title makes, so that scan read only this PC's side: the console-only saves were missing
        // and the premade rows all said "PC" until somebody pressed Rescan.
        if (!string.Equals(_title, title, StringComparison.Ordinal) || _game != session.Game) Rescan(state, title, session.Game);
        else if (!_consoleListed && ConsoleReadable(state)) Rescan(state, title);
        else if (_sortedBy != state.Settings.SaveFileSort) RescanFiles(state, title);

        bool premade = IsPremade;

        // The category's own folder on this PC, since that is where a mirrored file lands; its
        // path is on the button's tooltip rather than printed, being absolute and long enough
        // to wrap. The premade savefiles have none to offer: theirs is in the application folder,
        // which is the release's and not somewhere to put files.
        if (!premade)
        {
            Ui.OpenFolderButton(state, state.SaveFiles.CategoryFolder(title, Category));
            ImGui.SameLine();
        }

        ImGui.BeginDisabled(_busy);
        if (ImGui.SmallButton("Rescan")) Rescan(state, title);

        ImGui.SameLine();
        Ui.Text(Ui.Grey, premade
            ? "Premade savefiles ship with RaCMAN Reloaded and are read-only."
            : state.Settings.MirrorSaveFiles
                ? "Saves are stored on your console and mirrored here."
                : "Saves are stored on your console. Mirroring to this PC is off.");

        if (hasHelper)
        {
            Ui.DebugHint($"Console: {info.Size} bytes in the helper's aside buffer, " +
                         $"installed={info.Installed} running={info.Running}");
            Ui.DebugHint($"Actions: '{save!.Label}' (id {save.Id}, SAVE_ASIDE), '{load!.Label}' (id {load.Id}, LOAD_ASIDE)");
            Ui.DebugHint($"Console library: {QwarkClient.SaveFileConsoleFolder(title, ConsoleCategoryOf(state, title, Category))}");
        }

        ImGui.Spacing();
        DrawCategoryRow(state, title);

        ImGui.Spacing();
        DrawFileList(state, title);

        if (premade)
        {
            Ui.Hint("Right click to copy a save into your own");
        }

        ImGui.Spacing();
        DrawActions(state, title, save, load, enabled);

        if (_status.Length > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            Ui.Text(_busy ? Ui.Yellow : Ui.Grey, _status);
            if (_transferred > 0) Ui.Hint($"{_transferred} bytes");
        }

        if (!premade)
        {
            DrawRenameQuestion(state);
            DrawDeleteQuestion(state);
            DrawOverrideQuestion(state);
        }
    }

    /// <summary>
    /// Whether the game asks for a savefile helper and the console has one to ask: the flagged
    /// ACTIONs say the first, SAVEFILE_INFO the second. The console is the authority.
    /// </summary>
    private static bool HasHelper(AppState state) =>
        state.SaveFile.Supported && state.Describe.SaveAsideAction is not null && state.Describe.LoadAsideAction is not null;

    /// <summary>
    /// Whether a save or a load can be asked for now: INGAME, a helper for the game, the helper in
    /// the game (under RPCS3 only once the RPCS3 patch put it there at this boot, <see cref="HelperIn"/>;
    /// without it saving and loading are refused, so they are greyed out), and nothing else of this
    /// panel's under way.
    /// </summary>
    private static bool Ready(AppState state) =>
        state.Ingame && HasHelper(state) && HelperIn(state.Session, state.SaveFile) && !_busy;

    /// <summary>
    /// The one rule for storing the game's state into a file, which Save on console and a row's
    /// Override both go by: the panel is <paramref name="ready"/>, the game has a set-aside action,
    /// the category is not the read-only premade one, and <paramref name="fileName"/> (the whole
    /// name, .sav and all) is one the console takes.
    /// </summary>
    public static bool CanStore(bool ready, Feature? save, string category, string? fileName) =>
        ready && save is not null && !IsReadOnly(category)
        && !string.IsNullOrWhiteSpace(fileName) && SaveFileNames.Problem(fileName, file: true) is null;

    /// <summary>Whether a row's Override can be offered for <paramref name="entry"/> in <paramref name="category"/> now.</summary>
    public static bool CanOverride(AppState state, SaveFileEntry entry, string category) =>
        CanStore(Ready(state), state.Describe.SaveAsideAction, category, entry.Name);

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
            Ui.Text(Ui.Grey, "reading your console...");
        }

        ImGui.SetNextItemWidth(220);
        bool create = Ui.SubmitTextWithHint("##newcategory", "New category", ref _newCategory, 64);
        ImGui.SameLine();

        // The box stays live while the button beside it is greyed out, so Enter in it asks the same
        // two questions the button does.
        bool canCreate = CanCreateCategory(_newCategory) && !_busy;
        ImGui.BeginDisabled(!canCreate);
        if (ImGui.Button("Create category") || (create && canCreate)) CreateCategory(state, title);

        ImGui.EndDisabled();

        // Why it is greyed out, under the row, while there is something typed to be wrong.
        if (CategoryProblem(_newCategory) is { } problem) Ui.Warning(problem);
    }

    /// <summary>
    /// What is wrong with a typed category name, or null when it makes one (or nothing is typed
    /// yet): a name the console would refuse (<see cref="SaveFileNames"/>), or the premade
    /// savefiles' own, which is not the user's to have.
    /// </summary>
    public static string? CategoryProblem(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;

        string wanted = SaveFileLibrary.Sanitise(typed, SaveFileLibrary.DefaultCategory);
        if (IsReadOnly(wanted)) return "That name belongs to the premade savefiles. Pick another one.";

        return SaveFileNames.Problem(wanted, file: false);
    }

    /// <summary>
    /// Whether a typed name makes a category: anything that is not blank and that the console
    /// takes, except the premade savefiles' own name.
    /// </summary>
    public static bool CanCreateCategory(string? typed) =>
        !string.IsNullOrWhiteSpace(typed) && CategoryProblem(typed) is null;

    /// <summary>A category is made on both sides, so the next save has somewhere to go either way.</summary>
    private static void CreateCategory(AppState state, string title)
    {
        if (!CanCreateCategory(_newCategory)) return;

        string wanted = SaveFileLibrary.Sanitise(_newCategory, SaveFileLibrary.DefaultCategory);

        try
        {
            state.SaveFiles.EnsureCategory(title, wanted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.AddToast($"Category not created: {ex.Message}", ToastKind.Error);
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

    private static void DrawFileList(AppState state, string title)
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
                // highlighted row is always the one the menu is about. The row has no hover text:
                // the Where column says where the save lives, and the menu is found by right-clicking.
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _fileIndex = i;

                DrawRowMenu(state, entry, title);

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
        // A premade savefile has neither to ask.
        if (Selected is { } chosen && !_busy && !IsPremade && ImGui.IsWindowFocused())
        {
            if (ImGui.IsKeyPressed(ImGuiKey.F2, false)) AskRename(chosen, title);
            else if (ImGui.IsKeyPressed(ImGuiKey.Delete, false)) AskDelete(chosen, title);
        }

        ImGui.EndChild();
    }

    /// <summary>
    /// A row's right-click menu: Rename and Delete, the two things that used to be buttons under
    /// the list. Each only asks its question here; nothing changes until the user answers it. A
    /// premade savefile has neither: its menu copies it into one of the user's own categories.
    /// </summary>
    private static void DrawRowMenu(AppState state, SaveFileEntry entry, string title)
    {
        if (!ImGui.BeginPopupContextItem("row-menu")) return;

        const string busyReason = "Wait for the save, load or change under way to finish.";

        if (IsPremade)
        {
            DrawCopyMenu(state, entry, title);
            if (_busy) Ui.Tooltip(busyReason);
            ImGui.EndPopup();
            return;
        }

        if (ImGui.MenuItem("Rename...", "F2", false, !_busy)) AskRename(entry, title);
        if (_busy) Ui.Tooltip(busyReason);

        if (ImGui.MenuItem("Delete...", "Delete", false, !_busy)) AskDelete(entry, title);
        if (_busy) Ui.Tooltip(busyReason);

        // The game's current state over this save, under the rule Save on console goes by.
        bool canOverride = CanOverride(state, entry, Category);
        if (ImGui.MenuItem("Override...", null, false, canOverride)) AskOverride(entry, title, Category);
        Ui.Tooltip(canOverride ? "Replace this save with the game's current state."
            : _busy ? busyReason
            : SaveFileNames.Problem(entry.Name, file: true) ?? "Saving is not possible right now.");

        ImGui.EndPopup();
    }

    /// <summary>
    /// "Copy to my savefiles", with the user's categories under it. The copy is a file in their
    /// own library on this PC, like any save of theirs that the console has not seen yet.
    /// </summary>
    private static void DrawCopyMenu(AppState state, SaveFileEntry entry, string title)
    {
        if (!ImGui.BeginMenu("Copy to my savefiles", !_busy)) return;

        foreach (var category in CopyTargets(_categories))
        {
            // The id is the category: its name is the user's, and a "##" in it is not an id.
            ImGui.PushID(category);
            if (ImGui.MenuItem(category.Replace("##", "#"))) CopyPremade(state, title, entry, category);
            ImGui.PopID();
        }

        ImGui.EndMenu();
    }

    /// <summary>
    /// The categories a premade savefile can be copied into: every one of the user's in the
    /// combo's order, with <see cref="SaveFileLibrary.DefaultCategory"/> always among them, so a
    /// game with no categories yet still has somewhere to put the copy.
    /// </summary>
    public static string[] CopyTargets(IEnumerable<string>? categories) =>
        Merge((categories ?? Enumerable.Empty<string>()).Append(SaveFileLibrary.DefaultCategory), null);

    /// <summary>The toast for a copy: where it went, under which name, or that it was there already.</summary>
    public static string CopyToast(SaveFileEntry entry, PremadeCopy copy)
    {
        string landed = SaveFileLibrary.DisplayName(copy.Name);
        if (copy.AlreadyThere) return $"{entry.DisplayName} is already in {copy.Category} as {landed}";

        return string.Equals(copy.Name, entry.Name, StringComparison.Ordinal)
            ? $"Copied {entry.DisplayName} to {copy.Category}"
            : $"Copied {entry.DisplayName} to {copy.Category} as {landed}";
    }

    private static void CopyPremade(AppState state, string title, SaveFileEntry entry, string category)
    {
        try
        {
            var copy = state.PremadeSaveFiles.CopyTo(state.SaveFiles, title, entry.Name, category, _game);
            state.AddToast(CopyToast(entry, copy), ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.AddToast($"Not copied: {ex.Message}", ToastKind.Error);
        }
    }

    private static void AskRename(SaveFileEntry entry, string title)
    {
        _pendingDelete = null;
        _pendingOverride = null;
        _pendingRename = new FileQuestion(entry, title, Category);
        _renameTo = entry.DisplayName;
    }

    private static void AskDelete(SaveFileEntry entry, string title)
    {
        _pendingRename = null;
        _pendingOverride = null;
        _pendingDelete = new FileQuestion(entry, title, Category);
    }

    /// <summary>
    /// A row's Override: it only asks its question, as Delete does, since the save it replaces is
    /// gone once the console has stored over it. Nothing is sent until <see cref="ConfirmOverride"/>.
    /// </summary>
    public static FileQuestion AskOverride(SaveFileEntry entry, string title, string category)
    {
        _pendingRename = null;
        _pendingDelete = null;
        var question = new FileQuestion(entry, title, category);
        _pendingOverride = question;
        return question;
    }

    /// <summary>
    /// The yes to an override's question: stores the game's state over the save it named when
    /// storing is still possible (<see cref="CanOverride"/>), and says whether it started.
    /// </summary>
    public static bool ConfirmOverride(AppState state, FileQuestion question)
    {
        if (ReferenceEquals(_pendingOverride, question)) _pendingOverride = null;
        if (!CanOverride(state, question.Entry, question.Category)) return false;

        StartOverride(state, question.Title, question.Category, question.Entry);
        return true;
    }

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

        /// <summary>A name the console would refuse, or take and then never list; <see cref="NameProblem"/> says why.</summary>
        Invalid,
    }

    /// <summary>
    /// The file name a rename to <paramref name="typed"/> gives the save, the same way a save is
    /// named: kept inside the library and given the .sav suffix.
    /// </summary>
    public static string RenameTarget(string typed) =>
        SaveFileLibrary.EnsureExtension(SaveFileLibrary.Sanitise(typed, "savefile"));

    /// <summary>
    /// Why the file name <paramref name="typed"/> becomes would not do on the console, or null when
    /// it would (or nothing is typed yet). One check for a rename and for a save, the console's own
    /// (<see cref="SaveFileNames"/>).
    /// </summary>
    public static string? NameProblem(string? typed) =>
        string.IsNullOrWhiteSpace(typed) ? null : SaveFileNames.Problem(RenameTarget(typed), file: true);

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
        if (NameProblem(typed) is not null) return RenameCheck.Invalid;

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
        SaveFileLocation.Console => "Only your console has this save, so only its copy is renamed.",
        SaveFileLocation.Pc => "Only this PC has this save, so only its copy is renamed.",
        _ => "Your console's copy and this PC's are both renamed.",
    };

    /// <summary>What the delete dialog says will be deleted, by where the save lives.</summary>
    public static string DeleteWhere(SaveFileEntry entry) => entry.Location switch
    {
        SaveFileLocation.Console => $"{entry.DisplayName} will be deleted from your console. This PC has no copy of it.",
        SaveFileLocation.Pc => $"{entry.DisplayName} will be deleted from this PC. Your console has no copy of it.",
        _ => $"{entry.DisplayName} will be deleted from your console and from this PC.",
    };

    /// <summary>What the override dialog says will be replaced, by where the save lives.</summary>
    public static string OverrideWhere(SaveFileEntry entry) => entry.Location switch
    {
        SaveFileLocation.Console => $"{entry.DisplayName} will be replaced with the game's current state on your console.",
        _ => $"{entry.DisplayName} will be replaced with the game's current state on your console and on this PC.",
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
        else if (check == RenameCheck.Invalid && NameProblem(_renameTo) is { } problem)
        {
            Ui.Warning(problem);
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
        Ui.Hint("This action is permanent.");
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

    /// <summary>
    /// The question before the game's state is stored over a save, the way Delete asks before a
    /// save goes: which file, then Override and Cancel, with Cancel the answer Enter gives.
    /// Override is greyed out while storing is not possible, under the menu item's rule.
    /// </summary>
    private static void DrawOverrideQuestion(AppState state)
    {
        var question = _pendingOverride;
        if (question is null)
        {
            _shownOverride = null;
            return;
        }

        string label = $"Override {question.Entry.DisplayName}?{OverrideId}";
        if (!ReferenceEquals(question, _shownOverride))
        {
            _shownOverride = question;
            ImGui.OpenPopup(label);
        }
        else if (!ImGui.IsPopupOpen(label))
        {
            _pendingOverride = null;
            _shownOverride = null;
            return;
        }

        var centre = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(560, 0), ImGuiCond.Appearing);

        bool open = true;
        if (!ImGui.BeginPopupModal(label, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) _pendingOverride = null;
            return;
        }

        Ui.Paragraph(OverrideWhere(question.Entry));
        ImGui.Spacing();
        Ui.Hint("The old save cannot be recovered.");
        ImGui.Separator();

        ImGui.BeginDisabled(!CanOverride(state, question.Entry, question.Category));
        bool confirm = ImGui.Button("Override", new Vector2(140, 0));
        ImGui.EndDisabled();

        ImGui.SameLine();
        bool cancel = ImGui.Button("Cancel", new Vector2(120, 0));

        // Enter on a dialog that has just opened answers no.
        ImGui.SetItemDefaultFocus();

        if (confirm)
        {
            ConfirmOverride(state, question);
            ImGui.CloseCurrentPopup();
        }
        else if (cancel || !open)
        {
            _pendingOverride = null;
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
        // Nothing is saved into the premade savefiles: they are the release's.
        bool premade = IsPremade;
        string? problem = NameProblem(_name);
        bool canSave = CanStore(enabled, save, Category, string.IsNullOrWhiteSpace(_name) ? null : RenameTarget(_name));
        ImGui.BeginDisabled(!canSave);
        if (ImGui.Button("Save on console") || (saveNow && canSave)) StartSave(state, title);
        ImGui.EndDisabled();
        if (premade) Ui.Tooltip("The premade savefiles are read-only. Pick one of your own categories to save into.");

        // Rename and Delete are on each row's right-click menu, and on F2 and Delete.
        ImGui.SameLine();
        ImGui.BeginDisabled(!enabled || load is null || selected is null);
        if (ImGui.Button("Load into game")) StartLoad(state, title, selected!.Value);
        ImGui.EndDisabled();

        // Why Save is greyed out, under the row, for a name the console would not take.
        if (problem is not null && !premade) Ui.Warning(problem);
    }

    /// <summary>
    /// The toast for a save or a load that did not happen: one the console refused outright says
    /// so, with its reason (the game busy, no save yet), and one that stopped on the way says it did
    /// not finish, with what stopped it.
    /// </summary>
    public static string FailureToast(bool load, Exception ex) =>
        $"{(load ? "Load" : "Save")} {(ex is SaveFileTransfer.RefusedException ? "refused" : "failed")}: {ex.Message}";

    /// <summary>
    /// SAVEFILE_STORE, poll SAVEFILE_INFO to completion, then mirror the file down when mirroring
    /// is on. The console writes its own file whole before it says it is finished, so a save that
    /// stops half way leaves the console's library exactly as it was.
    /// </summary>
    private static void StartSave(AppState state, string title)
    {
        string category = Category;
        string name = RenameTarget(_name);
        var library = state.SaveFiles;
        bool mirror = state.Settings.MirrorSaveFiles;

        _busy = true;
        _transferred = 0;
        _status = "Asking your console to save...";

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
    /// A save over an existing file: the store <see cref="StartSave"/> makes, to the file's own
    /// name and category (<see cref="SaveFileTransfer.OverrideAsync"/>), then a rescan, so the row
    /// carries the new CRC on both sides.
    /// </summary>
    private static void StartOverride(AppState state, string title, string category, SaveFileEntry entry)
    {
        var library = state.SaveFiles;
        bool mirror = state.Settings.MirrorSaveFiles;

        _busy = true;
        _transferred = 0;
        _status = $"Asking your console to save over {entry.DisplayName}...";

        var status = new Progress<string>(text => state.Post(() => _status = text));
        var bytes = new Progress<long>(count => state.Post(() => _transferred = count));

        state.Run(async () =>
        {
            try
            {
                var path = await SaveFileTransfer.OverrideAsync(
                    state.Client, library, title, category, entry, mirror,
                    timeout: null, status: status, bytes: bytes)
                    .ConfigureAwait(false);

                state.Post(() =>
                {
                    _status = path.Length > 0 ? "Saved over and mirrored successfully." : "Saved over successfully.";
                    Rescan(state, title);
                    state.AddToast($"Saved over '{entry.DisplayName}'", ToastKind.Success);
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
        // A premade savefile's bytes come out of the application folder; everything after that is
        // the same load, into the console's premade category (one per game under a title that
        // holds several, ConsoleCategoryOf).
        string category = ConsoleCategoryOf(state, title, Category);
        ISaveFileSource library = IsReadOnly(category) ? state.PremadeSaveFiles : state.SaveFiles;

        _busy = true;
        _transferred = 0;
        _status = SaveFileMerge.PlanLoad(entry) == SaveFileLoadPlan.UploadThenRestore
            ? $"Uploading {entry.DisplayName} to your console..."
            : $"Asking your console to load {entry.DisplayName}...";

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
                        ? "Uploaded to your console and loaded into the game"
                        : "Loaded into the game from your console";
                    state.AddToast($"Loaded '{entry.DisplayName}'", ToastKind.Success);
                    if (uploaded) Rescan(state, title);

                    // A load may start a run, and with it the manips or their refusal.
                    state.RefreshManips();
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
                state.Post(() => state.AddToast($"Not renamed: {ex.Message}", ToastKind.Error));
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
                state.Post(() => state.AddToast($"Not deleted: {ex.Message}", ToastKind.Error));
            }
            finally
            {
                state.Post(() => _busy = false);
            }
        });
    }

    /// <summary>
    /// Whether the console's side of the library can be read now: a game is up and SAVEFILE_INFO
    /// said the console has a helper, and with it a library, for it.
    /// </summary>
    private static bool ConsoleReadable(AppState state) => state.Ingame && state.SaveFile.Supported;

    /// <summary>
    /// The console's own name for a category of the combo. The premade one is
    /// <see cref="PremadeSaveFiles.ConsoleCategory"/>, which carries the game under a title that
    /// holds several; every other category is the user's and is called the same on both sides.
    /// </summary>
    private static string ConsoleCategoryOf(AppState state, string title, string category) =>
        IsReadOnly(category) ? state.PremadeSaveFiles.ConsoleCategory(title, _game) : category;

    /// <summary>Whether an answer asked for <paramref name="title"/> and <paramref name="game"/> is still about the list on screen.</summary>
    private static bool StillOn(string title, GameId game) =>
        string.Equals(title, _title, StringComparison.Ordinal) && game == _game;

    private static void Rescan(AppState state, string title, GameId? game = null)
    {
        // A new game means a new answer about the console's helper, and this panel is the only
        // thing that reads it, so a rescan is a good moment to make sure it is current.
        var playing = game ?? _game;
        bool arrived = !StillOn(title, playing);
        if (arrived) state.RefreshSaveFileInfo();

        // Coming to a title opens the combo where that title was last left; a rescan after a save
        // or a delete leaves it exactly where the user put it.
        string wanted = arrived ? state.Settings.SaveFileCategoryFor(title) ?? string.Empty : Category;
        _title = title;
        _game = playing;

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

        // The premade savefiles are a category of their own, at the bottom, for a game the release
        // ships some for: by the game as well as the title, since a title can hold several.
        _premadeCount = state.PremadeSaveFiles.Files(title, playing).Length;
        bool premade = _premadeCount > 0;

        Select(Merge(local, Array.Empty<string>(), premade), wanted);
        RescanFiles(state, title);

        // The console's categories are read on its own time, and the two lists are merged when the
        // answer comes back: the panel is drawn from whatever it has, never blocked on a request.
        _consoleListed = ConsoleReadable(state);
        if (!_consoleListed) return;

        _listing = true;
        state.RunQuiet(async () =>
        {
            try
            {
                var categories = await state.Client.SaveFileCategoriesAsync().ConfigureAwait(false);
                state.Post(() =>
                {
                    // An answer for a title or game the panel has since left would put that one's
                    // categories in this one's combo.
                    if (!StillOn(title, playing)) return;

                    string wanted = Category;
                    Select(Merge(_categories, categories, premade), wanted);
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
    /// <para>
    /// Below even that, when <paramref name="premade"/> says the release ships some for this game,
    /// comes <see cref="PremadeSaveFiles.Category"/>. It is never taken from either side's list:
    /// the console has a folder of that name once a premade savefile has been loaded, and that
    /// folder is the premade category's, not one of the user's.
    /// </para>
    /// </summary>
    public static string[] Merge(IEnumerable<string>? local, IEnumerable<string>? console, bool premade = false)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in (local ?? Enumerable.Empty<string>()).Concat(console ?? Enumerable.Empty<string>()))
        {
            if (string.IsNullOrWhiteSpace(name) || IsReadOnly(name) || !seen.Add(name)) continue;
            names.Add(name);
        }

        names.Sort(CompareCategories);
        if (names.Count == 0) names.Add(SaveFileLibrary.DefaultCategory);
        if (premade) names.Add(PremadeSaveFiles.Category);
        return names.ToArray();
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

    /// <summary>
    /// A category's rows from the two listings. The premade category lists the release's files and
    /// nothing else, each one "both" once the console has its copy (<see cref="SaveFileMerge.BuildPremade"/>).
    /// Every category, the premade one included, is in the order <paramref name="sort"/> says
    /// (<see cref="SaveFileOrder"/>), by planet with each row's planet from <paramref name="planetOf"/>.
    /// </summary>
    public static SaveFileEntry[] Rows(string category, IEnumerable<ConsoleSaveFile>? console, IEnumerable<LocalSaveFile>? pc,
        GameId game = GameId.None, SaveFileSort sort = SaveFileSort.ByPlanet, Func<SaveFileEntry, int?>? planetOf = null) =>
        SaveFileOrder.Sort(IsReadOnly(category) ? SaveFileMerge.BuildPremade(console, pc) : SaveFileMerge.Build(console, pc),
            game, sort, planetOf);

    /// <summary>
    /// A row's planet as <paramref name="planets"/> knows it: this PC's copy when there is one,
    /// since that is the copy a load sends when the two differ, else the console's.
    /// </summary>
    public static Func<SaveFileEntry, int?> PlanetOf(SaveFilePlanetCache planets, GameId game) =>
        entry => planets.Planet(game, entry.OnPc ? entry.PcCrc : entry.ConsoleCrc);

    /// <summary>
    /// Reads the planet of every file of <paramref name="pc"/> in <paramref name="folder"/> that
    /// <paramref name="planets"/> does not know yet, a few bytes of each (<see cref="SaveFilePlanetCache.Local"/>).
    /// </summary>
    public static void ReadPcPlanets(SaveFilePlanetCache planets, GameId game, string? folder, IEnumerable<LocalSaveFile> pc)
    {
        if (folder is null || SaveFilePlanet.RangeFor(game) is null) return;
        foreach (var file in pc) planets.Local(game, Path.Combine(folder, file.Name), file.Crc);
    }

    /// <summary>
    /// The saves of <paramref name="rows"/> that only the console has whose planet is still to be
    /// read and can be (<see cref="SaveFilePlanet.ReadableOnConsole"/>).
    /// </summary>
    public static SaveFileEntry[] ConsolePlanetsToRead(SaveFilePlanetCache planets, GameId game, IEnumerable<SaveFileEntry> rows) =>
        SaveFilePlanet.ReadableOnConsole(game)
            ? rows.Where(e => e.OnConsole && !e.OnPc && !planets.TryGet(game, e.ConsoleCrc, out _)).ToArray()
            : Array.Empty<SaveFileEntry>();

    /// <summary>
    /// Reads the planet of each of <paramref name="rows"/> off the console, from
    /// <paramref name="consoleCategory"/> under <paramref name="title"/>, one FILE_READ of each
    /// file's head, and says how many it learnt. Nobody asked for this, so nothing is said when it
    /// fails: a file the console no longer has is skipped, and anything else (the session gone, the
    /// console busy) stops it, those saves staying below the others until the next scan.
    /// </summary>
    public static async Task<int> ReadConsolePlanetsAsync(QwarkClient client, SaveFilePlanetCache planets, GameId game,
        string title, string consoleCategory, IEnumerable<SaveFileEntry> rows, CancellationToken cancellationToken = default)
    {
        int read = 0;
        foreach (var entry in rows)
        {
            try
            {
                await planets.ConsoleAsync(client, game, QwarkClient.SaveFileConsolePath(title, consoleCategory, entry.Name),
                    entry.ConsoleCrc, entry.ConsoleSize, cancellationToken).ConfigureAwait(false);
                read++;
            }
            catch (QwarkStatusException ex) when (ex.Status == Status.NotFound)
            {
            }
            catch (Exception ex) when (ex is QwarkStatusException or ProtocolException or IOException
                                          or InvalidOperationException or OperationCanceledException)
            {
                break;
            }
        }

        return read;
    }

    /// <summary>The rows on screen sorted again, the selection kept, after more planets became known.</summary>
    private static void Resort(AppState state)
    {
        string? keep = Selected?.Name;
        _sortedBy = state.Settings.SaveFileSort;
        _entries = SaveFileOrder.Sort(_entries, _game, _sortedBy, PlanetOf(Planets, _game));
        _fileIndex = keep is null ? -1 : Array.FindIndex(_entries, e => e.Name == keep);
    }

    /// <summary>
    /// Off to the console for the planets of the console-only saves on screen, by planet only; the
    /// list is sorted again when they are in, if it is still the same list.
    /// </summary>
    private static void ReadConsolePlanets(AppState state, string title, string category, string consoleCategory, GameId game)
    {
        if (state.Settings.SaveFileSort != SaveFileSort.ByPlanet) return;

        // A listing that comes back while an earlier one's reads are still out asks only for the rest.
        var missing = ConsolePlanetsToRead(Planets, game, _entries)
            .Where(e => !_readingPlanets.Contains((game, e.ConsoleCrc)))
            .ToArray();
        if (missing.Length == 0) return;
        foreach (var entry in missing) _readingPlanets.Add((game, entry.ConsoleCrc));

        state.RunQuiet(async () =>
        {
            int read = 0;
            try
            {
                read = await ReadConsolePlanetsAsync(state.Client, Planets, game, title, consoleCategory, missing)
                    .ConfigureAwait(false);
            }
            finally
            {
                state.Post(() =>
                {
                    foreach (var entry in missing) _readingPlanets.Remove((game, entry.ConsoleCrc));
                    if (read > 0 && string.Equals(category, Category, StringComparison.Ordinal) && StillOn(title, game))
                    {
                        Resort(state);
                    }
                });
            }
        });
    }

    private static void RescanFiles(AppState state, string title)
    {
        string category = Category;
        GameId game = _game;
        var sort = state.Settings.SaveFileSort;
        LocalSaveFile[] pc;

        try
        {
            // The premade savefiles are read where the release put them; the user's own library is
            // never asked about the premade category.
            pc = IsReadOnly(category)
                ? state.PremadeSaveFiles.FilesWithCrc(title, game)
                : state.SaveFiles.FilesWithCrc(title, category);

            // By planet, each file's planet out of its own few bytes, read once per CRC.
            if (sort == SaveFileSort.ByPlanet)
            {
                string? folder = IsReadOnly(category)
                    ? state.PremadeSaveFiles.FolderFor(title, game)
                    : state.SaveFiles.CategoryFolder(title, category);
                ReadPcPlanets(Planets, game, folder, pc);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            pc = Array.Empty<LocalSaveFile>();
            state.AddToast($"Savefile library: {ex.Message}", ToastKind.Error);
        }

        _sortedBy = sort;
        _entries = Rows(category, null, pc, game, sort, PlanetOf(Planets, game));
        if (_fileIndex >= _entries.Length) _fileIndex = -1;

        if (!ConsoleReadable(state)) return;

        string consoleCategory = ConsoleCategoryOf(state, title, category);
        _listing = true;
        state.RunQuiet(async () =>
        {
            try
            {
                var console = await state.Client.SaveFileListAsync(consoleCategory).ConfigureAwait(false);
                state.Post(() =>
                {
                    // The list may have moved on while the console was answering: to another
                    // category, or to another title or game whose category has the same name, whose
                    // rows these are not.
                    if (!string.Equals(category, Category, StringComparison.Ordinal) || !StillOn(title, game)) return;

                    string? keep = Selected?.Name;
                    _sortedBy = state.Settings.SaveFileSort;
                    _entries = Rows(category, console, pc, game, _sortedBy, PlanetOf(Planets, game));
                    _fileIndex = keep is null ? -1 : Array.FindIndex(_entries, e => e.Name == keep);
                    ReadConsolePlanets(state, title, category, consoleCategory, game);
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
