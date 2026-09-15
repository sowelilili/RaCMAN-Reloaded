using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The save files and the mods that sat beside the old RaCMAN's config.txt, copied into this
/// client's own two libraries. Every folder here is a temp folder: no real old RaCMAN and no real
/// data folder is ever looked at.
/// </summary>
public class LegacyLibraryImportTests : IDisposable
{
    private const string Title = "NPEA00385";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "racman-legacy-" + Guid.NewGuid().ToString("N"));

    /// <summary>The folder the old racman.exe sat in.</summary>
    private string Old => Path.Combine(_root, "old-racman");

    private string Saves => Path.Combine(_root, "data", "savefiles");

    private string Mods => Path.Combine(_root, "data", "mods");

    private string Shipped => Path.Combine(_root, "app", "mods");

    private LegacyLibraryImport.Result Run() => LegacyLibraryImport.Run(Old, Saves, Mods, Shipped);

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>One file in the old folder, by its path under it.</summary>
    private void WriteOld(string relative, string content) =>
        Write(Path.Combine(Old, relative.Replace('/', Path.DirectorySeparatorChar)), content);

    /// <summary>One file already in this client's data folder.</summary>
    private void WriteHere(string relative, string content) =>
        Write(Path.Combine(_root, "data", relative.Replace('/', Path.DirectorySeparatorChar)), content);

    private void WriteShipped(string relative, string content) =>
        Write(Path.Combine(Shipped, relative.Replace('/', Path.DirectorySeparatorChar)), content);

    private bool Here(string relative) =>
        File.Exists(Path.Combine(_root, "data", relative.Replace('/', Path.DirectorySeparatorChar)));

    private string ReadHere(string relative) =>
        File.ReadAllText(Path.Combine(_root, "data", relative.Replace('/', Path.DirectorySeparatorChar)));

    // ---------------------------------------------------------------- save files

    [Fact]
    public void EverySaveComesAcrossIntoTheSameCategory()
    {
        WriteOld($"savefiles/{Title}/any%/veldin.sav", "one");
        WriteOld($"savefiles/{Title}/any%/kerwan.sav", "two");
        WriteOld($"savefiles/{Title}/misc/novalis.sav", "three");

        var result = Run();

        Assert.Equal(3, result.Saves);
        Assert.Equal(0, result.SavesAlreadyHere);
        Assert.Equal(0, result.SavesRenamed);
        Assert.True(Here($"savefiles/{Title}/any%/veldin.sav"));
        Assert.True(Here($"savefiles/{Title}/any%/kerwan.sav"));
        Assert.True(Here($"savefiles/{Title}/misc/novalis.sav"));
        Assert.Equal("one", ReadHere($"savefiles/{Title}/any%/veldin.sav"));
    }

    [Fact]
    public void ASaveWithNoExtensionGetsOneOnTheWayIn()
    {
        // The old client took any name at all; this one needs the suffix, and so does the console.
        WriteOld($"savefiles/{Title}/any%/start of veldin", "one");

        var result = Run();

        Assert.Equal(1, result.Saves);
        Assert.Equal(1, result.SavesRenamed);
        Assert.True(Here($"savefiles/{Title}/any%/start of veldin.sav"));
    }

    [Fact]
    public void ASaveAlreadyHereUnderAnotherNameIsNotImportedTwice()
    {
        // A save is its bytes, not its name: the same file under two names is one save.
        WriteHere($"savefiles/{Title}/any%/my copy.sav", "one");
        WriteOld($"savefiles/{Title}/any%/veldin.sav", "one");

        var result = Run();

        Assert.Equal(0, result.Saves);
        Assert.Equal(1, result.SavesAlreadyHere);
        Assert.False(Here($"savefiles/{Title}/any%/veldin.sav"));
    }

    [Fact]
    public void ANameTakenByOtherBytesIsImportedBesideIt()
    {
        WriteHere($"savefiles/{Title}/any%/veldin.sav", "mine");
        WriteOld($"savefiles/{Title}/any%/veldin.sav", "theirs");

        var result = Run();

        Assert.Equal(1, result.Saves);
        Assert.Equal(1, result.SavesRenamed);
        Assert.Equal("mine", ReadHere($"savefiles/{Title}/any%/veldin.sav"));
        Assert.Equal("theirs", ReadHere($"savefiles/{Title}/any%/veldin (2).sav"));
    }

    [Fact]
    public void TwoOldSavesOfTheSameNameBothLand()
    {
        // The same name in two categories is two saves, and neither treads on the other.
        WriteOld($"savefiles/{Title}/any%/veldin.sav", "one");
        WriteOld($"savefiles/{Title}/hundred/veldin.sav", "two");

        Assert.Equal(2, Run().Saves);
        Assert.Equal("one", ReadHere($"savefiles/{Title}/any%/veldin.sav"));
        Assert.Equal("two", ReadHere($"savefiles/{Title}/hundred/veldin.sav"));
    }

    [Fact]
    public void ImportingTwiceBringsNothingOverASecondTime()
    {
        WriteOld($"savefiles/{Title}/any%/veldin", "one");
        WriteOld($"mods/{Title}/flight/patch.txt", "0x100: 0x60000000");

        var first = Run();
        var second = Run();

        Assert.Equal(1, first.Saves);
        Assert.Equal(1, first.Mods);
        Assert.Equal(0, second.Saves);
        Assert.Equal(1, second.SavesAlreadyHere);
        Assert.Equal(0, second.Mods);
        Assert.Equal(1, second.ModsAlreadyHere);
    }

    // ---------------------------------------------------------------- mods

    [Fact]
    public void AModFolderComesAcrossWholeWithItsCaves()
    {
        WriteOld($"mods/{Title}/flight/patch.txt", "0x100: cave.bin");
        WriteOld($"mods/{Title}/flight/cave.bin", "bytes");
        WriteOld($"mods/{Title}/flight/notes/readme.txt", "a folder of its own");

        var result = Run();

        Assert.Equal(1, result.Mods);
        Assert.True(Here($"mods/{Title}/flight/patch.txt"));
        Assert.True(Here($"mods/{Title}/flight/cave.bin"));
        Assert.True(Here($"mods/{Title}/flight/notes/readme.txt"));
    }

    [Fact]
    public void AModTheReleaseAlreadyShipsIsNotImported()
    {
        WriteShipped(AppPaths.ShippedModsManifest, $"{Title}\n{Title}/flight\n");
        WriteShipped($"{Title}/flight/patch.txt", "0x100: 0x60000000");
        WriteOld($"mods/{Title}/flight/patch.txt", "0x100: 0x60000000");

        var result = Run();

        Assert.Equal(0, result.Mods);
        Assert.Equal(1, result.ModsAlreadyHere);
        Assert.Equal(new[] { "flight" }, result.Shipped);
        Assert.False(Directory.Exists(Path.Combine(Mods, Title, "flight")));
    }

    [Fact]
    public void AShippedModTheUserHasEditedIsStillWorthImporting()
    {
        // Same folder name, different bytes: the user's copy is the one this client uses, so it has
        // to come over or the edit is lost.
        WriteShipped(AppPaths.ShippedModsManifest, $"{Title}/flight\n");
        WriteShipped($"{Title}/flight/patch.txt", "0x100: 0x60000000");
        WriteOld($"mods/{Title}/flight/patch.txt", "0x100: 0x38600001");

        var result = Run();

        Assert.Equal(1, result.Mods);
        Assert.Empty(result.Shipped);
        Assert.Equal("0x100: 0x38600001", ReadHere($"mods/{Title}/flight/patch.txt"));
    }

    [Fact]
    public void AModAlreadyInstalledUnderAnotherNameIsNotImportedAgain()
    {
        WriteHere($"mods/{Title}/flight-of-my-own/patch.txt", "0x100: 0x60000000");
        WriteOld($"mods/{Title}/flight/patch.txt", "0x100: 0x60000000");

        var result = Run();

        Assert.Equal(0, result.Mods);
        Assert.Equal(1, result.ModsAlreadyHere);
        Assert.Empty(result.Shipped);
        Assert.False(Directory.Exists(Path.Combine(Mods, Title, "flight")));
    }

    [Fact]
    public void AFolderNameTakenBySomethingElseIsImportedBesideIt()
    {
        // The folder name is the id qwark loads a mod by, so the two cannot share one; both are
        // kept, because there is no telling which of them the user wants.
        WriteHere($"mods/{Title}/flight/patch.txt", "mine");
        WriteOld($"mods/{Title}/flight/patch.txt", "theirs");

        var result = Run();

        Assert.Equal(1, result.Mods);
        Assert.Equal(new[] { "flight (imported)" }, result.BesideYours);
        Assert.Equal("mine", ReadHere($"mods/{Title}/flight/patch.txt"));
        Assert.Equal("theirs", ReadHere($"mods/{Title}/flight (imported)/patch.txt"));
    }

    [Fact]
    public void ASecondClashTakesTheNextNumber()
    {
        WriteHere($"mods/{Title}/flight/patch.txt", "mine");
        WriteHere($"mods/{Title}/flight (imported)/patch.txt", "an older import");
        WriteOld($"mods/{Title}/flight/patch.txt", "theirs");

        var result = Run();

        Assert.Equal(new[] { "flight (imported 2)" }, result.BesideYours);
        Assert.Equal("theirs", ReadHere($"mods/{Title}/flight (imported 2)/patch.txt"));
        Assert.Equal("an older import", ReadHere($"mods/{Title}/flight (imported)/patch.txt"));
    }

    [Fact]
    public void AModIsTheFolderAsAWholeSoAnExtraFileMakesItADifferentMod()
    {
        WriteHere($"mods/{Title}/flight/patch.txt", "0x100: cave.bin");
        WriteOld($"mods/{Title}/flight/patch.txt", "0x100: cave.bin");
        WriteOld($"mods/{Title}/flight/cave.bin", "the cave the other one has not got");

        var result = Run();

        Assert.Equal(1, result.Mods);
        Assert.True(Here($"mods/{Title}/flight (imported)/cave.bin"));
    }

    // ---------------------------------------------------------------- the edges

    [Fact]
    public void AFolderWithNeitherLibraryIsNothingToImport()
    {
        Directory.CreateDirectory(Old);
        WriteOld("config.txt", "ip = 192.168.1.50");

        var result = Run();

        Assert.False(result.Anything);
        Assert.Empty(result.Lines);
        Assert.False(Directory.Exists(Saves));
        Assert.False(Directory.Exists(Mods));
    }

    [Fact]
    public void OneHalfMissingIsNothingToImportForThatHalfOnly()
    {
        WriteOld($"savefiles/{Title}/any%/veldin.sav", "one");

        var result = Run();

        Assert.Equal(1, result.Saves);
        Assert.Equal(0, result.Mods);
        Assert.False(Directory.Exists(Mods));
    }

    [Fact]
    public void AFolderThatIsNotThereAtAllImportsNothing()
    {
        Assert.False(LegacyLibraryImport.Run(Path.Combine(_root, "nowhere"), Saves, Mods, Shipped).Anything);
        Assert.False(LegacyLibraryImport.Run(null, Saves, Mods, Shipped).Anything);
        Assert.False(LegacyLibraryImport.Run("   ", Saves, Mods, Shipped).Anything);
    }

    [Fact]
    public void NothingOutsideTheTwoFoldersIsTouchedAndTheOldFolderIsLeftAsItWas()
    {
        WriteOld("config.txt", "ip = 192.168.1.50");
        WriteOld("controllerskins/DS3 Black/skin.txt", "not ours");
        WriteOld($"savefiles/{Title}/any%/veldin.sav", "one");
        WriteOld($"mods/{Title}/flight/patch.txt", "0x100: 0x60000000");

        Run();

        Assert.True(File.Exists(Path.Combine(Old, "config.txt")));
        Assert.True(File.Exists(Path.Combine(Old, "savefiles", Title, "any%", "veldin.sav")));
        Assert.True(File.Exists(Path.Combine(Old, "mods", Title, "flight", "patch.txt")));

        // The data folder got the two libraries and not one thing more.
        Assert.Equal(
            new[] { "mods", "savefiles" },
            new DirectoryInfo(Path.Combine(_root, "data")).GetDirectories()
                .Select(d => d.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void WithNoShippedLibraryTheImportStillWorks()
    {
        WriteOld($"mods/{Title}/flight/patch.txt", "0x100: 0x60000000");

        var result = LegacyLibraryImport.Run(Old, Saves, Mods, shippedModsRoot: null);

        Assert.Equal(1, result.Mods);
        Assert.True(Here($"mods/{Title}/flight/patch.txt"));
    }

    // ---------------------------------------------------------------- what the panel shows

    [Fact]
    public void TheSurveyCountsBothLibrariesBeforeAnythingIsCopied()
    {
        WriteOld($"savefiles/{Title}/any%/veldin.sav", "one");
        WriteOld($"savefiles/{Title}/any%/kerwan.sav", "two");
        WriteOld($"savefiles/{Title}/misc/novalis.sav", "three");
        WriteOld($"mods/{Title}/flight/patch.txt", "0x100: 0x60000000");
        WriteOld($"mods/{Title}/hardcore/patch.txt", "0x100: 0x60000000");

        var found = LegacyLibraryImport.Look(Old);

        Assert.Equal(3, found.Saves);
        Assert.Equal(2, found.Categories);
        Assert.Equal(2, found.Mods);
        Assert.True(found.Anything);
        Assert.Equal("3 saves in 2 categories, 2 mods", found.Describe());

        // And nothing was copied by the looking.
        Assert.False(Directory.Exists(Saves));
    }

    [Fact]
    public void TheSurveySaysSoWhenThereIsNothingToBringOver()
    {
        Directory.CreateDirectory(Old);

        var found = LegacyLibraryImport.Look(Old);

        Assert.False(found.Anything);
        Assert.Equal("no save files or mods", found.Describe());
        Assert.Equal("no save files or mods", LegacyLibraryImport.Look(null).Describe());
    }

    [Fact]
    public void OneOfEachIsCountedInTheSingular()
    {
        WriteOld($"savefiles/{Title}/misc/veldin.sav", "one");
        WriteOld($"mods/{Title}/flight/patch.txt", "0x100: 0x60000000");

        Assert.Equal("1 save in 1 category, 1 mod", LegacyLibraryImport.Look(Old).Describe());
    }

    [Fact]
    public void TheLinesSayWhatWasImportedAndWhatWasAlreadyHere()
    {
        WriteShipped(AppPaths.ShippedModsManifest, $"{Title}/flight\n");
        WriteShipped($"{Title}/flight/patch.txt", "0x100: 0x60000000");
        WriteOld($"mods/{Title}/flight/patch.txt", "0x100: 0x60000000");
        WriteOld($"mods/{Title}/hardcore/patch.txt", "0x100: 0x38600001");
        WriteHere($"savefiles/{Title}/any%/mine.sav", "one");
        WriteOld($"savefiles/{Title}/any%/veldin", "one");
        WriteOld($"savefiles/{Title}/any%/kerwan", "two");

        var lines = Run().Lines;

        Assert.Equal(new[]
        {
            "1 save file(s), 1 renamed",
            "1 save file(s) already here",
            "1 mod(s)",
            "1 mod(s) already here (this client ships flight)",
        }, lines);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not a test failure.
        }

        GC.SuppressFinalize(this);
    }
}
