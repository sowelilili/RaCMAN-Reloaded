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
            server.ClearRequestLog();
            await client.WriteFileAsync(path, data, new Progress<long>(written.Add));

            Assert.Equal(data, server.Files[path]);

            var read = await client.ReadFileAsync(path);
            Assert.Equal(data, read);

            // Revision 1.11 chunks: 16000 bytes, so 200 KB is twelve full writes and a tail, and
            // the read back is twelve full reads and the short one that says end of file.
            Assert.Equal(16000, QwarkClient.FileChunkSize);
            Assert.Equal(13, (int)Math.Ceiling(data.Length / (double)QwarkClient.FileChunkSize));

            var log = server.RequestLog();
            Assert.Equal(13, log.Count(op => op == Opcode.FileWrite));
            Assert.Equal(13, log.Count(op => op == Opcode.FileRead));
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
    public void TheInfoReplyIsTwentyBytesAndRoundTrips()
    {
        var info = new SaveFileInfo(true, true, false, SaveFileInfo.PendingLoad, 0x200000,
                                    Done: 0x40000, Total: 0x200000, Error: SaveFileError.ShortFile);
        var bytes = info.ToBytes();

        Assert.Equal(SaveFileInfo.WireSize, bytes.Length);
        Assert.Equal(20, bytes.Length);

        var parsed = SaveFileInfo.Parse(bytes);
        Assert.Equal(info, parsed);
        Assert.True(parsed.LoadPending);
        Assert.False(parsed.SetAsidePending);
        Assert.Equal(0.125f, parsed.Progress, 3);
    }

    [Fact]
    public void AnInfoReplyFromARevision19ModuleStillParses()
    {
        // Eight bytes and no more: every field it does send is still right, and the three it
        // knows nothing about read as zero rather than as garbage or an exception.
        var old = new byte[SaveFileInfo.WireSize19];
        old[0] = 1;
        old[1] = 1;
        old[2] = 1;
        old[3] = SaveFileInfo.PendingSetAside;
        old[7] = 0x10;   // size 0x10 in the low byte of the big-endian word

        var parsed = SaveFileInfo.Parse(old);

        Assert.True(parsed.Supported);
        Assert.True(parsed.SetAsidePending);
        Assert.Equal(0x10u, parsed.Size);
        Assert.False(parsed.TransferPending);
        Assert.Equal(0u, parsed.Total);
        Assert.Equal(SaveFileError.None, parsed.Error);
    }

    [Fact]
    public void ThePendingBitsAreBit0Bit1AndBit2()
    {
        Assert.Equal(1, SaveFileInfo.PendingSetAside);
        Assert.Equal(2, SaveFileInfo.PendingLoad);
        Assert.Equal(4, SaveFileInfo.PendingTransfer);
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
            server.ClearRequestLog();
            await client.SaveFileUploadAsync(data, new Progress<long>(written.Add));
            Assert.Equal(data, server.SaveFileBuffer);

            var read = new List<long>();
            var back = await client.SaveFileDownloadAsync((uint)data.Length, new Progress<long>(read.Add));
            Assert.Equal(data, back);

            // Revision 1.11 chunks: 16000 bytes, so 200 KB is twelve full ones and a tail, both ways.
            Assert.Equal(16000, QwarkClient.SaveFileChunkSize);
            int chunks = (int)Math.Ceiling(data.Length / (double)QwarkClient.SaveFileChunkSize);
            Assert.Equal(13, chunks);

            var log = server.RequestLog();
            Assert.Equal(chunks, log.Count(op => op == Opcode.SaveFileWrite));
            Assert.Equal(chunks, log.Count(op => op == Opcode.SaveFileRead));
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

    /// <summary>
    /// A whole save out and back again at the revision 1.11 chunk: 100 KB is seven SAVEFILE_WRITEs
    /// of 16000 bytes and a tail, in order and from offset zero, and the bytes that come back are
    /// the bytes that went out.
    /// </summary>
    [Fact]
    public async Task A100KbSaveRoundTripsIn16000ByteChunks()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.SaveAsideContent = Pattern(100 * 1024);

            var data = await SaveFileTransfer.DownloadAsync(client, server.Describe.SaveAsideAction!.Id);
            Assert.Equal(server.SaveAsideContent, data);

            server.SaveFileOrder.Clear();
            await SaveFileTransfer.UploadAsync(client, server.Describe.LoadAsideAction!.Id, data);

            var expected = new List<string>();
            for (int offset = 0; offset < data.Length; offset += QwarkClient.SaveFileChunkSize)
            {
                expected.Add($"write {offset}");
            }

            expected.Add("load");

            Assert.Equal(7, expected.Count - 1);
            Assert.Equal(expected, server.SaveFileOrder);
            Assert.Equal(data, server.LoadedSaveFile);
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
            var expected = new List<string>();
            for (int offset = 0; offset < data.Length; offset += chunk) expected.Add($"write {offset}");
            expected.Add("load");

            Assert.Equal(expected, server.SaveFileOrder);
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

    // ------------------------------- revision 1.10, the library on the console
    //
    // The console keeps the files now and copies them into the aside buffer itself, so what the
    // client has to get right is the merge view, the decision a load makes from it, the mirror a
    // save leaves behind, and saying what went wrong when the console says something went wrong.

    private const string Title = "NPEA00385";

    private static SaveFileEntry Entry(string name, bool console, bool pc, uint consoleCrc = 0, uint pcCrc = 0) =>
        new(name, console, pc, consoleCrc, pcCrc, console ? 16u : 0u, pc ? 16L : 0L);

    [Fact]
    public void TheMergeViewSaysWhichSideEachSaveIsOn()
    {
        var console = new[]
        {
            new ConsoleSaveFile("both.sav", 16, 0xAAAA),
            new ConsoleSaveFile("differs.sav", 16, 0x1111),
            new ConsoleSaveFile("console only.sav", 16, 0xBBBB),
        };

        var pc = new[]
        {
            new LocalSaveFile("both.sav", 16, 0xAAAA),
            new LocalSaveFile("differs.sav", 32, 0x2222),
            new LocalSaveFile("pc only.sav", 16, 0xCCCC),

            // A mirror that was interrupted leaves one of these, and it is not a save.
            new LocalSaveFile("both.sav.part", 8, 0xDDDD),
        };

        var rows = SaveFileMerge.Build(console, pc);

        Assert.Equal(new[] { "both.sav", "console only.sav", "differs.sav", "pc only.sav" },
                     rows.Select(r => r.Name).ToArray());

        var both = rows.Single(r => r.Name == "both.sav");
        Assert.Equal(SaveFileLocation.Both, both.Location);
        Assert.True(both.Agrees);
        Assert.False(both.Differs);
        Assert.Equal("both", both.Where);

        var differs = rows.Single(r => r.Name == "differs.sav");
        Assert.Equal(SaveFileLocation.Both, differs.Location);
        Assert.True(differs.Differs);
        Assert.Equal("both, differ", differs.Where);

        var consoleOnly = rows.Single(r => r.Name == "console only.sav");
        Assert.Equal(SaveFileLocation.Console, consoleOnly.Location);
        Assert.Equal("console", consoleOnly.Where);
        Assert.False(consoleOnly.Agrees);

        var pcOnly = rows.Single(r => r.Name == "pc only.sav");
        Assert.Equal(SaveFileLocation.Pc, pcOnly.Location);
        Assert.Equal("PC", pcOnly.Where);
        Assert.Equal("pc only", pcOnly.DisplayName);
    }

    [Fact]
    public void AnEmptySideIsStillAMergeView()
    {
        Assert.Empty(SaveFileMerge.Build(null, null));
        Assert.Single(SaveFileMerge.Build(new[] { new ConsoleSaveFile("a.sav", 1, 2) }, null));
        Assert.Single(SaveFileMerge.Build(null, new[] { new LocalSaveFile("a.sav", 1, 2) }));
    }

    [Theory]
    [InlineData(true, false, 0u, 0u, SaveFileLoadPlan.Restore)]            // console only
    [InlineData(true, true, 0xAAAAu, 0xAAAAu, SaveFileLoadPlan.Restore)]   // both, same bytes
    [InlineData(true, true, 0xAAAAu, 0xBBBBu, SaveFileLoadPlan.UploadThenRestore)]
    [InlineData(false, true, 0u, 0xBBBBu, SaveFileLoadPlan.UploadThenRestore)]
    [InlineData(false, false, 0u, 0u, SaveFileLoadPlan.Nothing)]
    public void LoadingSendsNothingUnlessTheConsoleLacksTheseBytes(
        bool console, bool pc, uint consoleCrc, uint pcCrc, SaveFileLoadPlan expected)
    {
        Assert.Equal(expected, SaveFileMerge.PlanLoad(Entry("one.sav", console, pc, consoleCrc, pcCrc)));
    }

    [Fact]
    public async Task TheConsoleListsItsCategoriesAndItsSaves()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");
            Assert.Equal(new[] { "any%" }, await client.SaveFileCategoriesAsync());

            // A file uploaded with the file ops, with no sum beside it: the console works out the
            // CRC itself the first time it lists the category, exactly as qwark does.
            var data = Pattern(64);
            await client.WriteFileAsync(QwarkClient.SaveFileConsolePath(Title, "any%", "veldin.sav"), data);

            var files = await client.SaveFileListAsync("any%");
            var row = Assert.Single(files);
            Assert.Equal("veldin.sav", row.Name);
            Assert.Equal(64u, row.Size);
            Assert.Equal(Crc32.Compute(data), row.Crc);
        }
    }

    [Fact]
    public async Task SavingStoresOnTheConsoleAndMirrorsItHere()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            server.SaveAsideContent = Pattern(server.SaveFileBuffer.Length);
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");

            var progress = new List<long>();
            var messages = new List<string>();
            var path = await SaveFileTransfer.StoreAsync(client, library, Title, "any%", "veldin.sav",
                mirror: true, timeout: null,
                status: new Progress<string>(messages.Add), bytes: new Progress<long>(progress.Add));

            // The console holds the file, sum and all, and this PC holds the same bytes.
            var console = Assert.Single(await client.SaveFileListAsync("any%"));
            Assert.Equal("veldin.sav", console.Name);
            Assert.Equal(Crc32.Compute(server.SaveAsideContent), console.Crc);

            Assert.Equal(server.SaveAsideContent, library.Read(Title, "any%", "veldin.sav"));
            Assert.Equal(library.FilePath(Title, "any%", "veldin.sav"), path);
            Assert.Equal(new[] { "store any%/veldin.sav" }, server.SaveFileLibraryOrder);

            // The progress moved and the last report is the whole file.
            Assert.NotEmpty(progress);
            Assert.Equal(server.SaveAsideContent.Length, progress[^1]);
        }
    }

    [Fact]
    public async Task SavingWithMirroringOffLeavesNothingOnThisPc()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");

            var path = await SaveFileTransfer.StoreAsync(client, library, Title, "any%", "veldin.sav",
                mirror: false);

            Assert.Equal(string.Empty, path);
            Assert.Empty(library.Files(Title, "any%"));
            Assert.Single(await client.SaveFileListAsync("any%"));
        }
    }

    [Fact]
    public async Task LoadingAFileTheConsoleAlreadyHasSendsNothing()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");
            await SaveFileTransfer.StoreAsync(client, library, Title, "any%", "veldin.sav", mirror: true);

            var entry = Assert.Single(SaveFileMerge.Build(
                await client.SaveFileListAsync("any%"), library.FilesWithCrc(Title, "any%")));
            Assert.True(entry.Agrees);

            server.SaveFileOrder.Clear();
            bool uploaded = await SaveFileTransfer.LoadAsync(client, library, Title, "any%", entry);

            Assert.False(uploaded);
            Assert.Equal(server.SaveAsideContent, server.LoadedSaveFile);

            // Nothing went over the wire but the request: no SAVEFILE_WRITE, no FILE_WRITE.
            Assert.Empty(server.SaveFileOrder);
            Assert.Equal("restore any%/veldin.sav", server.SaveFileLibraryOrder[^1]);
        }
    }

    [Fact]
    public async Task LoadingAFileOnlyThisPcHasUploadsItOnceAndThenRestores()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            var data = Pattern(server.SaveFileBuffer.Length);
            library.Write(Title, "any%", "veldin.sav", data);

            var entry = Assert.Single(SaveFileMerge.Build(null, library.FilesWithCrc(Title, "any%")));
            Assert.Equal(SaveFileLocation.Pc, entry.Location);

            bool uploaded = await SaveFileTransfer.LoadAsync(client, library, Title, "any%", entry);

            Assert.True(uploaded);
            Assert.Equal(data, server.LoadedSaveFile);

            // And it lives on the console from now on, with the sum the client wrote beside it, so
            // the next load of the same file sends nothing at all.
            var console = Assert.Single(await client.SaveFileListAsync("any%"));
            Assert.Equal(Crc32.Compute(data), console.Crc);
            Assert.Equal(Crc32.ToSumText(Crc32.Compute(data)),
                System.Text.Encoding.ASCII.GetString(
                    server.Files[QwarkClient.SaveFileConsolePath(Title, "any%", "veldin.sav")
                                 + QwarkClient.SaveFileSumExtension]));

            var again = Assert.Single(SaveFileMerge.Build(
                await client.SaveFileListAsync("any%"), library.FilesWithCrc(Title, "any%")));
            Assert.Equal(SaveFileLoadPlan.Restore, SaveFileMerge.PlanLoad(again));
        }
    }

    [Fact]
    public async Task WhenTheTwoCopiesDifferThisPcsCopyIsTheOneThatLoads()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");
            await SaveFileTransfer.StoreAsync(client, library, Title, "any%", "veldin.sav", mirror: true);

            // The PC's copy is edited behind the console's back.
            var edited = Pattern(server.SaveFileBuffer.Length).Select(b => (byte)(b ^ 0xFF)).ToArray();
            library.Write(Title, "any%", "veldin.sav", edited);

            var entry = Assert.Single(SaveFileMerge.Build(
                await client.SaveFileListAsync("any%"), library.FilesWithCrc(Title, "any%")));
            Assert.True(entry.Differs);

            Assert.True(await SaveFileTransfer.LoadAsync(client, library, Title, "any%", entry));
            Assert.Equal(edited, server.LoadedSaveFile);

            // And the console's copy is now the edited one, so they agree again.
            var after = Assert.Single(SaveFileMerge.Build(
                await client.SaveFileListAsync("any%"), library.FilesWithCrc(Title, "any%")));
            Assert.True(after.Agrees);
        }
    }

    [Fact]
    public async Task ARestoreOfAFileTheConsoleLostSaysWhichFileItWas()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var status = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.SaveFileRestoreAsync("any%", "gone.sav"));
            Assert.Equal(Status.NotFound, status.Status);

            // The error byte stays until the next transfer, which is what the panel reads.
            var info = await client.SaveFileInfoAsync();
            Assert.Equal(SaveFileError.FileMissing, info.Error);
            Assert.False(info.TransferPending);
            Assert.Contains("no longer has", info.Error.Describe(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ATransferThatFailsOnTheConsoleIsReportedByName()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");
            server.SaveFileTransferError = SaveFileError.IoError;

            var failed = await Assert.ThrowsAsync<SaveFileTransfer.TransferFailedException>(
                () => SaveFileTransfer.StoreAsync(client, library, Title, "any%", "veldin.sav", mirror: true));

            Assert.Equal(SaveFileError.IoError, failed.Error);
            Assert.Contains("could not read or write", failed.Message, StringComparison.Ordinal);

            // Nothing was written on either side.
            Assert.Empty(await client.SaveFileListAsync("any%"));
            Assert.Empty(library.Files(Title, "any%"));
        }
    }

    [Fact]
    public async Task ASecondTransferDuringOneIsRefused()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            // Long enough that the first is still running when the second goes out.
            server.SaveFileTransferPolls = 50;
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");
            await client.SaveFileStoreAsync("any%", "one.sav");

            var busy = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.SaveFileStoreAsync("any%", "two.sav"));
            Assert.Equal(Status.Busy, busy.Status);

            // And the listing steps aside for it rather than reporting half a file.
            var listing = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.SaveFileListAsync("any%"));
            Assert.Equal(Status.Busy, listing.Status);
        }
    }

    [Fact]
    public async Task ATransferThatNeverFinishesGivesUpRatherThanHanging()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            server.SaveFilePendingPolls = int.MaxValue;
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");

            await Assert.ThrowsAsync<SaveFileTransfer.NotAnsweredException>(
                () => SaveFileTransfer.StoreAsync(client, library, Title, "any%", "veldin.sav",
                    mirror: true, timeout: TimeSpan.FromMilliseconds(200)));
        }
    }

    [Fact]
    public async Task DeletingAndRenamingActOnBothCopies()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");
            await SaveFileTransfer.StoreAsync(client, library, Title, "any%", "veldin.sav", mirror: true);

            var entry = Assert.Single(SaveFileMerge.Build(
                await client.SaveFileListAsync("any%"), library.FilesWithCrc(Title, "any%")));

            await SaveFileTransfer.RenameAsync(client, library, Title, "any%", entry, "kerwan.sav");

            Assert.Equal(new[] { "kerwan.sav" }, library.Files(Title, "any%"));
            Assert.Equal(new[] { "kerwan.sav" },
                (await client.SaveFileListAsync("any%")).Select(f => f.Name).ToArray());

            // The sum went with it, so the console did not have to sum the file again.
            Assert.True(server.Files.ContainsKey(
                QwarkClient.SaveFileConsolePath(Title, "any%", "kerwan.sav") + QwarkClient.SaveFileSumExtension));

            var renamed = Assert.Single(SaveFileMerge.Build(
                await client.SaveFileListAsync("any%"), library.FilesWithCrc(Title, "any%")));
            await SaveFileTransfer.DeleteAsync(client, library, Title, "any%", renamed);

            Assert.Empty(library.Files(Title, "any%"));
            Assert.Empty(await client.SaveFileListAsync("any%"));
            Assert.False(server.Files.ContainsKey(
                QwarkClient.SaveFileConsolePath(Title, "any%", "kerwan.sav") + QwarkClient.SaveFileSumExtension));
        }
    }

    [Fact]
    public async Task DeletingAFileOnlyThisPcHasTouchesNothingOnTheConsole()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            library.Write(Title, "any%", "veldin.sav", new byte[] { 1, 2, 3 });

            var entry = Assert.Single(SaveFileMerge.Build(null, library.FilesWithCrc(Title, "any%")));
            await SaveFileTransfer.DeleteAsync(client, library, Title, "any%", entry);

            Assert.Empty(library.Files(Title, "any%"));
            Assert.Empty(server.Files);
        }
    }

    [Fact]
    public async Task FileRenameRefusesToOverwriteAndSaysWhenThereIsNothingToMove()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.WriteFileAsync("/dev_hdd0/qwark/one.bin", new byte[] { 1 });
            await client.WriteFileAsync("/dev_hdd0/qwark/two.bin", new byte[] { 2 });

            var onto = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.FileRenameAsync("/dev_hdd0/qwark/one.bin", "/dev_hdd0/qwark/two.bin"));
            Assert.Equal(Status.BadArg, onto.Status);

            var missing = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.FileRenameAsync("/dev_hdd0/qwark/nothing.bin", "/dev_hdd0/qwark/three.bin"));
            Assert.Equal(Status.NotFound, missing.Status);

            await client.FileRenameAsync("/dev_hdd0/qwark/one.bin", "/dev_hdd0/qwark/three.bin");
            Assert.False(server.Files.ContainsKey("/dev_hdd0/qwark/one.bin"));
            Assert.Equal(new byte[] { 1 }, server.Files["/dev_hdd0/qwark/three.bin"]);
        }
    }

    [Fact]
    public async Task ACategoryIsMadeAndRemovedOnTheConsole()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");
            await client.WriteFileAsync(QwarkClient.SaveFileConsolePath(Title, "any%", "veldin.sav"),
                new byte[] { 1, 2 });

            // A category with a save in it stays put.
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.SaveFileCategoryAsync(SaveFileCategoryOp.Delete, "any%"));
            Assert.Equal(Status.IoError, refused.Status);

            await client.FileDeleteAsync(QwarkClient.SaveFileConsolePath(Title, "any%", "veldin.sav"));
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Delete, "any%");
            Assert.Empty(await client.SaveFileCategoriesAsync());
        }
    }

    [Fact]
    public async Task TheLibraryOpsAreRefusedWhereCodeCannotBePatched()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.SaveFileUnsupported = true;

            foreach (var call in new Func<Task>[]
                     {
                         () => client.SaveFileCategoriesAsync(),
                         () => client.SaveFileListAsync("any%"),
                         () => client.SaveFileStoreAsync("any%", "a.sav"),
                         () => client.SaveFileRestoreAsync("any%", "a.sav"),
                         () => client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%"),
                     })
            {
                var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => call());
                Assert.Equal(Status.Unsupported, refused.Status);
            }
        }
    }

    [Fact]
    public async Task AStoreUnderANameThatIsNotASaveIsRefused()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.SaveFileStoreAsync("any%", "veldin.txt"));
            Assert.Equal(Status.BadArg, refused.Status);
        }
    }

    // ---------------------------------------------------------------- the local library

    [Fact]
    public void TheLocalListingCarriesASizeAndACrcPerFile()
    {
        var library = new SaveFileLibrary(_root);
        library.Write(Title, "misc", "one.sav", new byte[] { 1, 2, 3 });
        library.Write(Title, "misc", "two.sav", new byte[] { 4 });

        var files = library.FilesWithCrc(Title, "misc");

        Assert.Equal(new[] { "one.sav", "two.sav" }, files.Select(f => f.Name).ToArray());
        Assert.Equal(3, files[0].Size);
        Assert.Equal(Crc32.Compute(new byte[] { 1, 2, 3 }), files[0].Crc);
        Assert.Equal(files[0].Crc, library.Crc(Title, "misc", "one.sav"));

        // The cache follows the file rather than the name: a rewrite changes the answer.
        library.Write(Title, "misc", "one.sav", new byte[] { 9, 9, 9, 9 });
        Assert.Equal(Crc32.Compute(new byte[] { 9, 9, 9, 9 }), library.Crc(Title, "misc", "one.sav"));
    }

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

    [Theory]
    [InlineData("veldin.sav", "veldin")]
    [InlineData("veldin.SAV", "veldin")]
    [InlineData("start of veldin.sav", "start of veldin")]
    [InlineData("veldin.100.sav", "veldin.100")]
    [InlineData("veldin", "veldin")]
    [InlineData(".sav", ".sav")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TheListingDropsTheSuffixEveryFileInTheLibraryHas(string? file, string expected)
    {
        Assert.Equal(expected, SaveFileLibrary.DisplayName(file));

        // And what is shown goes back to the file it came from, which is what rename works on.
        if (file is not null && file.EndsWith(SaveFileLibrary.Extension, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(file.ToLowerInvariant(),
                SaveFileLibrary.EnsureExtension(SaveFileLibrary.DisplayName(file)).ToLowerInvariant());
        }
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
