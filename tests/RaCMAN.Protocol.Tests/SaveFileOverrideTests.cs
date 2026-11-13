using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// A save stored over an existing one against the fake console: the store goes to the file's own
/// name and category, the console's CRC and sidecar and this PC's mirror all follow, and a refusal
/// leaves the old save on both sides.
/// </summary>
public class SaveFileOverrideTests : IDisposable
{
    private const string Title = "NPEA00385";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "racman-override-" + Guid.NewGuid().ToString("N"));

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

    internal static byte[] Pattern(int size, int seed)
    {
        var bytes = new byte[size];
        for (int i = 0; i < size; i++) bytes[i] = (byte)(i * 7 + (i >> 8) + seed);
        return bytes;
    }

    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync()
    {
        var server = new FakeQwarkServer();
        server.Start();
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    private static string SumOnConsole(FakeQwarkServer server, string category, string name) =>
        System.Text.Encoding.ASCII.GetString(
            server.Files[QwarkClient.SaveFileConsolePath(Title, category, name) + QwarkClient.SaveFileSumExtension]);

    [Fact]
    public async Task AnOverrideStoresOverTheFileUnderItsNameAndBothCrcsFollow()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            var old = Pattern(server.SaveFileBuffer.Length, 1);
            server.SaveAsideContent = old;
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");
            await SaveFileTransfer.StoreAsync(client, library, Title, "any%", "Kerwan.sav", mirror: true);

            // The listing is read once first, so the PC's CRC of the old file is in the cache.
            var before = Assert.Single(SaveFileMerge.Build(
                await client.SaveFileListAsync("any%"), library.FilesWithCrc(Title, "any%")));
            Assert.Equal(Crc32.Compute(old), before.PcCrc);

            var current = Pattern(server.SaveFileBuffer.Length, 99);
            server.SaveAsideContent = current;

            string path = await SaveFileTransfer.OverrideAsync(client, library, Title, "any%", before, mirror: true);

            Assert.Equal(library.FilePath(Title, "any%", "Kerwan.sav"), path);
            Assert.Equal(new[] { "store any%/Kerwan.sav", "store any%/Kerwan.sav" }, server.SaveFileLibraryOrder);

            // Still one save in the category, the new bytes on both sides, the console's sidecar
            // rewritten for them, and the row agreeing with itself.
            var console = Assert.Single(await client.SaveFileListAsync("any%"));
            Assert.Equal("Kerwan.sav", console.Name);
            Assert.Equal(Crc32.Compute(current), console.Crc);
            Assert.Equal(Crc32.ToSumText(Crc32.Compute(current)), SumOnConsole(server, "any%", "Kerwan.sav"));
            Assert.Equal(current, library.Read(Title, "any%", "Kerwan.sav"));

            var after = Assert.Single(SaveFileMerge.Build(
                await client.SaveFileListAsync("any%"), library.FilesWithCrc(Title, "any%")));
            Assert.Equal(Crc32.Compute(current), after.PcCrc);
            Assert.True(after.Agrees);
        }
    }

    [Fact]
    public async Task WithMirroringOffACopyThisPcHasIsStillReplacedSoItCannotLoadTheOldSaveBack()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            server.SaveAsideContent = Pattern(server.SaveFileBuffer.Length, 1);
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");
            await SaveFileTransfer.StoreAsync(client, library, Title, "any%", "Kerwan.sav", mirror: true);
            await SaveFileTransfer.StoreAsync(client, library, Title, "any%", "Novalis.sav", mirror: false);

            var rows = SaveFileMerge.Build(await client.SaveFileListAsync("any%"), library.FilesWithCrc(Title, "any%"));
            var both = rows.Single(r => r.Name == "Kerwan.sav");
            var consoleOnly = rows.Single(r => r.Name == "Novalis.sav");
            Assert.Equal(SaveFileLocation.Console, consoleOnly.Location);

            var current = Pattern(server.SaveFileBuffer.Length, 42);
            server.SaveAsideContent = current;
            await SaveFileTransfer.OverrideAsync(client, library, Title, "any%", both, mirror: false);
            await SaveFileTransfer.OverrideAsync(client, library, Title, "any%", consoleOnly, mirror: false);

            var after = SaveFileMerge.Build(await client.SaveFileListAsync("any%"), library.FilesWithCrc(Title, "any%"));
            var kerwan = after.Single(r => r.Name == "Kerwan.sav");
            Assert.True(kerwan.Agrees);
            Assert.Equal(SaveFileLoadPlan.Restore, SaveFileMerge.PlanLoad(kerwan));
            Assert.Equal(current, library.Read(Title, "any%", "Kerwan.sav"));

            // A save only the console had stays only on the console, as mirroring off says.
            var novalis = after.Single(r => r.Name == "Novalis.sav");
            Assert.Equal(SaveFileLocation.Console, novalis.Location);
            Assert.Equal(Crc32.Compute(current), novalis.ConsoleCrc);
        }
    }

    [Fact]
    public async Task AnOverrideTheGameRefusesLeavesTheOldSaveOnBothSides()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            var old = Pattern(server.SaveFileBuffer.Length, 1);
            server.SaveAsideContent = old;
            await client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");
            await SaveFileTransfer.StoreAsync(client, library, Title, "any%", "Kerwan.sav", mirror: true);
            var entry = Assert.Single(SaveFileMerge.Build(
                await client.SaveFileListAsync("any%"), library.FilesWithCrc(Title, "any%")));

            server.SaveAsideContent = Pattern(server.SaveFileBuffer.Length, 7);
            server.SaveFileRefusal = SaveFileError.NotIdle;

            var refused = await Assert.ThrowsAsync<SaveFileTransfer.RefusedException>(
                () => SaveFileTransfer.OverrideAsync(client, library, Title, "any%", entry, mirror: true));
            Assert.Equal(SaveFileError.NotIdle, refused.Error);

            var console = Assert.Single(await client.SaveFileListAsync("any%"));
            Assert.Equal(Crc32.Compute(old), console.Crc);
            Assert.Equal(Crc32.ToSumText(Crc32.Compute(old)), SumOnConsole(server, "any%", "Kerwan.sav"));
            Assert.Equal(old, library.Read(Title, "any%", "Kerwan.sav"));
        }
    }

    [Fact]
    public async Task NothingIsStoredOverAPremadeSaveOrUnderANameTheConsoleRefuses()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var library = new SaveFileLibrary(_root);
            var premade = new SaveFileEntry("Veldin.sav", true, true, 1, 1, 1, 1);
            var accented = new SaveFileEntry("Véldin.sav", false, true, 0, 1, 0, 1);

            await Assert.ThrowsAsync<IOException>(
                () => SaveFileTransfer.OverrideAsync(client, library, Title, PremadeSaveFiles.Category, premade, mirror: true));
            await Assert.ThrowsAsync<IOException>(
                () => SaveFileTransfer.OverrideAsync(client, library, Title, "any%", accented, mirror: true));

            Assert.Empty(server.SaveFileLibraryOrder);
        }
    }

    [Fact]
    public void OverrideGoesByTheSaveButtonsRule()
    {
        var save = new Feature(9, FeatureKind.Action, 1, 0, FeatureFlags.SaveAside, 0xFF, 0, 0, "Set aside file");

        Assert.True(SaveFilesPanel.CanStore(true, save, "any%", "Kerwan.sav"));
        Assert.False(SaveFilesPanel.CanStore(false, save, "any%", "Kerwan.sav"));
        Assert.False(SaveFilesPanel.CanStore(true, null, "any%", "Kerwan.sav"));
        Assert.False(SaveFilesPanel.CanStore(true, save, PremadeSaveFiles.Category, "Kerwan.sav"));
        Assert.False(SaveFilesPanel.CanStore(true, save, "any%", "Véldin.sav"));
        Assert.False(SaveFilesPanel.CanStore(true, save, "any%", new string('k', 29) + ".sav"));
        Assert.False(SaveFilesPanel.CanStore(true, save, "any%", null));
    }

    [Fact]
    public void TheQuestionNamesTheFileAndWhereItIsReplaced()
    {
        var both = new SaveFileEntry("Kerwan.sav", true, true, 1, 1, 1, 1);
        var console = both with { OnPc = false };

        Assert.Equal("Kerwan will be replaced with the game's current state on your console and on this PC.",
            SaveFilesPanel.OverrideWhere(both));
        Assert.Equal("Kerwan will be replaced with the game's current state on your console.",
            SaveFilesPanel.OverrideWhere(console));
    }
}

