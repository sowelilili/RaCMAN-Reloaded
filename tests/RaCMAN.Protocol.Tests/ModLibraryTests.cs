using System.IO.Compression;
using System.Text;

namespace RaCMAN.Protocol.Tests;

public class ModLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "racman-reloaded-tests", Guid.NewGuid().ToString("N"));

    private const string TitleId = "NPEA00386";

    private string MakeModFolder(string parent, string dirName, string version, bool withLua = false)
    {
        var folder = Path.Combine(parent, dirName);
        Directory.CreateDirectory(folder);

        var patch = new StringBuilder()
            .Append("#- name: Bolt mod\n")
            .Append($"#- version: {version}\n")
            .Append("#- author: someone\n")
            .Append("#- description: Gives you bolts.\n")
            .Append("#- href: https://example.invalid/mod\n")
            .Append("# a plain comment\n")
            .Append("0x1B0000: 0x60000000\n")
            .Append("0x1B0004: 0x38600001\n")
            .Append("0x1C0000: cave.bin\n");

        if (withLua) patch.Append("automation: script.lua\n");

        File.WriteAllText(Path.Combine(folder, "patch.txt"), patch.ToString());
        File.WriteAllBytes(Path.Combine(folder, "cave.bin"), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        if (withLua) File.WriteAllText(Path.Combine(folder, "script.lua"), "-- nothing\n");
        return folder;
    }

    [Fact]
    public void ScanReadsMetadataPatchWordsAndCaves()
    {
        var library = new ModLibrary(_root);
        MakeModFolder(Path.Combine(_root, TitleId), "bolt-mod", "1.2.0");

        var mod = Assert.Single(library.Scan(TitleId));
        Assert.Equal("Bolt mod", mod.Name);
        Assert.Equal("1.2.0", mod.Version);
        Assert.Equal("someone", mod.Author);
        Assert.Equal("Gives you bolts.", mod.Description);
        Assert.Equal("https://example.invalid/mod", mod.Link);
        Assert.Equal(2, mod.PatchWordCount);
        Assert.Equal(new[] { "cave.bin" }, mod.BinFiles);
        Assert.False(mod.NeedsLua);
        Assert.NotEqual(0u, mod.Hash);
    }

    [Fact]
    public void AnAutomationLineMarksTheModAsNeedingLua()
    {
        var library = new ModLibrary(_root);
        MakeModFolder(Path.Combine(_root, TitleId), "flight", "1.0.0", withLua: true);

        var mod = Assert.Single(library.Scan(TitleId));
        Assert.True(mod.NeedsLua);
    }

    [Fact]
    public void HashCoversPatchTxtThenTheBinsInPatchOrder()
    {
        var library = new ModLibrary(_root);
        var folder = MakeModFolder(Path.Combine(_root, TitleId), "bolt-mod", "1.0.0");
        var mod = Assert.Single(library.Scan(TitleId));

        uint expected = Crc32.Finish(Crc32.Update(
            Crc32.Update(Crc32.Start(), File.ReadAllBytes(Path.Combine(folder, "patch.txt"))),
            File.ReadAllBytes(Path.Combine(folder, "cave.bin"))));
        Assert.Equal(expected, mod.Hash);

        // Touching a cave changes the hash, which is what makes the console re-upload.
        File.WriteAllBytes(Path.Combine(folder, "cave.bin"), new byte[] { 1, 2, 3, 4, 5 });
        Assert.NotEqual(mod.Hash, ModLibrary.ComputeHash(mod));
    }

    [Fact]
    public void InvisibleModsAreSkipped()
    {
        var library = new ModLibrary(_root);
        var folder = MakeModFolder(Path.Combine(_root, TitleId), "hidden", "1.0.0");
        File.AppendAllText(Path.Combine(folder, "patch.txt"), "#- visible: false\n");

        Assert.Empty(library.Scan(TitleId));
    }

    [Fact]
    public void FolderWithoutPatchTxtIsNotAMod()
    {
        Directory.CreateDirectory(Path.Combine(_root, TitleId, "empty"));
        Assert.Empty(new ModLibrary(_root).Scan(TitleId));
        Assert.Null(ModLibrary.Read(Path.Combine(_root, TitleId, "empty")));
    }

    [Fact]
    public void ZipInstallFindsTheFirstFolderWithAPatchFile()
    {
        var library = new ModLibrary(_root);
        var zip = BuildZip("bolt-mod", "1.0.0", extraEmptyFolder: true);

        using (var candidate = library.OpenZip(zip, TitleId))
        {
            Assert.Equal(ZipInstallKind.New, candidate.Kind);
            Assert.False(candidate.NeedsConfirmation);
            Assert.Equal("bolt-mod", candidate.Mod.DirName);

            var installed = library.CommitZip(candidate, TitleId);
            Assert.Equal("Bolt mod", installed.Name);
        }

        var mod = Assert.Single(library.Scan(TitleId));
        Assert.Equal("1.0.0", mod.Version);
        Assert.True(File.Exists(Path.Combine(_root, TitleId, "bolt-mod", "cave.bin")));
    }

    [Fact]
    public void ANewerZipIsAnUpgradeAndAnOlderOneNeedsConfirmation()
    {
        var library = new ModLibrary(_root);
        MakeModFolder(Path.Combine(_root, TitleId), "bolt-mod", "1.0.0");

        using (var newer = library.OpenZip(BuildZip("bolt-mod", "2.0.0"), TitleId))
        {
            Assert.Equal(ZipInstallKind.Upgrade, newer.Kind);
            Assert.False(newer.NeedsConfirmation);
            library.CommitZip(newer, TitleId);
        }

        Assert.Equal("2.0.0", Assert.Single(library.Scan(TitleId)).Version);

        using var older = library.OpenZip(BuildZip("bolt-mod", "1.5.0"), TitleId);
        Assert.Equal(ZipInstallKind.Downgrade, older.Kind);
        Assert.True(older.NeedsConfirmation);

        using var same = library.OpenZip(BuildZip("bolt-mod", "2.0.0"), TitleId);
        Assert.Equal(ZipInstallKind.Replace, same.Kind);
        Assert.True(same.NeedsConfirmation);
    }

    [Fact]
    public void AZipWithoutAPatchFileIsRejected()
    {
        var library = new ModLibrary(_root);
        var staging = Path.Combine(_root, "staging-bad", "junk");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "readme.txt"), "not a mod");

        var zip = Path.Combine(_root, "bad.zip");
        ZipFile.CreateFromDirectory(Path.Combine(_root, "staging-bad"), zip);

        Assert.Throws<InvalidDataException>(() => library.OpenZip(zip, TitleId));
    }

    [Fact]
    public void ConsoleFolderFollowsTheDocumentedLayout()
    {
        Assert.Equal("/dev_hdd0/qwark/mods/NPEA00385/crash-patch", ModLibrary.ConsoleFolder("NPEA00385", "crash-patch"));
    }

    // ---------------------------------------------------------------- shipped and user libraries

    /// <summary>The two roots the client runs with: the data folder's mods, and the release's own.</summary>
    private (ModLibrary Library, string User, string Shipped) TwoLibraries()
    {
        var user = Path.Combine(_root, "user-mods");
        var shipped = Path.Combine(_root, "shipped-mods");
        return (new ModLibrary(user, shipped), user, shipped);
    }

    [Fact]
    public void TheShippedLibraryAndTheUsersAreListedTogether()
    {
        var (library, user, shipped) = TwoLibraries();
        MakeModFolder(Path.Combine(shipped, TitleId), "flight", "1.0.0");
        MakeModFolder(Path.Combine(user, TitleId), "my-own-mod", "0.1.0");

        var mods = library.Scan(TitleId);

        Assert.Equal(2, mods.Count);
        Assert.True(mods.Single(m => m.DirName == "flight").Shipped);
        Assert.False(mods.Single(m => m.DirName == "my-own-mod").Shipped);
    }

    [Fact]
    public void AUserModOfTheSameFolderNameHidesTheShippedOne()
    {
        var (library, user, shipped) = TwoLibraries();
        MakeModFolder(Path.Combine(shipped, TitleId), "flight", "1.0.0");
        MakeModFolder(Path.Combine(user, TitleId), "flight", "9.9.9");

        var mod = Assert.Single(library.Scan(TitleId));

        Assert.Equal("9.9.9", mod.Version);
        Assert.False(mod.Shipped);
    }

    [Fact]
    public void WithNoShippedLibraryNothingChanges()
    {
        var library = new ModLibrary(_root);
        MakeModFolder(Path.Combine(_root, TitleId), "bolt-mod", "1.0.0");

        Assert.Null(library.ShippedRootPath);
        Assert.Null(library.ShippedTitleFolder(TitleId));
        Assert.False(Assert.Single(library.Scan(TitleId)).Shipped);
    }

    [Fact]
    public void AZipOverAShippedModIsAReplaceAndLandsInTheUsersFolder()
    {
        var (library, user, shipped) = TwoLibraries();
        MakeModFolder(Path.Combine(shipped, TitleId), "bolt-mod", "1.0.0");

        using (var candidate = library.OpenZip(BuildZip("bolt-mod", "1.0.0"), TitleId))
        {
            // The shipped mod is what it would replace, so the user is asked before it happens.
            Assert.Equal(ZipInstallKind.Replace, candidate.Kind);
            Assert.True(candidate.Installed?.Shipped);
            library.CommitZip(candidate, TitleId);
        }

        Assert.True(File.Exists(Path.Combine(user, TitleId, "bolt-mod", "patch.txt")));
        Assert.False(Assert.Single(library.Scan(TitleId)).Shipped);
    }

    private string BuildZip(string dirName, string version, bool extraEmptyFolder = false)
    {
        var staging = Path.Combine(_root, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        if (extraEmptyFolder) Directory.CreateDirectory(Path.Combine(staging, "0-docs"));
        MakeModFolder(staging, dirName, version);

        var zip = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
        ZipFile.CreateFromDirectory(staging, zip);
        return zip;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }
}

