using System.Buffers.Binary;
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
