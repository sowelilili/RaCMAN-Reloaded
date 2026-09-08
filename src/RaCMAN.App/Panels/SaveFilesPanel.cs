using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The savefile manager. Two flagged ACTIONs and the generic file ops are the whole of it: the
/// SAVE_ASIDE action makes the game write <c>USRDIR/tempsave</c>, the LOAD_ASIDE action makes it
/// read that file back, and this panel moves the bytes between there and a local library. No
/// address, no game knowledge and no savefile format ever reaches this client.
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
            Ui.Hint("Connect to a console: the save manager drives the game's own save and load actions.");
            return;
        }

        if (string.IsNullOrEmpty(title))
        {
            Ui.Hint("No game is running, so there is no savefile library and no set-aside actions.");
            return;
        }

        var save = state.Describe.SaveAsideAction;
        var load = state.Describe.LoadAsideAction;
        bool hasHelper = save is not null && load is not null;
        bool enabled = state.Ingame && hasHelper && !_busy;

        if (!hasHelper)
        {
            Ui.Warning("This game has no savefile helper, so set-aside save and load are not available.");
        }
        else if (!state.Ingame)
        {
            Ui.Warning($"Saving and loading need INGAME (state is {session.State}).");
        }

        if (!string.Equals(_title, title, StringComparison.Ordinal)) Rescan(state, title);

        ImGui.Spacing();
        Ui.Hint($"Library: {state.SaveFiles.CategoryFolder(title, Category)}");
        Ui.DebugHint($"Console: {SaveFileLibrary.TempSavePath(title)}");
        if (hasHelper)
        {
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
                if (!ImGui.Selectable(_files[i], _fileIndex == i)) continue;

                _fileIndex = i;
                _renameTo = _files[i];
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

        Ui.Hint("Save triggers the game's set-aside action, waits two seconds and downloads tempsave. "
                + "Load uploads the selected file over tempsave and triggers the load action.");

        ImGui.Spacing();
        ImGui.BeginDisabled(SelectedFile is null || _busy);

        ImGui.SetNextItemWidth(260);
        Ui.InputTextWithHint("##rename", "Rename to", ref _renameTo, 64);
        ImGui.SameLine();
        if (ImGui.Button("Rename"))
        {
            try
            {
                state.SaveFiles.Rename(title, Category, SelectedFile!, _renameTo);
                state.AddToast($"Renamed to {SaveFileLibrary.Sanitise(_renameTo, "savefile")}", ToastKind.Success);
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
            if (ImGui.Button($"Delete '{SelectedFile}'?"))
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
        string name = _name;
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
                    state.Client, saveActionId, title, SaveFileTransfer.DefaultSettle, status, bytes).ConfigureAwait(false);

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
            finally
            {
                state.Post(() => _busy = false);
            }
        }, $"Saved '{SaveFileLibrary.Sanitise(name, "savefile")}'");
    }

    private static void StartLoad(AppState state, string title, byte loadActionId)
    {
        string category = Category;
        string file = SelectedFile!;
        var library = state.SaveFiles;

        _busy = true;
        _transferred = 0;
        _status = $"Uploading {file}...";

        var status = new Progress<string>(text => state.Post(() => _status = text));
        var bytes = new Progress<long>(count => state.Post(() => _transferred = count));

        state.Run(async () =>
        {
            try
            {
                var data = library.Read(title, category, file);
                await SaveFileTransfer.UploadAsync(state.Client, loadActionId, title, data, status, bytes)
                    .ConfigureAwait(false);
                state.Post(() => _status = $"Uploaded {data.Length} bytes and triggered the load action");
            }
            finally
            {
                state.Post(() => _busy = false);
            }
        }, $"Loaded '{file}' onto the console");
    }

    private static void Rescan(AppState state, string title)
    {
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
