using RaCMAN.App;
using RaCMAN.App.Panels;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The order of a savefile category's rows: saves named after a planet first, in the running
/// game's planet order out of the shipped <c>&lt;game&gt;-planets.txt</c>, revisits in their place,
/// and everything else after them in natural order.
/// </summary>
public class SaveFileOrderTests
{
    private static SaveFileEntry Pc(string name) => new(name + ".sav", false, true, 0, 1, 0, 1);

    private static string[] Order(GameId game, params string[] names) =>
        SaveFileOrder.Sort(names.Select(Pc), game).Select(e => e.DisplayName).ToArray();

    [Fact]
    public void RaC1SavesGoInPlanetOrderWithVeldinsRevisitLastAndTheRestBelow()
    {
        var order = Order(GameId.Rac1,
            "misc thing", "Veldin 2 skip", "kerwan", "Veldin", "Novalis", "veldin_2", "Drek's Fleet",
            "Gemlik", "Any% start", "Blarg 10", "Blarg 9", "Veldin II", "100%", "Nova test", "Oltanis",
            "Oltanis Orbit", "veldin2");

        Assert.Equal(new[]
        {
            "Veldin",
            "Novalis",
            "kerwan",
            "Blarg 9", "Blarg 10",
            // "oltanis orbit" is Gemlik's alias, and longer than Oltanis's own name.
            "Gemlik", "Oltanis Orbit",
            "Oltanis",
            "Drek's Fleet",
            // Veldin's second visit, its own line in rac1-planets.txt, however it is written.
            "Veldin 2 skip", "Veldin II", "veldin2", "veldin_2",
            "100%", "Any% start", "misc thing", "Nova test",
        }, order);
    }

    [Fact]
    public void RaC2sAranosRevisitSortsAfterBoldanAndABareAranosIsTheFirstVisit()
    {
        var order = Order(GameId.Rac2,
            "random", "Damosel", "Aranos 2", "Snivelak", "Boldan", "Maktar Resort", "aranos ii", "Oozla",
            "Aranos", "jamming array", "Aranos-2 prison");

        Assert.Equal(new[]
        {
            "Aranos",
            "Oozla",
            "Maktar Resort",
            "Boldan",
            "Aranos 2", "aranos ii", "Aranos-2 prison",
            "Snivelak",
            "Damosel",
            // Maktar's "jamming" matches too, but the Jamming Array's own name is longer.
            "jamming array",
            "random",
        }, order);
    }

    [Fact]
    public void ToDsFastoonIIIsTheLastPlanetAndLevelNamesCountAsPlanetNames()
    {
        var order = Order(GameId.Tod,
            "my practice", "Fastoon II", "Rykan V", "fastoon-2 tachyon", "Metropolis start", "Fastoon",
            "Imperial Fight Festival", "Kerwan", "Stratus City", "Fastoon 2");

        Assert.Equal(new[]
        {
            "Kerwan", "Metropolis start",
            "Stratus City",
            "Fastoon",
            "Imperial Fight Festival",
            "Rykan V",
            "Fastoon 2", "Fastoon II", "fastoon-2 tachyon",
            "my practice",
        }, order);
    }

    [Fact]
    public void UyAsNamedReturnsGoToTheirOwnLines()
    {
        var order = Order(GameId.Rac3,
            "Metropolis Rangers", "Phoenix Rescue", "Metropolis", "Starship Phoenix", "Veldin", "VC1", "zzz");

        Assert.Equal(new[]
        {
            "Veldin", "Starship Phoenix", "Phoenix Rescue", "Metropolis", "Metropolis Rangers", "VC1", "zzz",
        }, order);
    }

    [Fact]
    public void DeadlockedMatchesWholePlanetNamesWhateverTheSpacing()
    {
        var order = Order(GameId.Rac4, "Sarathos", "Dread Zone", "Catacrom Four", "Maraxus", "practice");

        Assert.Equal(new[] { "Dread Zone", "Catacrom Four", "Sarathos", "Maraxus", "practice" }, order);
    }

