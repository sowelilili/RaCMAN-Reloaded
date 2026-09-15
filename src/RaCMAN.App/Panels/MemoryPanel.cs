using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

public static class MemoryPanel
{
    /// <summary>
    /// The address the boxes suggest. It is a hint rather than the box's contents: a default
    /// sitting in the box is something to select and delete before every address anyone actually
    /// wants, and there is nothing here worth reading twice.
    /// </summary>
    private const string AddressHint = "0x300000";

    /// <summary>The same for the two patch boxes, whose addresses are code rather than data.</summary>
    private const string PatchAddressHint = "0x1B0000";

    private static string _readAddress = string.Empty;
    private static int _readLength = 64;
    private static byte[] _dumpBytes = Array.Empty<byte>();
    private static uint _dumpBase;
    private static float _sinceRead;
    private static bool _reading;

    private static string _writeAddress = string.Empty;
    private static string _writeBytes = string.Empty;

    private static string _watchAddress = string.Empty;
    private static int _watchSizeIndex = 2;

    private static string _freezeAddress = string.Empty;
    private static int _freezeSizeIndex = 2;
    private static string _freezeValue = "0";

    private static string _patchText = string.Empty;
    private static string _revertAddress = string.Empty;

    private static string _watchlistName = string.Empty;

    /// <summary>Set when something outside the Viewer tab put an address in its box.</summary>
    private static bool _selectViewer;

    /// <summary>
    /// The sizes offered for a new watch or freeze, and in a row's size combo. Eight is missing on
    /// purpose: no value in these games is that wide, and the choice took room the table needed.
    /// The protocol still carries it, so a watch that arrived at size 8 - an old watchlist, or
    /// another client - keeps showing 8 and reading correctly; it is only not offered.
    /// </summary>
    private static readonly int[] Sizes = { 1, 2, 4 };
    private static readonly string[] SizeLabels = { "1", "2", "4" };

    /// <summary>Local names and formats for watches; qwark owns the watches themselves.</summary>
    private static readonly Dictionary<string, SavedWatch> Local = new(StringComparer.Ordinal);

    private static string Key(uint address, byte size) => $"{address:X8}:{size}";

    /// <summary>
    /// Points the Viewer tab at an address and opens it. The moby table's rows and the pointer
    /// fields in an inspector both come here, so there is one place that decides what the box says
    /// and how much is read.
    /// </summary>
    public static void ShowInViewer(AppState state, uint address, int length)
    {
        _readAddress = $"0x{address:X8}";
        _readLength = Math.Clamp(length, 1, 65536);
        _selectViewer = true;
        state.AddToast($"Viewer address set to 0x{address:X8}");
    }

    /// <summary>
    /// Adds a watch that arrives already named. The name and the format are the PC's half of a
    /// watch and are keyed by address and size, so they are put down before the request goes out:
    /// the refresh that follows then finds them rather than calling the row "watch N".
    /// </summary>
    public static void AddNamedWatch(AppState state, uint address, byte size, string name, string format)
    {
        Local[Key(address, size)] = new SavedWatch
        {
            Address = address,
            Size = size,
            Name = name,
            Format = format,
        };

        state.Run(async () =>
        {
            await state.Client.WatchAddAsync(address, size).ConfigureAwait(false);
            state.Post(() => state.RefreshWatches());
        }, $"Watching {name} at 0x{address:X8}");
    }

    /// <summary>
    /// Drops the hex dump and the moby rows: both are copies of the running process's memory and
    /// have no meaning once the session leaves INGAME. The open inspectors go with them, because a
    /// moby's address says nothing at all about the next process.
    /// </summary>
    public static void ClearGameData()
    {
        _dumpBytes = Array.Empty<byte>();
        _dumpBase = 0;
        _sinceRead = 0;
        ByteDrafts.Clear();
        MobyInspector.CloseAll();
        ResetMobyTab();
    }

