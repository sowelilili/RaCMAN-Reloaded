using System.Numerics;
using ImGuiNET;

namespace RaCMAN.App.Panels;

/// <summary>
/// One window per open moby: the whole row, field by field, as the game's layout file names it.
/// The old client had this and the table alone did not replace it - a row says what a moby is, not
/// what it is doing - so a double-click in the Mobys tab opens one here.
/// <para>
/// A window is keyed by the moby's address, re-reads its own row on the Settings panel's table
/// interval, and writes a field back the moment Enter is pressed in its box. The addresses mean
/// nothing once the process they came out of has gone, so <see cref="CloseAll"/> is what a game
/// change does with all of them.
/// </para>
/// </summary>
public static class MobyInspector
{
    /// <summary>How much of the pointed-to memory "Show target in viewer" reads: the viewer's own default.</summary>
    private const int PointerReadLength = 64;

    /// <summary>Field, Offset, Type, Value.</summary>
    private const int Columns = 4;

    /// <summary>
    /// How much of the main window an inspector may take up before it stops growing with its table.
    /// Past that the table scrolls down inside the window, and a raw block's hex scrolls along
    /// inside its own box.
    /// </summary>
    private const float Share = 0.7f;

    private sealed class Inspector
    {
        public required uint Address { get; init; }

        public required int Index { get; init; }

        /// <summary>The bytes one row holds, which is what one MEM_READ asks for.</summary>
        public required int Stride { get; init; }

        public required MobyLayout? Layout { get; init; }

        /// <summary>
        /// The lines the table draws: the struct list with every vector split into its floats. The
        /// layout of a window never changes under it, so they are made once, with the window.
        /// </summary>
        public required List<MobyInspectorRow> Rows { get; init; }

        /// <summary>Kept for the title bar, and re-read out of every row that arrives.</summary>
        public long? OClass { get; set; }

        public byte[] Row { get; set; } = Array.Empty<byte>();

        /// <summary>Counted up by every row that arrives, which is what makes the values stale.</summary>
        public int RowVersion { get; set; }

        /// <summary>The row, the font and the room the values and the widths were made for.</summary>
        public int MeasuredVersion { get; set; } = -1;

        public float MeasuredFont { get; set; }

        public float MeasuredRoom { get; set; }

        public MobyColumnWidths Widths { get; set; }

        /// <summary>How wide the line of buttons is, which is a floor under the window's width.</summary>
        public float ToolbarWidth { get; set; }

        /// <summary>The size the table asks for, once it has been drawn once, and the last one given.</summary>
        public Vector2? Wanted { get; set; }

        public Vector2 Given { get; set; }

        /// <summary>
        /// Set the first frame the window is not the size it was told to be, which is the frame
        /// after a corner was dragged. From then on the size is the user's and is left alone.
        /// </summary>
        public bool UserSized { get; set; }

        public float SinceRead { get; set; }

        public bool Reading { get; set; }

        /// <summary>Set when the row was double-clicked again, so the window it already has comes forward.</summary>
        public bool Focus { get; set; } = true;

        /// <summary>How far down and right of the centre this one opens, so a second does not hide the first.</summary>
        public int Cascade { get; init; }

        /// <summary>
        /// What is in a Value box while it has focus, by field offset: the watches table's rule, for
        /// the same reason. A row arriving from the console must not overwrite a half-typed number.
        /// </summary>
        public Dictionary<int, string> Drafts { get; } = new();
    }

    private static readonly List<Inspector> Windows = new();

    /// <summary>
    /// Opens the moby at this row, or brings its window forward when one is already open for that
    /// address. The layout and the stride come from the panel, which is where the table was read.
    /// </summary>
    public static void Open(AppState state, MobyRow row, MobyLayout? layout, int stride)
    {
        foreach (var open in Windows)
        {
            if (open.Address != row.Address) continue;

            // Opening a moby again is the way back to the size its table wants, after the window
            // has been dragged to some other one.
            open.Focus = true;
            open.UserSized = false;
            open.Given = Vector2.Zero;
            return;
        }

        var window = new Inspector
        {
            Address = row.Address,
            Index = row.Index,
            Stride = stride > 0 ? stride : 256,
            Layout = layout,
            Rows = MobyInspectorRows.Build(layout?.Struct ?? new List<MobyField>()),
            OClass = row.OClass,
            Cascade = Windows.Count % 6,
        };

        Windows.Add(window);
        Read(state, window, quiet: false);
    }

    /// <summary>Every window, because every address in them belongs to a process that has gone.</summary>
    public static void CloseAll() => Windows.Clear();

