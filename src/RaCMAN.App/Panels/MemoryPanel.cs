using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

public static class MemoryPanel
{
    private static string _readAddress = "0x300000";
    private static int _readLength = 64;
    private static string _dump = string.Empty;
    private static byte[] _dumpBytes = Array.Empty<byte>();
    private static uint _dumpBase;

    private static string _writeAddress = "0x300000";
    private static string _writeBytes = string.Empty;

    private static string _watchAddress = "0x300000";
    private static int _watchSizeIndex = 2;

    private static string _freezeAddress = "0x300000";
    private static int _freezeSizeIndex = 2;
    private static string _freezeValue = "0";

    private static string _patchText = "0x1B0000: 0x60000000";
    private static string _revertAddress = "0x1B0000";

    private static string _watchlistName = string.Empty;

    private static readonly int[] Sizes = { 1, 2, 4, 8 };
    private static readonly string[] SizeLabels = { "1", "2", "4", "8" };

    /// <summary>Local names and formats for watches; qwark owns the watches themselves.</summary>
    private static readonly Dictionary<string, SavedWatch> Local = new(StringComparer.Ordinal);

    private static string Key(uint address, byte size) => $"{address:X8}:{size}";

    /// <summary>
    /// Drops the hex dump and the moby rows: both are copies of the running process's memory and
    /// have no meaning once the session leaves INGAME.
    /// </summary>
    public static void ClearGameData()
    {
        _dump = string.Empty;
        _dumpBytes = Array.Empty<byte>();
        _dumpBase = 0;
        ResetMobyTab();
    }

    /// <summary>Everything, the per-title watch names included. Called on a game or connection change.</summary>
    public static void Reset()
    {
        ClearGameData();
        Local.Clear();
        _watchlistName = string.Empty;

        // The saved-list combo is per title, so it is re-listed on the next frame that draws it.
        _watchlistLabels = Array.Empty<string>();
        _watchlistNames = Array.Empty<string?>();
        _watchlistIndex = -1;
        _watchlistTitle = string.Empty;
        _deleteArmed = false;
    }