    [Fact]
    public void ThePlanetNameMustEndAtABoundaryADigitOrTheEnd()
    {
        IReadOnlyList<string[]> planets = new[] { new[] { "nova" }, new[] { "kerwan" } };

        Assert.Equal(-1, SaveFileOrder.PlanetOf("Novalis", planets));
        Assert.Equal(-1, SaveFileOrder.PlanetOf("Novas", planets));
        Assert.Equal(-1, SaveFileOrder.PlanetOf("Kerwanderer", planets));
        Assert.Equal(-1, SaveFileOrder.PlanetOf("my nova", planets));

        Assert.Equal(0, SaveFileOrder.PlanetOf("Nova", planets));
        Assert.Equal(0, SaveFileOrder.PlanetOf("nova2", planets));
        Assert.Equal(0, SaveFileOrder.PlanetOf("NOVA_prime", planets));
        Assert.Equal(0, SaveFileOrder.PlanetOf("Nova-Prime", planets));
        Assert.Equal(1, SaveFileOrder.PlanetOf("Kerwan (hoverboard)", planets));
    }

    [Fact]
    public void TheLongestAliasWinsAndATieGoesToTheEarlierPlanet()
    {
        var rac1 = AutosplitRoutes.For(GameId.Rac1);
        var tod = AutosplitRoutes.For(GameId.Tod);
        var rac2 = AutosplitRoutes.For(GameId.Rac2);

        Assert.Equal(18, SaveFileOrder.PlanetOf("Veldin2", rac1));
        Assert.Equal(0, SaveFileOrder.PlanetOf("Veldin", rac1));
        Assert.Equal(0, SaveFileOrder.PlanetOf("Veldin 3", rac1));
        Assert.Equal(18, SaveFileOrder.PlanetOf("Fastoon 2", tod));
        Assert.Equal(3, SaveFileOrder.PlanetOf("Fastoon", tod));

        // "aranos" is on both of Aranos's lines: the same length, so the first visit.
        Assert.Equal(0, SaveFileOrder.PlanetOf("Aranos", rac2));
        Assert.Equal(14, SaveFileOrder.PlanetOf("Aranos II", rac2));

        // And a made-up list where the longer alias is on the earlier line.
        IReadOnlyList<string[]> planets = new[] { new[] { "space combat ii" }, new[] { "space combat" } };
        Assert.Equal(0, SaveFileOrder.PlanetOf("Space Combat II", planets));
        Assert.Equal(1, SaveFileOrder.PlanetOf("Space Combat 1", planets));
    }

    [Fact]
    public void WithoutAGameOrAPlanetsFileTheOrderIsNaturalAndIgnoresCase()
    {
        string[] names = { "Veldin 2", "save 10", "kerwan", "Save 9", "Aridia" };
        string[] natural = { "Aridia", "kerwan", "Save 9", "save 10", "Veldin 2" };

        Assert.Equal(natural, Order(GameId.None, names));
        Assert.Equal(natural, SaveFileOrder.Sort(names.Select(Pc), Array.Empty<string[]>()).Select(e => e.DisplayName));
    }

    [Fact]
    public void NaturalOrderComparesNumbersAsNumbers()
    {
        Assert.True(SaveFileOrder.CompareNatural("Kerwan 9", "kerwan 10") < 0);
        Assert.True(SaveFileOrder.CompareNatural("Kerwan 010", "Kerwan 9") > 0);
        Assert.True(SaveFileOrder.CompareNatural("Kerwan", "Kerwan 2") < 0);
        Assert.True(SaveFileOrder.CompareNatural("kerwan b", "Kerwan A") > 0);
        Assert.NotEqual(0, SaveFileOrder.CompareNatural("Kerwan", "kerwan"));
    }

    [Fact]
    public void ThePanelsRowsAreInPlanetOrderInEveryCategoryThePremadeOneIncluded()
    {
        var pc = new[]
        {
            new LocalSaveFile("zz notes.sav", 1, 1),
            new LocalSaveFile("Veldin 2.sav", 1, 2),
            new LocalSaveFile("Kerwan.sav", 1, 3),
        };
        var console = new[] { new ConsoleSaveFile("Novalis.sav", 1, 4), new ConsoleSaveFile("Kerwan.sav", 1, 3) };

        Assert.Equal(new[] { "Novalis", "Kerwan", "Veldin 2", "zz notes" },
            SaveFilesPanel.Rows("any%", console, pc, GameId.Rac1).Select(e => e.DisplayName));

        // The premade category lists only the release's files, in the same order.
        Assert.Equal(new[] { "Kerwan", "Veldin 2", "zz notes" },
            SaveFilesPanel.Rows(PremadeSaveFiles.Category, console, pc, GameId.Rac1).Select(e => e.DisplayName));

        // No game: alphabetical.
        Assert.Equal(new[] { "Kerwan", "Novalis", "Veldin 2", "zz notes" },
            SaveFilesPanel.Rows("any%", console, pc).Select(e => e.DisplayName));
    }
}
