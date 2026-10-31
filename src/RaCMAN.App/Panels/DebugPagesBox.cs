using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// A game's debug pages (DEBUG_TEXT, revision 1.29), in the Debug section: read-only text your
/// console makes from the game's memory, shown as it comes. Today only Tools of Destruction has
/// any: "Now" (the level warmer, what an opening cutscene waits for, the save thread) and
/// "Cache" (the level cache's blocks per file).
/// <para>
/// The client knows nothing of what the pages say: their titles, their text and which of them are
/// slow all come from the console. A page that is not slow is read when it is first opened and,
/// with Auto-refresh on, twice a second while it is open. A slow page reads a lot of the game's
/// memory, so it is read only when Read is clicked, never on a timer.
/// </para>
/// </summary>
public static class DebugPagesBox
{
    /// <summary>How often an auto-refreshed page is read while it is open.</summary>
    public const float AutoSeconds = 0.5f;

    /// <summary>The tallest a page's text box grows before it scrolls, in lines.</summary>
    private const int MaxLines = 24;

    private static readonly HashSet<byte> AutoPages = new();
    private static float _sinceAuto;

    /// <summary>Forgets which pages refresh themselves: a different game, or everything read again.</summary>
    public static void Reset()
    {
        AutoPages.Clear();
        _sinceAuto = 0;
    }

    /// <summary>Whether the section has anything of this box to draw.</summary>
    public static bool HasContent(AppState state) => state.DebugPages.Count > 0;

    /// <summary>The line under a page's buttons: when it was read, or why it was refused.</summary>
    public static string? StatusLine(AppState.DebugPageText? last, bool slow) => last switch
    {
        null => slow ? "Not read yet. It reads a lot of the game's memory, so it waits for Read." : null,
        { Refusal: { } refusal } => $"Refused: {refusal.WireName()}" + (last.Text is null ? string.Empty : " (the text below is older)"),
        _ => $"Read at {last.At:HH:mm:ss}",
    };

    public static void Draw(AppState state)
    {
        var pages = state.DebugPages;
        if (pages.Count == 0) return;

        Ui.Heading("Debug pages");
        Ui.Hint("Read only: what the game on your console is doing, as text. Nothing here writes to the game.");

        _sinceAuto += ImGui.GetIO().DeltaTime;
        bool due = _sinceAuto >= AutoSeconds;
        if (due) _sinceAuto = 0;

        foreach (var page in pages)
        {
            ImGui.PushID(page.Index);
            if (ImGui.CollapsingHeader(page.Title + (page.Slow ? " (slow)" : string.Empty)))
            {
                DrawPage(state, page, due);
            }

            ImGui.PopID();
        }
    }

    private static void DrawPage(AppState state, DebugPage page, bool due)
    {
        bool busy = state.DebugTextInFlight(page.Index);
        var last = state.DebugTextOf(page.Index);

        // A page that is cheap to read is read as soon as it is opened.
        if (!page.Slow && last is null && !busy && state.Connected) state.RefreshDebugText(page.Index);

        ImGui.BeginDisabled(busy || !state.Connected);
        bool read = ImGui.Button(page.Slow ? "Read" : "Refresh");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            Ui.SetTooltip(!state.Connected ? "Connect to your console first"
                : busy ? "Reading..."
                : page.Slow ? "Reads the page once. It reads a lot of the game's memory, so it can take a few seconds"
                : "Reads the page again");
        }

        if (read) state.RefreshDebugText(page.Index);

        if (!page.Slow)
        {
            ImGui.SameLine();
            bool auto = AutoPages.Contains(page.Index);
            if (ImGui.Checkbox("Auto-refresh", ref auto))
            {
                if (auto) AutoPages.Add(page.Index);
                else AutoPages.Remove(page.Index);
            }

            if (auto && due && !busy) state.RefreshDebugText(page.Index);
        }

        if (last?.Text is { } shown)
        {
            ImGui.SameLine();
            if (ImGui.Button("Copy")) ImGui.SetClipboardText(shown);
        }

        if (StatusLine(last, page.Slow) is { } line)
        {
            ImGui.SameLine();
            Ui.Text(last?.Refusal is null ? Ui.Grey : Ui.Red, line);
        }

        if (last?.Text is not { } text) return;

        int lines = Math.Clamp(text.Count(c => c == '\n') + 1, 2, MaxLines);
        float height = ImGui.GetTextLineHeightWithSpacing() * lines + ImGui.GetStyle().WindowPadding.Y * 2;
        ImGui.BeginChild("##debug-text", new Vector2(-1, height), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.HorizontalScrollbar);
        ImGui.TextUnformatted(text);
        ImGui.EndChild();
    }
}