    /// <summary>
    /// Drawn outside the root window, beside the modals, so an inspector survives a panel switch.
    /// A window whose close box was pressed is dropped here.
    /// </summary>
    public static void Draw(AppState state)
    {
        for (int i = Windows.Count - 1; i >= 0; i--)
        {
            if (!DrawWindow(state, Windows[i])) Windows.RemoveAt(i);
        }
    }

    private static bool DrawWindow(AppState state, Inspector window)
    {
        // The title says which moby this is; the id behind ### is the address alone, so the window
        // keeps its place and its size when the oClass in the title changes under it.
        string oClass = window.OClass is { } value ? $", oClass {value}" : string.Empty;
        string title = $"Moby {window.Index} at 0x{window.Address:X8}{oClass}###moby-{window.Address:X8}";

        var centre = ImGui.GetMainViewport().GetCenter();
        float step = window.Cascade * 26f;
        ImGui.SetNextWindowPos(new Vector2(centre.X + step, centre.Y + step), ImGuiCond.Appearing,
            new Vector2(0.5f, 0.5f));

        // Until the table has been drawn once there is nothing to measure it against.
        ImGui.SetNextWindowSize(new Vector2(560, 480), ImGuiCond.Appearing);

        // After that the window is the size its table asks for. Always, so it shrinks as well as
        // grows, but only on the frames that size changes: forcing it every frame would sit on top
        // of a corner being dragged and the user could never make the window their own.
        bool given = false;
        if (!window.UserSized && window.Wanted is { } wanted && Differs(window.Given, wanted))
        {
            ImGui.SetNextWindowSize(wanted, ImGuiCond.Always);
            window.Given = wanted;
            given = true;
        }

        if (window.Focus)
        {
            window.Focus = false;
            ImGui.SetNextWindowFocus();
        }

        bool open = true;
        if (ImGui.Begin(title, ref open))
        {
            // A window that is not the size it was last given was dragged there by hand.
            if (!given && !window.UserSized && window.Wanted is not null
                && Differs(ImGui.GetWindowSize(), window.Given))
            {
                window.UserSized = true;
            }

            DrawBody(state, window);
        }

        // Always: Begin is false for a collapsed window as well as for a closed one.
        ImGui.End();
        return open;
    }

    /// <summary>
    /// Whether two sizes are the same window. A pixel of slack, because ImGui keeps a window's size
    /// as whole pixels and the one asked for is measured text.
    /// </summary>
    private static bool Differs(Vector2 a, Vector2 b) =>
        MathF.Abs(a.X - b.X) > 1f || MathF.Abs(a.Y - b.Y) > 1f;

    private static void DrawBody(AppState state, Inspector window)
    {
        bool canRead = state.Connected && !state.ConsoleBusy;

        ImGui.BeginDisabled(!canRead || window.Reading);
        if (ImGui.Button("Refresh")) Read(state, window, quiet: false);
        ImGui.EndDisabled();

        // Where the double-click used to go. A row is bytes as well as fields, and the two views
        // answer different questions.
        ImGui.SameLine();
        if (ImGui.Button("Show in viewer")) MemoryPanel.ShowInViewer(state, window.Address, window.Stride);

        ImGui.SameLine();
        Ui.TableLabel(Ui.Grey, $"{window.Stride} bytes at 0x{window.Address:X8}");

        // The line of buttons holds the window open as much as the table does: the address behind
        // them is wider than a narrow table.
        window.ToolbarWidth = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X - ImGui.GetCursorStartPos().X;

        Tick(state, window);

        var layout = window.Layout;
        if (layout is null || layout.Struct.Count == 0)
        {
            Ui.Hint("The layout file for this game lists no struct fields, so there is nothing to "
                    + "name. The viewer shows the row as bytes.");
            return;
        }

        if (window.Row.Length == 0)
        {
            Ui.Hint(canRead
                ? "Reading the row..."
                : "The row is read while the console is connected and answering.");
            return;
        }

        ImGui.Spacing();
        DrawFields(state, window);
    }

    /// <summary>
    /// The row again, on the Settings panel's table interval, for the reason the viewer's dump is:
    /// this is a window on memory the game is writing, and a stale window is worse than a re-read a
    /// second. An interval of zero leaves the Refresh button as the only thing that reads.
    /// </summary>
    private static void Tick(AppState state, Inspector window)
    {
        float period = state.Settings.TableRefreshSeconds;
        if (period <= 0 || !state.Connected || state.ConsoleBusy)
        {
            window.SinceRead = 0;
            return;
        }

        window.SinceRead += ImGui.GetIO().DeltaTime;

        // A read still on the wire means the interval is shorter than the round trip; skipping the
        // tick keeps that from queueing reads for ever.
        if (window.SinceRead < period || window.Reading) return;

        window.SinceRead = 0;
        Read(state, window);
    }

