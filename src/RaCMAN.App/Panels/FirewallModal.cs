using System.Numerics;
using ImGuiNET;

namespace RaCMAN.App.Panels;

/// <summary>
/// Shown once, on the first run of a published Windows build: the console streams its live state
/// over UDP, and Windows Firewall blocks unsolicited inbound UDP for a fresh unsigned app, usually
/// without a prompt. First boot is when the user expects to grant access, so we ask here rather
/// than waiting for the symptom. Declining is fine: the client falls back to TCP and the Connection
/// panel keeps the one-click offer.
/// </summary>
public static class FirewallModal
{
    private const string Title = "Allow RaCMAN through the firewall";
    private static bool _open;

    /// <summary>Call once at startup. Opens the modal only on a Windows published build that hasn't asked yet.</summary>
    public static void MaybeOffer(AppState state)
    {
        if (state.Settings.FirewallOffered) return;
        if (!FirewallHelper.IsSupported || !FirewallHelper.ScriptPresent) return;

        _open = true;
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
            if (!open) Dismiss(state);
            return;
        }

        ImGui.TextWrapped(
            "The PS3 sends its live state (readouts, toggle state, the pad for combos) to this PC over UDP. "
            + "Windows Firewall blocks that for a freshly unzipped app and often doesn't prompt, so RaCMAN "
            + "would silently fall back to slower TCP polling.");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Add a firewall rule now? This asks for administrator approval and only adds an inbound rule for "
            + "RaCMAN. You can do it later from the Connection panel, or with \"Allow through Firewall.cmd\".");
        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.Button("Add firewall rule", new Vector2(160, 0)))
        {
            var (ok, message) = FirewallHelper.RequestRule();
            state.AddToast(message, ok ? ToastKind.Info : ToastKind.Error);
            Dismiss(state);
        }

        ImGui.SameLine();
        if (ImGui.Button("Not now", new Vector2(120, 0)))
        {
            Dismiss(state);
        }

        ImGui.EndPopup();
    }

    private static void Dismiss(AppState state)
    {
        _open = false;
        state.Settings.FirewallOffered = true;
        state.Settings.Save();
        ImGui.CloseCurrentPopup();
    }
}
