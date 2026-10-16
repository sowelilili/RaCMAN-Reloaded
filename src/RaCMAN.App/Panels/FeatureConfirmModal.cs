using System.Numerics;
using ImGuiNET;

namespace RaCMAN.App.Panels;

/// <summary>
/// The question before a feature the console flags CONFIRM is used (revision 1.21), drawn the way
/// the Mods panel asks before an uninstall and the Save files panel before a delete: the console's
/// wording, then Continue and Cancel, with Cancel the answer Enter gives. It is drawn outside the
/// root window with the other modals, so it is the same question whichever panel the feature was
/// on, and <see cref="FeatureConfirmations"/> holds what it asks about.
/// </summary>
public static class FeatureConfirmModal
{
    private const string Id = "###feature-confirm";

    /// <summary>The question the popup was opened for, so a new one opens it again.</summary>
    private static PendingConfirm? _shown;

    public static void Draw(AppState state)
    {
        var confirms = state.Confirms;
        var question = confirms.Pending;
        if (question is null)
        {
            if (_shown is not null) CloseLeftOpen();
            return;
        }

        string label = $"{question.Feature.Label}?{Id}";
        if (!ReferenceEquals(question, _shown))
        {
            _shown = question;
            ImGui.OpenPopup(label);
        }
        else if (!ImGui.IsPopupOpen(label))
        {
            // Closed some other way than the two buttons: that is a no.
            confirms.Cancel(question);
            _shown = null;
            return;
        }

        var centre = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(centre, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(520, 0), ImGuiCond.Appearing);

        bool open = true;
        if (!ImGui.BeginPopupModal(label, ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!open) confirms.Cancel(question);
            return;
        }

        if (question.Loading)
        {
            Ui.Hint("Asking the console what this does...");
        }
        else
        {
            Ui.Paragraph(question.Body);

            // The status is shown rather than swallowed, under the generic question it led to.
            if (question.Problem is { } problem)
            {
                ImGui.Spacing();
                Ui.Hint($"The console's own wording could not be read ({problem}).");
            }
        }

        if (question.Request == FeatureRequest.SetAuto)
        {
            ImGui.Spacing();
            Ui.Hint("\"On boot\" turns it on by itself every time the game boots, with nobody asked.");
        }

        ImGui.Separator();

        // Continue waits for the console's answer, so what is agreed to is what it said.
        ImGui.BeginDisabled(question.Loading);
        bool confirm = ImGui.Button("Continue", new Vector2(140, 0));
        ImGui.EndDisabled();

        ImGui.SameLine();
        bool cancel = ImGui.Button("Cancel", new Vector2(120, 0));

        // Enter on a dialog that has just opened answers no.
        ImGui.SetItemDefaultFocus();

        if (confirm)
        {
            confirms.Continue(question);
            ImGui.CloseCurrentPopup();
        }
        else if (cancel || !open)
        {
            confirms.Cancel(question);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    /// <summary>
    /// The question went away without either button: the game changed under it and
    /// <see cref="FeatureConfirmations.Tick"/> cancelled it. The popup is still open as far as
    /// ImGui knows, and a modal nobody draws would go on holding the rest of the window, so it is
    /// closed here. After a button it is closed already, and this finds nothing to do.
    /// </summary>
    private static void CloseLeftOpen()
    {
        _shown = null;

        bool open = true;
        if (!ImGui.BeginPopupModal(Id, ref open, ImGuiWindowFlags.AlwaysAutoResize)) return;

        ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }
}
