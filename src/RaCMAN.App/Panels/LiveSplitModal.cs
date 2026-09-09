using System.Numerics;
using ImGuiNET;

namespace RaCMAN.App.Panels;

/// <summary>
/// What to say when nothing answers on LiveSplit's port. Its server ships with LiveSplit but is not
/// running until somebody starts it, which is the one thing everyone gets wrong, so a failed
/// connection says so where it cannot be missed instead of in a hint under a status line.
/// <para>
/// An attempt the user started (the Autosplitter panel's Connect button, the endpoint on the
/// Settings panel) always gets the popup. The reconnect loop's own retries get it once per run of
/// the client and never again: it retries every five seconds for as long as the autosplitter is on,
/// and a popup every five seconds is worse than no popup at all.
/// </para>
/// </summary>
public static class LiveSplitModal
{
    private const string Title = "LiveSplit not found";

    /// <summary>The whole message, in the words the panel and the tests both use.</summary>
    public const string Body =
        "LiveSplit server not found. Make sure LiveSplit is running, and the server is enabled "
        + "(Control -> Start TCP Server).";

    private static int _seenFailures;
    private static bool _armed;
    private static bool _shownAutomatically;
    private static bool _open;

    /// <summary>
    /// The next failed attempt is one the user asked for, so it gets the popup whatever has been
    /// shown before. Called by whatever starts the connection, not by the failure itself.
    /// </summary>
    public static void ArmForAttempt() => _armed = true;

    public static void Draw(AppState state)
    {
        Watch(state.LiveSplit.ConnectFailures);

        if (_open && !ImGui.IsPopupOpen(Title)) ImGui.OpenPopup(Title);
        if (!_open) return;

        var centre = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        bool open = true;
        if (!ImGui.BeginPopupModal(Title, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) _open = false;
            return;
        }

        // TextUnformatted inside an explicit wrap region: ImGui's Text is printf, and an
        // auto-resizing window needs to be told how wide the sentence is allowed to be.
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 420f);
        ImGui.TextUnformatted(Body);
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.Button("OK", new Vector2(120, 0)))
        {
            _open = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();

        if (!open) _open = false;
    }

    /// <summary>
    /// One frame's look at the client's failure count. Only a count that moved is a new failure,
    /// so a popup dismissed while the loop keeps retrying stays dismissed.
    /// </summary>
    private static void Watch(int failures)
    {
        if (failures == _seenFailures) return;
        _seenFailures = failures;

        if (_armed)
        {
            _armed = false;
            _open = true;
            return;
        }

        if (_shownAutomatically) return;

        _shownAutomatically = true;
        _open = true;
    }
}
