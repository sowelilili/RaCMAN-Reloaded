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
    /// Everything, drafts and per-title selections included. Called on a game or connection change.
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
        AutosplitterPanel.Reset();
    }
}
