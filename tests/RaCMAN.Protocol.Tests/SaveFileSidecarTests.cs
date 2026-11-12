using System.Text;
using RaCMAN.App;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The console's <c>&lt;name&gt;.sav.sum</c> CRC sidecars (PROTOCOL.md section 5.13) reaching the
/// PC's library, in a bundle somebody copied straight off a console's
/// <c>/dev_hdd0/qwark/savefiles/</c>. The PC has no use for them, so the import leaves them behind,
/// a scan deletes one that got in some other way, and the listing never shows one either way.
/// <para>
/// Everything here is in the test run's own data folder (<see cref="TestDataFolder"/>), the
/// library under <see cref="AppPaths.SaveFiles"/> where the client keeps it, in a category of each
/// test's own so tests running side by side never see each other's files.
/// </para>
/// </summary>
public class SaveFileSidecarTests : IDisposable
{
    private const string Title = "NPEA00385";

    private readonly string _category = "sum-" + Guid.NewGuid().ToString("N")[..8];

    private readonly string _scratch = Path.Combine(TestDataFolder.Root, "savsum-" + Guid.NewGuid().ToString("N"));

    private readonly SaveFileLibrary _library = new(AppPaths.SaveFiles);

    private string CategoryFolder => _library.CategoryFolder(Title, _category);

    private static byte[] Pattern(int size, byte seed)
    {
        var bytes = new byte[size];
        for (int i = 0; i < size; i++) bytes[i] = (byte)(i * 7 + seed);
        return bytes;
    }

