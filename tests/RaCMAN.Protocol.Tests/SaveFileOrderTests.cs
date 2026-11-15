using System.Buffers.Binary;
using System.Diagnostics;
using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The order of a savefile category's rows: by the planet each save says it was made on, in the
/// game's level order, natural order within a planet and for the saves with no planet below them;
/// or by name alone with the Alphabetical setting. The planet is read out of the real premade
/// saves the release ships, out of the PC library a few bytes at a time and once per CRC, and off
/// the fake console for a save only it has.
/// </summary>
public class SaveFileOrderTests : IDisposable
{
    private const string Title = "NPEA00385";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "racman-saveorder-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The premade savefiles in the source tree, which the release ships beside the executable.</summary>
    private static string PremadeFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, PremadeSaveFiles.FolderName);
            if (File.Exists(Path.Combine(candidate, PremadeSaveFiles.TitlesFileName))) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(PremadeSaveFiles.FolderName);
    }

    private static byte[] Premade(string title, string name) => File.ReadAllBytes(Path.Combine(PremadeFolder(), title, name));

    /// <summary>A real save of the game with its level word changed, as the game would have written it on another planet.</summary>
    private static byte[] On(byte[] save, int level)
    {
        var copy = (byte[])save.Clone();
        BinaryPrimitives.WriteInt32BigEndian(copy.AsSpan(SaveFilePlanet.ClassicLevelOffset), level);
        return copy;
    }

    private static byte[] TodSave(int level, long length = SaveFilePlanet.TodSaveLength)
    {
        var bytes = new byte[length];
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 0x10), (uint)i);
        if (length >= SaveFilePlanet.TodLevelOffset + 4)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan((int)SaveFilePlanet.TodLevelOffset), level);
        }

        return bytes;
    }

    private string Write(string category, string name, byte[] bytes)
    {
        var folder = Path.Combine(_root, Title, category);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string[] Names(IEnumerable<SaveFileEntry> rows) => rows.Select(r => r.DisplayName).ToArray();

    /// <summary>The panel's own steps for one PC category: list it, read the planets, sort.</summary>
    private static SaveFileEntry[] PcRows(SaveFileLibrary library, SaveFilePlanetCache planets, GameId game,
        string category, SaveFileSort sort = SaveFileSort.ByPlanet)
    {
        var pc = library.FilesWithCrc(Title, category);
        SaveFilesPanel.ReadPcPlanets(planets, game, library.CategoryFolder(Title, category), pc);
        return SaveFilesPanel.Rows(category, null, pc, game, sort, SaveFilesPanel.PlanetOf(planets, game));
    }

    // ---------------------------------------------------------------- the planet in the save

    [Fact]
    public void EveryPremadeSaveSaysItIsOnItsGamesFirstLevel()
    {
        string root = PremadeFolder();

        // Every NG+ save starts the game over: RaC1's Veldin and RaC2's Aranos are level 0, UYA's
        // Veldin and Deadlocked's Dreadzone Station level 1 (0 is their menus).
        Assert.Equal(0, SaveFilePlanet.ReadFile(GameId.Rac1, Path.Combine(root, "NPEA00385", "NG+.sav")));
        Assert.Equal(0, SaveFilePlanet.ReadFile(GameId.Rac2, Path.Combine(root, "NPEA00386", "NG+ & NG+ no IMG.sav")));
        Assert.Equal(1, SaveFilePlanet.ReadFile(GameId.Rac3, Path.Combine(root, "NPEA00387", "NG+.sav")));
        Assert.Equal(1, SaveFilePlanet.ReadFile(GameId.Rac3, Path.Combine(root, "NPEA00387", "NG+ no QE.sav")));
        Assert.Equal(1, SaveFilePlanet.ReadFile(GameId.Rac4, Path.Combine(root, "NPEA00423", "NG+.sav")));
        Assert.Equal(1, SaveFilePlanet.ReadFile(GameId.Rac4, Path.Combine(root, "NPEA00423", "NG+ no LC.sav")));

        // The trilogy disc's title takes the same folders, and its saves read the same.
        var premade = new PremadeSaveFiles(root);
        Assert.Equal(1, SaveFilePlanet.ReadFile(GameId.Rac3, Path.Combine(premade.FolderFor("BCES01503", GameId.Rac3)!, "NG+.sav")));
        Assert.Equal(0, SaveFilePlanet.ReadFile(GameId.Rac2, Path.Combine(premade.FolderFor("RC2ILYOOO", GameId.Rac2)!, "NG+ & NG+ no IMG.sav")));
    }

    [Fact]
    public void ASaveOfAnotherGameOrNoSaveAtAllHasNoPlanet()
    {
        var rac1 = Premade("NPEA00385", "NG+.sav");
        var uya = Premade("NPEA00387", "NG+.sav");

        // Each game's block sizes are its own, so another game's save is not one of its saves.
        Assert.Null(SaveFilePlanet.Parse(GameId.Rac3, rac1, rac1.Length));
        Assert.Null(SaveFilePlanet.Parse(GameId.Rac1, uya, uya.Length));
        Assert.Null(SaveFilePlanet.Parse(GameId.Rac4, uya, uya.Length));

        // The right sizes but not field 0 where it belongs, and a cut-off head.
        var moved = (byte[])rac1.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(moved.AsSpan(0x10), 1);
        Assert.Null(SaveFilePlanet.Parse(GameId.Rac1, moved, moved.Length));
        Assert.Null(SaveFilePlanet.Parse(GameId.Rac1, rac1.AsSpan(0, 0x18), 0x18));
        Assert.Null(SaveFilePlanet.Parse(GameId.Rac1, new byte[0x1C], 0x1C));
    }

    [Fact]
    public void ToDsLevelIsTheWordAtTheEndOfAWholeSave()
    {
        Directory.CreateDirectory(_root);
        string whole = Path.Combine(_root, "fastoon ii.sav");
        File.WriteAllBytes(whole, TodSave(18));
        Assert.Equal(18, SaveFilePlanet.ReadFile(GameId.Tod, whole));

        // A file of any other length is not a ToD save, whatever its bytes at that offset.
        string longer = Path.Combine(_root, "longer.sav");
        File.WriteAllBytes(longer, TodSave(18, SaveFilePlanet.TodSaveLength + 4));
        Assert.Null(SaveFilePlanet.ReadFile(GameId.Tod, longer));

        string shorter = Path.Combine(_root, "shorter.sav");
        File.WriteAllBytes(shorter, TodSave(0, 0x1000));
        Assert.Null(SaveFilePlanet.ReadFile(GameId.Tod, shorter));
    }

    // ---------------------------------------------------------------- the order

    [Fact]
    public void ALibraryIsInLevelOrderByWhatEachSaveSaysWithItsRevisitLastAndUnknownsBelow()
    {
        var ng = Premade("NPEA00385", "NG+.sav");
        Write("any%", "Kerwan 10.sav", On(ng, 3));
        Write("any%", "kerwan 9.sav", On(ng, 3));
        // Named for Veldin, saved on Veldin's second visit: the save decides, not the name.
        Write("any%", "Veldin start.sav", On(ng, 18));
        Write("any%", "novalis.sav", On(ng, 1));
        Write("any%", "a fresh file.sav", On(ng, 0));
        // Level 19 has no line in rac1-planets.txt, a UYA save is not RaC1's, and the rest is not a save.
        Write("any%", "beyond.sav", On(ng, 19));
        Write("any%", "uya save.sav", Premade("NPEA00387", "NG+.sav"));
        Write("any%", "notes 2.sav", new byte[0x10]);
        Write("any%", "Aridia 1.sav", Array.Empty<byte>());

        var rows = PcRows(new SaveFileLibrary(_root), new SaveFilePlanetCache(), GameId.Rac1, "any%");

        Assert.Equal(new[]
        {
            "a fresh file",
            "novalis",
            "kerwan 9", "Kerwan 10",
            "Veldin start",
            "Aridia 1", "beyond", "notes 2", "uya save",
        }, Names(rows));
    }

    [Fact]
    public void ToDsFastoonIIGoesAfterEveryOtherPlanet()
    {
        Write("story", "Fastoon II.sav", TodSave(18));
        Write("story", "Rykan.sav", TodSave(9));
        Write("story", "Fastoon.sav", TodSave(3));
        Write("story", "Kerwan.sav", TodSave(0));
        Write("story", "level 19.sav", TodSave(19));

        var rows = PcRows(new SaveFileLibrary(_root), new SaveFilePlanetCache(), GameId.Tod, "story");

        Assert.Equal(new[] { "Kerwan", "Fastoon", "Rykan", "Fastoon II", "level 19" }, Names(rows));
    }

    /// <summary>One save of <paramref name="game"/> per name, each a real save moved to its level, in a "story" category.</summary>
    private SaveFileEntry[] StoryRows(GameId game, byte[] ng, params (string Name, int Level)[] saves)
    {
        foreach (var (name, level) in saves) Write("story", name + ".sav", On(ng, level));
        return PcRows(new SaveFileLibrary(_root), new SaveFilePlanetCache(), game, "story");
    }

    /// <summary>
    /// UYA numbers its Clank, sewer and ranger levels and the vid comics after every planet, so the
    /// level order put them all at the end. The list is the planet picker's order, each mission
    /// level after its planet, and for the save list alone each ranger level right after its planet.
    /// </summary>
    [Fact]
    public void UyaSavesAreInThePickersOrderWithEachRangerLevelAfterItsPlanet()
    {
        var rows = StoryRows(GameId.Rac3, Premade("NPEA00387", "NG+.sav"),
            ("Vid Comic 1 SE", 36), ("Vid Comic 5", 35), ("Vid Comic 3", 34), ("Vid Comic 2", 33),
            ("Vid Comic 4", 32), ("Vid Comic 1", 31), ("Vid Comic 6", 30), ("Tyhrranosis Rangers", 29),
            ("Aquatos Sewers", 28), ("Aquatos Clank", 27), ("Metropolis Rangers", 26), ("Insomniac Museum", 24),
            ("Holostar Clank", 23), ("Command Center", 22), ("Obani Draco", 21), ("Launch Site", 20),
            ("Qwark's Hideout", 19), ("Aridia", 18), ("Crash Site", 17), ("Metropolis", 16), ("Koros", 14),
            ("Holostar", 13), ("Blackwater City", 12), ("Obani Gemini", 11), ("Zeldrin Starport", 10),
            ("Tyhrranosis", 9), ("Aquatos", 8), ("aquatos 2", 8), ("Annihilation Nation", 7),
            ("Phoenix Rescue", 6), ("Daxx", 5), ("Marcadia", 4), ("Starship Phoenix", 3), ("Florana", 2),
            ("Veldin", 1),
            // The menu and a gap in the numbering are null lines, so no planet.
            ("at the menu", 0), ("gap 15", 15));

        Assert.Equal(new[]
        {
            "Veldin", "Florana", "Starship Phoenix", "Marcadia", "Daxx", "Obani Gemini", "Blackwater City",
            "Annihilation Nation", "Aquatos", "aquatos 2", "Aquatos Clank", "Aquatos Sewers",
            "Tyhrranosis", "Tyhrranosis Rangers", "Zeldrin Starport", "Holostar", "Holostar Clank", "Obani Draco",
            "Metropolis", "Metropolis Rangers", "Crash Site", "Aridia", "Qwark's Hideout",
            "Phoenix Rescue", "Koros", "Command Center", "Launch Site", "Insomniac Museum",
            "Vid Comic 1", "Vid Comic 2", "Vid Comic 3", "Vid Comic 4", "Vid Comic 5", "Vid Comic 6", "Vid Comic 1 SE",
            "at the menu", "gap 15",
        }, Names(rows));
    }

    /// <summary>
    /// RaC2's picker is in id order, which numbers its orbits, Wupash and the Jamming Array after the
    /// Museum. For the save list each orbit follows its planet, the Jamming Array Maktar and Wupash
    /// Oozla; the return to Aranos stays where the game visits it, and Slim Cognito, which names no
    /// planet, where the picker has it.
    /// </summary>
    [Fact]
    public void RaC2SavesFollowThePickerWithEachOrbitAfterItsPlanet()
    {
        var rows = StoryRows(GameId.Rac2, Premade("NPEA00386", "NG+ & NG+ no IMG.sav"),
            ("Jamming Array", 26), ("Wupash", 25), ("Slim Cognito", 24), ("Damosel Orbit", 23), ("Dobbo Orbit", 22),
            ("Insomniac Museum", 21), ("Yeedil", 20), ("Grelbin", 19), ("Damosel", 18), ("Smolg", 17),
            ("Snivelak", 16), ("Gorn", 15), ("Aranos 2", 14), ("Boldan", 13), ("Todano", 12), ("Joba", 11),
            ("Hrugis", 10), ("Dobbo", 9), ("Tabora", 8), ("Siberius", 7), ("Notak", 6), ("Feltzin", 5),
            ("Barlow", 4), ("Endako", 3), ("Maktar", 2), ("Oozla", 1), ("Aranos", 0),
            ("level 27", 27));

        Assert.Equal(new[]
        {
            "Aranos", "Oozla", "Wupash", "Maktar", "Jamming Array", "Endako", "Barlow", "Feltzin", "Notak",
            "Siberius", "Tabora", "Dobbo", "Dobbo Orbit", "Hrugis", "Joba", "Todano", "Boldan", "Aranos 2",
            "Gorn", "Snivelak", "Smolg", "Damosel", "Damosel Orbit", "Grelbin", "Yeedil", "Insomniac Museum",
            "Slim Cognito",
            "level 27",
        }, Names(rows));
    }

    /// <summary>Deadlocked's picker lists Orxon (8) before Valix (7), and so does the save list.</summary>
    [Fact]
    public void DeadlockedSavesAreInThePickersOrderWithOrxonBeforeValix()
    {
        var rows = StoryRows(GameId.Rac4, Premade("NPEA00423", "NG+.sav"),
            ("Interior", 15), ("Ghost Station", 14), ("Maraxus", 13), ("Stygia", 11), ("Torval", 10),
            ("Orxon", 8), ("Valix", 7), ("Shaar", 6), ("Kronos", 5), ("Sarathos", 4), ("Catacrom", 2),
            ("DreadZone", 1),
            // An infinite loop and the unused id 0 are null lines.
            ("a loop", 3), ("id 0", 0));

        Assert.Equal(new[]
        {
            "DreadZone", "Catacrom", "Sarathos", "Kronos", "Shaar", "Orxon", "Valix", "Torval", "Stygia",
            "Maraxus", "Ghost Station", "Interior",
            "a loop", "id 0",
        }, Names(rows));
    }

    /// <summary>The planet combo's labels for each game's PLANET_LIST, exactly as it drew them before the save list followed it.</summary>
    public static TheoryData<GameId, string, string[]> PickerLists() => new()
    {
        { GameId.Rac1, "NPEA00385", new[]
            {
                "Veldin", "Novalis", "Aridia", "Kerwan", "Eudora", "Rilgar", "Blarg", "Umbris", "Batalia", "Gaspar",
                "Orxon", "Pokitaru", "Hoven", "Gemlik", "Oltanis", "Quartu", "Kalebo3", "Fleet", "Veldin2",
            } },
        { GameId.Rac2, "NPEA00386", new[]
            {
                "Aranos", "Oozla", "Maktar", "Endako", "Barlow", "Feltzin", "Notak", "Siberius", "Tabora", "Dobbo",
                "Hrugis", "Joba", "Todano", "Boldan", "Aranos 2", "Gorn", "Snivelak", "Smolg", "Damosel", "Grelbin",
                "Yeedil", "Insomniac Museum", "Dobbo Orbit", "Damosel Orbit", "Slim Cognito", "Wupash", "Jamming Array",
            } },
        { GameId.Rac3, "NPEA00387", new[]
            {
                "Veldin", "Florana", "Starship Phoenix", "Marcadia", "Daxx", "Obani Gemini", "Blackwater City",
                "Annihilation Nation", "Aquatos", "Aquatos Clank", "Aquatos Sewers", "Tyhrranosis", "Zeldrin Starport",
                "Holostar", "Holostar Clank", "Obani Draco", "Metropolis", "Crash Site", "Aridia", "Qwark's Hideout",
                "Phoenix Rescue", "Koros", "Command Center", "Launch Site", "Insomniac Museum",
                "Tyhrranosis Rangers", "Metropolis Rangers",
                "Vid Comic 1", "Vid Comic 2", "Vid Comic 3", "Vid Comic 4", "Vid Comic 5", "Vid Comic 6", "Vid Comic 1 SE",
            } },
        { GameId.Rac4, "NPEA00423", new[]
            {
                "DreadZone", "Catacrom", "Sarathos", "Kronos", "Shaar", "Orxon", "Valix", "Torval", "Stygia",
                "Maraxus", "Ghost Station", "Interior",
            } },
        { GameId.Tod, FakeQwarkServer.TodTitleId, new[]
            {
                "Kerwan", "Cobalia", "Kortog", "Fastoon", "Voron Asteroid Belt", "Mukow", "Nundac Asteroid Ring",
                "Ardolis", "Rakar Star Cluster", "Rykan V", "Sargasso", "Kreeli Comet", "Viceron",
                "Verdigris Black Hole", "Jasindu", "Ublik Passage", "Reepor", "Igliak", "Fastoon II",
            } },
    };

    /// <summary>
    /// Against the console's PLANET_LIST for each game, as the fake console answers it: the planet
    /// combo is what it was, the save list takes the order the console sent, and that order makes
    /// the same save list as the one the client keeps for when the console has not said it, which
    /// is the order the console sends. The save list's moves leave the combo alone.
    /// </summary>
    [Theory]
    [MemberData(nameof(PickerLists))]
    public async Task ThePickerIsUnchangedAndTheConsolesOrderSortsAsTheKeptOneDoes(GameId game, string title, string[] picker)
    {
        using var server = new FakeQwarkServer();
        server.Describe = server.Describe with { Game = game };
        server.Session = server.Session with { Game = game, TitleId = title };
        server.UsePlanetsOf(game);
        server.Start();
        using var state = await ConnectedStateAsync(server);

        int count = FakeQwarkServer.PlanetListOf(game).Names.Length;
        Assert.True(await PumpAsync(state, () =>
            state.DescribedGame == game && state.PlanetOrderSent && state.Planets.Length == count));

        Assert.Equal(picker, PlanetChoices.For(state.Planets, state.PlanetOrder).Labels);

        var sent = SaveFilesPanel.PickerOrder(state, game);
        Assert.NotNull(sent);
        Assert.Equal(state.PlanetOrder, SaveFilePlanetOrder.PickerOrder(game));
        Assert.Equal(SaveFilePlanetOrder.For(game), SaveFilePlanetOrder.For(game, sent));
    }

    /// <summary>
    /// The console's order is the one followed while it is the described game's: a qwark that listed
    /// Zeldrin Starport after Obani Draco (where the old client's combo listed it a second time)
    /// moves UYA's saves with it, and the rangers still follow their planets. A module before build
    /// 53 sends no order, and the save list keeps the picker's rather than falling to the numbering.
    /// </summary>
    [Fact]
    public async Task TheConsolesOrderIsFollowedAndAnOlderModulesMissingOneIsNotTheNumbering()
    {
        var library = new SaveFileLibrary(_root);
        var planets = new SaveFilePlanetCache();
        var ng = Premade("NPEA00387", "NG+.sav");
        foreach (var (name, level) in new[]
                 {
                     ("Metropolis", 16), ("Obani Draco", 21), ("Zeldrin Starport", 10), ("Tyhrranosis Rangers", 29),
                     ("Tyhrranosis", 9), ("Holostar", 13), ("Insomniac Museum", 24), ("Phoenix Rescue", 6),
                 })
        {
            library.Write(Title, "story", name + ".sav", On(ng, level));
        }

        var pc = library.FilesWithCrc(Title, "story");
        SaveFilesPanel.ReadPcPlanets(planets, GameId.Rac3, library.CategoryFolder(Title, "story"), pc);

        // The rows as the panel sorts them while connected to a UYA module that sends this PLANET_LIST.
        async Task<string[]> SortedAgainst(byte[]? order, bool sendsOrder)
        {
            using var server = new FakeQwarkServer();
            server.Describe = server.Describe with { Game = GameId.Rac3 };
            server.Session = server.Session with { Game = GameId.Rac3, TitleId = "NPEA00387" };
            server.UsePlanetsOf(GameId.Rac3);
            if (order is not null) server.PlanetOrder = order;
            server.SendsPlanetOrder = sendsOrder;
            server.Start();
            using var state = await ConnectedStateAsync(server);
            Assert.True(await PumpAsync(state, () =>
                state.DescribedGame == GameId.Rac3 && state.Planets.Length > 0 && state.PlanetOrderSent == sendsOrder));

            // Another game's saves are never sorted by UYA's order.
            Assert.Null(SaveFilesPanel.PickerOrder(state, GameId.Rac1));

            var picker = SaveFilesPanel.PickerOrder(state, GameId.Rac3);
            Assert.Equal(sendsOrder, picker is not null);
            return Names(SaveFilesPanel.Rows("story", null, pc, GameId.Rac3, SaveFileSort.ByPlanet,
                SaveFilesPanel.PlanetOf(planets, GameId.Rac3), picker));
        }

        var moved = FakeQwarkServer.Rac3PlanetOrder.Where(id => id != 10).ToList();
        moved.Insert(moved.IndexOf(21) + 1, 10);
        Assert.Equal(new[]
        {
            "Tyhrranosis", "Tyhrranosis Rangers", "Holostar", "Obani Draco", "Zeldrin Starport", "Metropolis",
            "Phoenix Rescue", "Insomniac Museum",
        }, await SortedAgainst(moved.ToArray(), sendsOrder: true));

        // A module before build 53: the ids in turn stand in for the combo, never for the save list.
        Assert.Equal(new[]
        {
            "Tyhrranosis", "Tyhrranosis Rangers", "Zeldrin Starport", "Holostar", "Obani Draco", "Metropolis",
            "Phoenix Rescue", "Insomniac Museum",
        }, await SortedAgainst(null, sendsOrder: false));
    }

    private static async Task<AppState> ConnectedStateAsync(FakeQwarkServer server)
    {
        var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);
        return state;
    }

    /// <summary>Runs frames until the condition holds, the way the render loop would.</summary>
    private static async Task<bool> PumpAsync(AppState state, Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            state.Tick(1f / 60f);
            if (condition()) return true;
            await Task.Delay(16);
        }

        state.Tick(1f / 60f);
        return condition();
    }

    [Fact]
    public void AlphabeticalIsNaturalOrderWhateverTheSavesSay()
    {
        var ng = Premade("NPEA00385", "NG+.sav");
        Write("any%", "save 10.sav", On(ng, 0));
        Write("any%", "Save 9.sav", On(ng, 18));
        Write("any%", "kerwan.sav", On(ng, 3));
        Write("any%", "Aridia.sav", On(ng, 2));

        var planets = new SaveFilePlanetCache();
        var library = new SaveFileLibrary(_root);

        Assert.Equal(new[] { "save 10", "Aridia", "kerwan", "Save 9" }, Names(PcRows(library, planets, GameId.Rac1, "any%")));
        Assert.Equal(new[] { "Aridia", "kerwan", "Save 9", "save 10" },
            Names(PcRows(library, planets, GameId.Rac1, "any%", SaveFileSort.Alphabetical)));

        // No game on screen: natural order too.
        Assert.Equal(new[] { "Aridia", "kerwan", "Save 9", "save 10" }, Names(PcRows(library, planets, GameId.None, "any%")));
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

    // ---------------------------------------------------------------- the cache

    [Fact]
    public void AnUnchangedFileIsNotReadAgainAndASaveWrittenOverIs()
    {
        var ng = Premade("NPEA00385", "NG+.sav");
        var library = new SaveFileLibrary(_root);
        var planets = new SaveFilePlanetCache();
        library.Write(Title, "any%", "run.sav", On(ng, 3));

        Assert.Equal(new[] { "run" }, Names(PcRows(library, planets, GameId.Rac1, "any%")));
        var crc = library.Crc(Title, "any%", "run.sav");
        Assert.Equal(3, planets.Planet(GameId.Rac1, crc));

        // The same CRC is the same planet without opening the file: gone from under it, it still is.
        string path = library.FilePath(Title, "any%", "run.sav");
        File.Move(path, path + ".away");
        Assert.Equal(3, planets.Local(GameId.Rac1, path, crc));
        File.Move(path + ".away", path);

        // A save written over it is new bytes, a new CRC and so its own planet.
        library.Write(Title, "any%", "run.sav", On(ng, 16));
        library.Write(Title, "any%", "other.sav", On(ng, 5));
        Assert.Equal(new[] { "other", "run" }, Names(PcRows(library, planets, GameId.Rac1, "any%")));
        Assert.Equal(16, planets.Planet(GameId.Rac1, library.Crc(Title, "any%", "run.sav")));
    }

    // ---------------------------------------------------------------- the console's saves

    [Fact]
    public async Task ASaveOnlyTheConsoleHasIsPlacedByOneReadOfItsHeadAndOnlyOnce()
    {
        var server = new FakeQwarkServer();
        server.Start();
        using var client = new QwarkClient { AutoReconnect = false };
        using (server)
        {
            await client.ConnectAsync("127.0.0.1", server.Port);
            string title = server.Session.TitleId;
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");

            var ng = Premade("NPEA00385", "NG+.sav");
            server.Files[QwarkClient.SaveFileConsolePath(title, "any%", "fleet.sav")] = On(ng, 17);
            server.Files[QwarkClient.SaveFileConsolePath(title, "any%", "blarg.sav")] = On(ng, 6);
            server.Files[QwarkClient.SaveFileConsolePath(title, "any%", "junk.sav")] = new byte[4];
            // A save this PC has, on Orxon, whose planet the PC side has read already.
            var pc = new[] { new LocalSaveFile("orxon.sav", 1, 0x1234) };

            var planets = new SaveFilePlanetCache();
            planets.Remember(GameId.Rac1, 0x1234, 10);
            var console = await client.SaveFileListAsync("any%");
            var rows = SaveFilesPanel.Rows("any%", console, pc, GameId.Rac1, SaveFileSort.ByPlanet,
                SaveFilesPanel.PlanetOf(planets, GameId.Rac1));

            // Nothing is known of the console's three yet, so they are below, in natural order.
            Assert.Equal(new[] { "orxon", "blarg", "fleet", "junk" }, Names(rows));

            var missing = SaveFilesPanel.ConsolePlanetsToRead(planets, GameId.Rac1, rows);
            Assert.Equal(new[] { "blarg", "fleet", "junk" }, Names(missing));

            server.ClearRequestLog();
            int read = await SaveFilesPanel.ReadConsolePlanetsAsync(client, planets, GameId.Rac1, title, "any%", missing);
            Assert.Equal(3, read);
            Assert.Equal(3, server.RequestLog().Count(op => op == Opcode.FileRead));

            // Blarg (6) before Orxon (10) before Drek's Fleet (17); the file that is no save stays below.
            rows = SaveFileOrder.Sort(rows, GameId.Rac1, SaveFileSort.ByPlanet, SaveFilesPanel.PlanetOf(planets, GameId.Rac1));
            Assert.Equal(new[] { "blarg", "orxon", "fleet", "junk" }, Names(rows));

            // Read once: the next scan has nothing left to fetch, the junk file included.
            Assert.Empty(SaveFilesPanel.ConsolePlanetsToRead(planets, GameId.Rac1, rows));
        }
    }

    [Fact]
    public async Task AConsoleFileThatIsGoneIsSkippedAndToDsAreNeverFetched()
    {
        var server = new FakeQwarkServer();
        server.Start();
        using var client = new QwarkClient { AutoReconnect = false };
        using (server)
        {
            await client.ConnectAsync("127.0.0.1", server.Port);
            string title = server.Session.TitleId;
            var ng = Premade("NPEA00385", "NG+.sav");
            var kept = On(ng, 12);
            server.Files[QwarkClient.SaveFileConsolePath(title, "any%", "hoven.sav")] = kept;

            var gone = new SaveFileEntry("gone.sav", true, false, 0xDEAD, 0, 0xB0000, 0);
            var hoven = new SaveFileEntry("hoven.sav", true, false, Crc32.Compute(kept), 0, (uint)kept.Length, 0);
            var planets = new SaveFilePlanetCache();

            Assert.Equal(1, await SaveFilesPanel.ReadConsolePlanetsAsync(client, planets, GameId.Rac1, title, "any%",
                new[] { gone, hoven }));
            Assert.Equal(12, planets.Planet(GameId.Rac1, hoven.ConsoleCrc));
            Assert.False(planets.TryGet(GameId.Rac1, gone.ConsoleCrc, out _));

            // ToD's level is at the end of the file and FILE_READ has no offset: never asked for.
            var tod = new SaveFileEntry("kerwan.sav", true, false, 1, 0, (uint)SaveFilePlanet.TodSaveLength, 0);
            Assert.Empty(SaveFilesPanel.ConsolePlanetsToRead(planets, GameId.Tod, new[] { tod }));
        }
    }

    // ---------------------------------------------------------------- the setting

    [Fact]
    public void TheSortSettingIsByPlanetUntilChangedAndKeepsItsChoice()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "settings.json");

        Assert.Equal(SaveFileSort.ByPlanet, Settings.Load(path).SaveFileSort);

        var settings = Settings.Load(path);
        settings.SaveFileSort = SaveFileSort.Alphabetical;
        settings.Save();
        Assert.Equal(SaveFileSort.Alphabetical, Settings.Load(path).SaveFileSort);

        // A file from before the key, and one with a value this build does not know.
        File.WriteAllText(path, """{ "mirrorSaveFiles": false }""");
        Assert.Equal(SaveFileSort.ByPlanet, Settings.Load(path).SaveFileSort);
        File.WriteAllText(path, """{ "saveFileSort": "by colour" }""");
        Assert.Equal(SaveFileSort.ByPlanet, Settings.Load(path).SaveFileSort);
    }
}
