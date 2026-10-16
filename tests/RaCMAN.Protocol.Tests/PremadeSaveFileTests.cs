using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The premade savefiles a release ships: the "(premade savefiles)" category at the bottom of a
/// game's list, its rows, a load of one against the fake console, the copy into the user's own
/// library, and the refusals that keep them read-only. The shipped folder is a temp folder whose
/// files are marked read-only, and every test that uses one checks it is exactly as it was.
/// </summary>
public class PremadeSaveFileTests : IDisposable
{
    private const string Title = "NPEA00385";

    private const string Premade = PremadeSaveFiles.Category;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "racman-premade-" + Guid.NewGuid().ToString("N"));

    private string Shipped => Path.Combine(_root, "app", PremadeSaveFiles.FolderName);

    private string Library => Path.Combine(_root, "data", "savefiles");

    private static byte[] Pattern(int size, int seed = 0)
    {
        var bytes = new byte[size];
        for (int i = 0; i < size; i++) bytes[i] = (byte)(i * 7 + (i >> 8) + seed);
        return bytes;
    }

    /// <summary>Puts a file in the shipped folder the way a release would, read-only.</summary>
    private void Ship(string title, string name, byte[] data, bool hidden = false)
    {
        var folder = Path.Combine(Shipped, title);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, data);
        File.SetAttributes(path, FileAttributes.ReadOnly | (hidden ? FileAttributes.Hidden : 0));
    }

    /// <summary>Every file under the shipped folder with its length, its write time and its bytes.</summary>
    private SortedDictionary<string, (long Length, DateTime Written, uint Crc)> ShippedSnapshot()
    {
        var files = new SortedDictionary<string, (long, DateTime, uint)>(StringComparer.Ordinal);
        if (!Directory.Exists(Shipped)) return files;

        foreach (var path in Directory.EnumerateFiles(Shipped, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            files[Path.GetRelativePath(Shipped, path)] =
                (info.Length, info.LastWriteTimeUtc, Crc32.Compute(File.ReadAllBytes(path)));
        }

        return files;
    }

    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync()
    {
        var server = new FakeQwarkServer();
        server.Start();
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    // ---------------------------------------------------------------- the category

    [Fact]
    public void ThePremadeCategoryIsLastAndOnlyForAGameThatHasSome()
    {
        Assert.Equal(new[] { "any%", "zebra", SaveFileLibrary.DefaultCategory, Premade },
            SaveFilesPanel.Merge(new[] { "zebra", SaveFileLibrary.DefaultCategory, "any%" }, null, premade: true));

        Assert.Equal(new[] { "any%", "zebra", SaveFileLibrary.DefaultCategory },
            SaveFilesPanel.Merge(new[] { "zebra", SaveFileLibrary.DefaultCategory, "any%" }, null, premade: false));

        // A game with no categories of the user's still has somewhere to save, above the premade.
        Assert.Equal(new[] { SaveFileLibrary.DefaultCategory, Premade }, SaveFilesPanel.Merge(null, null, premade: true));
        Assert.Equal(new[] { SaveFileLibrary.DefaultCategory }, SaveFilesPanel.Merge(null, null));
    }

    [Fact]
    public void TheConsolesFolderForThemIsNeverOneOfTheUsersCategories()
    {
        // The console has a folder of that name once a premade savefile has been loaded, and the
        // user's library might have one made by hand: neither is a category of the user's.
        var local = new[] { "any%", "(PREMADE SAVEFILES)" };
        var console = new[] { Premade, "hundred" };

        Assert.Equal(new[] { "any%", "hundred" }, SaveFilesPanel.Merge(local, console, premade: false));
        Assert.Equal(new[] { "any%", "hundred", Premade }, SaveFilesPanel.Merge(local, console, premade: true));

        // Merging the combo's own list again, as the console's answer does, keeps it once and last.
        var combo = SaveFilesPanel.Merge(local, null, premade: true);
        Assert.Equal(new[] { "any%", "hundred", Premade }, SaveFilesPanel.Merge(combo, console, premade: true));

        // Remembered as the last category, it is selected again on the way back.
        Assert.Equal(2, SaveFilesPanel.IndexOf(SaveFilesPanel.Merge(combo, console, premade: true), Premade));
    }

    [Fact]
    public void AGameHasPremadeSavefilesOnlyWhenItsFolderHoldsASave()
    {
        Assert.False(new PremadeSaveFiles(null).Has(Title));
        Assert.Empty(new PremadeSaveFiles(null).FilesWithCrc(Title));

        var premade = new PremadeSaveFiles(Shipped);
        Assert.False(premade.Has(Title));

        Directory.CreateDirectory(Path.Combine(Shipped, Title));
        File.WriteAllText(Path.Combine(Shipped, Title, "readme.txt"), "not a save");
        Assert.False(premade.Has(Title));

        Ship(Title, "NG+.sav", Pattern(64));
        Assert.True(premade.Has(Title));
        Assert.False(premade.Has("NPEA00386"));
    }

    [Fact]
    public void ThePremadeFilesAreListedWithTheirSizesAndCrcs()
    {
        var plus = Pattern(64);
        var noLc = Pattern(64, seed: 1);
        Ship(Title, "NG+.sav", plus);
        Ship(Title, "NG+ no LC.sav", noLc);
        Ship(Title, "notes.txt", Pattern(8));
        Ship(Title, ".secret.sav", Pattern(64), hidden: true);

        var premade = new PremadeSaveFiles(Shipped);
        Assert.Equal(new[] { "NG+ no LC.sav", "NG+.sav" }, premade.Files(Title));

        var rows = SaveFilesPanel.Rows(Premade, null, premade.FilesWithCrc(Title));
        Assert.Equal(new[] { "NG+ no LC", "NG+" }, rows.Select(r => r.DisplayName));
        Assert.All(rows, row => Assert.Equal(SaveFileLocation.Pc, row.Location));
        Assert.Equal(Crc32.Compute(noLc), rows[0].PcCrc);
        Assert.Equal(Crc32.Compute(plus), rows[1].PcCrc);
        Assert.Equal(64, rows[1].PcSize);
    }

    [Fact]
    public void AConsoleCopyMakesARowBothAndAFileTheReleaseDoesNotShipIsNotListed()
    {
        var plus = Pattern(64);
        var console = new[]
        {
            new ConsoleSaveFile("NG+.sav", 64, Crc32.Compute(plus)),
            new ConsoleSaveFile("an older release's.sav", 64, 0x1234u),
        };

        var row = Assert.Single(SaveFilesPanel.Rows(Premade, console,
            new[] { new LocalSaveFile("NG+.sav", 64, Crc32.Compute(plus)) }));
        Assert.Equal("NG+.sav", row.Name);
        Assert.True(row.Agrees);

        // Any other category still shows what only the console has.
        Assert.Equal(2, SaveFilesPanel.Rows("misc", console,
            new[] { new LocalSaveFile("NG+.sav", 64, Crc32.Compute(plus)) }).Length);
    }

    [Fact]
    public void ANameTheListingDoesNotHaveIsNotRead()
    {
        Ship(Title, "NG+.sav", Pattern(64));
        Ship("NPEA00386", "other.sav", Pattern(64));
        var premade = new PremadeSaveFiles(Shipped);

        Assert.Throws<FileNotFoundException>(() => premade.Read(Title, "../NPEA00386/other.sav"));
        Assert.Throws<FileNotFoundException>(() => premade.Read(Title, "missing.sav"));
        Assert.Equal(Pattern(64), premade.Read(Title, "NG+.sav"));
    }

    // ---------------------------------------------------------------- read-only

    [Fact]
    public void ThePanelOffersNoRenameDeleteOrSaveForThem()
    {
        Assert.True(SaveFilesPanel.IsReadOnly(Premade));
        Assert.True(SaveFilesPanel.IsReadOnly(" (Premade Savefiles) "));
        Assert.False(SaveFilesPanel.IsReadOnly(SaveFileLibrary.DefaultCategory));
        Assert.False(SaveFilesPanel.IsReadOnly("premade"));

        // Nor can the user make a category of their own with that name.
        Assert.False(SaveFilesPanel.CanCreateCategory(Premade));
        Assert.False(SaveFilesPanel.CanCreateCategory("  (premade SAVEFILES)  "));
        Assert.False(SaveFilesPanel.CanCreateCategory("   "));
        Assert.True(SaveFilesPanel.CanCreateCategory("any%"));
    }

    [Fact]
    public async Task RenameDeleteAndStoreAreRefusedAndTouchNothing()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            Ship(Title, "NG+.sav", Pattern(server.SaveFileBuffer.Length));
            var premade = new PremadeSaveFiles(Shipped);
            var library = new SaveFileLibrary(Library);
            var before = ShippedSnapshot();

            // Loaded once, so the console holds a copy a rename or a delete would act on.
            var entry = Assert.Single(SaveFilesPanel.Rows(Premade, null, premade.FilesWithCrc(Title)));
            await SaveFileTransfer.LoadAsync(client, premade, Title, Premade, entry);
            entry = Assert.Single(SaveFilesPanel.Rows(Premade,
                await client.SaveFileListAsync(Premade), premade.FilesWithCrc(Title)));
            Assert.Equal(SaveFileLocation.Both, entry.Location);

            var consolePath = QwarkClient.SaveFileConsolePath(Title, Premade, "NG+.sav");
            var consoleBefore = server.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            int requests = server.SaveFileLibraryOrder.Count;

            var rename = await Assert.ThrowsAsync<IOException>(
                () => SaveFileTransfer.RenameAsync(client, library, Title, Premade, entry, "mine.sav"));
            Assert.Contains("read-only", rename.Message);

            await Assert.ThrowsAsync<IOException>(
                () => SaveFileTransfer.DeleteAsync(client, library, Title, Premade, entry));

            await Assert.ThrowsAsync<IOException>(
                () => SaveFileTransfer.StoreAsync(client, library, Title, Premade, "mine.sav", mirror: true));

            await Assert.ThrowsAsync<IOException>(
                () => SaveFileTransfer.SaveToLibraryAsync(client, 0, library, Title, Premade, "mine.sav"));

            // Nothing reached the console, nothing changed on it, and nothing was written here.
            Assert.Equal(requests, server.SaveFileLibraryOrder.Count);
            Assert.Equal(consoleBefore, server.Files.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.True(server.Files.ContainsKey(consolePath));
            Assert.False(Directory.Exists(Library));
            Assert.Equal(before, ShippedSnapshot());
        }
    }

    // ---------------------------------------------------------------- loading

    [Fact]
    public async Task APremadeSavefileLoadsLikeAnyOtherAndLeavesTheShippedFolderAlone()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var data = Pattern(server.SaveFileBuffer.Length);
            Ship(Title, "NG+.sav", data);
            var premade = new PremadeSaveFiles(Shipped);
            var before = ShippedSnapshot();

            var entry = Assert.Single(SaveFilesPanel.Rows(Premade, null, premade.FilesWithCrc(Title)));
            Assert.Equal(SaveFileLoadPlan.UploadThenRestore, SaveFileMerge.PlanLoad(entry));

            // The first load uploads it into the console's category of the same name, then restores.
            Assert.True(await SaveFileTransfer.LoadAsync(client, premade, Title, Premade, entry));
            Assert.Equal(data, server.LoadedSaveFile);
            Assert.Equal($"restore {Premade}/NG+.sav", server.SaveFileLibraryOrder[^1]);
            Assert.Equal(Crc32.ToSumText(Crc32.Compute(data)), System.Text.Encoding.ASCII.GetString(
                server.Files[QwarkClient.SaveFileConsolePath(Title, Premade, "NG+.sav") + QwarkClient.SaveFileSumExtension]));

            // From then on the console has it, the row says both, and a load sends nothing.
            var again = Assert.Single(SaveFilesPanel.Rows(Premade,
                await client.SaveFileListAsync(Premade), premade.FilesWithCrc(Title)));
            Assert.True(again.Agrees);
            Assert.Equal(SaveFileLoadPlan.Restore, SaveFileMerge.PlanLoad(again));

            server.SaveFileOrder.Clear();
            Assert.False(await SaveFileTransfer.LoadAsync(client, premade, Title, Premade, again));
            Assert.Empty(server.SaveFileOrder);
            Assert.Equal(data, server.LoadedSaveFile);

            // The console's folder for them is the premade category's, once and last.
            var categories = await client.SaveFileCategoriesAsync();
            Assert.Contains(Premade, categories);
            Assert.Equal(new[] { SaveFileLibrary.DefaultCategory, Premade },
                SaveFilesPanel.Merge(null, categories, premade: true));

            Assert.Equal(before, ShippedSnapshot());
            Assert.False(Directory.Exists(Library));
        }
    }

    // ---------------------------------------------------------------- copying

    [Fact]
    public void ACopyGoesIntoTheUsersLibraryAndNeverOverAnotherSave()
    {
        var plus = Pattern(64);
        Ship(Title, "NG+.sav", plus);
        var premade = new PremadeSaveFiles(Shipped);
        var library = new SaveFileLibrary(Library);
        var before = ShippedSnapshot();

        var copy = premade.CopyTo(library, Title, "NG+.sav", SaveFileLibrary.DefaultCategory);
        Assert.Equal(new PremadeCopy(SaveFileLibrary.DefaultCategory, "NG+.sav", AlreadyThere: false), copy);
        Assert.Equal(plus, library.Read(Title, SaveFileLibrary.DefaultCategory, "NG+.sav"));

        // The same bytes again are not copied twice.
        Assert.True(premade.CopyTo(library, Title, "NG+.sav", SaveFileLibrary.DefaultCategory).AlreadyThere);
        Assert.Single(library.Files(Title, SaveFileLibrary.DefaultCategory));

        // A save of the user's own with that name is left alone, and the copy lands beside it.
        var mine = Pattern(64, seed: 9);
        library.Write(Title, "any%", "NG+.sav", mine);
        var beside = premade.CopyTo(library, Title, "NG+.sav", "any%");
        Assert.Equal(new PremadeCopy("any%", "NG+ (2).sav", AlreadyThere: false), beside);
        Assert.Equal(mine, library.Read(Title, "any%", "NG+.sav"));
        Assert.Equal(plus, library.Read(Title, "any%", "NG+ (2).sav"));
        Assert.Equal(new PremadeCopy("any%", "NG+ (2).sav", AlreadyThere: true),
            premade.CopyTo(library, Title, "NG+.sav", "any%"));

        // Never into the premade category, and never a file the release does not ship.
        Assert.Throws<IOException>(() => premade.CopyTo(library, Title, "NG+.sav", Premade));
        Assert.Throws<FileNotFoundException>(() => premade.CopyTo(library, Title, "nope.sav", "any%"));

        Assert.Equal(before, ShippedSnapshot());
    }

    [Fact]
    public void TheCopyMenuOffersTheUsersCategoriesAndAlwaysTheDefault()
    {
        Assert.Equal(new[] { "any%", SaveFileLibrary.DefaultCategory },
            SaveFilesPanel.CopyTargets(new[] { "any%", Premade }));
        Assert.Equal(new[] { "zebra", SaveFileLibrary.DefaultCategory },
            SaveFilesPanel.CopyTargets(new[] { Premade, SaveFileLibrary.DefaultCategory, "zebra" }));
        Assert.Equal(new[] { SaveFileLibrary.DefaultCategory }, SaveFilesPanel.CopyTargets(null));
    }

    [Fact]
    public void TheCopyToastSaysWhereItWentAndUnderWhatName()
    {
        var entry = new SaveFileEntry("NG+.sav", false, true, 0, 1, 0, 64);

        Assert.Equal("Copied NG+ to misc",
            SaveFilesPanel.CopyToast(entry, new PremadeCopy("misc", "NG+.sav", false)));
        Assert.Equal("Copied NG+ to any% as NG+ (2)",
            SaveFilesPanel.CopyToast(entry, new PremadeCopy("any%", "NG+ (2).sav", false)));
        Assert.Equal("NG+ is already in misc as NG+",
            SaveFilesPanel.CopyToast(entry, new PremadeCopy("misc", "NG+.sav", true)));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder is not a test failure.
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The premade savefiles this repository ships, as publish.ps1 stages them: the six the release is
/// meant to carry, each one a whole save of its game, and nothing else in the folder.
/// </summary>
public class ShippedPremadeSaveFileTests
{
    private static string LibraryPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, PremadeSaveFiles.FolderName);
            if (Directory.Exists(Path.Combine(candidate, "NPEA00385"))) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"the repository's {PremadeSaveFiles.FolderName}/ folder");
    }

    /// <summary>Title, file, and the length of that game's save.</summary>
    private static readonly (string Title, string Name, long Size)[] Expected =
    {
        ("NPEA00385", "NG+.sav", 720_896),
        ("NPEA00386", "NG+ & NG+ no IMG.sav", 2_097_152),
        ("NPEA00387", "NG+ no QE.sav", 2_097_152),
        ("NPEA00387", "NG+.sav", 2_097_152),
        ("NPEA00423", "NG+ no LC.sav", 1_048_576),
        ("NPEA00423", "NG+.sav", 1_048_576),
    };

    [Fact]
    public void TheRepositoryShipsTheSixPremadeSavefilesAndNothingElse()
    {
        string root = LibraryPath();

        var everything = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            Expected.Select(e => $"{e.Title}/{e.Name}").OrderBy(path => path, StringComparer.Ordinal),
            everything);
    }

    [Fact]
    public void EachIsAWholeSaveOfItsGame()
    {
        var premade = new PremadeSaveFiles(LibraryPath());

        foreach (var (title, name, size) in Expected)
        {
            var file = Assert.Single(premade.FilesWithCrc(title), f => f.Name == name);
            Assert.Equal(size, file.Size);
        }
    }
}
