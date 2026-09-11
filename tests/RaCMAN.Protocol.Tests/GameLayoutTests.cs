using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol;
using Xunit;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The client-owned feature layout. These load the shipped data/gamelayout.json from the source
/// tree and check the regroup it encodes: the setup/manip controls to a Manips sub-page, collectable
/// unlocks to Collectables, QE and debug controls to Debug, skins/armour to Cosmetics, and everything
/// else left where qwark's DESCRIBE group put it. The file is keyed by game, not by title id, because
/// BCES01503 hosts RaC1, RaC2 and RaC3 under one title id.
/// <para>
/// Four names are reserved for the panels that place them: "Quick", "Values" and "Options" on the
/// Game page itself, "Unlocks" on the Unlocks panel. None of them may reach a page of its own or the
/// tab order. Where the rest are drawn is the file's two lists: "subPages" for the sections indented
/// under Game in the side nav, "unlocksTabs" for the ones the Unlocks panel draws as tabs beside its
/// unlock categories.
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

    /// <summary>
    /// The shipped file gives UYA's Cosmetics page a "Chargeboots" header above the three colour
    /// rows, which is what the headings table is for: the armour and the ship colour keep the top
    /// of the page, and the colours are collected under the heading in their own order.
    /// </summary>
    [Fact]
    public void Rac3CosmeticsPutsTheChargebootColoursUnderAHeading()
    {
        var armour = Make(11, FeatureKind.Enum, 1, "Armour");
        var ship = Make(12, FeatureKind.Enum, 1, "Ship colour");
        var front = Make(30, FeatureKind.Color, 4, "Chargeboots primary front");
        var back = Make(31, FeatureKind.Color, 4, "Chargeboots primary back");
        var tint = Make(32, FeatureKind.Color, 4, "Chargeboots tint");

        var headings = GameLayout.HeadingsFor("NPEA00387", GameId.Rac3, "Cosmetics");
        Assert.Equal("Chargeboots", headings["Chargeboots primary front"]);
        Assert.Equal("Chargeboots", headings["Chargeboots primary back"]);
        Assert.Equal("Chargeboots", headings["Chargeboots tint"]);
        Assert.False(headings.ContainsKey("Armour"));

        var blocks = GameLayout.Blocks("NPEA00387", GameId.Rac3, "Cosmetics",
            new[] { armour, ship, front, back, tint });

        Assert.Equal(2, blocks.Count);
        Assert.Null(blocks[0].Heading);
        Assert.Equal(new[] { "Armour", "Ship colour" }, blocks[0].Features.Select(f => f.Label));
        Assert.Equal("Chargeboots", blocks[1].Heading);
        Assert.Equal(
            new[] { "Chargeboots primary front", "Chargeboots primary back", "Chargeboots tint" },
            blocks[1].Features.Select(f => f.Label));
    }

    [Fact]
    public void ASectionNoHeadingNamesIsStillOneBlock()
    {
        var fastLoads = Make(0, FeatureKind.Toggle, 0, "Fast loads");
        var trophy = Make(20, FeatureKind.Action, 2, "Refresh trophy state");

        Assert.Empty(GameLayout.HeadingsFor("NPEA00387", GameId.Rac3, "Savefile"));

        var blocks = GameLayout.Blocks("NPEA00387", GameId.Rac3, "Savefile", new[] { fastLoads, trophy });
        Assert.Single(blocks);
        Assert.Null(blocks[0].Heading);
        Assert.Equal(2, blocks[0].Features.Count);

        // And a game the file has no entry for at all asks for nothing.
        Assert.Empty(GameLayout.HeadingsFor("ZZZZ99999", GameId.None, "Cosmetics"));
        Assert.Empty(GameLayout.Blocks("ZZZZ99999", GameId.None, "Cosmetics", Array.Empty<Feature>()));
    }

    /// <summary>
    /// The general rule, on a file of its own: a heading collects the features it names wherever
    /// they are in the section, the unnamed ones stay together, and the blocks follow the order
    /// their first feature turns up in.
    /// </summary>
    [Fact]
    public void HeadingsCollectTheirFeaturesAndKeepFirstSeenOrder()
    {
        string path = Path.Combine(Path.GetTempPath(), "racman-layout-headings.json");
        File.WriteAllText(path, """
        {
          "sideSections": ["Debug"],
          "games": {
            "rac4": {
              "headings": {
                "Debug": {
                  "Second": ["Beta", "Delta"],
                  "First": ["Alpha"]
                }
              }
            }
          }
        }
        """);

        try
        {
            GameLayout.LoadFrom(path);

            var alpha = Make(1, FeatureKind.Action, 0, "Alpha");
            var beta = Make(2, FeatureKind.Action, 0, "Beta");
            var delta = Make(3, FeatureKind.Action, 0, "Delta");
            var loose = Make(4, FeatureKind.Action, 0, "Loose");

            // Beta first, then something with no heading, then Alpha and Delta: three blocks, in
            // the order each of them starts, and Delta joins the block Beta opened.
            var blocks = GameLayout.Blocks("NPEA00423", GameId.Rac4, "Debug",
                new[] { beta, loose, alpha, delta });

            Assert.Equal(3, blocks.Count);
            Assert.Equal("Second", blocks[0].Heading);
            Assert.Equal(new[] { "Beta", "Delta" }, blocks[0].Features.Select(f => f.Label));
            Assert.Null(blocks[1].Heading);
            Assert.Equal(new[] { "Loose" }, blocks[1].Features.Select(f => f.Label));
            Assert.Equal("First", blocks[2].Heading);
            Assert.Equal(new[] { "Alpha" }, blocks[2].Features.Select(f => f.Label));

            // Another section of the same game is untouched by that one's headings.
            Assert.Empty(GameLayout.HeadingsFor("NPEA00423", GameId.Rac4, "Cheats"));
        }
        finally
        {
            File.Delete(path);
            GameLayout.LoadFrom(ShippedLayoutPath());
        }
    }

    [Fact]
    public void EverydaySectionsStayOnTheGamePageAndTheRestGetAPageOrATab()
    {
        // The Game page keeps the common controls; three of the rest are sub-pages indented under
        // it, and the collectables are a tab on the Unlocks panel, beside the table they edit.
        Assert.Equal(new[] { "Manips", "Cosmetics", "Debug" }, GameLayout.SubPages);
        Assert.Equal(new[] { "Collectables" }, GameLayout.UnlocksTabs);

        // Either way, a section drawn on its own never stacks on the Game page as well.
        var elsewhere = GameLayout.SectionsDrawnElsewhere;
        Assert.Equal(new[] { "Manips", "Cosmetics", "Debug", "Collectables" }, elsewhere);
        Assert.DoesNotContain("Cheats", elsewhere);
        Assert.DoesNotContain("Player", elsewhere);
        Assert.DoesNotContain("Savefile", elsewhere);
    }

    /// <summary>
    /// The one rule that is not simply what the file says: a game with no unlock table has no
    /// Unlocks entry in the nav, so a tab on that panel would have nowhere to be reached from. Its
    /// sections are listed as sub-pages under Game for that game instead.
    /// </summary>
    [Fact]
    public void WithNoUnlockTableTheUnlocksTabsFallBackToGame()
    {
        Assert.Equal(new[] { "Manips", "Cosmetics", "Debug" }, GameLayout.SubPagesFor(unlocksHidden: false));
        Assert.Equal(
            new[] { "Manips", "Cosmetics", "Debug", "Collectables" },
            GameLayout.SubPagesFor(unlocksHidden: true));

        // Which is the same thing said the other way round: what the Unlocks panel draws as a tab.
        Assert.True(GameLayout.IsUnlocksTab("Collectables", unlocksHidden: false));
        Assert.False(GameLayout.IsUnlocksTab("Collectables", unlocksHidden: true));
        Assert.False(GameLayout.IsUnlocksTab("Debug", unlocksHidden: false));

        // A section the file lists nowhere is the Game page's, which is what lets --game-section
        // open a stacked section as a page of its own.
        Assert.False(GameLayout.IsUnlocksTab("Cheats", unlocksHidden: false));
    }

    /// <summary>
    /// What the two lists come to for one described game, which is what the side nav and the
    /// Unlocks panel draw from: a listed section the running game has nothing in is neither a page
    /// nor a tab, and with the unlock table gone the tabs are sub-pages instead.
    /// </summary>
    [Fact]
    public void OnlyTheListedSectionsTheGameHasSomethingInBecomePagesAndTabs()
    {
        var goldBolts = Make(20, FeatureKind.Action, 2, "Unlock all gold bolts");
        var drek = Make(40, FeatureKind.Action, 2, "Drek skip");
        var fastLoads = Make(0, FeatureKind.Toggle, 0, "Fast loads");
        var d = Describe(GameId.Rac1, Rac1Groups, goldBolts, drek, fastLoads);

        // This game has collectables and a manip, and nothing at all in Cosmetics or Debug, so
        // those two are listed by the file and drawn nowhere.
        Assert.Equal(new[] { "Collectables" }, GamePanel.UnlocksTabsWithContent(DiscTitle, GameId.Rac1, d));
        Assert.Equal(
            new[] { "Manips" },
            GamePanel.SubPagesWithContent(DiscTitle, GameId.Rac1, d, unlocksHidden: false));

        // No unlock table, no Unlocks entry in the nav: the tab is a sub-page under Game instead.
        Assert.Equal(
            new[] { "Manips", "Collectables" },
            GamePanel.SubPagesWithContent(DiscTitle, GameId.Rac1, d, unlocksHidden: true));

        // A game with nothing in either list gets no tab and no sub-page at all.
        var plain = Describe(GameId.Rac1, Rac1Groups, fastLoads);
        Assert.Empty(GamePanel.UnlocksTabsWithContent(DiscTitle, GameId.Rac1, plain));
        Assert.Empty(GamePanel.SubPagesWithContent(DiscTitle, GameId.Rac1, plain, unlocksHidden: false));

        // And neither does a game that has not been described yet.
        var undescribed = Describe(GameId.Rac1, Rac1Groups);
        Assert.Empty(GamePanel.UnlocksTabsWithContent(DiscTitle, GameId.Rac1, undescribed));
        Assert.Empty(GamePanel.SubPagesWithContent(DiscTitle, GameId.Rac1, undescribed, unlocksHidden: true));
    }

    /// <summary>
    /// The older spelling: a copy of the file edited before the key was renamed still reads, and
    /// means exactly the sub-page list.
    /// </summary>
    [Fact]
    public void TheOldSideSectionsKeyIsReadAsTheSubPageList()
    {
        string path = Path.Combine(Path.GetTempPath(), "racman-layout-legacy-side.json");
        File.WriteAllText(path, """
        {
          "sideSections": ["Manips", "Collectables", "Unlocks", "Debug"],
          "games": {}
        }
        """);

        try
        {
            GameLayout.LoadFrom(path);

            // Reserved names go the same way they always did. The file says nothing about tabs, so
            // the default one would stand, but it names Collectables as a sub-page: that wins.
            Assert.Equal(new[] { "Manips", "Collectables", "Debug" }, GameLayout.SubPages);
            Assert.Empty(GameLayout.UnlocksTabs);
            Assert.False(GameLayout.IsUnlocksTab("Collectables", unlocksHidden: false));
        }
        finally
        {
            File.Delete(path);
            GameLayout.LoadFrom(ShippedLayoutPath());
        }
    }

    /// <summary>A file with both keys means what its new one says; the old key is only the fallback.</summary>
    [Fact]
    public void SubPagesWinsOverTheOldKey()
    {
        string path = Path.Combine(Path.GetTempPath(), "racman-layout-both-keys.json");
        File.WriteAllText(path, """
        {
          "sideSections": ["Manips", "Collectables", "Cosmetics", "Debug"],
          "subPages": ["Debug"],
          "unlocksTabs": ["Collectables"],
          "games": {}
        }
        """);

        try
        {
            GameLayout.LoadFrom(path);
            Assert.Equal(new[] { "Debug" }, GameLayout.SubPages);
            Assert.Equal(new[] { "Collectables" }, GameLayout.UnlocksTabs);
        }
        finally
        {
            File.Delete(path);
            GameLayout.LoadFrom(ShippedLayoutPath());
        }
    }

    /// <summary>
    /// The shape "subPages" briefly had, keyed by the panel each section hung under. Nothing writes
    /// it now, but an override file written while it existed has to keep saying what its author
    /// meant: the Game key is the sub-page list and the Unlocks key is what "unlocksTabs" spells.
    /// </summary>
    [Fact]
    public void ThePanelKeyedSubPagesTableIsStillRead()
    {
        string path = Path.Combine(Path.GetTempPath(), "racman-layout-hosts.json");
        File.WriteAllText(path, """
        {
          "subPages": {
            "game": ["Manips", "Values", "Quick"],
            "Unlocks": ["Collectables", "Unlocks", "Options"],
            "Mods": ["Nowhere"],
            "Autosplitter": ["Nowhere either"]
          },
          "games": {}
        }
        """);

        try
        {
            GameLayout.LoadFrom(path);

            // A key is matched the way the property names are, without caring about case, and the
            // reserved names in both lists are dropped as they always were.
            Assert.Equal(new[] { "Manips" }, GameLayout.SubPages);
            Assert.Equal(new[] { "Collectables" }, GameLayout.UnlocksTabs);

            // Keys naming any other panel meant nothing then and mean nothing now.
            Assert.Equal(new[] { "Manips", "Collectables" }, GameLayout.SectionsDrawnElsewhere);
            Assert.DoesNotContain("Nowhere", GameLayout.SectionsDrawnElsewhere);
            Assert.DoesNotContain("Nowhere either", GameLayout.SectionsDrawnElsewhere);
        }
        finally
        {
            File.Delete(path);
            GameLayout.LoadFrom(ShippedLayoutPath());
        }
    }

    /// <summary>The current key wins over the old table, so a half-converted file reads as written.</summary>
    [Fact]
    public void UnlocksTabsWinsOverThePanelKeyedTable()
    {
        string path = Path.Combine(Path.GetTempPath(), "racman-layout-hosts-and-tabs.json");
        File.WriteAllText(path, """
        {
          "subPages": { "Game": ["Manips"], "Unlocks": ["Collectables"] },
          "unlocksTabs": ["Cosmetics"],
          "games": {}
        }
        """);

        try
        {
            GameLayout.LoadFrom(path);
            Assert.Equal(new[] { "Manips" }, GameLayout.SubPages);
            Assert.Equal(new[] { "Cosmetics" }, GameLayout.UnlocksTabs);
        }
        finally
        {
            File.Delete(path);
            GameLayout.LoadFrom(ShippedLayoutPath());
        }
    }

    /// <summary>
    /// One section, one place: a section in both lists is a sub-page and not a tab, so nothing is
    /// drawn twice. A list that names one twice draws it once, too.
    /// </summary>
    [Fact]
    public void ASectionInBothListsStaysASubPage()
    {
        string path = Path.Combine(Path.GetTempPath(), "racman-layout-twice.json");
        File.WriteAllText(path, """
        {
          "subPages": ["Debug", "Debug"],
          "unlocksTabs": ["Debug", "Collectables"],
          "games": {}
        }
        """);

        try
        {
            GameLayout.LoadFrom(path);
            Assert.Equal(new[] { "Debug" }, GameLayout.SubPages);
            Assert.Equal(new[] { "Collectables" }, GameLayout.UnlocksTabs);
            Assert.False(GameLayout.IsUnlocksTab("Debug", unlocksHidden: false));
        }
        finally
        {
            File.Delete(path);
            GameLayout.LoadFrom(ShippedLayoutPath());
        }
    }

    /// <summary>Every name a panel places itself, so a loop can check them all the same way.</summary>
    private static readonly string[] Reserved =
    {
        GameLayout.QuickSection, GameLayout.ValuesSection, GameLayout.OptionsSection, GameLayout.UnlocksSection,
    };

    [Fact]
    public void TheReservedSectionsAreNeverSidePagesAndNeverInTheTabOrder()
    {
        // A panel places each of these itself: Quick at the very top of the Game page, Values and
        // Options under it, Unlocks on the Unlocks panel. None may become a page, a tab or a header.
        foreach (var reserved in Reserved) Assert.True(GameLayout.IsReserved(reserved));
        Assert.False(GameLayout.IsReserved(GameLayout.PlayerSection));

        foreach (var reserved in Reserved) Assert.DoesNotContain(reserved, GameLayout.SectionsDrawnElsewhere);

        foreach (var game in new[] { GameId.Rac1, GameId.Rac2, GameId.Rac3, GameId.Rac4 })
        {
            var order = GameLayout.TabOrder(string.Empty, game, Reserved.Append("Cheats"));

            foreach (var reserved in Reserved) Assert.DoesNotContain(reserved, order);
            Assert.Contains(GameLayout.PlayerSection, order);
        }
    }

    /// <summary>
    /// "Quick" is the name a layout sends a feature to the block at the top of the Game page with.
    /// It is reserved like the other three, so a file that lists it as a side page or a tab is
    /// ignored, but a move to it is honoured.
    /// </summary>
    [Fact]
    public void AFeatureCanBeMovedToTheQuickBlockButTheSectionIsNeverDrawnAsOne()
    {
        string path = Path.Combine(Path.GetTempPath(), "racman-layout-quick.json");
        File.WriteAllText(path, """
        {
          "subPages": ["Quick", "Debug"],
          "games": {
            "rac4": { "tabOrder": ["Quick", "Cheats"], "moves": { "Unlock all planets": "Quick" } }
          }
        }
        """);

        try
        {
            GameLayout.LoadFrom(path);
            var planets = Make(11, FeatureKind.Action, 2, "Unlock all planets");
            var d = Describe(GameId.Rac4, Rac4Groups, planets);

            Assert.Equal(GameLayout.QuickSection, GameLayout.SectionFor("NPEA00423", GameId.Rac4, planets, d));
            Assert.Equal(new[] { "Debug" }, GameLayout.SubPages);
            Assert.Equal(new[] { "Cheats" }, GameLayout.TabOrder("NPEA00423", GameId.Rac4, Array.Empty<string>()));
        }
        finally
        {
            File.Delete(path);
            GameLayout.LoadFrom(ShippedLayoutPath());
        }
    }

    [Fact]
    public void AFileThatListsAReservedSectionAsASubPageIsIgnored()
    {
        string path = Path.Combine(Path.GetTempPath(), "racman-layout-reserved-side.json");
        File.WriteAllText(path, """
        {
          "subPages": ["Options", "Unlocks", "Values", "Debug"],
          "unlocksTabs": ["Quick", "Unlocks", "Collectables"],
          "games": {
            "rac2": { "tabOrder": ["Options", "Cheats", "Unlocks"], "moves": {} }
          }
        }
        """);

        try
        {
            GameLayout.LoadFrom(path);
            Assert.Equal(new[] { "Debug" }, GameLayout.SubPages);
            Assert.Equal(new[] { "Collectables" }, GameLayout.UnlocksTabs);
            Assert.Equal(new[] { "Cheats" }, GameLayout.TabOrder("NPEA00386", GameId.Rac2, Array.Empty<string>()));
        }
        finally
        {
            File.Delete(path);
            GameLayout.LoadFrom(ShippedLayoutPath());
        }
    }

    [Fact]
    public void MissingFileStillHasTheSubPageDefaults()
    {
        try
        {
            GameLayout.LoadFrom(Path.Combine(Path.GetTempPath(), "racman-no-such-layout.json"));
            Assert.Empty(GameLayout.Problems);
            Assert.Contains("Manips", GameLayout.SubPages);
            Assert.Contains("Debug", GameLayout.SubPages);
            Assert.Contains("Collectables", GameLayout.UnlocksTabs);
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
