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

    private sealed class Inspector
    {
        public required uint Address { get; init; }

        public required int Index { get; init; }

        /// <summary>The bytes one row holds, which is what one MEM_READ asks for.</summary>
        public required int Stride { get; init; }

        public required MobyLayout? Layout { get; init; }

        /// <summary>Kept for the title bar, and re-read out of every row that arrives.</summary>
        public long? OClass { get; set; }

        public byte[] Row { get; set; } = Array.Empty<byte>();

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

            open.Focus = true;
            return;
        }

        var window = new Inspector
        {
            Address = row.Address,
            Index = row.Index,
            Stride = stride > 0 ? stride : 256,
            Layout = layout,
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
        ImGui.SetNextWindowSize(new Vector2(560, 480), ImGuiCond.Appearing);

        if (window.Focus)
        {
            window.Focus = false;
            ImGui.SetNextWindowFocus();
        }

        bool open = true;
        if (ImGui.Begin(title, ref open)) DrawBody(state, window);

        // Always: Begin is false for a collapsed window as well as for a closed one.
        ImGui.End();
        return open;
    }

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
        DrawFields(state, window, layout);
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

    private static void DrawFields(AppState state, Inspector window, MobyLayout layout)
    {
        if (!ImGui.BeginTable("fields", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg
                | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 180);
        ImGui.TableSetupColumn("Offset", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        // Writing a field is writing game memory, so it needs a game; reading one only needs the
        // row that is already here.
        bool enabled = state.Ingame;

        foreach (var field in layout.Struct)
        {
            ImGui.TableNextRow();
            ImGui.PushID(field.Offset);

            // A selectable rather than a label, so the whole row takes the right-click that turns a
            // field into a watch; it allows overlap so the box in the last column stays reachable.
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.Selectable(field.Name, false,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap);
            DrawContextMenu(state, window, field);

            ImGui.TableNextColumn();
            Ui.TableLabel($"0x{field.Offset:X2}");

            ImGui.TableNextColumn();
            Ui.TableLabel(field.Type == "bytes" ? $"bytes[{field.Length}]" : field.Type);

            ImGui.TableNextColumn();
            DrawValue(state, window, field, enabled);

            ImGui.PopID();
        }

        ImGui.EndTable();
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
        else if (MobyFieldCodec.IsVector(field.Type))
        {
            // A watch holds one number, so a vector is offered as the floats it is made of.
            for (int i = 0; i < MobyFieldCodec.ComponentCount(field.Type); i++)
            {
                string component = MobyFieldCodec.Components[i];
                if (!ImGui.MenuItem($"Add {component} to watches")) continue;

                MemoryPanel.AddNamedWatch(state, address + (uint)(i * 4), 4, $"{name}.{component}", "float");
            }
        }
        else
        {
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
    private static void DrawValue(AppState state, Inspector window, MobyField field, bool enabled)
    {
        string live = MobyFieldCodec.Format(field, window.Row);
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