/// <summary>
/// The Save files panel's Override, through the client's own state against the fake console. The
/// panel keeps its question in statics that every session change clears, so these run alone.
/// </summary>
[Collection(SaveFilePanelCollection.Name)]
public class SaveFileOverridePanelTests : IDisposable
{
    private const string Title = "NPEA00385";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "racman-override-panel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SaveFilesPanel.Reset();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(FakeQwarkServer Server, AppState State)> StartAsync(FakeQwarkServer server)
    {
        server.Start();
        var state = new AppState(new Settings { Ps3 = { AutoReconnect = false }, SaveFilesPath = _root });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);
        return (server, state);
    }

    private static async Task<bool> PumpAsync(AppState state, Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            state.Tick(1f / 60f);
            if (condition()) return true;
            await Task.Delay(16);
        }

        state.Tick(1f / 60f);
        return condition();
    }

    /// <summary>Keeps the client ticking for a while, for a test that something did not happen.</summary>
    private static async Task IdleAsync(AppState state, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            state.Tick(1f / 60f);
            await Task.Delay(16);
        }
    }

    private static async Task<SaveFileEntry> RowAsync(AppState state, string category, string name) =>
        SaveFileMerge.Build(await state.Client.SaveFileListAsync(category), state.SaveFiles.FilesWithCrc(Title, category))
            .Single(r => r.Name == name);

    [Fact]
    public async Task OverrideAsksFirstAndStoresOnlyOnTheYes()
    {
        var (server, state) = await StartAsync(new FakeQwarkServer());
        using (server)
        using (state)
        {
            var old = SaveFileOverrideTests.Pattern(server.SaveFileBuffer.Length, 1);
            server.SaveAsideContent = old;
            await state.Client.SaveFileCategoryAsync(SaveFileCategoryOp.Create, "any%");
            await SaveFileTransfer.StoreAsync(state.Client, state.SaveFiles, Title, "any%", "Kerwan.sav", mirror: true);
            var entry = await RowAsync(state, "any%", "Kerwan.sav");

            Assert.True(await PumpAsync(state, () => SaveFilesPanel.CanOverride(state, entry, "any%")),
                "Override is not offered on a console in game");

            var current = SaveFileOverrideTests.Pattern(server.SaveFileBuffer.Length, 50);
            server.SaveAsideContent = current;

            // The menu item only asks: nothing reaches the console while the question is open.
            var question = SaveFilesPanel.AskOverride(entry, Title, "any%");
            await IdleAsync(state, 400);
            Assert.Single(server.SaveFileLibraryOrder);
            Assert.Equal(Crc32.Compute(old), (await RowAsync(state, "any%", "Kerwan.sav")).ConsoleCrc);

            Assert.True(SaveFilesPanel.ConfirmOverride(state, question));
            Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Text == "Saved over 'Kerwan'")),
                "the override never finished: " + string.Join(" | ", state.Toasts.Select(t => t.Text)));

            Assert.Equal(new[] { "store any%/Kerwan.sav", "store any%/Kerwan.sav" }, server.SaveFileLibraryOrder);
            var after = await RowAsync(state, "any%", "Kerwan.sav");
            Assert.True(after.Agrees);
            Assert.Equal(Crc32.Compute(current), after.ConsoleCrc);
            Assert.Equal(current, state.SaveFiles.Read(Title, "any%", "Kerwan.sav"));
        }
    }

    [Fact]
    public async Task OverrideIsOfferedExactlyWhereSavingIsAndWorksUnderRpcs3()
    {
        // RPCS3 with no qwark patch: the helper is not in the game, so nothing can be stored.
        var (server, state) = await StartAsync(new FakeQwarkServer { Emulator = true, NoCodePatches = true });
        using (server)
        using (state)
        {
            var entry = new SaveFileEntry("Kerwan.sav", false, true, 0, 1, 0, (long)server.SaveFileBuffer.Length);
            state.SaveFiles.Write(Title, "any%", "Kerwan.sav", SaveFileOverrideTests.Pattern(server.SaveFileBuffer.Length, 1));

            Assert.True(await PumpAsync(state, () => state.Ingame && state.CodePatchesUnsupported
                                                     && state.SaveFile.Supported && state.Describe.SaveAsideAction is not null));
            Assert.False(SaveFilesPanel.CanOverride(state, entry, "any%"));
            Assert.False(SaveFilesPanel.ConfirmOverride(state, SaveFilesPanel.AskOverride(entry, Title, "any%")));
            await IdleAsync(state, 300);
            Assert.Empty(server.SaveFileLibraryOrder);

            // The patch in at the next boot: now it is offered, but never on a premade savefile.
            FakeScript.Apply(server, "sfpatch");
            Assert.True(await PumpAsync(state, () => SaveFilesPanel.CanOverride(state, entry, "any%")),
                "Override is not offered once the RPCS3 patch is in");
            Assert.False(SaveFilesPanel.CanOverride(state, entry, PremadeSaveFiles.Category));

            var current = SaveFileOverrideTests.Pattern(server.SaveFileBuffer.Length, 77);
            server.SaveAsideContent = current;
            server.SaveFileTransferPolls = 30;

            Assert.True(SaveFilesPanel.ConfirmOverride(state, SaveFilesPanel.AskOverride(entry, Title, "any%")));

            // While it runs, a second one is not offered and a second yes starts nothing.
            Assert.False(SaveFilesPanel.CanOverride(state, entry, "any%"));
            Assert.False(SaveFilesPanel.ConfirmOverride(state, SaveFilesPanel.AskOverride(entry, Title, "any%")));

            Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Text == "Saved over 'Kerwan'")),
                "the override never finished: " + string.Join(" | ", state.Toasts.Select(t => t.Text)));
            Assert.Equal(new[] { "store any%/Kerwan.sav" }, server.SaveFileLibraryOrder);
            Assert.Equal(current, state.SaveFiles.Read(Title, "any%", "Kerwan.sav"));

            // And at the XMB there is nothing to store from.
            Assert.True(await PumpAsync(state, () => SaveFilesPanel.CanOverride(state, entry, "any%")));
            FakeScript.Apply(server, "xmb");
            Assert.True(await PumpAsync(state, () => !state.Ingame));
            Assert.False(SaveFilesPanel.CanOverride(state, entry, "any%"));
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SaveFilePanelCollection
{
    public const string Name = "Save files panel";
}
