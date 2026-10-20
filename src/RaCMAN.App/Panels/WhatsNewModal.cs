using System.Numerics;
using ImGuiNET;

namespace RaCMAN.App.Panels;

/// <summary>
/// The What's new window that the update banner's button opens: each release's notes, newest
/// first, scrolling once they are taller than a good part of the window. A modal like the client's
/// other dialogs, closed by its Close button or the cross on its title bar. The client has one font,
/// so a heading and bold text are that font drawn twice a pixel apart; a link is underlined, shows
/// its address on hover and opens in the browser on a click.
/// </summary>
public static class WhatsNewModal
{
    /// <summary>The popup's id. The title in front of it names the version, so it is not the id.</summary>
    private const string Id = "###whats-new";

    private static Vector4 LinkColour => Ui.Light ? new(0.10f, 0.35f, 0.80f, 1f) : new(0.45f, 0.65f, 1.00f, 1f);

    public static void Draw(AppState state)
    {
        var updates = state.Updates;
        var notes = updates.Notes;
        bool wanted = updates.NotesOpen && notes.Count > 0;

        if (wanted && !ImGui.IsPopupOpen(Id)) ImGui.OpenPopup(Id);
        if (!ImGui.IsPopupOpen(Id)) return;

        // Centred on every frame rather than on the first: the window sizes itself to the notes a
        // frame after it appears, and centred on that first guess a long one runs off the bottom.
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Always, new Vector2(0.5f, 0.5f));

        bool open = true;
        string version = updates.UpdatedTo ?? updates.AvailableVersion ?? (notes.Count > 0 ? notes[0].Version : string.Empty);
        if (!ImGui.BeginPopupModal($"What's new in RaCMAN Reloaded {version}{Id}", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) updates.CloseNotes();
            return;
        }