    public static void Draw(AppState state)
    {
        Ui.Heading("Memory");

        bool enabled = state.Ingame;
        if (!enabled) Ui.Warning($"Memory commands need INGAME (state is {state.Session.State}).");

        if (ImGui.BeginTabBar("memory-tabs"))
        {
            // Watches is the tab people live in, so it is first and opens selected by default.
            if (ImGui.BeginTabItem("Watches"))
            {
                DrawWatches(state, enabled);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Viewer"))
            {
                DrawViewer(state, enabled);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Freezes"))
            {
                DrawFreezes(state, enabled);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Patches"))
            {
                DrawPatches(state, enabled);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Mobys"))
            {
                DrawMobys(state, enabled);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    // ---------------------------------------------------------------- moby table

    private static MobyRow[] _mobyRows = Array.Empty<MobyRow>();
    private static string _mobyFilter = string.Empty;
    private static string _mobyStatus = string.Empty;
    private static MobyTableInfo? _mobyInfo;
    private static bool _mobyBusy;
    private static bool _mobyOpened;

    /// <summary>How many rows the last read produced, for the smoke-run summary.</summary>
    public static int MobyRowCount => _mobyRows.Length;

    public static void ResetMobyTab()
    {
        _mobyRows = Array.Empty<MobyRow>();
        _mobyStatus = string.Empty;
        _mobyInfo = null;
        _mobyBusy = false;
        _mobyOpened = false;
    }

    private static void DrawMobys(AppState state, bool enabled)
    {
        var layout = MobyLayouts.For(state.Session.Game);

        // Read once when the tab is first opened; after that it is on the button, because a
        // full table is a few hundred kilobytes of MEM_READ.
        if (!_mobyOpened && enabled)
        {
            _mobyOpened = true;
            LoadMobys(state, layout);
        }

        ImGui.BeginDisabled(!enabled || _mobyBusy);
        if (ImGui.Button("Read moby table")) LoadMobys(state, layout);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        Ui.InputTextWithHint("Filter oClass", "1234 or 0x4D2", ref _mobyFilter, 32);

        ImGui.SameLine();
        if (ImGui.Button("Clear##mobyfilter")) _mobyFilter = string.Empty;

        ImGui.Spacing();

        if (layout is null)
        {
            Ui.Warning($"No moby layout for game {(byte)state.Session.Game} ({state.Session.Game}): "
                       + "index, address and position only.");
        }
        else
        {
            Ui.Hint($"{layout.Name} layout, from {layout.Source}");
        }

        foreach (var problem in MobyLayouts.Problems) Ui.Error(problem);

        if (_mobyInfo is { } info)
        {
            Ui.DebugHint($"MOBY_TABLE: pointer 0x{info.TablePointerAddress:X8}, end pointer 0x{info.TableEndPointerAddress:X8}, stride {info.Stride} bytes");
        }

        if (_mobyStatus.Length > 0) ImGui.TextUnformatted(_mobyStatus);

        if (_mobyRows.Length == 0)
        {
            Ui.Hint(enabled
                ? "Read the table to list the mobys the console is updating."
                : $"The moby table is game memory; the session is {state.Session.State}, not INGAME.");
            return;
        }

        var rows = FilterMobys(_mobyRows, _mobyFilter);
        ImGui.Spacing();
        Ui.Hint($"{rows.Length} of {_mobyRows.Length} rows shown. Double-click a row to put its address in the viewer.");

        bool hasClass = layout?.Has("oClass") == true;
        bool hasUid = layout?.Has("uid") == true;
        bool hasState = layout?.Has("state") == true;
        int columns = 5 + (hasClass ? 1 : 0) + (hasUid ? 1 : 0) + (hasState ? 1 : 0);

        if (!ImGui.BeginChild("##mobys", new Vector2(-1, -1), ImGuiChildFlags.Borders)) { ImGui.EndChild(); return; }

        if (ImGui.BeginTable("mobys", columns,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Address", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("x", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("y", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("z", ImGuiTableColumnFlags.WidthFixed, 80);
            if (hasClass) ImGui.TableSetupColumn("oClass", ImGuiTableColumnFlags.WidthFixed, 90);
            if (hasUid) ImGui.TableSetupColumn("UID", ImGuiTableColumnFlags.WidthFixed, 70);
            if (hasState) ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                if (ImGui.Selectable($"{row.Index}##moby{row.Index}", false, ImGuiSelectableFlags.SpanAllColumns
                        | ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(0))
                {
                    _readAddress = $"0x{row.Address:X8}";
                    _readLength = _mobyInfo?.Stride ?? 256;
                    state.AddToast($"Viewer address set to 0x{row.Address:X8}");
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"0x{row.Address:X8}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.X.ToString("0.##", CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Y.ToString("0.##", CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Z.ToString("0.##", CultureInfo.InvariantCulture));

                if (hasClass)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.OClass is { } c ? $"{c} (0x{c:X})" : "-");
                }

                if (hasUid)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.Uid?.ToString(CultureInfo.InvariantCulture) ?? "-");
                }

                if (hasState)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.State?.ToString(CultureInfo.InvariantCulture) ?? "-");
                }
            }

            ImGui.EndTable();
        }

        ImGui.EndChild();
    }

    private static MobyRow[] FilterMobys(MobyRow[] rows, string filter)
    {
        filter = (filter ?? string.Empty).Trim();
        if (filter.Length == 0) return rows;

        if (Ui.TryParseValue(filter, out ulong exact))
        {
            var matches = rows.Where(r => r.OClass == (long)exact).ToArray();
            if (matches.Length > 0) return matches;
        }

        return rows.Where(r => r.OClass is { } c
                               && (c.ToString(CultureInfo.InvariantCulture).Contains(filter, StringComparison.OrdinalIgnoreCase)
                                   || c.ToString("X", CultureInfo.InvariantCulture).Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    /// <summary>
    /// The read itself lives in MobyTableReader so it can be tested without a window; the panel
    /// only owns the button, the busy flag and where the result is put.
    /// </summary>
    private static void LoadMobys(AppState state, MobyLayout? layout)
    {
        _mobyBusy = true;
        state.Run(async () =>
        {
            try
            {
                var snapshot = await MobyTableReader.ReadAsync(state.Client, layout).ConfigureAwait(false);
                state.Post(() =>
                {
                    _mobyInfo = snapshot.Info;
                    _mobyRows = snapshot.Rows;
                    _mobyStatus = snapshot.Describe();
                });
            }
            finally
            {
                state.Post(() => _mobyBusy = false);
            }
        });
    }

    private static void DrawViewer(AppState state, bool enabled)
    {
        ImGui.BeginDisabled(!enabled);

        ImGui.SetNextItemWidth(160);
        ImGui.InputText("Address", ref _readAddress, 16);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Bytes", ref _readLength)) _readLength = Math.Clamp(_readLength, 1, 65536);
        ImGui.SameLine();

        if (ImGui.Button("Read"))
        {
            if (Ui.TryParseAddress(_readAddress, out uint address))
            {
                uint length = (uint)_readLength;
                state.Run(() => state.Client.MemReadAsync(address, length), bytes =>
                {
                    _dumpBytes = bytes;
                    _dumpBase = address;
                    _dump = Ui.HexDump(address, bytes);
                });
            }
            else
            {
                state.AddToast($"'{_readAddress}' is not a hex address", ToastKind.Error);
            }
        }

        ImGui.EndDisabled();

        if (_dumpBytes.Length > 0)
        {
            ImGui.Spacing();
            DrawTypedViews();
            ImGui.Spacing();

            // A read-only child rather than InputTextMultiline: ImGui.NET would allocate the whole
            // edit buffer every frame for a dump that is never edited.
            if (ImGui.BeginChild("##dump", new Vector2(-1, 260), ImGuiChildFlags.Borders,
                    ImGuiWindowFlags.HorizontalScrollbar))
            {
                ImGui.TextUnformatted(_dump);
            }

            ImGui.EndChild();
            if (ImGui.SmallButton("Copy dump")) ImGui.SetClipboardText(_dump);
        }

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Write");

        ImGui.BeginDisabled(!enabled);
        ImGui.SetNextItemWidth(160);
        ImGui.InputText("Address##write", ref _writeAddress, 16);
        ImGui.SetNextItemWidth(420);
        Ui.InputTextWithHint("Bytes (hex)", "60000000 or 60 00 00 00", ref _writeBytes, 4096);
        ImGui.SameLine();
        if (ImGui.Button("Write"))
        {
            var bytes = Ui.TryParseHexBytes(_writeBytes);
            if (!Ui.TryParseAddress(_writeAddress, out uint address))
            {
                state.AddToast($"'{_writeAddress}' is not a hex address", ToastKind.Error);
            }
            else if (bytes is null)
            {
                state.AddToast("Write needs an even number of hex digits", ToastKind.Error);
            }
            else
            {
                state.Run(() => state.Client.MemWriteAsync(address, bytes), $"Wrote {bytes.Length} bytes to 0x{address:X8}");
            }
        }

        ImGui.EndDisabled();
    }

    private static void DrawTypedViews()
    {
        if (!ImGui.BeginTable("typed", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit)) return;

        ImGui.TableSetupColumn("Type");
        ImGui.TableSetupColumn("Big endian");
        ImGui.TableSetupColumn("Address");
        ImGui.TableHeadersRow();

        Row("u8", _dumpBytes.Length >= 1 ? _dumpBytes[0].ToString() : "-");
        Row("u16", _dumpBytes.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(_dumpBytes).ToString() : "-");
        Row("u32", _dumpBytes.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(_dumpBytes).ToString() : "-");
        Row("i32", _dumpBytes.Length >= 4 ? BinaryPrimitives.ReadInt32BigEndian(_dumpBytes).ToString() : "-");
        Row("f32", _dumpBytes.Length >= 4 ? BinaryPrimitives.ReadSingleBigEndian(_dumpBytes).ToString("0.#####", CultureInfo.InvariantCulture) : "-");
        Row("u64", _dumpBytes.Length >= 8 ? BinaryPrimitives.ReadUInt64BigEndian(_dumpBytes).ToString() : "-");

        ImGui.EndTable();

        void Row(string type, string value)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(type);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(value);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"0x{_dumpBase:X8}");
        }
    }

    private static void DrawWatches(AppState state, bool enabled)
    {
        ImGui.BeginDisabled(!state.Connected);
        ImGui.SetNextItemWidth(160);
        ImGui.InputText("Address##watch", ref _watchAddress, 16);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.Combo("Size##watch", ref _watchSizeIndex, SizeLabels, SizeLabels.Length);
        ImGui.SameLine();
        if (ImGui.Button("Add watch"))
        {
            if (Ui.TryParseAddress(_watchAddress, out uint address))
            {
                byte size = (byte)Sizes[_watchSizeIndex];
                state.Run(async () =>
                {
                    await state.Client.WatchAddAsync(address, size);
                    state.Post(state.RefreshWatches);
                });
            }
            else
            {
                state.AddToast($"'{_watchAddress}' is not a hex address", ToastKind.Error);
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh##watches")) state.RefreshWatches();
        ImGui.EndDisabled();

        ImGui.Spacing();

        if (state.Watches.Length == 0)
        {
            Ui.Hint("No watches. The console owns this list and keeps it across a same-game reboot.");
        }
        else if (ImGui.BeginTable("watches", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Address", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 45);
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 160);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableHeadersRow();

            foreach (var watch in state.Watches)
            {
                ImGui.TableNextRow();
                ImGui.PushID(watch.Id);

                string key = Key(watch.Address, watch.Size);
                if (!Local.TryGetValue(key, out var saved))
                {
                    saved = new SavedWatch { Address = watch.Address, Size = watch.Size, Name = $"watch {watch.Id}" };
                    Local[key] = saved;
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(watch.Id.ToString());

                ImGui.TableNextColumn();
                string name = saved.Name;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##name", ref name, 64)) saved.Name = name;

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"0x{watch.Address:X8}");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(watch.Size.ToString());

                ImGui.TableNextColumn();
                var live = state.WatchValueFor(watch.Id);
                if (live is { Valid: true })
                {
                    ImGui.TextUnformatted(Ui.FormatValue(live.Value.Value, watch.Size, saved.Format));
                }
                else
                {
                    ImGui.TextColored(Ui.Grey, live is null ? "no telemetry" : "invalid");
                }

                ImGui.TableNextColumn();
                int format = Array.IndexOf(Ui.ValueFormats, saved.Format);
                if (format < 0) format = 0;
                ImGui.SetNextItemWidth(80);
                if (ImGui.Combo("##format", ref format, Ui.ValueFormats, Ui.ValueFormats.Length))
                {
                    saved.Format = Ui.ValueFormats[format];
                }

                ImGui.SameLine();
                byte id = watch.Id;
                if (ImGui.SmallButton("Remove"))
                {
                    state.Run(async () =>
                    {
                        await state.Client.WatchRemoveAsync(id);
                        state.Post(state.RefreshWatches);
                    });
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Watchlist file");

        string title = string.IsNullOrEmpty(state.Session.TitleId) ? "unknown" : state.Session.TitleId;

        // Only when the title changes: this walks the watchlists folder, which is not something to
        // do once a frame.
        if (!string.Equals(_watchlistTitle, title, StringComparison.Ordinal)) RefreshWatchlists(state, title);

        DrawWatchlistPicker(state, title);

        ImGui.SetNextItemWidth(200);
        if (Ui.InputTextWithHint("Name", "default", ref _watchlistName, 64))
        {
            // Typing a name is how a new list is made, so the combo follows the box rather than
            // the other way round; a name with no file behind it simply selects nothing.
            SelectWatchlist(_watchlistName);
            _deleteArmed = false;
        }

        ImGui.SameLine();
        if (ImGui.Button("Save watchlist")) SaveWatchlist(state, title);

        ImGui.SameLine();
        ImGui.BeginDisabled(!state.Connected);
        if (ImGui.Button("Load watchlist")) LoadWatchlist(state, title);
        ImGui.EndDisabled();
    }

    // ---------------------------------------------------------------- watchlist files

    /// <summary>What the combo shows, and the name behind each entry (null is the default list).</summary>
    private static string[] _watchlistLabels = Array.Empty<string>();
    private static string?[] _watchlistNames = Array.Empty<string?>();
    private static int _watchlistIndex = -1;

    /// <summary>The title the two arrays above were listed for, so the listing happens once.</summary>
    private static string _watchlistTitle = string.Empty;

    private static bool _deleteArmed;

    private static void DrawWatchlistPicker(AppState state, string title)
    {
        string preview = _watchlistIndex >= 0 && _watchlistIndex < _watchlistLabels.Length
            ? _watchlistLabels[_watchlistIndex]
            : _watchlistLabels.Length == 0 ? "(none saved)" : "(pick one)";

        ImGui.SetNextItemWidth(200);
        ImGui.BeginDisabled(_watchlistLabels.Length == 0);
        if (ImGui.BeginCombo("Saved lists", preview))
        {
            for (int i = 0; i < _watchlistLabels.Length; i++)
            {
                if (!ImGui.Selectable(_watchlistLabels[i], i == _watchlistIndex)) continue;

                _watchlistIndex = i;
                _deleteArmed = false;
                _watchlistName = _watchlistNames[i] ?? string.Empty;
                LoadWatchlist(state, title);
            }

            ImGui.EndCombo();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();

        // The same two-step the level flags use for a reset: nothing on this panel deletes a file
        // on one click.
        if (_deleteArmed)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
            if (ImGui.Button("Confirm delete")) DeleteWatchlist(state, title);
            ImGui.PopStyleColor();

            ImGui.SameLine();
            if (ImGui.Button("Cancel##watchlist")) _deleteArmed = false;
        }
        else
        {
            ImGui.BeginDisabled(_watchlistIndex < 0);
            if (ImGui.Button("Delete...")) _deleteArmed = true;
            ImGui.EndDisabled();
        }
    }

    /// <summary>Re-lists the watchlist files for a title and keeps the Name box's entry selected.</summary>
    private static void RefreshWatchlists(AppState state, string title)
    {
        _watchlistTitle = title;
        _deleteArmed = false;

        var found = new List<(string Label, string? Name)>();
        try
        {
            foreach (var file in state.Watchlists.ListFor(title))
            {
                // The listing is a prefix match, so another title's file can turn up in it.
                if (WatchlistStore.TryGetName(title, file, out var name))
                {
                    found.Add((name is null ? "(default)" : name, name));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.AddToast($"Watchlists: {ex.Message}", ToastKind.Error);
        }

        // The unnamed list first, then the named ones in alphabetical order.
        found.Sort((left, right) => left.Name is null
            ? right.Name is null ? 0 : -1
            : right.Name is null ? 1 : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        _watchlistLabels = found.Select(entry => entry.Label).ToArray();
        _watchlistNames = found.Select(entry => entry.Name).ToArray();
        SelectWatchlist(_watchlistName);
    }

    /// <summary>Points the combo at the file the Name box names, or at nothing.</summary>
    private static void SelectWatchlist(string name)
    {
        string? wanted = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        _watchlistIndex = Array.FindIndex(_watchlistNames,
            entry => string.Equals(entry, wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static string? WatchlistName() => string.IsNullOrWhiteSpace(_watchlistName) ? null : _watchlistName.Trim();

    private static void SaveWatchlist(AppState state, string title)
    {
        string? name = WatchlistName();
        try
        {
            var entries = state.Watches
                .Select(w => Local.TryGetValue(Key(w.Address, w.Size), out var saved)
                    ? new SavedWatch { Address = w.Address, Size = w.Size, Name = saved.Name, Format = saved.Format }
                    : new SavedWatch { Address = w.Address, Size = w.Size, Name = $"watch {w.Id}" })
                .ToList();

            state.Watchlists.Save(title, entries, name);
            state.AddToast($"Watchlist saved to {state.Watchlists.FileFor(title, name)}", ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            state.AddToast($"Watchlist save failed: {ex.Message}", ToastKind.Error);
        }

        RefreshWatchlists(state, title);
    }

    private static void LoadWatchlist(AppState state, string title)
    {
        var entries = state.Watchlists.Load(title, WatchlistName());
        if (entries.Count == 0)
        {
            state.AddToast("No watchlist file for this title", ToastKind.Error);
            return;
        }

        foreach (var entry in entries) Local[Key(entry.Address, entry.Size)] = entry;

        if (!state.Connected)
        {
            // The names are the PC's, the watches are the console's, and the combo is usable
            // offline: half a load is worth saying out loud rather than a refused request.
            state.AddToast($"Loaded {entries.Count} names; connect to add the watches themselves");
            return;
        }

        state.Run(async () =>
        {
            foreach (var entry in entries)
            {
                await state.Client.WatchAddAsync(entry.Address, entry.Size);
            }

            state.Post(state.RefreshWatches);
        }, $"Added {entries.Count} watches");
    }

    private static void DeleteWatchlist(AppState state, string title)
    {
        _deleteArmed = false;
        if (_watchlistIndex < 0 || _watchlistIndex >= _watchlistNames.Length) return;

        string? name = _watchlistNames[_watchlistIndex];
        string label = _watchlistLabels[_watchlistIndex];

        try
        {
            if (state.Watchlists.Delete(title, name)) state.AddToast($"Watchlist {label} deleted", ToastKind.Success);
            else state.AddToast($"Watchlist {label} was already gone", ToastKind.Error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.AddToast($"Watchlist delete failed: {ex.Message}", ToastKind.Error);
        }

        _watchlistName = string.Empty;
        RefreshWatchlists(state, title);
    }

    private static void DrawFreezes(AppState state, bool enabled)
    {
        ImGui.BeginDisabled(!enabled);
        ImGui.SetNextItemWidth(160);
        ImGui.InputText("Address##freeze", ref _freezeAddress, 16);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.Combo("Size##freeze", ref _freezeSizeIndex, SizeLabels, SizeLabels.Length);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        ImGui.InputText("Value##freeze", ref _freezeValue, 32);
        ImGui.SameLine();
        if (ImGui.Button("Freeze"))
        {
            if (!Ui.TryParseAddress(_freezeAddress, out uint address))
            {
                state.AddToast($"'{_freezeAddress}' is not a hex address", ToastKind.Error);
            }
            else if (!Ui.TryParseValue(_freezeValue, out ulong value))
            {
                state.AddToast($"'{_freezeValue}' is not a number", ToastKind.Error);
            }
            else
            {
                byte size = (byte)Sizes[_freezeSizeIndex];
                state.Run(async () =>
                {
                    await state.Client.FreezeAddAsync(address, size, value);
                    state.Post(state.RefreshFreezes);
                });
            }
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Refresh##freezes")) state.RefreshFreezes();

        ImGui.Spacing();

        if (state.Freezes.Length == 0)
        {
            Ui.Hint("No freezes. The console keeps re-writing every active freeze while the game runs.");
        }
        else if (ImGui.BeginTable("freezes", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Address", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Value");
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableHeadersRow();

            foreach (var freeze in state.Freezes)
            {
                ImGui.TableNextRow();
                ImGui.PushID(1000 + freeze.Id);

                ImGui.TableNextColumn();
                bool active = (state.Session.FreezeActive & (1UL << freeze.Id)) != 0;
                ImGui.TextColored(active ? Ui.Green : Ui.Grey, freeze.Id.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"0x{freeze.Address:X8}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(freeze.Size.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{freeze.Value} (0x{freeze.Value:X})");
                ImGui.TableNextColumn();

                byte id = freeze.Id;
                if (ImGui.SmallButton("Remove"))
                {
                    state.Run(async () =>
                    {
                        await state.Client.FreezeRemoveAsync(id);
                        state.Post(state.RefreshFreezes);
                    });
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private static void DrawPatches(AppState state, bool enabled)
    {
        // PATCH_APPLY is refused outright on a platform without code patches (RPCS3). Reverting is
        // left alone: there is nothing to revert there, and a stale entry should still be clearable.
        bool blocked = state.CodePatchesUnsupported;
        if (blocked) Ui.Warning(Ui.PatchesAreCodePatches);

        Ui.Hint("One 'address: word' per line, at most 64 words. The first address is the patch's key.");
        ImGui.InputTextMultiline("##patch", ref _patchText, 8192, new Vector2(-1, 120));

        ImGui.BeginDisabled(!enabled || blocked);
        if (ImGui.Button("Apply patch"))
        {
            var words = ParsePatch(_patchText, out string? error);
            if (error is not null)
            {
                state.AddToast(error, ToastKind.Error);
            }
            else
            {
                state.Run(async () =>
                {
                    await state.Client.PatchApplyAsync(words);
                    state.Post(state.RefreshPatches);
                }, $"Applied {words.Count} words at 0x{words[0].Address:X8}");
            }
        }

        ImGui.EndDisabled();
        if (blocked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Ui.NoCodePatches);

        ImGui.BeginDisabled(!enabled);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        ImGui.InputText("First address", ref _revertAddress, 16);
        ImGui.SameLine();
        if (ImGui.Button("Revert"))
        {
            if (Ui.TryParseAddress(_revertAddress, out uint address))
            {
                state.Run(async () =>
                {
                    await state.Client.PatchRevertAsync(address);
                    state.Post(state.RefreshPatches);
                });
            }
            else
            {
                state.AddToast($"'{_revertAddress}' is not a hex address", ToastKind.Error);
            }
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Refresh##patches")) state.RefreshPatches();

        ImGui.Spacing();

        if (state.Patches.Length == 0)
        {
            Ui.Hint("No patches.");
        }
        else if (ImGui.BeginTable("patches", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("First address", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Words", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableHeadersRow();

            foreach (var patch in state.Patches)
            {
                ImGui.TableNextRow();
                ImGui.PushID((int)patch.FirstAddress);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"0x{patch.FirstAddress:X8}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(patch.WordCount.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(patch.Kind.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(patch.Name);
                ImGui.TableNextColumn();

                uint address = patch.FirstAddress;
                ImGui.BeginDisabled(!enabled || patch.Kind != PatchKind.Client);
                if (ImGui.SmallButton("Revert"))
                {
                    state.Run(async () =>
                    {
                        await state.Client.PatchRevertAsync(address);
                        state.Post(state.RefreshPatches);
                    });
                }

                ImGui.EndDisabled();
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.BeginDisabled(!state.Connected);
        if (ImGui.Button("CLEAR_CLIENT (drop every watch, freeze and client patch)"))
        {
            state.Run(async () =>
            {
                await state.Client.ClearClientAsync();
                state.Post(() =>
                {
                    state.RefreshWatches();
                    state.RefreshFreezes();
                    state.RefreshPatches();
                });
            }, "Client state cleared on the console");
        }

        ImGui.EndDisabled();
    }

    private static List<PatchWord> ParsePatch(string text, out string? error)
    {
        var words = new List<PatchWord>();
        foreach (var rawLine in (text ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split(':', 2);
            if (parts.Length != 2 || !Ui.TryParseAddress(parts[0], out uint address) || !Ui.TryParseAddress(parts[1], out uint word))
            {
                error = $"'{line}' is not 'address: word'";
                return words;
            }

            words.Add(new PatchWord(address, word));
        }

        if (words.Count == 0)
        {
            error = "A client patch needs at least one word";
            return words;
        }

        if (words.Count > 64)
        {
            error = "A client patch is at most 64 words";
            return words;
        }

        error = null;
        return words;
    }
}
