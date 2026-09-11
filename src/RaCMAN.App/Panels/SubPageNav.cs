namespace RaCMAN.App.Panels;

/// <summary>
/// Which sub-page each hosting panel is showing, and the one a headless run asked for by name. Two
/// panels draw sub-pages now (Game, and Unlocks for whatever the layout hangs under it), so the
/// selection lives here rather than on either of them: the side nav draws both the same way, and a
/// section that moves from one host to the other moves with the same two calls.
/// </summary>
public static class SubPageNav
{
    /// <summary>Host panel -> the section it is showing. A host with no entry is on its own page.</summary>
    private static readonly Dictionary<string, string> Open = new(StringComparer.Ordinal);

    /// <summary>
    /// A sub-page to open once the game's descriptors arrive (the <c>--game-section</c> flag),
    /// under whichever panel hosts it. Cleared by the panel that takes it up.
    /// </summary>
    public static string? Requested { get; set; }

    /// <summary>The section a host panel is showing, or null for the panel's own page.</summary>
    public static string? For(string host) => Open.GetValueOrDefault(host);

    /// <summary>
    /// Shows a section under a host panel, or its own page again when null. Each panel clears its
    /// own on a reset, which is how a game change or a re-describe puts both back to their pages.
    /// </summary>
    public static void Set(string host, string? section)
    {
        if (section is null) Open.Remove(host);
        else Open[host] = section;
    }
}
