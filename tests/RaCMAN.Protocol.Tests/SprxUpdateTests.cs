using System.Diagnostics;
using System.Text;

using RaCMAN.App;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The standalone module update: a console that loads qwark.sprx itself at boot and reports a build
/// older than this client's gets the shipped module written to its boot path through qwark's own
/// file ops, once per connection, and is then told to restart. Everything here runs against the
/// fake console's in-memory filesystem, so the whole sequence — where the boot copy is, what is
/// uploaded, what is moved aside — can be read off <see cref="FakeQwarkServer.Files"/>.
/// </summary>
public class SprxUpdateTests
{
    private const string OtherBootPath = "/dev_hdd0/qwark/qwark.sprx";

    private static string TempPath(string bootPath) => bootPath + SprxUpdate.TempSuffix;

    private static string BackupPath(string bootPath) => bootPath + SprxUpdate.BackupSuffix;

    /// <summary>A module-sized blob: several chunks of it, so the upload and the read-back are chunked.</summary>
    private static byte[] Module(byte seed, int length = 70_000)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(i * 31 + seed);
        return bytes;
    }

    /// <summary>The qwark.sprx "beside the client": a real file, pointed at by the settings.</summary>
    private static string WriteModule(byte[] bytes)
    {
        var path = Path.Combine(TestDataFolder.Root, $"qwark-{Guid.NewGuid():N}.sprx");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task<AppState> ConnectAsync(FakeQwarkServer server, string sprxPath, bool standalone = true)
    {
        var settings = new Settings
        {
            AutoReconnect = false,
            SprxPath = sprxPath,
            StandaloneConnection = standalone,
        };

        var state = new AppState(settings);
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);
        return state;
    }

    /// <summary>Runs frames until the condition holds, the way the render loop would.</summary>
    private static async Task<bool> PumpAsync(AppState state, Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            state.Tick(1f / 60f);
            if (condition()) return true;
            await Task.Delay(16);
        }

        state.Tick(1f / 60f);
        return condition();
    }

    /// <summary>Runs frames for a while and asserts nothing: what "the client left it alone" needs.</summary>
    private static async Task PumpForAsync(AppState state, int milliseconds = 600)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            state.Tick(1f / 60f);
            await Task.Delay(16);
        }

        state.Tick(1f / 60f);
    }

    // ---------------------------------------------------------------- where the boot copy is

    [Fact]
    public void TheBootPathIsTheLineThatNamesTheModule()
    {
        Assert.Equal(OtherBootPath, SprxUpdate.BootPathFrom(
            $"/dev_hdd0/plugins/webftp_server.sprx\n{OtherBootPath}\n"));

        // Windows line endings and the spaces a hand-edited list collects.
        Assert.Equal("/dev_hdd0/plugins/QWARK.SPRX", SprxUpdate.BootPathFrom(
            "  /dev_hdd0/plugins/QWARK.SPRX  \r\n/dev_hdd0/plugins/webftp_server.sprx\r\n"));
    }

    [Fact]
    public void AListThatNamesNoModuleFallsBackToWhereTheBootInstallPutsIt()
    {
        Assert.Equal(WebManLoader.BootPath, SprxUpdate.BootPathFrom(null));
        Assert.Equal(WebManLoader.BootPath, SprxUpdate.BootPathFrom(string.Empty));
        Assert.Equal(WebManLoader.BootPath, SprxUpdate.BootPathFrom("/dev_hdd0/plugins/webftp_server.sprx\n"));

        // Not a path, so not something FILE_OPEN could ever be given.
        Assert.Equal(WebManLoader.BootPath, SprxUpdate.BootPathFrom("qwark.sprx\n"));
    }

    // ---------------------------------------------------------------- the update itself

    [Fact]
    public async Task AStaleStandaloneConsoleGetsTheShippedModuleAndKeepsTheOldOne()
    {
        var shipped = Module(7);
        var onConsole = Module(200, 4096);

        using var server = new FakeQwarkServer();
        server.Files[WebManLoader.BootPath] = onConsole;
        server.Files[WebManLoader.BootPluginsPath] = Utf8($"{WebManLoader.BootPath}\n");
        server.Start();

        using var state = await ConnectAsync(server, WriteModule(shipped));

        Assert.True(await PumpAsync(state, () => state.QwarkUpdateStaged > 0));

        Assert.Equal(QwarkClient.ExpectedQwarkBuild, state.QwarkUpdateStaged);
        Assert.Equal(shipped, server.Files[WebManLoader.BootPath]);
        Assert.Equal(onConsole, server.Files[BackupPath(WebManLoader.BootPath)]);
        Assert.False(server.Files.ContainsKey(TempPath(WebManLoader.BootPath)));

        // The module in memory is still the old one, so the warning stays; what changed is what it
        // asks the user to do.
        Assert.True(state.QwarkStale);
        Assert.Contains($"reboot to update", state.QwarkUpdateNotice, StringComparison.Ordinal);
        Assert.Contains(state.Toasts, toast => toast.Text == state.QwarkUpdateNotice);
        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);
    }

    /// <summary>
    /// Revision 1.11 chunks: a 70 KB module goes up in five FILE_WRITEs of 16000 bytes and is read
    /// back in as many FILE_READs. More round trips, and the same module at the other end.
    /// </summary>
    [Fact]
    public async Task TheModuleGoesUpInChunksTheConsoleWillTake()
    {
        var shipped = Module(5);

        using var server = new FakeQwarkServer();
        server.Files[WebManLoader.BootPath] = Module(90, 4096);
        server.Files[WebManLoader.BootPluginsPath] = Utf8($"{WebManLoader.BootPath}\n");
        server.Start();

        using var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);

        server.ClearRequestLog();
        var result = await SprxUpdate.RunAsync(client, shipped);

        Assert.Equal(WebManLoader.BootPath, result.BootPath);
        Assert.Equal(shipped, server.Files[WebManLoader.BootPath]);

        int chunks = (int)Math.Ceiling(shipped.Length / (double)QwarkClient.FileChunkSize);
        Assert.Equal(5, chunks);

        var log = server.RequestLog();
        Assert.Equal(chunks, log.Count(op => op == Opcode.FileWrite));

        // The read-back is the same count, and the boot list before it was one short read of its own.
        Assert.Equal(chunks + 1, log.Count(op => op == Opcode.FileRead));
    }

    [Fact]
    public async Task TheBootCopyIsTheOneBootPluginsTxtNames()
    {
        var shipped = Module(11);
        var onConsole = Module(90, 4096);

        using var server = new FakeQwarkServer();
        server.Files[OtherBootPath] = onConsole;
        server.Files[WebManLoader.BootPluginsPath] =
            Utf8($"/dev_hdd0/plugins/webftp_server.sprx\r\n{OtherBootPath}\r\n");
        server.Start();

        using var state = await ConnectAsync(server, WriteModule(shipped));

        Assert.True(await PumpAsync(state, () => state.QwarkUpdateStaged > 0));

        Assert.Equal(shipped, server.Files[OtherBootPath]);
        Assert.Equal(onConsole, server.Files[BackupPath(OtherBootPath)]);

        // Nothing was written where the boot install would have put it: the list is the authority.
        Assert.False(server.Files.ContainsKey(WebManLoader.BootPath));
    }

    [Fact]
    public async Task AConsoleWithNoBootListStillGetsTheModuleWhereTheBootInstallPutsIt()
    {
        var shipped = Module(3);

        using var server = new FakeQwarkServer();
        server.Start();

        using var state = await ConnectAsync(server, WriteModule(shipped));

        Assert.True(await PumpAsync(state, () => state.QwarkUpdateStaged > 0));

        // FILE_READ answered NOT_FOUND for the list, and there was no copy to move aside.
        Assert.Equal(shipped, server.Files[WebManLoader.BootPath]);
        Assert.False(server.Files.ContainsKey(BackupPath(WebManLoader.BootPath)));
        Assert.False(server.Files.ContainsKey(TempPath(WebManLoader.BootPath)));
    }

    [Fact]
    public async Task TheUpdateWaitsForASessionTheFileOpsWillAnswer()
    {
        var shipped = Module(21);

        using var server = new FakeQwarkServer();
        server.Session = server.Session with { State = SessionState.Booting };
        server.Files[WebManLoader.BootPath] = Module(90, 4096);
        server.Start();

        using var state = await ConnectAsync(server, WriteModule(shipped));

        // A launch answers the file ops BUSY, so nothing is sent while one is running.
        await PumpForAsync(state);
        Assert.Equal(0, state.QwarkUpdateStaged);
        Assert.False(server.Files.ContainsKey(TempPath(WebManLoader.BootPath)));

        server.Session = server.Session with { State = SessionState.Xmb };
        Assert.True(await PumpAsync(state, () => state.QwarkUpdateStaged > 0));
        Assert.Equal(shipped, server.Files[WebManLoader.BootPath]);
    }

    // ---------------------------------------------------------------- when nothing should happen

    [Fact]
    public async Task WebManModeIsLeftExactlyAsItWas()
    {
        var onConsole = Module(90, 4096);

        using var server = new FakeQwarkServer();
        server.Files[WebManLoader.BootPath] = onConsole;
        server.Files[WebManLoader.BootPluginsPath] = Utf8($"{WebManLoader.BootPath}\n");
        server.Start();

        using var state = await ConnectAsync(server, WriteModule(Module(7)), standalone: false);

        await PumpForAsync(state);

        // That mode sends and loads the module through webMAN, so the file ops never come into it.
        Assert.True(state.QwarkStale);
        Assert.Equal(0, state.QwarkUpdateStaged);
        Assert.Equal(onConsole, server.Files[WebManLoader.BootPath]);
        Assert.False(server.Files.ContainsKey(TempPath(WebManLoader.BootPath)));
        Assert.False(server.Files.ContainsKey(BackupPath(WebManLoader.BootPath)));
    }

    [Fact]
    public async Task AConsoleOnTheCurrentBuildIsNotWrittenTo()
    {
        var onConsole = Module(90, 4096);

        using var server = new FakeQwarkServer();
        server.Session = server.Session with { QwarkVersion = QwarkClient.ExpectedQwarkBuild };
        server.Files[WebManLoader.BootPath] = onConsole;
        server.Start();

        using var state = await ConnectAsync(server, WriteModule(Module(7)));

        await PumpForAsync(state);

        Assert.False(state.QwarkStale);
        Assert.Equal(0, state.QwarkUpdateStaged);
        Assert.Equal(onConsole, server.Files[WebManLoader.BootPath]);
        Assert.False(server.Files.ContainsKey(TempPath(WebManLoader.BootPath)));
    }

    [Fact]
    public async Task WithNoModuleBesideTheClientNothingIsSentAndNothingIsSaid()
    {
        var onConsole = Module(90, 4096);

        using var server = new FakeQwarkServer();
        server.Files[WebManLoader.BootPath] = onConsole;
        server.Start();

        var missing = Path.Combine(TestDataFolder.Root, $"absent-{Guid.NewGuid():N}.sprx");
        using var state = await ConnectAsync(server, missing);

        await PumpForAsync(state);

        Assert.Equal(0, state.QwarkUpdateStaged);
        Assert.Equal(onConsole, server.Files[WebManLoader.BootPath]);

        // A source build has no SPRX beside it and the user did nothing to be told about.
        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);
    }

    // ---------------------------------------------------------------- when a step fails

    [Fact]
    public async Task AFailedUploadLeavesTheBootCopyAloneAndIsNotRetried()
    {
        var onConsole = Module(90, 4096);

        using var server = new FakeQwarkServer();
        server.Files[WebManLoader.BootPath] = onConsole;
        server.Files[WebManLoader.BootPluginsPath] = Utf8($"{WebManLoader.BootPath}\n");
        server.FileWriteStatus = Status.IoError;
        server.Start();

        using var state = await ConnectAsync(server, WriteModule(Module(7)));

        Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Kind == ToastKind.Error)));

        // One message, and it says which step went wrong and where.
        var errors = state.Toasts.Where(t => t.Kind == ToastKind.Error).ToArray();
        var error = Assert.Single(errors);
        Assert.Contains("uploading it to", error.Text, StringComparison.Ordinal);
        Assert.Contains(TempPath(WebManLoader.BootPath), error.Text, StringComparison.Ordinal);

        // The swap is the last step, so a failure before it cannot have touched the boot copy.
        Assert.Equal(onConsole, server.Files[WebManLoader.BootPath]);
        Assert.False(server.Files.ContainsKey(BackupPath(WebManLoader.BootPath)));
        Assert.Equal(0, state.QwarkUpdateStaged);

        // One attempt per connection: the console could answer again now and is not asked.
        server.FileWriteStatus = Status.Ok;
        await PumpForAsync(state);

        Assert.Equal(onConsole, server.Files[WebManLoader.BootPath]);
        Assert.Equal(0, state.QwarkUpdateStaged);
        Assert.Single(state.Toasts.Where(t => t.Kind == ToastKind.Error));
    }

    [Fact]
    public async Task TheUpdateRunsOnceWhileTheConsoleKeepsReportingTheOldBuild()
    {
        var shipped = Module(7);

        using var server = new FakeQwarkServer();
        server.Files[WebManLoader.BootPath] = Module(90, 4096);
        server.Start();

        using var state = await ConnectAsync(server, WriteModule(shipped));

        Assert.True(await PumpAsync(state, () => state.QwarkUpdateStaged > 0));

        // The console is still running the old module, which is what a boot plugin does until the
        // console is restarted. The file is already there, so it is not sent a second time.
        server.Files[WebManLoader.BootPath] = Module(123, 4096);
        await PumpForAsync(state);

        Assert.Equal(Module(123, 4096), server.Files[WebManLoader.BootPath]);
        Assert.False(server.Files.ContainsKey(TempPath(WebManLoader.BootPath)));
    }

    [Fact]
    public async Task TheRestartNoticeBelongsToTheConsoleItWasWrittenTo()
    {
        var shipped = Module(7);
        string sprx = WriteModule(shipped);

        using var first = new FakeQwarkServer();
        first.Files[WebManLoader.BootPath] = Module(90, 4096);
        first.Start();

        using var state = await ConnectAsync(first, sprx);
        Assert.True(await PumpAsync(state, () => state.QwarkUpdateStaged > 0));

        // Another console, which is stale too and has nothing staged on it. The notice from the
        // first one must not stand in for it, or this one would never be updated at all.
        var onSecond = Module(120, 4096);
        using var second = new FakeQwarkServer();
        second.Files[WebManLoader.BootPath] = onSecond;
        second.Start();

        await state.Client.ConnectAsync("127.0.0.1", second.Port);

        // Two notices: one per console that had the module written to it.
        Assert.True(await PumpAsync(state,
            () => state.Toasts.Count(toast => toast.Text == state.QwarkUpdateNotice) == 2));

        Assert.Equal(shipped, second.Files[WebManLoader.BootPath]);
        Assert.Equal(onSecond, second.Files[BackupPath(WebManLoader.BootPath)]);
    }

    // ---------------------------------------------------------------- the sequence by hand

    [Fact]
    public async Task TheUpdateByHandSwapsTheModuleInAndSaysWhatToDoNext()
    {
        var shipped = Module(44);
        var onConsole = Module(90, 4096);

        using var server = new FakeQwarkServer();

        // A console that is not stale at all: the button is the qwark developer's, not the rule's.
        server.Session = server.Session with { QwarkVersion = QwarkClient.ExpectedQwarkBuild };
        server.Files[OtherBootPath] = onConsole;
        server.Files[WebManLoader.BootPluginsPath] = Utf8($"{OtherBootPath}\n");
        server.Start();

        string sprx = WriteModule(shipped);
        using var state = await ConnectAsync(server, sprx);

        await PumpForAsync(state, 200);
        Assert.Equal(0, state.QwarkUpdateStaged);

        state.UpdateConsoleModule(sprx);

        Assert.True(await PumpAsync(state, () => state.QwarkUpdateStaged > 0));
        Assert.Equal(shipped, server.Files[OtherBootPath]);
        Assert.Equal(onConsole, server.Files[BackupPath(OtherBootPath)]);
        Assert.Contains(state.Toasts, toast => toast.Text == state.QwarkUpdateNotice);
    }
}
