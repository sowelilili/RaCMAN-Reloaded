using System.Numerics;
using ImGuiNET;

namespace RaCMAN.App.Panels;

/// <summary>
/// What to say when a connection to LiveSplit does not end in a LiveSplit this client can drive.
/// There are two ways for that to happen, and they want different words:
/// <list type="bullet">
/// <item>
/// Nothing answers on the port. The server ships with LiveSplit but is not running until somebody
/// starts it, which is the one thing everyone gets wrong.
/// </item>
/// <item>
/// Something answers but will not name its version, which is every build before the development
/// one. Telling that user LiveSplit was "not found" would send them looking for the wrong thing.
/// </item>
/// </list>
/// Either way it says so where it cannot be missed instead of in a hint under a status line.
/// <para>
/// An attempt the user started (the Autosplitter panel's Connect button, the endpoint on the
/// Settings panel) always gets the popup. The reconnect loop's own retries get it once per run of
/// the client and never again: it retries every five seconds for as long as the autosplitter is on,
/// and a popup every five seconds is worse than no popup at all. The two messages are counted
/// apart, so a user who starts the server and then meets the version wall sees both.
/// </para>
/// </summary>
public static class LiveSplitModal
{
    private const string NotFoundTitle = "LiveSplit not found";

    private const string TooOldTitle = "LiveSplit too old";

    /// <summary>The whole message, in the words the panel and the tests both use.</summary>
    public const string Body =
        "LiveSplit server not found. Make sure LiveSplit is running, and the server is enabled "
        + "(Control -> Start TCP Server).";

    /// <summary>The other message: LiveSplit is there, and it is not the build this client needs.</summary>
    public const string TooOldBody =
        "This LiveSplit is too old. RaCMAN needs the LiveSplit development build, whose server "
        + "answers getupcomingsplitname. Download it from livesplit.org.";

    private static int _seenFailures;
    private static int _seenTooOld;
    private static bool _armed;
    private static bool _shownNotFound;
    private static bool _shownTooOld;
    private static bool _tooOld;
    private static bool _open;

    /// <summary>
    /// The next failed attempt is one the user asked for, so it gets the popup whatever has been
    /// shown before. Called by whatever starts the connection, not by the failure itself.
    /// </summary>
    public static void ArmForAttempt() => _armed = true;

    public static void Draw(AppState state)
    {
        Watch(state.LiveSplit.ConnectFailures, state.LiveSplit.TooOldFailures);

        string title = _tooOld ? TooOldTitle : NotFoundTitle;
        if (_open && !ImGui.IsPopupOpen(title)) ImGui.OpenPopup(title);
        if (!_open) return;

        var centre = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        bool open = true;
        if (!ImGui.BeginPopupModal(title, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) _open = false;
            return;
        }

        // TextUnformatted inside an explicit wrap region: ImGui's Text is printf, and an
        // auto-resizing window needs to be told how wide the sentence is allowed to be.
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 420f);
        ImGui.TextUnformatted(_tooOld ? TooOldBody : Body);
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
    /// One frame's look at the client's two failure counts. Only a count that moved is a new
    /// failure, so a popup dismissed while the loop keeps retrying stays dismissed. The version
    /// wall is checked first: it is the more specific of the two answers.
    /// </summary>
    private static void Watch(int failures, int tooOld)
    {
        if (tooOld != _seenTooOld)
        {
            _seenTooOld = tooOld;
            Show(ref _shownTooOld, tooOld: true);
            return;
        }

        if (failures != _seenFailures)
        {
            _seenFailures = failures;
            Show(ref _shownNotFound, tooOld: false);
        }
    }

    /// <summary>Opens the popup, unless this message has already shown itself unasked.</summary>
    private static void Show(ref bool shownAutomatically, bool tooOld)
    {
        if (_armed)
        {
            _armed = false;
        }
        else if (shownAutomatically)
        {
            return;
        }
        else
        {
            shownAutomatically = true;
        }

        _tooOld = tooOld;
        _open = true;
    }
}
