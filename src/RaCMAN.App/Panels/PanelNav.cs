namespace RaCMAN.App.Panels;

/// <summary>
/// The side nav's own rules: the panels it lists, the order it lists them in, where the thin
/// separators between the groups go, which panels can hold sub-pages, and which panels the
/// connected console cannot drive. They live here rather than in the window so they can be read,
/// and tested, without a window on screen. The order is also what <c>--panel N</c> counts, so the
/// help text is built from this list too.
/// </summary>
public static class PanelNav
{
    /// <summary>Where the nav sends someone whose panel has just become unusable.</summary>
    public const int Connection = 0;

    /// <summary>The Game page, whose sub-pages are drawn indented under it.</summary>
    public const int Game = 1;

    /// <summary>
    /// The Unlocks panel's index, hidden for a game with no unlock table. It holds sub-pages of its
    /// own, so a layout can hang a section (Collectables, in the shipped file) under it.
    /// </summary>
    public const int Unlocks = 2;

    public const int Positions = 3;

    public const int Combos = 4;

    /// <summary>The Mods panel's index in the window's panel list.</summary>
    public const int Mods = 5;

    /// <summary>The Save files panel's index in the window's panel list.</summary>
    public const int SaveFiles = 6;

    public const int Autosplitter = 7;

    public const int InputDisplay = 8;

    /// <summary>The Level flags panel's index, hidden for a game whose flag layout is not known.</summary>
    public const int LevelFlags = 9;

    public const int Memory = 10;

    public const int Settings = 11;

    /// <summary>
    /// The nav in order, which is the order <c>--panel N</c> counts in. The groups below decide
    /// where a separator is drawn between them.
    /// </summary>
    public static readonly string[] Names =
    {
        "Connection",
        "Game",
        "Unlocks",
        "Positions",
        "Combos",
        "Mods",
        "Save files",
        "Autosplitter",
        "Input display",
        "Level flags",
        "Memory",
        "Settings",
    };

    /// <summary>How many panels there are, so a caller does not index past the list.</summary>
    public static int Count => Names.Length;

    /// <summary>
    /// The panel each group of the nav starts at: the connection on its own, then the game and its
    /// unlocks, then the two the run itself fills in as it goes, then the two libraries kept per
    /// title, then the two that talk to something else on this PC, then the two that read raw game
    /// state, and the settings on their own at the end.
    /// </summary>
    private static readonly int[] GroupStarts =
        { Connection, Game, Positions, Mods, Autosplitter, LevelFlags, Settings };

    /// <summary>
    /// True for a panel that opens a group, which is where the nav draws a thin separator. The
    /// first group opens the list, so nothing is drawn above it.
    /// </summary>
    public static bool StartsGroup(int panel) => panel != GroupStarts[0] && Array.IndexOf(GroupStarts, panel) >= 0;

    /// <summary>
    /// The name a layout file hangs sub-pages under for this panel, or null for a panel that has
    /// none. The two are spelled exactly as the nav lists them, which is what the file's
    /// <c>subPages</c> keys are; see <see cref="GameLayout.Hosts"/>.
    /// </summary>
    public static string? SubPageHost(int panel) => panel switch
    {
        Game => GameLayout.GameHost,
        Unlocks => GameLayout.UnlocksHost,
        _ => null,
    };

    /// <summary>The panel a layout host names, or <see cref="Game"/> for anything else.</summary>
    public static int PanelForHost(string host) =>
        string.Equals(host, GameLayout.UnlocksHost, StringComparison.Ordinal) ? Unlocks : Game;

    /// <summary>
    /// The <c>--panel</c> help's index list, wrapped so a line of it fits the rest of the help. Built
    /// from <see cref="Names"/>, so the numbers in the help are the numbers the nav uses.
    /// </summary>
    public static IReadOnlyList<string> HelpLines(int perLine = 4)
    {
        var lines = new List<string>();
        for (int start = 0; start < Names.Length; start += perLine)
        {
            var part = new List<string>();
            for (int i = start; i < Math.Min(start + perLine, Names.Length); i++)
            {
                part.Add($"{i} {Names[i].ToLowerInvariant()}");
            }

            bool last = start + perLine >= Names.Length;
            lines.Add(string.Join(", ", part) + (last ? string.Empty : ","));
        }

        return lines;
    }

    /// <summary>
    /// Why the nav greys a panel out, or null when the panel is usable. Every mod is patch words
    /// or code caves, and the savefile helper the Save files panel works through is a code cave
    /// and a branch into it, so a console that refuses code patches (RPCS3) leaves both with
    /// nothing they can do. They stay in the list, greyed out with this as their tooltip, because
    /// a panel that vanished would read as a client that had lost a feature.
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
