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

    // ---------------------------------------------------- revision 1.9, the savefile block

    [Fact]
    public void TheInfoReplyIsEightBytesAndRoundTrips()
    {
        var info = new SaveFileInfo(true, true, false, SaveFileInfo.PendingLoad, 0x200000);
        var bytes = info.ToBytes();

        Assert.Equal(SaveFileInfo.WireSize, bytes.Length);
        Assert.Equal(8, bytes.Length);

        var parsed = SaveFileInfo.Parse(bytes);
        Assert.Equal(info, parsed);
        Assert.True(parsed.LoadPending);
        Assert.False(parsed.SetAsidePending);
    }

    [Fact]
    public void ThePendingBitsAreBit0AndBit1()
    {
        Assert.Equal(1, SaveFileInfo.PendingSetAside);
        Assert.Equal(2, SaveFileInfo.PendingLoad);
    }

    [Fact]
    public async Task InfoReportsTheConsolesHelper()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var info = await client.SaveFileInfoAsync();

            Assert.True(info.Supported);
            Assert.True(info.Installed);
            Assert.Equal((uint)server.SaveFileBuffer.Length, info.Size);
            Assert.Equal(0, info.Pending);
        }
    }

    [Fact]
    public async Task AGameWithNoHelperAnswersOkWithSupportedZero()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.SaveFileSupported = false;
            var info = await client.SaveFileInfoAsync();

            Assert.False(info.Supported);
            Assert.Equal(0u, info.Size);
        }
    }

    [Fact]
    public async Task AConsoleThatCannotPatchCodeRefusesTheWholeBlock()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.SaveFileUnsupported = true;

            var info = await Assert.ThrowsAsync<QwarkStatusException>(() => client.SaveFileInfoAsync());
            Assert.Equal(Status.Unsupported, info.Status);

            var read = await Assert.ThrowsAsync<QwarkStatusException>(() => client.SaveFileReadAsync(0, 16));
            Assert.Equal(Status.Unsupported, read.Status);

            var write = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.SaveFileWriteAsync(0, new byte[] { 1, 2, 3, 4 }));
            Assert.Equal(Status.Unsupported, write.Status);
        }
    }

    [Fact]
    public async Task TheAsideBufferRoundTripsInChunks()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var data = Pattern(server.SaveFileBuffer.Length);

            var written = new List<long>();
            await client.SaveFileUploadAsync(data, new Progress<long>(written.Add));
            Assert.Equal(data, server.SaveFileBuffer);

            var read = new List<long>();
            var back = await client.SaveFileDownloadAsync((uint)data.Length, new Progress<long>(read.Add));
            Assert.Equal(data, back);

            // 200 KB is three full 64 KB chunks and an 8 KB tail, both ways.
            int chunks = (int)Math.Ceiling(data.Length / (double)QwarkClient.SaveFileChunkSize);
            Assert.Equal(4, chunks);
        }
    }

    [Fact]
    public async Task AReadPastTheEndIsTrimmedAndAWritePastItIsRefused()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            uint size = (uint)server.SaveFileBuffer.Length;

            var tail = await client.SaveFileReadAsync(size - 16, 4096);
            Assert.Equal(16, tail.Length);

            var offEnd = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.SaveFileReadAsync(size, 4));
            Assert.Equal(Status.BadArg, offEnd.Status);

            var overrun = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.SaveFileWriteAsync(size - 2, new byte[] { 1, 2, 3, 4 }));
            Assert.Equal(Status.BadArg, overrun.Status);
        }
    }

    [Fact]
    public async Task AChunkLongerThanTheCapIsRefusedBeforeItIsSent()
    {
        var (_, client) = await ConnectAsync();
        using (client)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => client.SaveFileWriteAsync(0, new byte[QwarkClient.SaveFileChunkSize + 1]));
        }
    }

    // ---------------------------------------------------------------- the two sequences

    [Fact]
    public async Task SaveTriggersTheSetAsideActionThenReadsTheBuffer()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.SaveAsideContent = Pattern(200 * 1024);
            byte id = server.Describe.SaveAsideAction!.Id;

            var messages = new List<string>();
            var data = await SaveFileTransfer.DownloadAsync(client, id,
                status: new Progress<string>(messages.Add));

            Assert.Equal(server.SaveAsideContent, data);
            Assert.Equal(new[] { id }, server.Triggered);
        }
    }

    [Fact]
    public async Task SaveWaitsForThePendingBitToClear()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            // The helper takes a few frames to notice, as it does on hardware.
            server.SaveFilePendingPolls = 3;
            byte id = server.Describe.SaveAsideAction!.Id;

            var messages = new List<string>();
            var data = await SaveFileTransfer.DownloadAsync(client, id,
                status: new Progress<string>(messages.Add));

            Assert.Equal(server.SaveAsideContent, data);
            Assert.Contains(messages, m => m.Contains("Waiting", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task SaveGivesUpWhenTheHelperNeverAnswers()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            // A game sitting on a loading screen: the hook is in and never reached.
            server.SaveFilePendingPolls = int.MaxValue;
            byte id = server.Describe.SaveAsideAction!.Id;

            await Assert.ThrowsAsync<SaveFileTransfer.NotAnsweredException>(
                () => SaveFileTransfer.DownloadAsync(client, id, timeout: TimeSpan.FromMilliseconds(200)));
        }
    }

    [Fact]
    public async Task SaveRefusesAGameTheConsoleHasNoHelperFor()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.SaveFileSupported = false;
            byte id = server.Describe.SaveAsideAction!.Id;

            await Assert.ThrowsAsync<SaveFileTransfer.NotAnsweredException>(
                () => SaveFileTransfer.DownloadAsync(client, id));
            Assert.Empty(server.Triggered);
        }
    }

    [Fact]
    public async Task LoadWritesTheBufferThenTriggersTheLoadAction()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var data = Pattern(server.SaveFileBuffer.Length);
            byte id = server.Describe.LoadAsideAction!.Id;

            await SaveFileTransfer.UploadAsync(client, id, data);

            Assert.Equal(data, server.SaveFileBuffer);
            Assert.Equal(new[] { id }, server.Triggered);

            // The action fired after the write, so the game saw the new bytes.
            Assert.Equal(data, server.LoadedSaveFile);
        }
    }

    [Fact]
    public async Task LoadRefusesAFileBiggerThanTheConsolesBuffer()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var data = Pattern(server.SaveFileBuffer.Length + 1);
            byte id = server.Describe.LoadAsideAction!.Id;

            await Assert.ThrowsAsync<SaveFileTransfer.NotAnsweredException>(
                () => SaveFileTransfer.UploadAsync(client, id, data));
            Assert.Empty(server.Triggered);
        }
    }

    /// <summary>
    /// A save file for a game is one fixed length, so a short one is either a file for another
    /// game or one that was cut short on the way in. Neither is a file the game can take, and the
    /// console cannot tell: nothing is sent at all.
    /// </summary>
    [Fact]
    public async Task LoadRefusesAFileShorterThanTheConsolesBuffer()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var data = Pattern(server.SaveFileBuffer.Length - 1);
            byte id = server.Describe.LoadAsideAction!.Id;
            var before = (byte[])server.SaveFileBuffer.Clone();

            var ex = await Assert.ThrowsAsync<SaveFileTransfer.NotAnsweredException>(
                () => SaveFileTransfer.UploadAsync(client, id, data));

            Assert.Contains("exactly", ex.Message);
            Assert.Empty(server.Triggered);
            Assert.Empty(server.SaveFileOrder);
            Assert.Equal(before, server.SaveFileBuffer);
            Assert.Null(server.LoadedSaveFile);
        }
    }

    /// <summary>
    /// The chunks go out in order, one at a time, and the action that hands the buffer to the game
    /// fires after the last of them: the game must never be given a half-written buffer.
    /// </summary>
    [Fact]
    public async Task LoadWritesEveryChunkInOrderBeforeTheActionFires()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var data = Pattern(server.SaveFileBuffer.Length);
            byte id = server.Describe.LoadAsideAction!.Id;

            await SaveFileTransfer.UploadAsync(client, id, data);

            int chunk = QwarkClient.SaveFileChunkSize;
            Assert.Equal(
                new[]
                {
                    "write 0",
                    $"write {chunk}",
                    $"write {chunk * 2}",
                    $"write {chunk * 3}",
                    "load",
                },
                server.SaveFileOrder);
            Assert.Equal(data, server.LoadedSaveFile);
        }
    }

    // ------------------------------------------------- the save writes one whole file or none

    [Fact]
    public async Task ASaveWritesTheWholeFileUnderItsOwnName()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            server.SaveAsideContent = Pattern(200 * 1024);

            var path = await SaveFileTransfer.SaveToLibraryAsync(
                client, server.Describe.SaveAsideAction!.Id, library,
                server.Session.TitleId, "any%", "whole.sav");

            Assert.Equal("whole.sav", Path.GetFileName(path));
            Assert.Equal(server.SaveAsideContent, File.ReadAllBytes(path));
            Assert.Equal(new[] { "whole.sav" }, library.Files(server.Session.TitleId, "any%"));
        }
    }

    /// <summary>
    /// The torn save. The console stops answering half way through the read, and what the library
    /// must not end up holding is the half that arrived: loading that file back is what crashes
    /// the game. Nothing is written, and the temporary the write would have used is not left
    /// behind either.
    /// </summary>
    [Fact]
    public async Task ASaveThatFailsPartWayThroughLeavesNoFile()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            string title = server.Session.TitleId;
            server.SaveFileReadFailsFrom = QwarkClient.SaveFileChunkSize;

            var ex = await Assert.ThrowsAsync<QwarkStatusException>(
                () => SaveFileTransfer.SaveToLibraryAsync(
                    client, server.Describe.SaveAsideAction!.Id, library, title, "any%", "torn.sav"));

            Assert.Equal(Status.Busy, ex.Status);
            Assert.Empty(library.Files(title, "any%"));
            Assert.False(File.Exists(library.FilePath(title, "any%", "torn.sav")));
            Assert.False(File.Exists(
                library.FilePath(title, "any%", "torn.sav") + SaveFileLibrary.PartialExtension));
        }
    }

    /// <summary>
    /// And the one that was already there is still the one that was already there: a failed save
    /// over an existing name does not take the old file with it.
    /// </summary>
    [Fact]
    public async Task AFailedSaveLeavesAnExistingFileAlone()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            string title = server.Session.TitleId;
            var old = new byte[] { 1, 2, 3, 4 };
            library.Write(title, "any%", "keep.sav", old);

            server.SaveFileReadFailsFrom = 0;

            await Assert.ThrowsAsync<QwarkStatusException>(
                () => SaveFileTransfer.SaveToLibraryAsync(
                    client, server.Describe.SaveAsideAction!.Id, library, title, "any%", "keep.sav"));

            Assert.Equal(old, library.Read(title, "any%", "keep.sav"));
        }
    }

    /// <summary>A save that does arrive whole replaces the file that was there.</summary>
    [Fact]
    public async Task ASaveOverAnExistingNameReplacesIt()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            string title = server.Session.TitleId;
            library.Write(title, "any%", "over.sav", new byte[] { 9 });
            server.SaveAsideContent = Pattern(200 * 1024);

            await SaveFileTransfer.SaveToLibraryAsync(
                client, server.Describe.SaveAsideAction!.Id, library, title, "any%", "over.sav");

            Assert.Equal(server.SaveAsideContent, library.Read(title, "any%", "over.sav"));
            Assert.Equal(new[] { "over.sav" }, library.Files(title, "any%"));
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
                client, server.Describe.SaveAsideAction!.Id);
            var path = library.Write(title, "any%", "start of veldin.sav", downloaded);

            Assert.True(File.Exists(path));
            Assert.Contains("any%", library.Categories(title));
            Assert.Contains("start of veldin.sav", library.Files(title, "any%"));

            // Something else fills the buffer, so the upload has to put the bytes back.
            Array.Clear(server.SaveFileBuffer);

            await SaveFileTransfer.UploadAsync(client, server.Describe.LoadAsideAction!.Id,
                library.Read(title, "any%", "start of veldin.sav"));

            Assert.Equal(server.SaveAsideContent, server.LoadedSaveFile);
        }
    }

    // ---------------------------------------------------------------- the local library

    [Theory]
    [InlineData("veldin", "veldin.sav")]
    [InlineData("veldin.sav", "veldin.sav")]
    [InlineData("veldin.SAV", "veldin.SAV")]
    [InlineData("  veldin  ", "veldin.sav")]
    [InlineData("", "")]
    public void ATypedNameGetsTheSaveExtension(string typed, string expected)
    {
        Assert.Equal(expected, SaveFileLibrary.EnsureExtension(typed));
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
