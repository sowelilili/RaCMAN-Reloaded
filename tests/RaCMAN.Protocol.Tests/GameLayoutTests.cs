using RaCMAN.App;
using RaCMAN.Protocol;
using Xunit;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The client-owned Game panel layout. These load the shipped data/gamelayout.json from the source
/// tree and check the regroup it encodes: QE and debug controls to a Debug tab, skins/armour to
/// Cosmetics, and everything else left where qwark's DESCRIBE group put it.
/// </summary>
public class GameLayoutTests
{
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
    public void Rac2DebugModeAndQeMoveToDebug()
    {
        var debugMode = Make(5, FeatureKind.Toggle, 0, "Enable debug mode");
        var qe = Make(23, FeatureKind.Value, 2, "QE save write-offset");
        var d = Describe(GameId.Rac2, Rac2Groups, debugMode, qe);

        Assert.Equal("Debug", GameLayout.SectionFor("NPEA00386", debugMode, d));
        Assert.Equal("Debug", GameLayout.SectionFor("NPEA00386", qe, d));
    }

    [Fact]
    public void UnmovedFeaturesKeepTheirDefaults()
    {
        var fastLoads = Make(0, FeatureKind.Toggle, 0, "Fast loads");
        var bolts = Make(7, FeatureKind.Value, 1, "Bolts");
        var d = Describe(GameId.Rac2, Rac2Groups, fastLoads, bolts);

        // A toggle stays in its qwark group; a VALUE defaults to the top value table.
        Assert.Equal("Cheats", GameLayout.SectionFor("NPEA00386", fastLoads, d));
        Assert.Equal(GameLayout.ValuesSection, GameLayout.SectionFor("NPEA00386", bolts, d));
    }

    [Fact]
    public void Rac3CosmeticsAndQeRegroup()
    {
        var armour = Make(11, FeatureKind.Enum, 1, "Armour");
        var ship = Make(12, FeatureKind.Enum, 1, "Ship colour");
        var qe = Make(14, FeatureKind.Value, 1, "QE offset");
        var vendorQe = Make(15, FeatureKind.Action, 1, "Enable vendor QE");
        var d = Describe(GameId.Rac3, Rac3Groups, armour, ship, qe, vendorQe);

        Assert.Equal("Cosmetics", GameLayout.SectionFor("NPEA00387", armour, d));
        Assert.Equal("Cosmetics", GameLayout.SectionFor("NPEA00387", ship, d));
        Assert.Equal("Debug", GameLayout.SectionFor("NPEA00387", qe, d));
        Assert.Equal("Debug", GameLayout.SectionFor("NPEA00387", vendorQe, d));
    }

    [Fact]
    public void DeadlockedSkinMovesToCosmeticsAndTabOrderFollowsConfig()
    {
        var skin = Make(10, FeatureKind.Enum, 1, "Skin");
        var d = Describe(GameId.Rac4, Rac4Groups, skin);

        Assert.Equal("Cosmetics", GameLayout.SectionFor("NPEA00423", skin, d));

        // Deadlocked has no Cosmetics group from qwark; the move creates the section, in configured order.
        var order = GameLayout.TabOrder("NPEA00423", new[] { "Cheats", "Player", "Cosmetics" });
        Assert.Equal(new[] { "Cheats", "Player", "Savefile", "Progress", "Cosmetics" }, order);
    }

    [Fact]
    public void EverydaySectionsStayOnTheGamePageAndTheRestGoToTheSide()
    {
        // The Game page keeps the common controls; the rest are sub-pages under Game in the nav.
        var side = GameLayout.SideSections;
        Assert.Equal(new[] { "Collectables", "Cosmetics", "Debug" }, side);
        Assert.DoesNotContain("Cheats", side);
        Assert.DoesNotContain("Player", side);
        Assert.DoesNotContain("Savefile", side);
    }

    [Fact]
    public void MissingFileStillHasSideSectionDefaults()
    {
        try
        {
            GameLayout.LoadFrom(Path.Combine(Path.GetTempPath(), "racman-no-such-layout.json"));
            Assert.Empty(GameLayout.Problems);
            Assert.Contains("Debug", GameLayout.SideSections);
        }
        finally
        {
            GameLayout.LoadFrom(ShippedLayoutPath());
        }
    }

    [Fact]
    public void UnknownTitleFallsBackToQwarkGroups()
    {
        var toggle = Make(1, FeatureKind.Toggle, 0, "Anything");
        var value = Make(2, FeatureKind.Value, 1, "A number");
        var d = Describe(GameId.Rac1, new[] { "Cheats", "Player" }, toggle, value);

        Assert.Equal("Cheats", GameLayout.SectionFor("ZZZZ99999", toggle, d));
        Assert.Equal(GameLayout.ValuesSection, GameLayout.SectionFor("ZZZZ99999", value, d));
    }

    [Fact]
    public void TabOrderAppendsUnlistedTabsAndNeverIncludesValues()
    {
        var order = GameLayout.TabOrder("NPEA00386", new[] { GameLayout.ValuesSection, "Extra", "Cheats" });

        Assert.DoesNotContain(GameLayout.ValuesSection, order);
        Assert.Equal("Cheats", order[0]);            // configured order wins
        Assert.Equal("Extra", order[^1]);            // an unlisted tab is appended, not dropped
    }
}
