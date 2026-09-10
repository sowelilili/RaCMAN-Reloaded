namespace RaCMAN.App.Panels;

/// <summary>
/// Panels keep their own drafts and per-game caches in statics. A session change has to drop
/// them, so <see cref="AppState.ResetPanels"/> and <see cref="AppState.ClearGameViews"/> call
/// these rather than leaving a previous game's values on screen.
/// </summary>
public static class PanelState
{
    /// <summary>
    /// Everything read out of the running process: the level-flag bytes, the moby rows, the
    /// memory dump. Called whenever the session leaves INGAME or the game reboots.
    /// </summary>
    public static void ClearGameData()
    {
        UnlocksPanel.ClearData();
        LevelFlagsPanel.ClearData();
        MemoryPanel.ClearGameData();
        SaveFilesPanel.ClearTransfer();
    }

    /// <summary>
    /// A capture in flight is holding the console's own combos off, so a connection that goes away
    /// must drop it even though everything else on the panels is kept. The console expires the
    /// hold by itself, so there is nothing owed to a console that is no longer there.
    /// </summary>
    public static void DropCapture(AppState state) => CombosPanel.Reset(state);

    /// <summary>
    /// Everything, drafts and per-title selections included. Called when a different game turns up
    /// or the user asks for a re-read, and no longer on a quit, a reboot or a dropped connection.
    /// The state comes in because a dropped combo capture has a request to send to the console.
    /// </summary>
    public static void ResetAll(AppState state)
    {
        GamePanel.Reset();
        UnlocksPanel.Reset();
        LevelFlagsPanel.Reset();
        MemoryPanel.Reset();
        CombosPanel.Reset(state);
        SaveFilesPanel.Reset();
    }
}
