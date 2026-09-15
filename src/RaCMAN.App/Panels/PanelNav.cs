namespace RaCMAN.App.Panels;

/// <summary>
/// The side nav's own rules: the panels it lists, the order it lists them in, where the thin
/// separators between the groups go, and which panels the connected console cannot drive. They live
/// here rather than in the window so they can be read, and tested, without a window on screen. The
/// order is also what <c>--panel N</c> counts, so the help text is built from this list too.
/// </summary>
public static class PanelNav
{
    /// <summary>Where the nav sends someone whose panel has just become unusable.</summary>
    public const int Connection = 0;

    /// <summary>The Game page, whose sub-pages are drawn indented under it.</summary>
    public const int Game = 1;

    /// <summary>
    /// The Unlocks panel's index, hidden for a game with no unlock table. A layout can give it tabs
    /// of its own beside the unlock categories (Collectables, in the shipped file).
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
    /// The panels that draw what qwark knows about the running game: its description and its own
    /// tables, the slots and flags read out of it, the libraries kept for it. A title the console
    /// has no game module for answers every one of those UNSUPPORTED, so none of them has anything
    /// to draw. What is left is the connection, the memory tools, the pad and the settings.
    /// </summary>
    private static readonly int[] GamePanels =
        { Game, Unlocks, Positions, Combos, Mods, SaveFiles, Autosplitter, LevelFlags };

    /// <summary>Whether a panel is one of those, so the rule below can be read in one line.</summary>
    public static bool IsGamePanel(int panel) => Array.IndexOf(GamePanels, panel) >= 0;

    /// <summary>
    /// Whether the nav lists a panel at all. A panel with nothing behind it is hidden rather than
    /// greyed out: greying says "this console cannot do that", and there is a tooltip to explain
    /// it, while these are panels the running game simply has no such thing for.
    /// </summary>
    public static bool Visible(int panel, bool unknownGame, bool unlocksUnsupported, bool levelFlagsUnsupported)
    {
        if (unknownGame) return !IsGamePanel(panel);

        return panel switch
        {
            Unlocks => !unlocksUnsupported,
            LevelFlags => !levelFlagsUnsupported,
            _ => true,
        };
    }

    /// <summary>
    /// Where the nav puts someone whose panel has just been hidden: the game, or the memory tools
    /// when there is no game to show.
    /// </summary>
    public static int Fallback(bool unknownGame) => unknownGame ? Memory : Game;

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
