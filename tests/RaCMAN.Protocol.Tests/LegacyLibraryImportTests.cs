using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The save files and the mods that sat beside the old RaCMAN's config.txt, copied into this
/// client's own two libraries. Every folder here is a temp folder: no real old RaCMAN and no real
/// data folder is ever looked at.
/// <para>
/// The exclusion list is the tests' own except where the shipped one is what is being checked, so
/// a change to the file this release carries cannot quietly change what these mean.
/// </para>
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

    private LegacyLibraryImport.Result Run() => Run(LegacyModExclusions.None);

    private LegacyLibraryImport.Result Run(LegacyModExclusions exclusions) =>
        LegacyLibraryImport.Run(Old, Saves, Mods, Shipped, exclusions);

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

    // ---------------------------------------------------------------- what is left behind

    /// <summary>A short list of the shape the shipped one has, for the tests that need one.</summary>
    private static LegacyModExclusions Excluding(params string[] lines) =>
        LegacyModExclusions.Parse(string.Join("\n", lines));

    [Fact]
    public void AModOnTheListIsLeftWhereItIsAndSaysWhy()
    {
        WriteOld($"mods/{Title}/quartu-grinder/patch.txt", "0x100: 0x60000000");
        WriteOld($"mods/{Title}/hardcore/patch.txt", "0x100: 0x38600001");

        var result = Run(Excluding($"{Title}/quartu-grinder   retired"));

        Assert.Equal(1, result.Mods);
        Assert.False(Directory.Exists(Path.Combine(Mods, Title, "quartu-grinder")));
        Assert.True(Here($"mods/{Title}/hardcore/patch.txt"));

        var left = Assert.Single(result.Excluded);
        Assert.Equal(Title, left.TitleId);
        Assert.Equal("quartu-grinder", left.DirName);
        Assert.Equal("retired", left.Reason);
        Assert.Contains("excluded: quartu-grinder (retired)", result.Lines);
    }

    [Fact]
    public void AnExcludedModIsNeverCountedAsOneAlreadyHere()
    {
        // The exclusion is asked first, so a copy of it in this client's own folder — from an
        // import before the list named it — does not turn it into a duplicate.
        WriteHere($"mods/{Title}/quartu-grinder/patch.txt", "0x100: 0x60000000");
        WriteOld($"mods/{Title}/quartu-grinder/patch.txt", "0x100: 0x60000000");

        var result = Run(Excluding($"{Title}/quartu-grinder   retired"));

        Assert.Equal(0, result.Mods);
        Assert.Equal(0, result.ModsAlreadyHere);
        Assert.Equal("retired", Assert.Single(result.Excluded).Reason);
    }

    [Fact]
    public void AModWithAnAutomationLineNeedsLuaAndStaysWhereItIs()
    {
        // The same test publish.ps1 makes on the shipped library: an "automation:" line.
        WriteOld($"mods/{Title}/flight/patch.txt", "#- name: Flight\n  automation : main.lua\n0x100: 0x60000000");

        var result = Run();

        Assert.Equal(0, result.Mods);
        Assert.False(Directory.Exists(Path.Combine(Mods, Title, "flight")));
        Assert.Equal("needs Lua", Assert.Single(result.Excluded).Reason);
    }

    [Fact]
    public void AModCarryingALuaFileNeedsLuaHoweverDeepItIs()
    {
        WriteOld($"mods/{Title}/randomizer/patch.txt", "0x100: 0x60000000");
        WriteOld($"mods/{Title}/randomizer/scripts/logic/main.lua", "-- two folders down");

        var result = Run();

        Assert.Equal(0, result.Mods);
        Assert.Equal("needs Lua", Assert.Single(result.Excluded).Reason);
    }

    [Fact]
    public void AnAutomationWordThatIsNotTheLineDoesNotCount()
    {
        WriteOld($"mods/{Title}/hardcore/patch.txt", "#- description: no automation here\n0x100: 0x60000000");

        Assert.Equal(1, Run().Mods);
    }

    [Fact]
    public void ASavefileManagerIsKnownByWhatItsPatchFileCallsIt()
    {
        // Several old releases shipped one of these under a folder name of their own; the name line
        // is what they have in common.
        WriteOld($"mods/{Title}/gigahelper/patch.txt", "#- name: Savefile Manager\n#- version: 1.1\n0x100: rack.bin");
        WriteOld($"mods/{Title}/my-old-helper/patch.txt", "#- name: RaC1 Savefile helper\n0x100: tramp.bin");

        var result = Run();

        Assert.Equal(0, result.Mods);
        Assert.Equal(new[] { "built into qwark", "built into qwark" }, result.Excluded.Select(m => m.Reason).ToArray());
        Assert.Contains("excluded: gigahelper and my-old-helper (built into qwark)", result.Lines);
    }

    [Fact]
    public void ASavefileHelperIsKnownByItsFolderNameToo()
    {
        // A patch.txt with no name line at all, under the folder names the helpers were kept in.
        WriteOld($"mods/{Title}/sfhelper/patch.txt", "0x100: tramp.bin");
        WriteOld($"mods/{Title}/rc9-save/patch.txt", "0x100: rack.bin");
        WriteOld($"mods/{Title}/savefile-thing/patch.txt", "0x100: rack.bin");

        var result = Run();

        Assert.Equal(0, result.Mods);
        Assert.Equal(3, result.Excluded.Count);
        Assert.All(result.Excluded, mod => Assert.Equal("built into qwark", mod.Reason));
    }

    [Fact]
    public void TheOldLuaLibraryFolderIsNotATitle()
    {
        WriteOld("mods/libs/standard/middleclass.lua", "-- the shared helpers");
        WriteOld($"mods/{Title}/hardcore/patch.txt", "0x100: 0x60000000");

        var result = Run();

        Assert.Equal(1, result.Mods);
        Assert.Empty(result.Excluded);
        Assert.False(Directory.Exists(Path.Combine(Mods, "libs")));
    }

    [Fact]
    public void AFolderWithNoPatchFileIsNotAModAndIsNotWorthAWord()
    {
        // The old library keeps the source a few mods were built from beside the mods themselves.
        WriteOld($"mods/{Title}/new_barlow_trainer/Makefile", "all:");
        WriteOld($"mods/{Title}/new_barlow_trainer/src/main.c", "int main(void) { return 0; }");
        WriteOld($"mods/{Title}/hardcore/patch.txt", "0x100: 0x60000000");

        var result = Run();

        Assert.Equal(1, result.Mods);
        Assert.Empty(result.Excluded);
        Assert.False(Directory.Exists(Path.Combine(Mods, Title, "new_barlow_trainer")));
    }

    [Fact]
    public void AModOnNoListAndWithNoLuaComesOverAsItAlwaysDid()
    {
        WriteOld($"mods/{Title}/hardcore/patch.txt", "#- name: Hardcore\n0x100: 0x38600001");

        var result = Run(LegacyModExclusions.Shipped);

        Assert.Equal(1, result.Mods);
        Assert.Empty(result.Excluded);
        Assert.True(Here($"mods/{Title}/hardcore/patch.txt"));
    }

    [Fact]
    public void TheExcludedLineGathersTheModsUnderTheirReasons()
    {
        // Built by hand rather than walked, so the line is the format and not the order a
        // filesystem happens to hand the folders over in.
        var result = LegacyLibraryImport.Result.Nothing with
        {
            Excluded = new[]
            {
                new LegacyLibraryImport.ExcludedMod(Title, "sfhelper", "built into qwark"),
                new LegacyLibraryImport.ExcludedMod("NPEA00386", "rc2-save", "built into qwark"),
                new LegacyLibraryImport.ExcludedMod(Title, "flight", "needs Lua"),
                new LegacyLibraryImport.ExcludedMod("NPEA00386", "flight", "needs Lua"),
                new LegacyLibraryImport.ExcludedMod("NPEA00386", "ohko", "needs Lua"),
                new LegacyLibraryImport.ExcludedMod("NPEA00387", "coolcam", "not shipped"),
            },
        };

        Assert.Equal(
            "excluded: sfhelper and rc2-save (built into qwark), flight and ohko (need Lua), coolcam (not shipped)",
            Assert.Single(result.Lines));
    }

    // ---------------------------------------------------------------- the shipped list

    /// <summary>The file this release carries, read out of the source tree as the app would.</summary>
    private static string ShippedListPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName, "src", "RaCMAN.App", "data", LegacyModExclusions.FileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "data", LegacyModExclusions.FileName);
    }

    [Fact]
    public void TheShippedListReadsAndEveryEntryIsATitleAndAFolder()
    {
        var shipped = LegacyModExclusions.Load(ShippedListPath());

        Assert.Null(shipped.Problem);
        Assert.NotEmpty(shipped.Entries);

        foreach (var entry in shipped.Entries)
        {
            Assert.Matches(@"^[A-Z]{4}[0-9]{5}/[^/]+$", entry);

            var parts = entry.Split('/');
            Assert.NotNull(shipped.Reason(parts[0], parts[1]));
        }

        // The helpers qwark carries itself, and the one this client never shipped.
        Assert.Equal("built into qwark", shipped.Reason("NPEA00385", "sfhelper"));
        Assert.Equal("built into qwark", shipped.Reason("NPEA00386", "rc2-save"));
        Assert.Equal("superseded by barlow_joba_trainer", shipped.Reason("NPEA00386", "joba_trainer"));
        Assert.Equal("not shipped", shipped.Reason("NPEA00387", "coolcam"));
    }

    [Fact]
    public void TheListIsReadByTitleAndFolderWhateverTheCase()
    {
        var exclusions = Excluding(
            "# a comment, and a blank line",
            string.Empty,
            "NPEA00385/sfhelper          built into qwark",
            "NPEA00387/coolcam\tnot shipped",
            "NPEA00423/odd-one");

        Assert.Equal(3, exclusions.Count);
        Assert.Equal("built into qwark", exclusions.Reason("npea00385", "SFHELPER"));
        Assert.True(exclusions.Excludes("NPEA00387", "coolcam"));
        Assert.Equal(LegacyModExclusions.DefaultReason, exclusions.Reason("NPEA00423", "odd-one"));
        Assert.Null(exclusions.Reason("NPEA00385", "hardcore"));
    }

    [Fact]
    public void NoListFileMeansNothingIsExcludedByNameAndSaysSo()
    {
        var missing = LegacyModExclusions.Load(Path.Combine(_root, "nowhere", LegacyModExclusions.FileName));

        Assert.Equal(0, missing.Count);
        Assert.NotNull(missing.Problem);
        Assert.Contains(LegacyModExclusions.FileName, missing.Problem);

        // And the rules that do not need it still hold.
        WriteOld($"mods/{Title}/sfhelper/patch.txt", "0x100: tramp.bin");
        Assert.Equal(0, Run(missing).Mods);
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
        Assert.False(LegacyLibraryImport.Run(Path.Combine(_root, "nowhere"), Saves, Mods, Shipped, LegacyModExclusions.None).Anything);
        Assert.False(LegacyLibraryImport.Run(null, Saves, Mods, Shipped, LegacyModExclusions.None).Anything);
        Assert.False(LegacyLibraryImport.Run("   ", Saves, Mods, Shipped, LegacyModExclusions.None).Anything);
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

        var result = LegacyLibraryImport.Run(Old, Saves, Mods, shippedModsRoot: null, LegacyModExclusions.None);

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

        var found = LegacyLibraryImport.Look(Old, LegacyModExclusions.None);

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

        var found = LegacyLibraryImport.Look(Old, LegacyModExclusions.None);

        Assert.False(found.Anything);
        Assert.Equal("no save files or mods", found.Describe());
        Assert.Equal("no save files or mods", LegacyLibraryImport.Look(null, LegacyModExclusions.None).Describe());
    }

    [Fact]
    public void TheSurveyCountsTheModsThatComeOverApartFromTheOnesThatDoNot()
    {
        WriteOld($"savefiles/{Title}/any%/veldin.sav", "one");
        WriteOld($"mods/{Title}/hardcore/patch.txt", "0x100: 0x38600001");
        WriteOld($"mods/{Title}/flight/patch.txt", "automation: main.lua");
        WriteOld($"mods/{Title}/sfhelper/patch.txt", "0x100: tramp.bin");
        WriteOld($"mods/{Title}/quartu-grinder/patch.txt", "0x100: 0x60000000");

        // Neither of these is a mod, so neither is on either side of the count.
        WriteOld($"mods/{Title}/new_barlow_trainer/Makefile", "all:");
        WriteOld("mods/libs/standard/middleclass.lua", "-- the shared helpers");

        var found = LegacyLibraryImport.Look(Old, Excluding($"{Title}/quartu-grinder   retired"));

        Assert.Equal(1, found.Mods);
        Assert.Equal(3, found.Excluded);
        Assert.Equal("1 save in 1 category, 1 mod, 3 excluded", found.Describe());
        Assert.Equal("4 mods, 6 excluded", new LegacyLibraryImport.Survey(0, 0, 4, 6).Describe());
    }

    [Fact]
    public void OneOfEachIsCountedInTheSingular()
    {
        WriteOld($"savefiles/{Title}/misc/veldin.sav", "one");
        WriteOld($"mods/{Title}/flight/patch.txt", "0x100: 0x60000000");

        Assert.Equal("1 save in 1 category, 1 mod", LegacyLibraryImport.Look(Old, LegacyModExclusions.None).Describe());
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
