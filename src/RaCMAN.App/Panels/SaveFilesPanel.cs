using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The savefile manager. Two flagged ACTIONs and the savefile block (section 5.12) are the whole
/// of it: the SAVE_ASIDE action asks the game to park its save in the console's aside buffer, the
/// LOAD_ASIDE action asks it to take back whatever is in there, and this panel moves the bytes
/// between that buffer and a local library. No address, no game knowledge and no savefile format
/// ever reaches this client, and nothing is written to the console's filesystem.
/// </summary>
public static class SaveFilesPanel
{
    private static string _title = string.Empty;
    private static string[] _categories = { SaveFileLibrary.DefaultCategory };
    private static int _categoryIndex;
    private static string _newCategory = string.Empty;
    private static string[] _files = Array.Empty<string>();
    private static int _fileIndex = -1;
    private static string _name = string.Empty;
    private static string _renameTo = string.Empty;

    private static bool _busy;
    private static string _status = string.Empty;
    private static long _transferred;
    private static bool _confirmDelete;

    /// <summary>Category and file count, for the smoke-run summary.</summary>
    public static string Summary => $"{_categories.Length}/{_files.Length}";

    public static void Reset()
    {
        _title = string.Empty;
        _categories = new[] { SaveFileLibrary.DefaultCategory };
        _categoryIndex = 0;
        _newCategory = string.Empty;
        _files = Array.Empty<string>();
        _fileIndex = -1;
        _name = string.Empty;
        _renameTo = string.Empty;
        ClearTransfer();
    }

    /// <summary>The progress readout describes a transfer against a session that is now gone.</summary>
    public static void ClearTransfer()
    {
        _status = string.Empty;
        _transferred = 0;
        _confirmDelete = false;
    }

    private static string Category =>
        _categories.Length == 0 ? SaveFileLibrary.DefaultCategory
        : _categories[Math.Clamp(_categoryIndex, 0, _categories.Length - 1)];

    private static string? SelectedFile =>
        _fileIndex >= 0 && _fileIndex < _files.Length ? _files[_fileIndex] : null;

    public static void Draw(AppState state)
    {
        Ui.Heading("Save files");

        var session = state.Session;
        string title = session.TitleId;

        if (!state.Connected)
        {
            Ui.Hint("Connect to a console to manage save files.");
            return;
        }

        if (string.IsNullOrEmpty(title))
        {
            Ui.Hint("No game is running, so there is no savefile library to show.");
            return;
        }

        var save = state.Describe.SaveAsideAction;
        var load = state.Describe.LoadAsideAction;
        var info = state.SaveFile;

        // The console is the authority on whether it has a helper for this game: the flagged
        // ACTIONs say a game asks for one, SAVEFILE_INFO says whether there is one to ask.
        bool hasHelper = info.Supported && save is not null && load is not null;
        bool enabled = state.Ingame && hasHelper && !_busy;

        if (state.CodePatchesUnsupported)
        {
            Ui.Warning("The savefile helper is a code cave the console branches the game into, and " +
                       "RPCS3 cannot apply one, so saving and loading are not available here.");
        }
        else if (!hasHelper)
        {
            Ui.Warning("This game has no savefile helper, so saving and loading are not available.");
        }
        else if (!state.Ingame)
        {
            Ui.Warning($"Saving and loading need INGAME (state is {session.State}).");
        }
        else if (!info.Running)
        {
            Ui.Hint("The helper is installed but has not run a frame yet. It runs while the game " +
                    "is in play, so a save started on a loading screen or in a menu may wait.");
        }

        if (!string.Equals(_title, title, StringComparison.Ordinal)) Rescan(state, title);

        ImGui.Spacing();

        // The category's own folder, since that is the one a file lands in; its path is on the
        // button's tooltip rather than printed, being absolute and long enough to wrap.
        Ui.OpenFolderButton(state, state.SaveFiles.CategoryFolder(title, Category));
        if (hasHelper)
        {
            Ui.DebugHint($"Console: {info.Size} bytes in the helper's aside buffer, " +
                         $"installed={info.Installed} running={info.Running}");
            Ui.DebugHint($"Actions: '{save!.Label}' (id {save.Id}, SAVE_ASIDE), '{load!.Label}' (id {load.Id}, LOAD_ASIDE)");
        }

        ImGui.Spacing();
        DrawCategoryRow(state, title);

        ImGui.Spacing();
        DrawFileList(state);

        ImGui.Spacing();
        DrawActions(state, title, save, load, enabled);

        if (_status.Length > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(_busy ? Ui.Yellow : Ui.Grey, _status);
            if (_transferred > 0) Ui.Hint($"{_transferred} bytes");
        }
    }

