using System.Numerics;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The copy-or-defaults question per-user settings ask before a console user's profile is set
/// up (revision 1.24). The question itself, and when it is asked, is <see cref="AppState"/>'s
/// (<see cref="AppState.ProfileQuestion"/>); this only draws it and hands the answer back.
/// It comes up only when the Settings panel's Per-user settings box is ticked on.
/// </summary>
public static class ProfileSetupModal
{
    public const string Title = "Per-user settings";

    public const string CopyButton = "Copy current settings";

    public const string FreshButton = "Start from defaults";

    private static bool _open;

    /// <summary>What the question says.</summary>
    public static string Body(ProfileQuestion question) =>
        $"Turning on per-user settings. {question.Who} has no settings of their own yet. Copy the "
        + "current settings for them, or start from the defaults?";

    /// <summary>The way out without an answer: the switch stays off.</summary>
    public const string CancelButton = "Cancel";

    public static void Draw(AppState state)
    {
        var question = state.ProfileQuestion;
        if (question is null)
        {
            // The question went away under the popup (the link dropped): close it from inside,
            // the only place ImGui closes one.
            if (_open && ImGui.BeginPopupModal(Title))
            {
                ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
            }

            _open = false;
            return;
        }

        // Opened again whenever it is not, as LiveSplitModal does: the question stays until it is answered.
        _open = true;
        if (!ImGui.IsPopupOpen(Title)) ImGui.OpenPopup(Title);

        var centre = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        bool open = true;
        if (!ImGui.BeginPopupModal(Title, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) Answer(state, null);
            return;
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 420f);
        ImGui.TextUnformatted(Body(question));
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.Button(CopyButton))
        {
            Answer(state, ProfileSetup.Copy);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button(FreshButton))
        {
            Answer(state, ProfileSetup.Fresh);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button(CancelButton))
        {
            Answer(state, null);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();

        if (!open) Answer(state, null);
    }

    private static void Answer(AppState state, ProfileSetup? setup)
    {
        _open = false;
        if (state.ProfileQuestion is not null) state.AnswerProfileQuestion(setup);
    }
}