    /// <summary>Everything, the per-title watch names included. Called on a game or connection change.</summary>
    public static void Reset()
    {
        ClearGameData();
        Local.Clear();
        ValueDrafts.Clear();
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
        if (!enabled) Ui.Warning($"Memory commands need INGAME (state is {state.Session.State.DisplayName()}).");

        // A title the console has no game module for: the memory ops are the whole of what works,
        // which is why this is the only panel left in the nav.
        bool unknown = state.UnknownGame;
        if (unknown) Ui.Hint(Ui.NoGameModule);

        if (ImGui.BeginTabBar("memory-tabs"))
        {
            // Watches is the tab people live in, so it is first and opens selected by default.
            if (ImGui.BeginTabItem("Watches"))
            {
                DrawWatches(state, enabled);
                ImGui.EndTabItem();
            }

            // The one tab another window sends the user to: a moby row and a pointer inside one
            // both put an address in its box, and a box nobody can see is not an answer.
            var viewerFlags = _selectViewer ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            _selectViewer = false;
            if (Ui.BeginTabItem("Viewer", viewerFlags))
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

            // The moby table is read through MOBY_TABLE, which is the game's own: a title qwark has
            // no module for has no table to point at, so the tab is not offered.
            if (!unknown && ImGui.BeginTabItem("Mobys"))
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
                       + "index and address only.");
        }

        foreach (var problem in MobyLayouts.Problems) Ui.Error(problem);

        // Which layout file the columns came from, and where the table itself lives, are both
        // wire-level detail: the columns are named in the header row either way.
        if (layout is not null) Ui.DebugHint($"{layout.Name} layout, from {layout.Source}");

        if (_mobyInfo is { } info)
        {
            Ui.DebugHint($"MOBY_TABLE: pointer 0x{info.TablePointerAddress:X8}, end pointer 0x{info.TableEndPointerAddress:X8}, stride {info.Stride} bytes");
        }

        if (_mobyStatus.Length > 0) ImGui.TextUnformatted(_mobyStatus);

        if (_mobyRows.Length == 0)
        {
            Ui.Hint(enabled
                ? "Read the table to list the mobys the console is updating."
                : $"Reading the moby table needs INGAME (state is {state.Session.State.DisplayName()}).");
            return;
        }

        var rows = FilterMobys(_mobyRows, _mobyFilter);
        ImGui.Spacing();
        Ui.Hint($"{rows.Length} of {_mobyRows.Length} rows shown. Double-click a row to open it; "
                + "right-click it for the viewer.");

        bool hasClass = layout?.Has("oClass") == true;
        bool hasUid = layout?.Has("uid") == true;
        bool hasState = layout?.Has("state") == true;

        // The row index and the address, then whatever the layout names. The coordinates are what
        // the Positions panel is for, and three more columns of them did not fit the window.
        int columns = 2 + (hasClass ? 1 : 0) + (hasUid ? 1 : 0) + (hasState ? 1 : 0);

        if (!ImGui.BeginChild("##mobys", new Vector2(-1, -1), ImGuiChildFlags.Borders)) { ImGui.EndChild(); return; }