    private static void DrawCategoryRow(AppState state, string title)
    {
        ImGui.SetNextItemWidth(220);
        int index = Math.Clamp(_categoryIndex, 0, Math.Max(0, _categories.Length - 1));
        if (ImGui.Combo("Category", ref index, _categories, _categories.Length))
        {
            _categoryIndex = index;
            _fileIndex = -1;
            RescanFiles(state, title);
        }

        ImGui.SameLine();
        if (ImGui.Button("Rescan")) Rescan(state, title);

        ImGui.SetNextItemWidth(220);
        Ui.InputTextWithHint("##newcategory", "New category", ref _newCategory, 64);
        ImGui.SameLine();
        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_newCategory));
        if (ImGui.Button("Create category"))
        {
            try
            {
                var folder = state.SaveFiles.EnsureCategory(title, _newCategory);
                string created = Path.GetFileName(folder);
                _newCategory = string.Empty;
                Rescan(state, title);
                _categoryIndex = Math.Max(0, Array.IndexOf(_categories, created));
                RescanFiles(state, title);
                state.AddToast($"Category '{created}' created", ToastKind.Success);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.AddToast($"Could not create the category: {ex.Message}", ToastKind.Error);
            }
        }

        ImGui.EndDisabled();
    }

    private static void DrawFileList(AppState state)
    {
        if (!ImGui.BeginChild("##savefiles", new Vector2(-1, 220), ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        if (_files.Length == 0)
        {
            Ui.Hint("No files in this category yet.");
        }
        else
        {
            for (int i = 0; i < _files.Length; i++)
            {
                // The row shows the name without the .sav every file in here ends in; the id after
                // the ## is the file itself, so two files that only differ by their suffix are
                // still two rows to ImGui.
                if (!ImGui.Selectable($"{SaveFileLibrary.DisplayName(_files[i])}##{_files[i]}", _fileIndex == i)) continue;

                _fileIndex = i;
                _renameTo = SaveFileLibrary.DisplayName(_files[i]);
                _confirmDelete = false;
            }
        }

        ImGui.EndChild();
    }

    private static void DrawActions(AppState state, string title, Feature? save, Feature? load, bool enabled)
    {
        ImGui.SetNextItemWidth(260);
        Ui.InputTextWithHint("##name", "File name to save as", ref _name, 64);
        ImGui.SameLine();

        ImGui.BeginDisabled(!enabled || save is null || string.IsNullOrWhiteSpace(_name));
        if (ImGui.Button("Save from console")) StartSave(state, title, save!.Id);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(!enabled || load is null || SelectedFile is null);
        if (ImGui.Button("Load to console")) StartLoad(state, title, load!.Id);
        ImGui.EndDisabled();

        ImGui.BeginDisabled(SelectedFile is null || _busy);

        ImGui.SetNextItemWidth(260);
        Ui.InputTextWithHint("##rename", "Rename to", ref _renameTo, 64);
        ImGui.SameLine();
        if (ImGui.Button("Rename"))
        {
            try
            {
                // The box holds a name without a suffix, the library takes whole file names: the
                // .sav goes back on here so the file on disk keeps it.
                string renamed = SaveFileLibrary.EnsureExtension(_renameTo);
                state.SaveFiles.Rename(title, Category, SelectedFile!, renamed);
                state.AddToast($"Renamed to {SaveFileLibrary.DisplayName(SaveFileLibrary.Sanitise(renamed, "savefile"))}",
                    ToastKind.Success);
                RescanFiles(state, title);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.AddToast($"Rename failed: {ex.Message}", ToastKind.Error);
            }
        }

        ImGui.SameLine();
        if (_confirmDelete)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
            if (ImGui.Button($"Delete '{SaveFileLibrary.DisplayName(SelectedFile)}'?"))
            {
                _confirmDelete = false;
                try
                {
                    state.SaveFiles.Delete(title, Category, SelectedFile!);
                    state.AddToast("Deleted", ToastKind.Success);
                    _fileIndex = -1;
                    RescanFiles(state, title);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    state.AddToast($"Delete failed: {ex.Message}", ToastKind.Error);
                }
            }

            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) _confirmDelete = false;
        }
        else if (ImGui.Button("Delete..."))
        {
            _confirmDelete = true;
        }

        ImGui.EndDisabled();
    }

    private static void StartSave(AppState state, string title, byte saveActionId)
    {
        string category = Category;
        string name = SaveFileLibrary.EnsureExtension(_name);
        var library = state.SaveFiles;

        _busy = true;
        _transferred = 0;
        _status = "Triggering the set-aside action...";

        var status = new Progress<string>(text => state.Post(() => _status = text));
        var bytes = new Progress<long>(count => state.Post(() => _transferred = count));

        state.Run(async () =>
        {
            try
            {
                var data = await SaveFileTransfer.DownloadAsync(
                    state.Client, saveActionId, timeout: null, status: status, bytes: bytes)
                    .ConfigureAwait(false);

                var path = library.Write(title, category, name, data);
                state.Post(() =>
                {
                    _status = $"Saved {data.Length} bytes to {path}";
                    _transferred = data.Length;
                    _name = string.Empty;
                    Rescan(state, title);
                    _categoryIndex = Math.Max(0, Array.IndexOf(_categories, SaveFileLibrary.Sanitise(category, SaveFileLibrary.DefaultCategory)));
                    RescanFiles(state, title);
                    _fileIndex = Array.IndexOf(_files, Path.GetFileName(path));
                });
            }
            catch (SaveFileTransfer.NotAnsweredException ex)
            {
                state.Post(() =>
                {
                    _status = ex.Message;
                    state.AddToast($"The save did not finish: {ex.Message}", ToastKind.Error);
                });
            }
            finally
            {
                state.Post(() => _busy = false);
            }
        }, $"Saved '{SaveFileLibrary.DisplayName(SaveFileLibrary.Sanitise(name, "savefile"))}'");
    }

    private static void StartLoad(AppState state, string title, byte loadActionId)
    {
        string category = Category;
        string file = SelectedFile!;
        var library = state.SaveFiles;

        string shown = SaveFileLibrary.DisplayName(file);

        _busy = true;
        _transferred = 0;
        _status = $"Uploading {shown}...";

        var status = new Progress<string>(text => state.Post(() => _status = text));
        var bytes = new Progress<long>(count => state.Post(() => _transferred = count));

        state.Run(async () =>
        {
            try
            {
                var data = library.Read(title, category, file);
                await SaveFileTransfer.UploadAsync(state.Client, loadActionId, data,
                        timeout: null, status: status, bytes: bytes)
                    .ConfigureAwait(false);
                state.Post(() => _status = $"Sent {data.Length} bytes and triggered the load action");
            }
            catch (SaveFileTransfer.NotAnsweredException ex)
            {
                state.Post(() =>
                {
                    _status = ex.Message;
                    state.AddToast($"The load did not finish: {ex.Message}", ToastKind.Error);
                });
            }
            finally
            {
                state.Post(() => _busy = false);
            }
        }, $"Loaded '{shown}' onto the console");
    }

    private static void Rescan(AppState state, string title)
    {
        // A new game means a new answer about the console's helper, and this panel is the only
        // thing that reads it, so a rescan is a good moment to make sure it is current.
        if (!string.Equals(_title, title, StringComparison.Ordinal)) state.RefreshSaveFileInfo();

        _title = title;
        try
        {
            _categories = state.SaveFiles.Categories(title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _categories = new[] { SaveFileLibrary.DefaultCategory };
            state.AddToast($"Savefile library: {ex.Message}", ToastKind.Error);
        }

        _categoryIndex = Math.Clamp(_categoryIndex, 0, Math.Max(0, _categories.Length - 1));
        RescanFiles(state, title);
    }

    private static void RescanFiles(AppState state, string title)
    {
        try
        {
            _files = state.SaveFiles.Files(title, Category);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _files = Array.Empty<string>();
            state.AddToast($"Savefile library: {ex.Message}", ToastKind.Error);
        }

        if (_fileIndex >= _files.Length) _fileIndex = -1;
    }
}
