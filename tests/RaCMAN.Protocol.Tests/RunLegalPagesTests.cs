using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol;
using Xunit;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Qwark revision 1.34 (D10): which page a row is on is the module's to say, once: a row it flags
/// RUN_LEGAL is on Manips, a collectable action is in its Collectables group, and the shipped
/// gamelayout.json keeps only presentation. For each of the five games, the rows as a build 58
/// module describes them (no extension, run-legal rows in Cheats or a header's group, the collectable
/// actions in Progress, the old labels) and as a build 59 module does land on the same pages: what
/// the user sees does not move. The rows are the module's own, by its tables (src/games), with the
/// labels the D9 round changed.
/// </summary>
[Collection(GameLayoutCollection.Name)]
public class RunLegalPagesTests
{
    public RunLegalPagesTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "RaCMAN.App", "data", "gamelayout.json")))
            directory = directory.Parent;
        GameLayout.LoadFrom(Path.Combine(directory!.FullName, "src", "RaCMAN.App", "data", "gamelayout.json"));
    }

    /// <summary>One row: its id and kind, its group and label in builds 58 and 59, whether 59 flags it, the page it is on.</summary>
    private sealed record Row(byte Id, FeatureKind Kind, string Group58, string Group59, string Label58, string? Label59,
        bool Legal, string Page, FeatureFlags Flags = FeatureFlags.None);

    private const string Options = GameLayout.OptionsSection;
    private const string Unlocks = GameLayout.UnlocksSection;
    private const string Values = GameLayout.ValuesSection;

    private static readonly Row[] Rac1 =
    {
        new(0, FeatureKind.Toggle, "Cheats", "Cheats", "Fast loads", null, false, "Cheats", FeatureFlags.WritesCode),
        new(3, FeatureKind.Toggle, "Cheats", "Cheats", "Ghost Ratchet", null, false, "Cheats"),
        new(1, FeatureKind.Toggle, "Cheats", "Cheats", "Infinite ammo", null, false, "Cheats"),
        new(2, FeatureKind.Toggle, "Cheats", "Cheats", "Freeze health", null, false, "Cheats"),
        new(6, FeatureKind.Toggle, "Cheats", "Cheats", "Goodies menu", null, false, Options, FeatureFlags.Live),
        new(5, FeatureKind.Value, "Player", "Player", "Bolts", null, false, Values),
        new(12, FeatureKind.Action, "Progress", "Collectables", "Reset all gold bolts", null, false, "Collectables"),
        new(13, FeatureKind.Action, "Progress", "Collectables", "Unlock all gold bolts", null, false, "Collectables"),
        new(14, FeatureKind.Action, "Progress", "Collectables", "Reset skill points", "Reset all skill points", false, "Collectables"),
        new(15, FeatureKind.Action, "Progress", "Collectables", "Unlock all skill points", null, false, "Collectables"),
        // 20, Force autosave, is retired in build 59: no row.
        new(21, FeatureKind.Action, "Jankpot", "Jankpot", "Activate jankpot", null, false, "Debug"),
        new(24, FeatureKind.Toggle, "Debug", "Debug", "Update Ratchet", null, false, "Debug", FeatureFlags.Live),
        // Build 59's weapon bulk actions: Progress on the wire, the Unlocks panel's buttons by the layout.
        new(29, FeatureKind.Action, "Progress", "Progress", "Upgrade all weapons to gold", null, false, Unlocks),
        new(30, FeatureKind.Action, "Progress", "Progress", "Reset all gold weapons", null, false, Unlocks),
    };

    private static readonly Row[] Rac2 =
    {
        new(0, FeatureKind.Toggle, "Cheats", "Cheats", "Fast loads", null, false, "Cheats", FeatureFlags.WritesCode),
        new(5, FeatureKind.Toggle, "Cheats", "Cheats", "Enable debug mode", null, false, "Debug", FeatureFlags.Live),
        new(13, FeatureKind.Toggle, "Player", "Player", "Reset bosses on death", null, false, "Player"),
        new(14, FeatureKind.Toggle, "Player", "Player", "Reset platinum bolts on death", null, false, "Player"),
        new(32, FeatureKind.Action, "Progress", "Progress", "Max all weapon levels", null, false, Unlocks),
        new(25, FeatureKind.Action, "Collectables", "Collectables", "Unlock all platinum bolts", null, false, "Collectables"),
        new(24, FeatureKind.Action, "Collectables", "Collectables", "Reset platinum bolts", "Reset all platinum bolts", false, "Collectables"),
        new(26, FeatureKind.Action, "Collectables", "Collectables", "Reset nanotech boosts", "Reset all nanotech boosts", false, "Collectables"),
        new(28, FeatureKind.Action, "Collectables", "Collectables", "Reset skill points", "Reset all skill points", false, "Collectables"),
    };

    private static readonly Row[] Uya =
    {
        new(38, FeatureKind.Toggle, "Cheats", "Cheats", "Fast loads", null, false, "Cheats", FeatureFlags.WritesCode),
        new(5, FeatureKind.Toggle, "Cheats", "Cheats", "Quick-select pause", null, false, Options, FeatureFlags.Live),
        new(17, FeatureKind.Action, "Progress", "Collectables", "Unlock all skill points", null, false, "Collectables"),
        new(22, FeatureKind.Action, "Progress", "Collectables", "Reset all titanium bolts", null, false, "Collectables"),
        new(23, FeatureKind.Action, "Progress", "Progress", "Max all weapon levels", null, false, Unlocks),
        new(21, FeatureKind.Action, "Trophies", "Trophies", "Reset trophies", null, true, GameLayout.ManipsSection),
        new(39, FeatureKind.Toggle, "Trophies", "Trophies", "Reset trophies on Veldin load", null, true, GameLayout.ManipsSection),
    };

    private static readonly Row[] Deadlocked =
    {
        new(0, FeatureKind.Toggle, "Fixes", "Fixes", "Crash patches", null, true, GameLayout.ManipsSection, FeatureFlags.WritesCode),
        new(1, FeatureKind.Toggle, "Fixes", "Fixes", "Fix reset softlocks", null, true, GameLayout.ManipsSection),
        new(2, FeatureKind.Toggle, "Cheats", "Cheats", "Fast loads", null, false, "Cheats", FeatureFlags.WritesCode),
        new(3, FeatureKind.Toggle, "Cheats", "Cheats", "Refill ammo", null, false, "Cheats", FeatureFlags.WritesCode),
        new(15, FeatureKind.Action, "Progress", "Progress", "Max all weapon levels", null, false, Unlocks),
        new(25, FeatureKind.Toggle, "Cosmetics", "Cosmetics", "Correct HUD brightness", null, false, "Cosmetics", FeatureFlags.WritesCode),
    };

    private static readonly Row[] Tod =
    {
        new(17, FeatureKind.Toggle, "Cheats", "Cheats", "Fast loads", null, false, "Cheats", FeatureFlags.WritesCode),
        new(18, FeatureKind.Toggle, "Cheats", "Manips", "Remove cutscenes", null, true, GameLayout.ManipsSection),
        new(8, FeatureKind.Toggle, "Cheats", "Cheats", "Infinite ammo", null, false, "Cheats"),
        new(9, FeatureKind.Toggle, "Cheats", "Cheats", "Infinite health", null, false, "Cheats"),
        new(16, FeatureKind.Toggle, "Cheats", "Cheats", "God Ratchet", null, false, Options, FeatureFlags.Live),
        new(24, FeatureKind.Toggle, "Cheats", "Cheats", "Remove black screens", null, false, "Cheats"),
        new(19, FeatureKind.Action, "Cheats", "Cutscene cache", "Cache cutscenes", null, true, GameLayout.ManipsSection),
        new(20, FeatureKind.Toggle, "Cheats", "Cutscene cache", "Cache cutscenes on reset", null, true, GameLayout.ManipsSection),
        new(21, FeatureKind.Toggle, "Cheats", "Cutscene cache", "Disable cutscene caching", null, true, GameLayout.ManipsSection),
        new(10, FeatureKind.Action, "Progress", "Collectables", "Unlock all skill points", null, false, "Collectables"),
        new(11, FeatureKind.Action, "Progress", "Collectables", "Reset skill points", "Reset all skill points", false, "Collectables"),
        new(12, FeatureKind.Action, "Progress", "Collectables", "Unlock all gold bolts", null, false, "Collectables"),
        new(13, FeatureKind.Action, "Progress", "Collectables", "Reset gold bolts", "Reset all gold bolts", false, "Collectables"),
        // Build 59's pickers and weapon bulk actions.
        new(25, FeatureKind.Enum, "Player", "Player", "Armour", null, false, "Cosmetics"),
        new(26, FeatureKind.Enum, "Player", "Player", "Skin", null, false, "Cosmetics"),
        new(27, FeatureKind.Action, "Progress", "Progress", "Max all weapon levels", null, false, Unlocks),
        new(28, FeatureKind.Action, "Progress", "Progress", "Reset all weapon levels", null, false, Unlocks),
        new(29, FeatureKind.Action, "Progress", "Progress", "Max all weapon ammo", null, false, Unlocks),
    };

    public static TheoryData<GameId, string> Games => new()
    {
        { GameId.Rac1, "NPEA00385" },
        { GameId.Rac2, "NPEA00386" },
        { GameId.Rac3, "NPEA00387" },
        { GameId.Rac4, "NPEA00423" },
        { GameId.Tod, "NPEA00452" },
    };

    private static Row[] RowsOf(GameId game) => game switch
    {
        GameId.Rac1 => Rac1,
        GameId.Rac2 => Rac2,
        GameId.Rac3 => Uya,
        GameId.Rac4 => Deadlocked,
        _ => Tod,
    };

    /// <summary>MANIP_LIST's headers as the module sends them: UYA's Trophies, Deadlocked's Fixes; none for ToD (no categories).</summary>
    private static ManipList? ManipsOf(GameId game) => game switch
    {
        GameId.Tod => null,
        _ => new ManipList(0, true, false, 0, ManipEventKind.None, 0, Array.Empty<ManipGroup>(), game switch
        {
            GameId.Rac3 => new[] { new ManipHeader("Trophies", new ushort[] { 21, 39 }) },
            GameId.Rac4 => new[] { new ManipHeader("Fixes", new ushort[] { 0, 1 }) },
            _ => Array.Empty<ManipHeader>(),
        }),
    };

    private static DescribeResult Describe(GameId game, bool build59)
    {
        var rows = RowsOf(game);
        var groups = rows.Select(r => build59 ? r.Group59 : r.Group58).Distinct().ToArray();
        var features = rows.Select(r => new Feature(r.Id, r.Kind, (byte)Array.IndexOf(groups, build59 ? r.Group59 : r.Group58),
            0, r.Flags, 0xFF, 0, 0, build59 ? r.Label59 ?? r.Label58 : r.Label58, 0,
            build59 && r.Legal ? FeatureFlags2.RunLegal : FeatureFlags2.None)).ToArray();
        return new DescribeResult(game, groups, Array.Empty<string>(), features)
        {
            Extension = build59 ? new DescribeExtension(PlanetFlags.None, true, "Help") : null,
        };
    }

    private static Dictionary<byte, string> PageOf(string title, GameId game, DescribeResult d) =>
        GamePanel.SectionsFor(title, game, d, ManipsOf(game))
            .SelectMany(s => s.Value.Select(f => (f.Id, s.Key)))
            .ToDictionary(p => p.Id, p => p.Key);

    [Theory]
    [MemberData(nameof(Games))]
    public void EveryRowIsOnThePageItWasOnBeforeTheModuleSaidIt(GameId game, string title)
    {
        var before = PageOf(title, game, Describe(game, build59: false));
        var after = PageOf(title, game, Describe(game, build59: true));

        foreach (var row in RowsOf(game))
        {
            Assert.Equal((row.Label58, row.Page), (row.Label58, before[row.Id]));
            Assert.Equal((row.Label58, row.Page), (row.Label58, after[row.Id]));
        }
    }

    /// <summary>
    /// Build 59's pages come from the module alone: with a layout that says nothing of these games
    /// (no moves, no olderModules), every run-legal row is still on Manips and every collectable
    /// action on the Collectables tab, and no row the module does not flag is on Manips but a
    /// MANIP_LIST header's. A build 58 description under that layout loses them, which is the
    /// difference the declaration makes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Games))]
    public void TheModuleAloneDecidesManipsAndCollectables(GameId game, string title)
    {
        string path = Path.Combine(TestDataFolder.Root, "layout-tests", $"empty-{game}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"games\": {} }");
        try
        {
            GameLayout.LoadFrom(path);
            var after = PageOf(title, game, Describe(game, build59: true));
            var before = PageOf(title, game, Describe(game, build59: false));
            foreach (var row in RowsOf(game))
            {
                if (row.Page == GameLayout.ManipsSection) Assert.Equal(GameLayout.ManipsSection, after[row.Id]);
                if (row.Page == "Collectables") Assert.Equal("Collectables", after[row.Id]);
                if (after[row.Id] == GameLayout.ManipsSection) Assert.True(row.Legal, row.Label58);
            }

            if (game == GameId.Tod)
                Assert.Equal("Cheats", before[18]);
            if (game is GameId.Rac1 or GameId.Rac3 or GameId.Tod)
                Assert.NotEqual("Collectables", before[RowsOf(game).First(r => r.Page == "Collectables").Id]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
