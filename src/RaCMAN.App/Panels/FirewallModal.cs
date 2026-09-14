using System.Numerics;
using ImGuiNET;

namespace RaCMAN.App.Panels;

/// <summary>
/// The "allow RaCMAN through the firewall" offer. The console streams its live state over UDP, and
/// Windows Firewall blocks unsolicited inbound UDP for an unsigned app, usually without prompting,
/// so the client would silently fall back to slower TCP polling.
/// <para>
/// It is shown when the firewall really has no rule for the executable that is running — not merely
/// once ever. A firewall rule names a path, and the path moves: the updater replaces the installed
/// <c>current\</c> folder on every release, a portable copy gets dragged somewhere else, and another
/// copy's run of the helper can take the rule away. The old marker recorded only that the question
/// had been put, so none of that was ever noticed. See <see cref="FirewallHelper.Decide"/>, which
/// holds the whole rule.
/// </para>
/// <para>
/// Declining is still fine and still remembered: the client falls back to TCP and the Connection
/// panel keeps the one-click offer.
/// </para>
/// </summary>
public static class FirewallModal
{
    private const string Title = "Allow RaCMAN through the firewall";

    private static bool _open;
    private static FirewallOffer _offer;
    private static string _executable = string.Empty;

    /// <summary>
    /// Call once at startup. Reads the firewall rules off the render thread — the list can be
    /// thousands of entries — and comes back through the queue like everything else. Does nothing
    /// at all on a build with no helper beside it, which is every dev run and every other platform.
    /// </summary>
    public static void MaybeOffer(AppState state)
    {
        if (!FirewallHelper.IsSupported || !FirewallHelper.ScriptPresent) return;

        string executable = FirewallHelper.TargetExecutable;
        _ = Task.Run(() =>
        {
            var found = FirewallHelper.StateOf(FirewallRules.ForProgram(executable), executable);
            state.Post(() => Settle(state, executable, found));
        });
    }

    /// <summary>What the rule list came back with, on the render thread.</summary>
    private static void Settle(AppState state, string executable, FirewallState found)
    {
        _offer = FirewallHelper.Decide(FirewallHelper.IsSupported, FirewallHelper.ScriptPresent, found,
            state.Settings.Firewall.Marker, executable);
        _executable = executable;

        if (_offer != FirewallOffer.None)
        {
            _open = true;
            return;
        }

        // Nothing to ask. If a rule is in place — ours, or one the user made through Windows' own
        // Allow-access prompt — remember that it is this executable that has it, so the start after
        // the next update can tell that the rule went missing rather than guessing.
        if (found == FirewallState.Allowed) state.Settings.RecordFirewall(granted: true, executable);
    }

    public static void Draw(AppState state)
    {
        if (_open && !ImGui.IsPopupOpen(Title)) ImGui.OpenPopup(Title);
        if (!_open) return;

        var centre = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(540, 0), ImGuiCond.Appearing);

        bool open = true;
        if (!ImGui.BeginPopupModal(Title, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) Dismiss(state, granted: false);
            return;
        }

        if (_offer == FirewallOffer.Again)
        {
            // The case an update leaves behind: it worked, and now it does not.
            ImGui.TextWrapped(
                "You allowed RaCMAN through the firewall before, but no rule covers the copy that is "
                + "running now. A firewall rule names one executable, and this one has moved or had its "
                + "rule removed since — an update replaces the application folder, and a second copy of "
                + "RaCMAN adding its own rule takes this one's away.");
            ImGui.Spacing();
            if (state.Settings.Firewall.Executable is { Length: > 0 } previous
                && !FirewallHelper.SamePath(previous, _executable))
            {
                Ui.DebugHint($"Allowed before for {previous}");
            }
        }
        else
        {
            ImGui.TextWrapped(
                "The PS3 sends its live state (readouts, toggle state, the pad for combos) to this PC over UDP. "
                + "Windows Firewall blocks that for a freshly unzipped app and often doesn't prompt, so RaCMAN "
                + "would silently fall back to slower TCP polling.");
        }

        ImGui.Spacing();
        ImGui.TextWrapped(
            "Add a firewall rule now? This asks for administrator approval and only adds an inbound rule for "
            + "RaCMAN. You can do it later from the Connection panel, or with \"Allow through Firewall.cmd\".");
        ImGui.Spacing();
        Ui.DebugHint($"Rule would name {_executable}");
        ImGui.Separator();

        if (ImGui.Button("Add firewall rule", new Vector2(160, 0)))
        {
            var (ok, message) = FirewallHelper.RequestRule(_executable);
            state.AddToast(message, ok ? ToastKind.Info : ToastKind.Error);

            // "Granted" is the user's intent, not the outcome: the UAC prompt owns that, and the
            // next start reads the rules and corrects this either way.
            Dismiss(state, granted: ok);
        }

        ImGui.SameLine();
        if (ImGui.Button("Not now", new Vector2(120, 0)))
        {
            Dismiss(state, granted: false);
        }

        ImGui.EndPopup();
    }

    private static void Dismiss(AppState state, bool granted)
    {
        _open = false;
        state.Settings.RecordFirewall(granted, _executable);
        ImGui.CloseCurrentPopup();
    }
}