/// <summary>
/// The library this repository ships. A mod's caves are read, hashed and uploaded from beside its
/// patch.txt, so a cave line that names a folder of its own would upload nothing and hash to the
/// patch file alone: that rule is what these check, on the files a release actually carries.
/// </summary>
public class ShippedModLibraryTests
{
    private static string LibraryPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "mods");
            if (Directory.Exists(Path.Combine(candidate, "NPEA00385"))) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("the repository's mods/ library");
    }

    [Fact]
    public void EveryShippedModsCavesSitBesideItsPatchFile()
    {
        foreach (var title in Directory.EnumerateDirectories(LibraryPath()))
        {
            foreach (var folder in Directory.EnumerateDirectories(title))
            {
                if (ModLibrary.Read(folder, shipped: true) is not { } mod) continue;

                foreach (var bin in mod.BinFiles)
                {
                    Assert.True(File.Exists(Path.Combine(folder, bin)),
                        $"{mod.DirName}: patch.txt names {bin}, which is not beside it");
                }
            }
        }
    }

    [Fact]
    public void TheBarlowJobaTrainerIsInTheRac2Library()
    {
        string folder = Path.Combine(LibraryPath(), "NPEA00386", "barlow_joba_trainer");
        var mod = ModLibrary.Read(folder, shipped: true);

        Assert.NotNull(mod);
        Assert.Equal("Barlow/Joba Trainer", mod!.Name);
        Assert.Equal("robo", mod.Author);
        Assert.Equal("2.0", mod.Version);
        Assert.Equal(2, mod.PatchWordCount);

        // The cave came out of the author's tree as bin/rack.bin and is beside patch.txt here.
        Assert.Equal(new[] { "rack.bin" }, mod.BinFiles);
        Assert.Equal(534, new FileInfo(Path.Combine(folder, "rack.bin")).Length);
    }

    [Fact]
    public void TheFreecamIsNotShippedBecauseItFightsTheSavefileHelper()
    {
        Assert.False(Directory.Exists(Path.Combine(LibraryPath(), "NPEA00387", "coolcam")));
    }
}