        if (ImGui.BeginTable("mobys", columns,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("Address", ImGuiTableColumnFlags.WidthFixed, 90);
            if (hasClass) ImGui.TableSetupColumn("oClass", ImGuiTableColumnFlags.WidthFixed, 90);
            if (hasUid) ImGui.TableSetupColumn("UID", ImGuiTableColumnFlags.WidthFixed, 70);
            if (hasState) ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();

            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                int stride = _mobyInfo?.Stride ?? layout?.Stride ?? 256;

                if (ImGui.Selectable($"{row.Index}##moby{row.Index}", false, ImGuiSelectableFlags.SpanAllColumns
                        | ImGuiSelectableFlags.AllowDoubleClick) && ImGui.IsMouseDoubleClicked(0))
                {
                    MobyInspector.Open(state, row, layout, stride);
                }

                // What the double-click used to do. It is still wanted - a row is an address like
                // any other - but it is not what opening a moby means any more.
                if (ImGui.BeginPopupContextItem())
                {
                    if (ImGui.MenuItem("Show in viewer")) ShowInViewer(state, row.Address, stride);
                    if (ImGui.MenuItem("Open inspector")) MobyInspector.Open(state, row, layout, stride);
                    ImGui.EndPopup();
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"0x{row.Address:X8}");

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
        Ui.InputTextWithHint("Address", AddressHint, ref _readAddress, 16);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Bytes", ref _readLength)) _readLength = Math.Clamp(_readLength, 1, 65536);
        ImGui.SameLine();

        if (ImGui.Button("Read"))
        {
            if (Ui.TryParseAddress(_readAddress, out uint address))
            {
                ReadDump(state, address, (uint)_readLength, quiet: false);
            }
            else
            {
                state.AddToast($"'{_readAddress}' is not a hex address", ToastKind.Error);
            }
        }

        ImGui.EndDisabled();

        // The dump re-reads itself on the Settings panel's table interval, the way the level flags
        // and the unlocks do: this is a window on memory the game is writing, and a stale window is
        // worse than a re-read a second. Only what has already been read is read again, so the
        // address and the length stay the ones the button used, and an interval of zero leaves the
        // button as the only thing that reads.
        float period = state.Settings.TableRefreshSeconds;
        if (period > 0 && _dumpBytes.Length > 0 && state.Connected && !state.ConsoleBusy)
        {
            _sinceRead += ImGui.GetIO().DeltaTime;

            // A read still on the wire means the interval is shorter than the round trip; skipping
            // the tick keeps that from queueing reads for ever.
            if (_sinceRead >= period && !_reading)
            {
                _sinceRead = 0;
                ReadDump(state, _dumpBase, (uint)_dumpBytes.Length, quiet: true);
            }
        }
        else
        {
            _sinceRead = 0;
        }

        if (_dumpBytes.Length > 0)
        {
            ImGui.Spacing();
            DrawTypedViews();
            ImGui.Spacing();
            DrawDump(state, enabled);

            // Built here rather than kept: the table draws from the bytes, and the only thing the
            // old text was still for is this button.
            if (ImGui.SmallButton("Copy dump")) ImGui.SetClipboardText(Ui.HexDump(_dumpBase, _dumpBytes));
        }

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Write");

        ImGui.BeginDisabled(!enabled);
        ImGui.SetNextItemWidth(160);
        Ui.InputTextWithHint("Address##write", AddressHint, ref _writeAddress, 16);
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

    /// <summary>
    /// One range of game memory, from as many MEM_READs as its length needs: revision 1.11 caps
    /// one at <see cref="QwarkClient.MaxReadLength"/> and the Bytes box still takes 64 KB, so
    /// 64 KB is four requests, each answered before the next goes out.
    /// <para>
    /// A piece the console refuses throws, and what had already arrived is handed to
    /// <paramref name="got"/> before it does: a short dump of the address someone asked about is
    /// worth more than none, and reporting the status stays the caller's job, so it is said once
    /// rather than once per piece. <paramref name="got"/> is not called at all when nothing
    /// arrived, which is what leaves an earlier dump where it was.
    /// </para>
    /// </summary>
    public static async Task ReadRangeAsync(
        QwarkClient client,
        uint address,
        uint length,
        Action<byte[]> got,
        CancellationToken cancellationToken = default)
    {
        var buffer = new byte[length];
        int have = 0;

        try
        {
            while (have < length)
            {
                uint want = Math.Min((uint)QwarkClient.MaxReadLength, length - (uint)have);
                var piece = await client.MemReadAsync(address + (uint)have, want, cancellationToken)
                    .ConfigureAwait(false);
                piece.CopyTo(buffer, have);
                have += piece.Length;

                // A console that answered short has no more to give at that address.
                if (piece.Length < want) break;
            }
        }
        finally
        {
            if (have > 0) got(have == buffer.Length ? buffer : buffer[..have]);
        }
    }

    /// <summary>
    /// The dump, read through <see cref="ReadRangeAsync"/> and put where the table draws from.
    /// A read that brought nothing back leaves the dump that was already there alone, so a
    /// failure does not also empty the table and stop the timer below from ever retrying.
    /// <para>
    /// The in-flight flag is what the timer above looks at, so it is cleared in a finally: a read
    /// that failed must not stop the next one from ever starting.
    /// </para>
    /// </summary>
    private static void ReadDump(AppState state, uint address, uint length, bool quiet)
    {
        _reading = true;

        async Task Read()
        {
            try
            {
                await ReadRangeAsync(state.Client, address, length, bytes => state.Post(() =>
                {
                    _dumpBytes = bytes;
                    _dumpBase = address;
                })).ConfigureAwait(false);
            }
            finally
            {
                state.Post(() => _reading = false);
            }
        }

        if (quiet) state.RunQuiet(Read);
        else state.Run(Read);
    }

    // ---------------------------------------------------------------- the hex dump

    /// <summary>How tall the dump is. Sixteen or so lines, the way the text block was.</summary>
    private const float DumpHeight = 260f;

    /// <summary>
    /// What is in a byte cell while it has focus, by address. The watches table's rule, for the
    /// same reason: a re-read arriving mid-edit must not take the half-typed pair away.
    /// </summary>
    private static readonly Dictionary<uint, string> ByteDrafts = new();

    /// <summary>
    /// Writes a committed cell into this side's copy of memory, so the byte on screen is the byte
    /// that was sent rather than the one the last read brought back. False - and nothing written -
    /// for a pair the user has not finished typing and for a byte that is already what is there.
    /// </summary>
    public static bool TryApplyByteEdit(byte[] dump, int index, string? text, out byte value)
    {
        value = 0;
        if (index < 0 || index >= dump.Length) return false;
        if (!WatchValueCodec.TryParseByte(text, out value)) return false;
        if (dump[index] == value) return false;

        dump[index] = value;
        return true;
    }

    /// <summary>
    /// The dump as a table of editable bytes: the address, sixteen cells, and the text column the
    /// old dump ended each line with. Only the rows on screen are drawn, because a dump is up to
    /// 64 KB and every cell of it is a box.
    /// </summary>
    private static unsafe void DrawDump(AppState state, bool enabled)
    {
        // Measured from the font rather than guessed, and tight: sixteen boxes, an address and the
        // text column have to fit the window at the size it opens at without a scrollbar sideways.
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(2f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2f, ImGui.GetStyle().FramePadding.Y));

        float byteWidth = ImGui.CalcTextSize("FF").X + (ImGui.GetStyle().FramePadding.X * 2f);
        float addressWidth = ImGui.CalcTextSize("00000000").X;
        float textWidth = ImGui.CalcTextSize("0123456789ABCDEF").X;

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY
                                      | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersOuter;

        if (ImGui.BeginTable("dump", 18, flags, new Vector2(-1, DumpHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Address", ImGuiTableColumnFlags.WidthFixed, addressWidth);

            // The column headings are the low nibble of the address, so a byte in the middle of a
            // line can be found without counting along it.
            for (int i = 0; i < 16; i++)
            {
                ImGui.TableSetupColumn(i.ToString("X"), ImGuiTableColumnFlags.WidthFixed, byteWidth);
            }

            ImGui.TableSetupColumn("Text", ImGuiTableColumnFlags.WidthFixed, textWidth);
            ImGui.TableHeadersRow();

            var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin((_dumpBytes.Length + 15) / 16);
            while (clipper.Step())
            {
                for (int row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
                {
                    DrawDumpRow(state, row, enabled);
                }
            }

            clipper.End();
            clipper.Destroy();
            ImGui.EndTable();
        }

        ImGui.PopStyleVar(2);
    }

    private static void DrawDumpRow(AppState state, int row, bool enabled)
    {
        int first = row * 16;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        Ui.TableLabel((_dumpBase + (uint)first).ToString("X8"));

        for (int column = 0; column < 16; column++)
        {
            ImGui.TableNextColumn();
            int index = first + column;
            if (index >= _dumpBytes.Length) continue;

            // The cells all draw the same box, so the byte's place in the dump is what tells them
            // apart to ImGui.
            ImGui.PushID(index);
            DrawByteCell(state, index, enabled);
            ImGui.PopID();
        }

        ImGui.TableNextColumn();
        Ui.TableLabel(AsciiLine(first));
    }

    /// <summary>
    /// One byte, as two hex digits that write themselves back. Enter sends it, and so does clicking
    /// away from a cell that was changed; a pair that was left half typed is dropped without a
    /// write and without a word, because a stray keystroke over a dump is not a request.
    /// </summary>
    private static void DrawByteCell(AppState state, int index, bool enabled)
    {
        uint address = _dumpBase + (uint)index;
        string text = ByteDrafts.TryGetValue(address, out var draft) ? draft : _dumpBytes[index].ToString("X2");

        ImGui.BeginDisabled(!enabled);
        ImGui.SetNextItemWidth(-1);
        Ui.PushTableInput();
        bool entered = ImGui.InputText("##byte", ref text, 2, ImGuiInputTextFlags.CharsHexadecimal
                                                              | ImGuiInputTextFlags.CharsUppercase
                                                              | ImGuiInputTextFlags.EnterReturnsTrue);
        Ui.PopTableInput();
        bool active = ImGui.IsItemActive();
        bool committed = entered || ImGui.IsItemDeactivatedAfterEdit();
        ImGui.EndDisabled();

        DrawByteMenu(state, address, index);

        if (active) ByteDrafts[address] = text;
        else ByteDrafts.Remove(address);

        if (!committed) return;

        ByteDrafts.Remove(address);
        if (!TryApplyByteEdit(_dumpBytes, index, text, out byte value)) return;

        var bytes = new[] { value };
        state.Run(() => state.Client.MemWriteAsync(address, bytes));
    }

    /// <summary>
    /// What a byte of the dump can become: a watch of any of the three sizes, or an address on the
    /// clipboard. A size that would run off the end of what was read is not offered.
    /// </summary>
    private static void DrawByteMenu(AppState state, uint address, int index)
    {
        if (!ImGui.BeginPopupContextItem()) return;

        ImGui.TextUnformatted($"0x{address:X8}");
        ImGui.Separator();

        ImGui.BeginDisabled(!state.Connected);
        foreach (int size in Sizes)
        {
            ImGui.BeginDisabled(index + size > _dumpBytes.Length);
            if (ImGui.MenuItem($"Add {size}-byte watch"))
            {
                AddNamedWatch(state, address, (byte)size, $"0x{address:X8} u{size * 8}", "hex");
            }

            ImGui.EndDisabled();
        }

        ImGui.EndDisabled();

        if (ImGui.MenuItem("Copy address")) ImGui.SetClipboardText($"0x{address:X8}");
        ImGui.EndPopup();
    }

    /// <summary>The sixteen bytes of a line as text, the way the old dump's last column had them.</summary>
    private static string AsciiLine(int first)
    {
        int count = Math.Min(16, _dumpBytes.Length - first);
        var line = new char[count];
        for (int i = 0; i < count; i++)
        {
            char c = (char)_dumpBytes[first + i];
            line[i] = c is >= ' ' and <= '~' ? c : '.';
        }

        return new string(line);
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
        Ui.InputTextWithHint("Address##watch", AddressHint, ref _watchAddress, 16);
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
                    state.Post(() => state.RefreshWatches());
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
            Ui.Hint("No watches...");
        }
        // No Id column: the name is what a watch is known by, and the room the id took was the
        // room the name needed. The id is on the name's tooltip with debug information on.
        else if (ImGui.BeginTable("watches", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            // The last column is the format combo with the two buttons stacked beside it, measured
            // rather than guessed: side by side the buttons pushed the Name column down to a
            // couple of letters at the size the window opens at. A fixed width is the cell's own
            // contents, so there is no padding to add to it here.
            var buttons = ButtonSize();
            float actions = FormatWidth + ImGui.GetStyle().ItemSpacing.X + buttons.X;
            float rowHeight = Ui.StackedButtonRowHeight();

            // Name takes what the other four leave, which is now most of the row.
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Address", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 45);

            // Wide enough for a signed 32-bit number spelled out in decimal, which is the longest
            // thing any of the four formats puts in the box.
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, actions);
            ImGui.TableHeadersRow();

            foreach (var watch in state.Watches)
            {
                ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                ImGui.PushID(watch.Id);

                string key = Key(watch.Address, watch.Size);
                if (!Local.TryGetValue(key, out var saved))
                {
                    saved = new SavedWatch { Address = watch.Address, Size = watch.Size, Name = $"watch {watch.Id}" };
                    Local[key] = saved;
                }

                // The boxes in a row are drawn without a frame of their own, so the table reads as
                // text until it is reached for: a watchlist is something to look at far more often
                // than something to edit.
                ImGui.TableNextColumn();
                Ui.CentreInRow(rowHeight);
                string name = saved.Name;
                ImGui.SetNextItemWidth(-1);
                Ui.PushTableInput();
                if (ImGui.InputText("##name", ref name, 64)) saved.Name = name;
                Ui.PopTableInput();
                if (Ui.Debug && ImGui.IsItemHovered()) ImGui.SetTooltip($"watch id {watch.Id}");

                ImGui.TableNextColumn();
                Ui.CentreInRow(rowHeight);
                Ui.TableLabel($"0x{watch.Address:X8}");

                ImGui.TableNextColumn();
                Ui.CentreInRow(rowHeight);
                DrawSizeCell(state, watch, saved);

                ImGui.TableNextColumn();
                Ui.CentreInRow(rowHeight);
                var live = state.WatchValueFor(watch.Id);
                DrawValueCell(state, watch, saved, live, enabled);

                // The last cell holds the combo and, stacked beside it, the two buttons. The row's
                // top is kept because SameLine comes back to the combo's own line, which is the
                // centred one, and the stack has to start at the top to fit.
                ImGui.TableNextColumn();
                float top = ImGui.GetCursorPosY();
                Ui.CentreInRow(rowHeight);
                int format = Array.IndexOf(Ui.ValueFormats, saved.Format);
                if (format < 0) format = 0;
                ImGui.SetNextItemWidth(FormatWidth);
                if (ImGui.Combo("##format", ref format, Ui.ValueFormats, Ui.ValueFormats.Length))
                {
                    saved.Format = Ui.ValueFormats[format];
                    ValueDrafts.Remove(watch.Id);
                }

                ImGui.SameLine();
                ImGui.SetCursorPosY(top);

                byte id = watch.Id;
                uint address = watch.Address;
                byte size = watch.Size;
                ImGui.BeginGroup();

                // Remove on top, because it is the one that is always available: a Freeze greyed
                // out for want of a reading would otherwise leave a gap where the row's live
                // button should be.
                if (ImGui.Button("Remove", buttons))
                {
                    ValueDrafts.Remove(id);
                    state.Run(async () =>
                    {
                        await state.Client.WatchRemoveAsync(id);
                        state.Post(() => state.RefreshWatches());
                    });
                }

                // The value the row is showing, held where it stands. A freeze is a write repeated
                // every frame, so it needs a game to write to and a reading to repeat.
                ImGui.BeginDisabled(!enabled || live is not { Valid: true });
                if (ImGui.Button("Freeze", buttons))
                {
                    ulong value = live!.Value.Value;
                    state.Run(async () =>
                    {
                        await state.Client.FreezeAddAsync(address, size, value).ConfigureAwait(false);
                        state.Post(() => state.RefreshFreezes());
                    });
                }

                ImGui.EndDisabled();
                ImGui.EndGroup();

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        Ui.Heading("Watchlist file");

        // The running title, and the described one at the XMB, so a quit does not swap the saved
        // lists for "unknown" and walk the folder again on the way back. A game qwark cannot name
        // still has a title id, and its watchlists are kept under it like anybody else's.
        string title = string.IsNullOrEmpty(state.CurrentTitle) ? "unknown" : state.CurrentTitle;

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

    // ---------------------------------------------------------------- watch rows

    /// <summary>
    /// What is in a Value box while it has focus, by watch id. It exists only for as long as the
    /// box is being typed in, which is what keeps telemetry from overwriting a half-typed number
    /// and what makes the box follow the live value again the moment it is let go.
    /// </summary>
    private static readonly Dictionary<byte, string> ValueDrafts = new();

    /// <summary>The row's format combo, which the last column is sized around.</summary>
    private const float FormatWidth = 80f;

    /// <summary>
    /// The width Remove and Freeze share, so the stack reads as one block of buttons rather than
    /// as two of different lengths. Measured, because the label lengths are the font's business.
    /// </summary>
    private static Vector2 ButtonSize() => new(
        MathF.Max(ImGui.CalcTextSize("Remove").X, ImGui.CalcTextSize("Freeze").X)
            + (ImGui.GetStyle().FramePadding.X * 2f),
        0f);

    /// <summary>
    /// The size as a combo rather than a number. The arrow is left off so the cell reads as the
    /// text it replaced; the frame appears on hover like the boxes beside it.
    /// </summary>
    private static void DrawSizeCell(AppState state, WatchEntry watch, SavedWatch saved)
    {
        ImGui.SetNextItemWidth(-1);
        Ui.PushTableInput();
        if (ImGui.BeginCombo("##size", watch.Size.ToString(), ImGuiComboFlags.NoArrowButton))
        {
            for (int i = 0; i < SizeLabels.Length; i++)
            {
                if (ImGui.Selectable(SizeLabels[i], Sizes[i] == watch.Size)) ChangeSize(state, watch, saved, (byte)Sizes[i]);
            }

            ImGui.EndCombo();
        }

        Ui.PopTableInput();
    }

    /// <summary>
    /// Puts the same address back under another size. There is no resize op: a watch is removed and
    /// added again, and qwark hands back the id it already has for an address and size it is
    /// already watching (section 5.4).
    /// </summary>
    private static void ChangeSize(AppState state, WatchEntry watch, SavedWatch saved, byte size)
    {
        if (size == watch.Size) return;

        uint address = watch.Address;
        byte id = watch.Id;

        // The local names are keyed by address and size, and so is a saved watchlist entry, so the
        // name has to move to the new key with the size written through it. Left behind, the row
        // would come back from the refresh calling itself "watch N" and save under the old size.
        Local.Remove(Key(address, watch.Size));
        Local[Key(address, size)] = new SavedWatch
        {
            Address = address,
            Size = size,
            Name = saved.Name,
            Format = saved.Format,
        };

        ValueDrafts.Remove(id);

        state.Run(async () =>
        {
            await state.Client.WatchRemoveAsync(id).ConfigureAwait(false);
            await state.Client.WatchAddAsync(address, size).ConfigureAwait(false);
            state.Post(() => state.RefreshWatches());
        });
    }

    /// <summary>
    /// The live value, as a box that writes it back. What it takes is what it shows: the row's own
    /// format, parsed by <see cref="WatchValueCodec"/>, sent on Enter as the watch's own number of
    /// big-endian bytes. A row with nothing to show is left as the words it used to be.
    /// </summary>
    private static void DrawValueCell(AppState state, WatchEntry watch, SavedWatch saved, WatchValue? live, bool enabled)
    {
        if (live is not { Valid: true })
        {
            ValueDrafts.Remove(watch.Id);
            Ui.TableLabel(Ui.Grey, live is null ? "no telemetry" : "invalid");
            return;
        }

        string text = ValueDrafts.TryGetValue(watch.Id, out var draft)
            ? draft
            : Ui.FormatValue(live.Value.Value, watch.Size, saved.Format);

        ImGui.BeginDisabled(!enabled);
        ImGui.SetNextItemWidth(-1);
        Ui.PushTableInput();
        bool entered = ImGui.InputText("##value", ref text, 32, ImGuiInputTextFlags.EnterReturnsTrue);
        Ui.PopTableInput();
        bool active = ImGui.IsItemActive();
        ImGui.EndDisabled();

        if (active) ValueDrafts[watch.Id] = text;
        else ValueDrafts.Remove(watch.Id);

        if (!entered) return;

        ValueDrafts.Remove(watch.Id);

        if (!WatchValueCodec.TryParse(text, watch.Size, saved.Format, out ulong value))
        {
            state.AddToast($"'{text.Trim()}' is not a {saved.Format} value that fits {watch.Size} bytes", ToastKind.Error);
            return;
        }

        uint address = watch.Address;
        var bytes = WatchValueCodec.Encode(value, watch.Size);
        state.Run(() => state.Client.MemWriteAsync(address, bytes));
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

            state.Post(() => state.RefreshWatches());
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
        Ui.InputTextWithHint("Address##freeze", AddressHint, ref _freezeAddress, 16);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.Combo("Size##freeze", ref _freezeSizeIndex, SizeLabels, SizeLabels.Length);
        
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
                    state.Post(() => state.RefreshFreezes());
                });
            }
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Refresh##freezes")) state.RefreshFreezes();

        ImGui.Spacing();

        if (state.Freezes.Length == 0)
        {
            Ui.Hint("No freezes...");
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
                        state.Post(() => state.RefreshFreezes());
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

        // The example moved out of the box and into the sentence above it: a multiline box takes no
        // hint text, and a default sitting in it was something to delete before every real patch.
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
                    state.Post(() => state.RefreshPatches());
                }, $"Applied {words.Count} words at 0x{words[0].Address:X8}");
            }
        }

        ImGui.EndDisabled();
        if (blocked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Ui.NoCodePatches);

        ImGui.BeginDisabled(!enabled);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        Ui.InputTextWithHint("First address", PatchAddressHint, ref _revertAddress, 16);
        ImGui.SameLine();

        // Spelled out rather than left as "Revert", which is also what every row of the table below
        // calls its own button.
        if (ImGui.Button("Revert##typed"))
        {
            if (Ui.TryParseAddress(_revertAddress, out uint address))
            {
                state.Run(async () =>
                {
                    await state.Client.PatchRevertAsync(address);
                    state.Post(() => state.RefreshPatches());
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

            // The row's own place in the table is its id, not its first address: two patches can
            // share one. A mod and the feature that carries the same words are the everyday case,
            // Deadlocked's crash patches being both, and two rows under one id is what Dear ImGui
            // reports as visible items with conflicting ID.
            for (int row = 0; row < state.Patches.Length; row++)
            {
                var patch = state.Patches[row];
                ImGui.TableNextRow();
                ImGui.PushID(row);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"0x{patch.FirstAddress:X8}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(patch.WordCount.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(patch.Kind.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(PatchName(patch));
                ImGui.TableNextColumn();

                uint address = patch.FirstAddress;
                ImGui.BeginDisabled(!enabled || patch.Kind != PatchKind.Client);
                if (ImGui.SmallButton("Revert"))
                {
                    state.Run(async () =>
                    {
                        await state.Client.PatchRevertAsync(address);
                        state.Post(() => state.RefreshPatches());
                    });
                }

                ImGui.EndDisabled();
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        // Every watch, freeze and client patch in one go, named after the op it sends: a wire-level
        // tool, and one keystroke away from throwing away a session's work, so it stays behind the
        // debug switch with the rest of them.
        if (!Ui.Debug) return;

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

    /// <summary>
    /// What the Name column shows. PATCH_LIST names every row: a mod's row carries the mod's own
    /// name, a feature's the label of the toggle behind it, and a client patch the name qwark made
    /// out of its first address. A row that arrives without one says what kind of thing it is
    /// instead, which is still more than a blank cell.
    /// </summary>
    public static string PatchName(PatchEntry patch)
    {
        string name = (patch.Name ?? string.Empty).Trim();
        if (name.Length > 0) return name;

        return patch.Kind switch
        {
            PatchKind.Mod => "(a mod)",
            PatchKind.Feature => "(a feature)",
            _ => "(this client)",
        };
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
