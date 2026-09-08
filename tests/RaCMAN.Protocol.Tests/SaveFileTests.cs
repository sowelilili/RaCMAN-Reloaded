using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The savefile manager end to end against the fake console: the two flagged ACTIONs, the file
/// ops that carry the bytes, and the local library the files land in.
/// </summary>
public class SaveFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "racman-savefiles-" + Guid.NewGuid().ToString("N"));

    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync()
    {
        var server = new FakeQwarkServer();
        server.Start();
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    private static byte[] Pattern(int size)
    {
        var bytes = new byte[size];
        for (int i = 0; i < size; i++) bytes[i] = (byte)(i * 7 + (i >> 8));
        return bytes;
    }

    // ---------------------------------------------------------------- revision 1.2 flags

    [Fact]
    public void SaveAsideAndLoadAsideAreBits2And3OfFeatureFlags()
    {
        Assert.Equal(4, (byte)FeatureFlags.SaveAside);
        Assert.Equal(8, (byte)FeatureFlags.LoadAside);
    }

    [Fact]
    public void AFlaggedActionRoundTripsThroughTheFeatureEncoding()
    {
        var feature = new Feature(9, FeatureKind.Action, 1, 0, FeatureFlags.SaveAside, 0xFF, 0, 0, "Set aside file");
        var parsed = Feature.Parse(feature.ToBytes());

        Assert.Equal(FeatureFlags.SaveAside, parsed.Flags);
        Assert.True(parsed.SavesAside);
        Assert.False(parsed.LoadsAside);
    }

    [Fact]
    public void OnlyAnActionCountsAsASaveOrLoadAside()
    {
        // A TOGGLE that happens to carry the bit is not the savefile helper.
        var toggle = new Feature(3, FeatureKind.Toggle, 0, 0, FeatureFlags.LoadAside, 0xFF, 0, 0, "Not an action");
        Assert.False(toggle.LoadsAside);
    }

    [Fact]
    public void DescribeFindsBothHalvesOfTheHelperByFlag()
    {
        using var server = new FakeQwarkServer();
        var describe = server.Describe;

        Assert.True(describe.HasSaveFileHelper);
        Assert.Equal("Set aside file", describe.SaveAsideAction!.Label);
        Assert.Equal("Load set-aside file", describe.LoadAsideAction!.Label);
        Assert.NotEqual(describe.SaveAsideAction.Id, describe.LoadAsideAction.Id);
    }

    [Fact]
    public void AGameWithoutTheHelperSaysSo()
    {
        var describe = new DescribeResult(GameId.Rac2, new[] { "Cheats" }, Array.Empty<string>(), new[]
        {
            new Feature(0, FeatureKind.Action, 0, 0, FeatureFlags.None, 0xFF, 0, 0, "Die"),
        });

        Assert.False(describe.HasSaveFileHelper);
        Assert.Null(describe.SaveAsideAction);
        Assert.Null(describe.LoadAsideAction);
    }

    // ---------------------------------------------------------------- file ops

    [Fact]
    public async Task A200KbFileRoundTripsThroughTheFileOpsInChunks()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var data = Pattern(200 * 1024);
            const string path = "/dev_hdd0/qwark/roundtrip.bin";

            var written = new List<long>();
            await client.WriteFileAsync(path, data, new Progress<long>(written.Add));

            Assert.Equal(data, server.Files[path]);

            var read = await client.ReadFileAsync(path);
            Assert.Equal(data, read);

            // 64 KB chunks, so 200 KB is three full writes and an 8 KB tail.
            Assert.Equal(4, (int)Math.Ceiling(data.Length / (double)QwarkClient.FileChunkSize));
        }
    }

    [Fact]
    public async Task AFileThatIsAnExactMultipleOfTheChunkStillTerminates()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var data = Pattern(QwarkClient.FileChunkSize);
            const string path = "/dev_hdd0/qwark/exact.bin";

            await client.WriteFileAsync(path, data);
            Assert.Equal(data, await client.ReadFileAsync(path));
        }
    }

    [Fact]
    public async Task AnEmptyFileReadsBackEmpty()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            const string path = "/dev_hdd0/qwark/empty.bin";
            await client.WriteFileAsync(path, Array.Empty<byte>());
            Assert.Empty(await client.ReadFileAsync(path));
        }
    }

    [Fact]
    public async Task ReadingAMissingFileIsNotFound()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var ex = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.ReadFileAsync("/dev_hdd0/qwark/nothing-here"));
            Assert.Equal(Status.NotFound, ex.Status);
        }
    }

    [Fact]
    public async Task DirListReportsFilesAndFolders()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.DirCreateAsync("/dev_hdd0/qwark/saves/any");
            await client.WriteFileAsync("/dev_hdd0/qwark/saves/one.bin", new byte[] { 1, 2, 3 });

            var entries = await client.DirListAsync("/dev_hdd0/qwark/saves");
            var file = Assert.Single(entries.Where(e => !e.IsDirectory));
            Assert.Equal("one.bin", file.Name);
            Assert.Equal(3u, file.Size);
            Assert.Contains(entries, e => e.IsDirectory && e.Name == "any");
        }
    }

    [Fact]
    public async Task FileDeleteRemovesTheFile()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            const string path = "/dev_hdd0/qwark/gone.bin";
            await client.WriteFileAsync(path, new byte[] { 9 });
            await client.FileDeleteAsync(path);
            Assert.False(server.Files.ContainsKey(path));
        }
    }

    // ---------------------------------------------------------------- the two sequences

    [Fact]
    public async Task SaveTriggersTheSetAsideActionThenDownloadsTempsave()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.SaveAsideContent = Pattern(200 * 1024);
            byte id = server.Describe.SaveAsideAction!.Id;

            var messages = new List<string>();
            var data = await SaveFileTransfer.DownloadAsync(client, id, server.Session.TitleId,
                settle: TimeSpan.Zero, status: new Progress<string>(messages.Add));

            Assert.Equal(server.SaveAsideContent, data);
            Assert.Equal(new[] { id }, server.Triggered);
            Assert.Equal("/dev_hdd0/game/NPEA00385/USRDIR/tempsave", server.TempSavePath);
        }
    }

    [Fact]
    public async Task SaveWaitsAndRetriesOnceWhenTempsaveIsNotThereYet()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            // The game has not finished writing when the first read lands, as on hardware.
            server.SaveAsideMisses = 1;
            byte id = server.Describe.SaveAsideAction!.Id;

            var messages = new List<string>();
            var data = await SaveFileTransfer.DownloadAsync(client, id, server.Session.TitleId,
                settle: TimeSpan.Zero, status: new Progress<string>(messages.Add));

            Assert.Equal(server.SaveAsideContent, data);
            Assert.Contains(messages, m => m.Contains("retrying", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task SaveGivesUpAfterTheSecondFailureRatherThanLooping()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.SaveAsideMisses = 2;
            byte id = server.Describe.SaveAsideAction!.Id;

            var ex = await Assert.ThrowsAsync<QwarkStatusException>(() => SaveFileTransfer.DownloadAsync(
                client, id, server.Session.TitleId, settle: TimeSpan.Zero));
            Assert.Equal(Status.NotFound, ex.Status);
        }
    }

    [Fact]
    public async Task LoadUploadsTheFileThenTriggersTheLoadAction()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var data = Pattern(150 * 1024);
            byte id = server.Describe.LoadAsideAction!.Id;

            await SaveFileTransfer.UploadAsync(client, id, server.Session.TitleId, data);

            Assert.Equal(data, server.Files[server.TempSavePath]);
            Assert.Equal(new[] { id }, server.Triggered);

            // The action fired after the write, so the game saw the new bytes.
            Assert.Equal(data, server.LoadedSaveFile);
        }
    }

    [Fact]
    public async Task ASaveThenALoadRoundTripsThroughTheLocalLibrary()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            string title = server.Session.TitleId;
            server.SaveAsideContent = Pattern(200 * 1024);

            var downloaded = await SaveFileTransfer.DownloadAsync(
                client, server.Describe.SaveAsideAction!.Id, title, settle: TimeSpan.Zero);
            var path = library.Write(title, "any%", "start of veldin", downloaded);

            Assert.True(File.Exists(path));
            Assert.Contains("any%", library.Categories(title));
            Assert.Contains("start of veldin", library.Files(title, "any%"));

            // Something else overwrites tempsave, so the upload has to put the bytes back.
            server.Files[server.TempSavePath] = new byte[] { 0 };

            await SaveFileTransfer.UploadAsync(client, server.Describe.LoadAsideAction!.Id, title,
                library.Read(title, "any%", "start of veldin"));

            Assert.Equal(server.SaveAsideContent, server.LoadedSaveFile);
        }
    }

    // ---------------------------------------------------------------- the local library

    [Fact]
    public void TheTempsavePathIsTheDocumentedOne()
    {
        Assert.Equal("/dev_hdd0/game/NPEA00387/USRDIR/tempsave", SaveFileLibrary.TempSavePath("NPEA00387"));
    }

    [Fact]
    public void ATitleWithNoFoldersOffersTheDefaultCategory()
    {
        var library = new SaveFileLibrary(_root);
        Assert.Equal(new[] { SaveFileLibrary.DefaultCategory }, library.Categories("NPEA00385"));
        Assert.Empty(library.Files("NPEA00385", SaveFileLibrary.DefaultCategory));
    }

    [Fact]
    public void CategoriesAndFilesComeBackSorted()
    {
        var library = new SaveFileLibrary(_root);
        library.EnsureCategory("NPEA00385", "zebra");
        library.EnsureCategory("NPEA00385", "any%");
        library.Write("NPEA00385", "any%", "b", new byte[] { 1 });
        library.Write("NPEA00385", "any%", "a", new byte[] { 1 });

        Assert.Equal(new[] { "any%", "zebra" }, library.Categories("NPEA00385"));
        Assert.Equal(new[] { "a", "b" }, library.Files("NPEA00385", "any%"));
    }

    [Fact]
    public void RenameAndDeleteMoveTheLocalFile()
    {
        var library = new SaveFileLibrary(_root);
        library.Write("NPEA00385", "misc", "old", new byte[] { 4, 5 });

        library.Rename("NPEA00385", "misc", "old", "new");
        Assert.Equal(new[] { "new" }, library.Files("NPEA00385", "misc"));
        Assert.Equal(new byte[] { 4, 5 }, library.Read("NPEA00385", "misc", "new"));

        library.Delete("NPEA00385", "misc", "new");
        Assert.Empty(library.Files("NPEA00385", "misc"));
    }

    [Fact]
    public void RenamingOntoAnExistingFileIsRefused()
    {
        var library = new SaveFileLibrary(_root);
        library.Write("NPEA00385", "misc", "one", new byte[] { 1 });
        library.Write("NPEA00385", "misc", "two", new byte[] { 2 });

        Assert.Throws<IOException>(() => library.Rename("NPEA00385", "misc", "one", "two"));
    }

    [Theory]
    [InlineData("../escape", "escape")]
    [InlineData("sub/dir", "subdir")]
    [InlineData("   ", "fallback")]
    [InlineData("ok name", "ok name")]
    public void NamesAreKeptInsideTheLibrary(string input, string expected)
    {
        Assert.Equal(expected, SaveFileLibrary.Sanitise(input, "fallback"));
    }

    [Fact]
    public void ATraversingNameStaysUnderTheCategoryFolder()
    {
        var library = new SaveFileLibrary(_root);
        var path = library.FilePath("NPEA00385", "../../etc", "../../../passwd");
        Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
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
