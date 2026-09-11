namespace RaCMAN.App.Panels;

/// <summary>
/// Which sub-page the Game page is showing, and the section a headless run asked for by name. The
/// request lives here rather than on either panel because two of them can take it up: the Game page
/// opens it as a sub-page of its own, and the Unlocks panel selects it as one of its tabs.
/// </summary>
public static class SubPageNav
{
    /// <summary>The section the Game page is showing, or null while its own page is the one drawn.</summary>
    public static string? Open { get; set; }

    /// <summary>
    /// A section to show once the game's descriptors arrive (the <c>--game-section</c> flag),
    /// wherever the layout draws it. Cleared by whichever panel takes it up.
    /// </summary>
    public static string? Requested { get; set; }
}