    private static void Write(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    /// <summary>The sidecar qwark writes: eight lowercase hex digits of the save's CRC32.</summary>
    private static byte[] SumOf(byte[] save) => Encoding.ASCII.GetBytes(Crc32.ToSumText(Crc32.Compute(save)));

    private string InCategory(string name) => Path.Combine(CategoryFolder, name);

    private string[] NamesOnDisk() =>
        Directory.GetFiles(CategoryFolder).Select(p => Path.GetFileName(p)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void AnImportedConsoleBundleListsOnlyItsSavesAndLeavesNoSumBehind()
    {
        // The bundle as it comes off the console: every save with its sidecar beside it.
        var veldin = Pattern(64, 1);
        var kerwan = Pattern(64, 2);
        var bundle = Path.Combine(_scratch, "bundle");
        var source = Path.Combine(bundle, LegacyLibraryImport.SaveFolderName, Title, _category);
        Write(Path.Combine(source, "veldin.sav"), veldin);
        Write(Path.Combine(source, "veldin.sav.sum"), SumOf(veldin));
        Write(Path.Combine(source, "kerwan.sav"), kerwan);
        Write(Path.Combine(source, "kerwan.sav.sum"), SumOf(kerwan));

        // What the Settings panel says before the button is pressed counts the saves, not the sums.
        var found = LegacyLibraryImport.Look(bundle, LegacyModExclusions.None);
        Assert.Equal(2, found.Saves);
        Assert.Equal(1, found.Categories);

        var result = LegacyLibraryImport.Run(
            bundle, AppPaths.SaveFiles, Path.Combine(_scratch, "mods"), shippedModsRoot: null, LegacyModExclusions.None);

        Assert.Equal(2, result.Saves);
        Assert.Equal(0, result.SavesRenamed);

        // Straight after the import and before any scan: the sums were never copied in at all.
        Assert.Equal(new[] { "kerwan.sav", "veldin.sav" }, NamesOnDisk());

        var listed = _library.FilesWithCrc(Title, _category);
        Assert.Equal(new[] { "kerwan.sav", "veldin.sav" }, listed.Select(f => f.Name).ToArray());
        Assert.Equal(Crc32.Compute(veldin), listed[1].Crc);

        // The import reads the bundle and never changes it.
        Assert.True(File.Exists(Path.Combine(source, "veldin.sav.sum")));
        Assert.True(File.Exists(Path.Combine(source, "kerwan.sav.sum")));
    }

    [Fact]
    public void ASidecarPutInTheLibraryByHandIsGoneAfterAScanAndNothingElseIsTouched()
    {
        var veldin = Pattern(64, 3);
        _library.Write(Title, _category, "veldin.sav", veldin);
        Write(InCategory("veldin.sav.sum"), SumOf(veldin));

        // Files that are not the console's sidecar, however close their names come: none of them
        // is deleted and none of them is a save.
        Write(InCategory("notes.txt"), "route notes"u8.ToArray());
        Write(InCategory("qwark.sum"), "0badf00d"u8.ToArray());
        Write(InCategory("veldin.sum"), "0badf00d"u8.ToArray());
        Write(InCategory("veldin.sav.sum.txt"), "kept"u8.ToArray());
        Write(InCategory(".sav.sum"), "no name in front"u8.ToArray());

        // The scan the panel runs when it shows a category.
        var listed = _library.FilesWithCrc(Title, _category);

        Assert.Equal(new[] { "veldin.sav" }, listed.Select(f => f.Name).ToArray());
        Assert.False(File.Exists(InCategory("veldin.sav.sum")));

        Assert.Equal(veldin, File.ReadAllBytes(InCategory("veldin.sav")));
        Assert.Equal("route notes", File.ReadAllText(InCategory("notes.txt")));
        Assert.Equal(
            new[] { ".sav.sum", "notes.txt", "qwark.sum", "veldin.sav", "veldin.sav.sum.txt", "veldin.sum" },
            NamesOnDisk());

        // The sidecar of a save renamed after it was put there is an orphan, and goes the same way.
        Write(InCategory("veldin.sav.sum"), SumOf(veldin));
        _library.Rename(Title, _category, "veldin.sav", "kerwan.sav");
        Assert.Equal(new[] { "kerwan.sav" }, _library.Files(Title, _category));
        Assert.False(File.Exists(InCategory("veldin.sav.sum")));
    }

    [Fact]
    public void ASidecarThatCannotBeDeletedIsStillNeverASave()
    {
        var veldin = Pattern(64, 4);
        _library.Write(Title, _category, "veldin.sav", veldin);
        var sum = InCategory("veldin.sav.sum");
        Write(sum, SumOf(veldin));
        File.SetAttributes(sum, FileAttributes.ReadOnly);

        try
        {
            // Windows refuses to delete a read-only file, so the hiding cannot lean on the delete.
            Assert.Equal(new[] { "veldin.sav" }, _library.Files(Title, _category));
            Assert.Equal(new[] { "veldin.sav" }, _library.FilesWithCrc(Title, _category).Select(f => f.Name).ToArray());

            // Nor can the panel's rows: a sidecar handed to the merge directly is not one of them.
            var row = Assert.Single(SaveFileMerge.Build(null, new[]
            {
                new LocalSaveFile("veldin.sav", veldin.Length, Crc32.Compute(veldin)),
                new LocalSaveFile("veldin.sav.sum", 8, 0x1234),
            }));
            Assert.Equal("veldin.sav", row.Name);
        }
        finally
        {
            if (File.Exists(sum)) File.SetAttributes(sum, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task UploadingTheLibrarysSavesSendsTheConsolesOwnSumAndNoStrayOne()
    {
        var server = new FakeQwarkServer();
        server.Start();
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);

        using (server)
        using (client)
        {
            var data = Pattern(server.SaveFileBuffer.Length, 5);
            _library.Write(Title, _category, "veldin.sav", data);

            // A sidecar copied in with the save, and a wrong one: the console's must come from the
            // bytes. Read-only, so on Windows it outlives the scan and only the listing keeps it
            // from being a row of its own, uploaded as a save with a .sum.sum beside it.
            var sidecar = InCategory("veldin.sav.sum");
            Write(sidecar, "deadbeef"u8.ToArray());
            File.SetAttributes(sidecar, FileAttributes.ReadOnly);

            // Every row the panel would offer, loaded: each one only on the PC, so each is uploaded.
            var rows = SaveFileMerge.Build(null, _library.FilesWithCrc(Title, _category));
            foreach (var row in rows)
            {
                await SaveFileTransfer.LoadAsync(client, _library, Title, _category, row);
            }

            var save = QwarkClient.SaveFileConsolePath(Title, _category, "veldin.sav");
            var folder = save[..(save.LastIndexOf('/') + 1)];
            var onConsole = server.Files.Keys
                .Where(k => k.StartsWith(folder, StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(new[] { save, save + QwarkClient.SaveFileSumExtension }, onConsole);
            Assert.Equal(Crc32.ToSumText(Crc32.Compute(data)),
                Encoding.ASCII.GetString(server.Files[save + QwarkClient.SaveFileSumExtension]));
            Assert.Equal(data, server.LoadedSaveFile);
        }
    }

    public void Dispose()
    {
        foreach (var folder in new[] { CategoryFolder, _scratch })
        {
            try
            {
                if (!Directory.Exists(folder)) continue;

                foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The run's data folder is removed whole when it ends.
            }
        }
    }
}