    /// <summary>
    /// One MEM_READ of the whole row. The in-flight flag is what the tick above looks at, so it is
    /// cleared in a finally: a read that failed must not stop the next one from ever starting.
    /// </summary>
    private static void Read(AppState state, Inspector window, bool quiet = true)
    {
        if (window.Reading || !state.Connected || state.ConsoleBusy) return;

        window.Reading = true;
        uint address = window.Address;
        uint length = (uint)window.Stride;

        async Task ReadRow()
        {
            try
            {
                var bytes = await state.Client.MemReadAsync(address, length).ConfigureAwait(false);
                state.Post(() =>
                {
                    window.Row = bytes;
                    window.RowVersion++;
                    if (window.Layout is { } layout && layout.TryReadInteger(bytes, "oClass", out long oClass))
                    {
                        window.OClass = oClass;
                    }
                });
            }
            finally
            {
                state.Post(() => window.Reading = false);
            }
        }

        if (quiet) state.RunQuiet(ReadRow);
        else state.Run(ReadRow);
    }

    private static void DrawFields(AppState state, Inspector window)
    {
        var style = ImGui.GetStyle();
        var viewport = ImGui.GetMainViewport();
        float overhead = TableOverhead(style);

        // What the four columns may take between them: whatever a window that wide has left after
        // the table's own padding and the window's.
        Measure(window, (viewport.Size.X * Share) - overhead - (style.WindowPadding.X * 2f));
        var widths = window.Widths;

        // A row is as tall as the box in its Value cell and the header is a line of text, both with
        // the cell padding above and below, and two pixels of slack keep the last row off the
        // border rather than half a pixel behind a scrollbar.
        float rowHeight = ImGui.GetFrameHeight() + (style.CellPadding.Y * 2f);
        float padded = (style.CellPadding.Y * 2f) + 2f;
        float whole = ImGui.GetTextLineHeight() + padded + (window.Rows.Count * rowHeight);

        // The window is as tall as the table up to the cap; past it the rows that fit are shown and
        // the rest are scrolled to, which is what a hundred-row struct needs on any screen.
        float top = ImGui.GetCursorPosY();
        float room = (viewport.Size.Y * Share) - top - style.WindowPadding.Y;
        float height = MathF.Ceiling(whole <= room ? whole : MathF.Max(room, (rowHeight * 4f) + padded));
        bool scrolls = height < whole;

        // The height is given here rather than left to the window: it is the window that is made to
        // fit the table, and a table told to fill what is left would be measuring itself.
        if (!ImGui.BeginTable("fields", Columns, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg
                | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit, new Vector2(0f, height)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(MobyInspectorRows.FieldHeader, ImGuiTableColumnFlags.WidthFixed, widths.Field);
        ImGui.TableSetupColumn(MobyInspectorRows.OffsetHeader, ImGuiTableColumnFlags.WidthFixed, widths.Offset);
        ImGui.TableSetupColumn(MobyInspectorRows.TypeHeader, ImGuiTableColumnFlags.WidthFixed, widths.Type);
        ImGui.TableSetupColumn(MobyInspectorRows.ValueHeader, ImGuiTableColumnFlags.WidthFixed, widths.Value);
        ImGui.TableHeadersRow();

        // Writing a field is writing game memory, so it needs a game; reading one only needs the
        // row that is already here.
        bool enabled = state.Ingame;

        foreach (var row in window.Rows)
        {
            ImGui.TableNextRow();
            ImGui.PushID(row.Field.Offset);

            // A selectable rather than a label, so the whole row takes the right-click that turns a
            // field into a watch; it allows overlap so the box in the last column stays reachable.
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Selectable(row.Name, false,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap);
            DrawContextMenu(state, window, row.Field);

            ImGui.TableNextColumn();
            Ui.TableLabel(row.Offset);

            ImGui.TableNextColumn();
            Ui.TableLabel(row.Type);

            ImGui.TableNextColumn();
            DrawValue(state, window, row, enabled);

            ImGui.PopID();
        }

        ImGui.EndTable();

        // What the window is made to be next frame: the table, the line of buttons above it, and
        // the scrollbar when the rows did not all fit.
        float width = MathF.Max(widths.Total + overhead + (scrolls ? style.ScrollbarSize : 0f), window.ToolbarWidth);
        window.Wanted = new Vector2(MathF.Ceiling(width + (style.WindowPadding.X * 2f)),
            MathF.Ceiling(top + height + style.WindowPadding.Y));
    }

    /// <summary>
    /// What the table puts around the four columns: the cell padding on both sides of each of them,
    /// the border line between two, and the outer border down each edge.
    /// </summary>
    private static float TableOverhead(ImGuiStylePtr style) =>
        (style.CellPadding.X * 2f * Columns) + (Columns - 1) + 2f;

    /// <summary>
    /// The values and the column widths, made again when a row has arrived, the font has changed
    /// size or the main window has left them less room. Measuring a struct's worth of strings is
    /// not a per-frame job and does not have to be one: a column only moves when what is in it does.
    /// </summary>
    private static void Measure(Inspector window, float room)
    {
        float font = ImGui.GetFontSize();
        if (window.MeasuredVersion == window.RowVersion
            && MathF.Abs(window.MeasuredFont - font) < 0.01f
            && MathF.Abs(window.MeasuredRoom - room) < 0.5f)
        {
            return;
        }

        window.MeasuredVersion = window.RowVersion;
        window.MeasuredFont = font;
        window.MeasuredRoom = room;

        MobyInspectorRows.Refresh(window.Rows, window.Row);
        window.Widths = MobyInspectorRows.Measure(window.Rows, static text => ImGui.CalcTextSize(text).X, room);
    }

    /// <summary>
    /// What a field can become: a watch on the console, or an address in the viewer. The names are
    /// the moby's and the field's, so a watchlist made this way still reads as one a week later.
    /// </summary>
    private static void DrawContextMenu(AppState state, Inspector window, MobyField field)
    {
        if (!ImGui.BeginPopupContextItem()) return;

        uint address = window.Address + (uint)field.Offset;
        ImGui.TextUnformatted($"{field.Name} at 0x{address:X8}");
        ImGui.Separator();

        string name = $"moby {window.Index} {field.Name}";
        byte size = MobyFieldCodec.WatchSize(field);

        ImGui.BeginDisabled(!state.Connected);
        if (size > 0)
        {
            if (ImGui.MenuItem("Add to watches"))
            {
                MemoryPanel.AddNamedWatch(state, address, size, name, MobyFieldCodec.FormatFor(field.Type));
            }
        }
        else
        {
            // The 8-byte fields and the raw blocks. A vector is not one of them any more: each of
            // its floats is a row of its own here, and four bytes is a watch like any other.
            ImGui.MenuItem("Add to watches", string.Empty, false, false);
            Ui.Tooltip(MobyFieldCodec.WatchSizes);
        }

        ImGui.EndDisabled();

        // A pointer is the one field whose value is another address, so it is the one field with
        // somewhere to go. A null pointer has nothing to show.
        if (field.Type == "ptr")
        {
            bool has = MobyFieldCodec.TryReadPointer(field, window.Row, out uint target) && target != 0;
            ImGui.BeginDisabled(!has);
            if (ImGui.MenuItem("Show target in viewer")) MemoryPanel.ShowInViewer(state, target, PointerReadLength);
            ImGui.EndDisabled();
        }

        ImGui.EndPopup();
    }

    /// <summary>
    /// The live value, as a box that writes it back: the watches table's Value cell, with the
    /// field's own type deciding what the box shows and what it takes. A raw block is shown and
    /// never written, because nothing here knows what is in one.
    /// </summary>
    private static void DrawValue(AppState state, Inspector window, MobyInspectorRow row, bool enabled)
    {
        var field = row.Field;

        // Formatted when the row arrived rather than here: the same string the column was measured
        // against is the one the box shows.
        string live = row.Value;
        if (live.Length == 0)
        {
            window.Drafts.Remove(field.Offset);
            Ui.TableLabel(Ui.Grey, "-");
            return;
        }

        if (!MobyFieldCodec.IsEditable(field.Type))
        {
            // Still a box rather than a label: a block of 48 bytes is wider than the column, and a
            // box can be scrolled along and copied out of.
            ImGui.SetNextItemWidth(-1);
            Ui.PushTableInput();
            ImGui.InputText("##value", ref live, (uint)live.Length + 1, ImGuiInputTextFlags.ReadOnly);
            Ui.PopTableInput();
            return;
        }

        string text = window.Drafts.TryGetValue(field.Offset, out var draft) ? draft : live;

        ImGui.BeginDisabled(!enabled);
        ImGui.SetNextItemWidth(-1);
        Ui.PushTableInput();
        bool entered = ImGui.InputText("##value", ref text, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        Ui.PopTableInput();
        bool active = ImGui.IsItemActive();
        ImGui.EndDisabled();

        if (active) window.Drafts[field.Offset] = text;
        else window.Drafts.Remove(field.Offset);

        if (!entered) return;

        window.Drafts.Remove(field.Offset);

        if (!MobyFieldCodec.TryEncode(field, text, out var bytes))
        {
            state.AddToast($"'{text.Trim()}' is not a {field.Type} value", ToastKind.Error);
            return;
        }

        uint address = window.Address + (uint)field.Offset;
        state.Run(() => state.Client.MemWriteAsync(address, bytes));
    }
}
