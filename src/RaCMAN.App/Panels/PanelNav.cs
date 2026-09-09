namespace RaCMAN.App.Panels;

/// <summary>
/// The side nav's rules about panels the connected console cannot drive. They live here rather
/// than in the window so they can be read, and tested, without a window on screen.
/// </summary>
public static class PanelNav
{
    /// <summary>The Mods panel's index in the window's panel list.</summary>
    public const int Mods = 6;

    /// <summary>The Save files panel's index in the window's panel list.</summary>
    public const int SaveFiles = 7;

    /// <summary>Where the nav sends someone whose panel has just become unusable.</summary>
    public const int Connection = 0;

    /// <summary>
    /// Why the nav greys a panel out, or null when the panel is usable. Every mod is patch words
    /// or code caves, and the Save files panel moves files through the savefile helper, which is
    /// itself a mod, so a console that refuses code patches (RPCS3) leaves both with nothing they
    /// can do. They stay in the list, greyed out with this as their tooltip, because a panel that
    /// vanished would read as a client that had lost a feature.
    /// </summary>
    public static string? DisabledReason(int panel, bool codePatchesUnsupported)
    {
        if (!codePatchesUnsupported) return null;

        return panel switch
        {
            Mods => Ui.ModsAreCodePatches,
            SaveFiles => Ui.NoCodePatches,
            _ => null,
        };
    }
}
