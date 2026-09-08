using RaCMAN.App;
using RaCMAN.Protocol;
using Xunit;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The client-owned Game panel layout. These load the shipped data/gamelayout.json from the source
/// tree and check the regroup it encodes: the setup/manip controls to a Manips sub-page, collectable
/// unlocks to Collectables, QE and debug controls to Debug, skins/armour to Cosmetics, and everything
/// else left where qwark's DESCRIBE group put it. The file is keyed by game, not by title id, because
/// BCES01503 hosts RaC1, RaC2 and RaC3 under one title id.
/// <para>
/// Three names are reserved for the panels that place them: "Values" and "Options" at the top of the
/// Game page, "Unlocks" on the Unlocks panel. None of them may reach a side sub-page or the tab order.
/// </para>
/// </summary>
public class GameLayoutTests
{
    /// <summary>The one disc that carries three games; no entry is keyed by it, so the game key must do the work.</summary>
    private const string DiscTitle = "BCES01503";

    private static string ShippedLayoutPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "RaCMAN.App", "data", "gamelayout.json");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "data", "gamelayout.json");
    }

    private static Feature Make(byte id, FeatureKind kind, byte group, string label) =>
        new(id, kind, group, 0, FeatureFlags.None, 0xFF, 0, 0, label);

    private static DescribeResult Describe(GameId game, string[] groups, params Feature[] features) =>
        new(game, groups, Array.Empty<string>(), features);

    private static readonly string[] Rac1Groups = { "Cheats", "Player", "Progress", "Savefile", "Jankpot", "Debug" };
    private static readonly string[] Rac2Groups = { "Cheats", "Player", "Progress", "Collectables", "Savefile", "Cosmetics" };
    private static readonly string[] Rac3Groups = { "Cheats", "Player", "Progress", "Savefile", "Cosmetics" };
    private static readonly string[] Rac4Groups = { "Cheats", "Player", "Progress", "Savefile" };

    public GameLayoutTests()
    {
        GameLayout.LoadFrom(ShippedLayoutPath());
    }

    [Fact]
    public void ShippedLayoutLoadsWithoutProblems()
    {
        Assert.True(File.Exists(ShippedLayoutPath()), "data/gamelayout.json should ship with the app");
        Assert.Empty(GameLayout.Problems);
    }

    [Fact]
    public void Rac1RegroupsByGameKeyWhenTheTitleIdHasNoEntry()
    {
        // BCES01503 is RaC1, RaC2 and RaC3 on one disc, so the layout has to come from the game.
        var goldBolts = Make(20, FeatureKind.Action, 2, "Unlock all gold bolts");
        var resetGold = Make(21, FeatureKind.Action, 2, "Reset all gold bolts");
        var skill = Make(22, FeatureKind.Action, 2, "Unlock all skill points");
        var resetSkill = Make(23, FeatureKind.Action, 2, "Reset skill points");
        var jankBolts = Make(30, FeatureKind.Value, 4, "Jankpot bolts");
        var jankGo = Make(31, FeatureKind.Action, 4, "Activate jankpot");
        var drek = Make(40, FeatureKind.Action, 2, "Drek skip");
        var d = Describe(GameId.Rac1, Rac1Groups, goldBolts, resetGold, skill, resetSkill, jankBolts, jankGo, drek);

        Assert.Equal("Collectables", GameLayout.SectionFor(DiscTitle, GameId.Rac1, goldBolts, d));
        Assert.Equal("Collectables", GameLayout.SectionFor(DiscTitle, GameId.Rac1, resetGold, d));
        Assert.Equal("Collectables", GameLayout.SectionFor(DiscTitle, GameId.Rac1, skill, d));
        Assert.Equal("Collectables", GameLayout.SectionFor(DiscTitle, GameId.Rac1, resetSkill, d));
        Assert.Equal("Debug", GameLayout.SectionFor(DiscTitle, GameId.Rac1, jankBolts, d));
        Assert.Equal("Debug", GameLayout.SectionFor(DiscTitle, GameId.Rac1, jankGo, d));
        Assert.Equal("Manips", GameLayout.SectionFor(DiscTitle, GameId.Rac1, drek, d));
    }

    [Fact]
    public void Rac1ShootingSkillPointsAreAManipAndGoodiesIsAnOption()
    {
        // The shooting pair sets up (or clears) an in-run strategy, so it sits with the manips; the
        // plain unlock/reset pair stays with the other collectables.
        var setup = Make(24, FeatureKind.Action, 2, "Setup shooting skill points");
        var reset = Make(25, FeatureKind.Action, 2, "Reset shooting skill points");
        var skill = Make(22, FeatureKind.Action, 2, "Unlock all skill points");
        var goldBolts = Make(20, FeatureKind.Action, 2, "Unlock all gold bolts");
        var goodies = Make(5, FeatureKind.Toggle, 0, "Goodies menu");
        var d = Describe(GameId.Rac1, Rac1Groups, setup, reset, skill, goldBolts, goodies);

        Assert.Equal("Manips", GameLayout.SectionFor(DiscTitle, GameId.Rac1, setup, d));
        Assert.Equal("Manips", GameLayout.SectionFor(DiscTitle, GameId.Rac1, reset, d));
        Assert.Equal("Collectables", GameLayout.SectionFor(DiscTitle, GameId.Rac1, skill, d));
        Assert.Equal("Collectables", GameLayout.SectionFor(DiscTitle, GameId.Rac1, goldBolts, d));
        Assert.Equal(GameLayout.OptionsSection, GameLayout.SectionFor(DiscTitle, GameId.Rac1, goodies, d));
    }

    [Fact]
    public void Rac2ManipsAndDebugRegroup()
    {
        var debugMode = Make(5, FeatureKind.Toggle, 0, "Enable debug mode");
        var qe = Make(23, FeatureKind.Value, 2, "QE save write-offset");
        var ngPlus = Make(24, FeatureKind.Action, 2, "NG+ setup");
        var autoReset = Make(25, FeatureKind.Toggle, 2, "Auto-reset (any%)");
        var d = Describe(GameId.Rac2, Rac2Groups, debugMode, qe, ngPlus, autoReset);

        Assert.Equal("Debug", GameLayout.SectionFor("NPEA00386", GameId.Rac2, debugMode, d));
        Assert.Equal("Debug", GameLayout.SectionFor("NPEA00386", GameId.Rac2, qe, d));
        Assert.Equal("Manips", GameLayout.SectionFor("NPEA00386", GameId.Rac2, ngPlus, d));
        Assert.Equal("Manips", GameLayout.SectionFor("NPEA00386", GameId.Rac2, autoReset, d));
    }

    [Fact]
    public void UnmovedFeaturesKeepTheirDefaults()
    {
        var fastLoads = Make(0, FeatureKind.Toggle, 0, "Fast loads");
        var bolts = Make(7, FeatureKind.Value, 1, "Bolts");
        var d = Describe(GameId.Rac2, Rac2Groups, fastLoads, bolts);

        // A toggle stays in its qwark group; a VALUE defaults to the top value table.
        Assert.Equal("Cheats", GameLayout.SectionFor("NPEA00386", GameId.Rac2, fastLoads, d));
        Assert.Equal(GameLayout.ValuesSection, GameLayout.SectionFor("NPEA00386", GameId.Rac2, bolts, d));
    }

    [Fact]
    public void Rac3ProgressSplitsAcrossTheOtherSections()
    {
        var trophy = Make(20, FeatureKind.Action, 2, "Refresh trophy state");
        var titanium = Make(21, FeatureKind.Action, 2, "Unlock all titanium bolts");
        var resetTitanium = Make(22, FeatureKind.Action, 2, "Reset all titanium bolts");
        var upgrade = Make(23, FeatureKind.Action, 2, "Max all weapon levels");
        var downgrade = Make(24, FeatureKind.Action, 2, "Reset all weapon levels");
        var ngPlus = Make(25, FeatureKind.Action, 2, "Setup NG+ manips");
        var d = Describe(GameId.Rac3, Rac3Groups, trophy, titanium, resetTitanium, upgrade, downgrade, ngPlus);

        Assert.Equal("Savefile", GameLayout.SectionFor("NPEA00387", GameId.Rac3, trophy, d));
        Assert.Equal("Collectables", GameLayout.SectionFor("NPEA00387", GameId.Rac3, titanium, d));
        Assert.Equal("Collectables", GameLayout.SectionFor("NPEA00387", GameId.Rac3, resetTitanium, d));

        // The weapon-level pair rewrites the unlock table, so the Unlocks panel draws it, not the Game page.
        Assert.Equal(GameLayout.UnlocksSection, GameLayout.SectionFor("NPEA00387", GameId.Rac3, upgrade, d));
        Assert.Equal(GameLayout.UnlocksSection, GameLayout.SectionFor("NPEA00387", GameId.Rac3, downgrade, d));
        Assert.Equal("Manips", GameLayout.SectionFor("NPEA00387", GameId.Rac3, ngPlus, d));
    }

    [Fact]
    public void Rac3QuickSelectPauseIsAnOption()
    {
        // A save-file switch rather than a run tool: it belongs beside the value table, not in Cheats.
        var quickSelect = Make(6, FeatureKind.Toggle, 0, "Quick-select pause");
        var fastLoads = Make(0, FeatureKind.Toggle, 0, "Fast loads");
        var d = Describe(GameId.Rac3, Rac3Groups, quickSelect, fastLoads);

        Assert.Equal(GameLayout.OptionsSection, GameLayout.SectionFor("NPEA00387", GameId.Rac3, quickSelect, d));
        Assert.Equal("Cheats", GameLayout.SectionFor("NPEA00387", GameId.Rac3, fastLoads, d));
    }

    [Fact]
    public void Rac3CosmeticsAndQeRegroup()
    {
        var armour = Make(11, FeatureKind.Enum, 1, "Armour");
        var ship = Make(12, FeatureKind.Enum, 1, "Ship colour");
        var qe = Make(14, FeatureKind.Value, 1, "QE offset");
        var vendorQe = Make(15, FeatureKind.Action, 1, "Enable vendor QE");
        var d = Describe(GameId.Rac3, Rac3Groups, armour, ship, qe, vendorQe);

        Assert.Equal("Cosmetics", GameLayout.SectionFor("NPEA00387", GameId.Rac3, armour, d));
        Assert.Equal("Cosmetics", GameLayout.SectionFor("NPEA00387", GameId.Rac3, ship, d));
        Assert.Equal("Debug", GameLayout.SectionFor("NPEA00387", GameId.Rac3, qe, d));
        Assert.Equal("Debug", GameLayout.SectionFor("NPEA00387", GameId.Rac3, vendorQe, d));
    }

    [Fact]
    public void DeadlockedSkinMovesToCosmeticsAndPlanetsToPlayer()
    {
        var skin = Make(10, FeatureKind.Enum, 1, "Skin");
        var planets = Make(11, FeatureKind.Action, 2, "Unlock all planets");
        var actTune = Make(12, FeatureKind.Action, 2, "Act tune bosses");
        var d = Describe(GameId.Rac4, Rac4Groups, skin, planets, actTune);

        Assert.Equal("Cosmetics", GameLayout.SectionFor("NPEA00423", GameId.Rac4, skin, d));

        // Unlocking the planets is everyday progress, not a manip; only the act tune stays there.
        Assert.Equal(GameLayout.PlayerSection, GameLayout.SectionFor("NPEA00423", GameId.Rac4, planets, d));
        Assert.Equal("Manips", GameLayout.SectionFor("NPEA00423", GameId.Rac4, actTune, d));

        // Deadlocked has no Cosmetics group from qwark; the move creates the section, in configured order.
        var order = GameLayout.TabOrder("NPEA00423", GameId.Rac4, new[] { "Cheats", "Player", "Cosmetics" });
        Assert.Equal(new[] { "Cheats", "Player", "Savefile", "Manips", "Collectables", "Cosmetics", "Debug" }, order);
    }

    [Fact]
    public void EverydaySectionsStayOnTheGamePageAndTheRestGoToTheSide()
    {
        // The Game page keeps the common controls; the rest are sub-pages under Game in the nav.
        var side = GameLayout.SideSections;
        Assert.Equal(new[] { "Manips", "Collectables", "Cosmetics", "Debug" }, side);
        Assert.DoesNotContain("Cheats", side);
        Assert.DoesNotContain("Player", side);
        Assert.DoesNotContain("Savefile", side);
    }

    [Fact]
    public void TheReservedSectionsAreNeverSidePagesAndNeverInTheTabOrder()
    {
        // A panel places each of these itself: Values and Options at the top of the Game page,
        // Unlocks on the Unlocks panel. None may become a sub-page or a stacked header.
        Assert.True(GameLayout.IsReserved(GameLayout.ValuesSection));
        Assert.True(GameLayout.IsReserved(GameLayout.OptionsSection));
        Assert.True(GameLayout.IsReserved(GameLayout.UnlocksSection));
        Assert.False(GameLayout.IsReserved(GameLayout.PlayerSection));

        foreach (var reserved in new[] { GameLayout.ValuesSection, GameLayout.OptionsSection, GameLayout.UnlocksSection })
        {
            Assert.DoesNotContain(reserved, GameLayout.SideSections);
        }

        foreach (var game in new[] { GameId.Rac1, GameId.Rac2, GameId.Rac3, GameId.Rac4 })
        {
            var order = GameLayout.TabOrder(string.Empty, game,
                new[] { GameLayout.ValuesSection, GameLayout.OptionsSection, GameLayout.UnlocksSection, "Cheats" });

            Assert.DoesNotContain(GameLayout.ValuesSection, order);
            Assert.DoesNotContain(GameLayout.OptionsSection, order);
            Assert.DoesNotContain(GameLayout.UnlocksSection, order);
            Assert.Contains(GameLayout.PlayerSection, order);
        }
    }

    [Fact]
    public void AFileThatListsAReservedSectionAsASidePageIsIgnored()
    {
        string path = Path.Combine(Path.GetTempPath(), "racman-layout-reserved-side.json");
        File.WriteAllText(path, """
        {
          "sideSections": ["Options", "Unlocks", "Values", "Debug"],
          "games": {
            "rac2": { "tabOrder": ["Options", "Cheats", "Unlocks"], "moves": {} }
          }
        }
        """);

        try
        {
            GameLayout.LoadFrom(path);
            Assert.Equal(new[] { "Debug" }, GameLayout.SideSections);
            Assert.Equal(new[] { "Cheats" }, GameLayout.TabOrder("NPEA00386", GameId.Rac2, Array.Empty<string>()));
        }
        finally
        {
            File.Delete(path);
            GameLayout.LoadFrom(ShippedLayoutPath());
        }
    }

    [Fact]
    public void MissingFileStillHasSideSectionDefaults()
    {
        try
        {
            GameLayout.LoadFrom(Path.Combine(Path.GetTempPath(), "racman-no-such-layout.json"));
            Assert.Empty(GameLayout.Problems);
            Assert.Contains("Manips", GameLayout.SideSections);
            Assert.Contains("Debug", GameLayout.SideSections);
        }
        finally
        {
            GameLayout.LoadFrom(ShippedLayoutPath());
        }
    }

    [Fact]
    public void UnknownTitleAndUnknownGameFallBackToQwarkGroups()
    {
        var toggle = Make(1, FeatureKind.Toggle, 0, "Anything");
        var value = Make(2, FeatureKind.Value, 1, "A number");
        var d = Describe(GameId.None, new[] { "Cheats", "Player" }, toggle, value);

        Assert.Equal("Cheats", GameLayout.SectionFor("ZZZZ99999", GameId.None, toggle, d));
        Assert.Equal(GameLayout.ValuesSection, GameLayout.SectionFor("ZZZZ99999", GameId.None, value, d));
    }

    [Fact]
    public void ATitleIdEntryWinsOverTheGameKey()
    {
        string path = Path.Combine(Path.GetTempPath(), "racman-layout-title-override.json");
        File.WriteAllText(path, """
        {
          "sideSections": ["Debug"],
          "games": {
            "rac2": { "moves": { "Fast loads": "FromGame" } },
            "NPEA00386": { "moves": { "Fast loads": "FromTitle" } }
          }
        }
        """);

        try
        {
            GameLayout.LoadFrom(path);
            var fastLoads = Make(0, FeatureKind.Toggle, 0, "Fast loads");
            var d = Describe(GameId.Rac2, Rac2Groups, fastLoads);

            Assert.Equal("FromTitle", GameLayout.SectionFor("NPEA00386", GameId.Rac2, fastLoads, d));
            Assert.Equal("FromGame", GameLayout.SectionFor("NPEA00999", GameId.Rac2, fastLoads, d));
        }
        finally
        {
            File.Delete(path);
            GameLayout.LoadFrom(ShippedLayoutPath());
        }
    }

    [Fact]
    public void TabOrderStartsWithTheEverydaySectionsAndNeverIncludesTheReservedOnes()
    {
        var order = GameLayout.TabOrder("NPEA00386", GameId.Rac2,
            new[] { GameLayout.ValuesSection, GameLayout.OptionsSection, GameLayout.UnlocksSection, "Extra", "Cheats" });

        Assert.DoesNotContain(GameLayout.ValuesSection, order);
        Assert.DoesNotContain(GameLayout.OptionsSection, order);
        Assert.DoesNotContain(GameLayout.UnlocksSection, order);
        Assert.Equal(new[] { "Cheats", "Player", "Savefile" }, order.Take(3));
        Assert.Equal("Extra", order[^1]);            // an unlisted tab is appended, not dropped
    }
}
