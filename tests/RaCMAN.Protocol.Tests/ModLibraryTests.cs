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