        // Taken away underneath it, by a check that found something else: nothing left to show.
        if (!wanted)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        float width = MathF.Max(240f, MathF.Min(620f, viewport.WorkSize.X - 80f));
        float tallest = MathF.Max(160f, viewport.WorkSize.Y * 0.6f);
        ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0), new Vector2(width, tallest));

        if (ImGui.BeginChild("##notes", new Vector2(width, 0), ImGuiChildFlags.AutoResizeY))
        {
            var text = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];

            // The notes of the version the title names need no heading of their own; with skipped
            // releases, or when only an older one has notes, every release says which it is.
            bool headings = notes.Count > 1 || AppVersion.Compare(notes[0].Version, version) != 0;

            for (int r = 0; r < notes.Count; r++)
            {
                ImGui.PushID(r);

                if (headings)
                {
                    if (r > 0) ImGui.Dummy(new Vector2(0, ImGui.GetTextLineHeight() * 0.6f));
                    Flow(state, new[] { new NoteSpan($"RaCMAN Reloaded {notes[r].Version}") }, Ui.Green, bold: true);
                    ImGui.Separator();
                }

                DrawBlocks(state, notes[r].Blocks, text);
                ImGui.PopID();
            }
        }

        ImGui.EndChild();
        ImGui.Separator();

        bool close = ImGui.Button("Close", new Vector2(120, 0));
        ImGui.SetItemDefaultFocus();
        if (close || !open)
        {
            updates.CloseNotes();
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private static void DrawBlocks(AppState state, IReadOnlyList<NoteBlock> blocks, Vector4 text)
    {
        float line = ImGui.GetTextLineHeight();

        for (int b = 0; b < blocks.Count; b++)
        {
            var block = blocks[b];
            ImGui.PushID(b);

            switch (block.Kind)
            {
                case NoteBlockKind.Blank:
                    ImGui.Dummy(new Vector2(0, line * 0.4f));
                    break;

                case NoteBlockKind.Rule:
                    ImGui.Separator();
                    break;

                case NoteBlockKind.Heading:
                    if (b > 0 && blocks[b - 1].Kind != NoteBlockKind.Blank) ImGui.Spacing();
                    Flow(state, block.Spans, text, bold: true);
                    break;

                case NoteBlockKind.Bullet:
                {
                    // Room on the left for the dot, or for the whole of a number.
                    float marker = block.Marker.Length == 0 ? 0 : ImGui.CalcTextSize(block.Marker).X + line * 0.3f;
                    float indent = line * 1.2f * block.Level + MathF.Max(line * 1.2f, marker + line * 0.2f);
                    ImGui.Indent(indent);

                    var at = ImGui.GetCursorScreenPos();
                    var drawList = ImGui.GetWindowDrawList();
                    uint colour = ImGui.GetColorU32(text);
                    if (block.Marker.Length == 0)
                    {
                        drawList.AddCircleFilled(new Vector2(at.X - line * 0.6f, at.Y + line * 0.5f), line * 0.17f, colour);
                    }
                    else
                    {
                        drawList.AddText(new Vector2(at.X - marker, at.Y), colour, block.Marker);
                    }

                    Flow(state, block.Spans, text, bold: false);
                    ImGui.Unindent(indent);
                    break;
                }

                case NoteBlockKind.Quote:
                    ImGui.Indent(line);
                    Flow(state, block.Spans, Ui.Grey, bold: false);
                    ImGui.Unindent(line);
                    break;

                case NoteBlockKind.Code:
                    Flow(state, block.Spans, Ui.Grey, bold: false);
                    break;

                default:
                {
                    // A line indented under a bullet sits with that bullet's text.
                    float indent = line * 1.2f * block.Level;
                    if (indent > 0) ImGui.Indent(indent);
                    Flow(state, block.Spans, text, bold: false);
                    if (indent > 0) ImGui.Unindent(indent);
                    break;
                }
            }

            ImGui.PopID();
        }
    }

    /// <summary>
    /// Lays the spans out word by word, wrapping at the box's edge, because a line can change from
    /// plain to bold to a link and back, which no single ImGui call draws. Every word is drawn
    /// through the draw list, which takes text as it is rather than as a format.
    /// </summary>
    private static void Flow(AppState state, IReadOnlyList<NoteSpan> spans, Vector4 colour, bool bold)
    {
        var drawList = ImGui.GetWindowDrawList();
        float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        bool first = true;
        int link = 0;

        foreach (var span in spans)
        {
            bool heavy = bold || span.Bold;
            uint ink = ImGui.GetColorU32(span.Link is null ? colour : LinkColour);

            int offset = 0;
            foreach (string word in Words(span.Text))
            {
                offset += word.Length;
                var size = ImGui.CalcTextSize(word);
                if (size.X <= 0) continue;
                if (heavy) size.X += 1;

                if (!first)
                {
                    ImGui.SameLine(0, 0);
                    float inkWidth = ImGui.CalcTextSize(word.TrimEnd()).X;
                    if (!char.IsWhiteSpace(word[0]) && ImGui.GetCursorPosX() + inkWidth > right) ImGui.NewLine();
                }

                first = false;
                var at = ImGui.GetCursorScreenPos();

                if (span.Link is { } address)
                {
                    ImGui.PushID(link++);
                    bool clicked = ImGui.InvisibleButton("##link", size);
                    ImGui.PopID();

                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        Ui.SetTooltip(address);
                    }

                    if (clicked)
                    {
                        var (ok, message) = FileExplorer.OpenLink(address);
                        if (!ok) state.AddToast(message, ToastKind.Error);
                    }

                    // Under the space between two words of one link as well, not after its last.
                    float underline = at.Y + size.Y - 1;
                    float length = offset < span.Text.Length ? size.X : ImGui.CalcTextSize(word.TrimEnd()).X;
                    drawList.AddLine(new Vector2(at.X, underline), new Vector2(at.X + length, underline), ink);
                }
                else
                {
                    ImGui.Dummy(size);
                }

                drawList.AddText(at, ink, word);
                if (heavy) drawList.AddText(at + new Vector2(1, 0), ink, word);
            }
        }

        // A line with nothing on it still takes its height.
        if (first) ImGui.NewLine();
    }

    /// <summary>Each word with the spaces after it, and any spaces before the first on their own.</summary>
    private static IEnumerable<string> Words(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            yield return text[start..i];
        }
    }
}
