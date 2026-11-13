using System.Text;

using RaCMAN.App;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// A webMAN load on a console that also boots qwark. The load writes ui.pak and the game modules to
/// their final paths, where the module it loads reads them, and they belong to that module's build;
/// so the copy boot_plugins.txt names is replaced with the same module, the safe way the standalone
/// update does it (written as <c>.new</c>, read back, the old one kept as <c>.old</c>, swapped in),
/// or the next reboot would start the old module beside the new files. A console whose list names
/// no qwark is loaded live only and keeps it that way. A boot copy that cannot be replaced is left
/// whole, the load still happens, and the user is told once.
/// </summary>
public class WebManBootCopyTests
{
    private static readonly string Boot = WebManLoader.BootPath;
    private const string WebManLine = "/dev_hdd0/plugins/webftp_server.sprx";
    private const string BootList = WebManLoader.BootPluginsPath;

    private static readonly byte[] OldModule = { 9, 8, 7, 6, 5 };

    /// <summary>The module the fake release ships (<see cref="BootInstallTests.FakeRelease"/>).</summary>
    private static readonly byte[] NewModule = { 1, 2, 3, 4 };

    [Fact]
    public async Task TheBootCopyTheListNamesIsReplacedTheOldOneKeptAndTheModuleStillLoadedLive()
    {
        var list = Encoding.UTF8.GetBytes($"{Boot}\n{WebManLine}\n");

        using var server = new FakeFtpServer();
        server.Stored[BootList] = list;
        server.Stored[Boot] = OldModule;
        server.Start();

        // What the boot path held when webMAN was asked to load: the new module, already in place.
        var webMan = new WebManStub(() => server.Stored.TryGetValue(Boot, out var held) ? held : null);
        var loader = new WebManLoader(new HttpClient(webMan)) { FtpPort = server.Port };

        var result = await loader.LoadAsync("127.0.0.1", BootInstallTests.FakeRelease(null), 5);

        Assert.Equal(BootCopyOutcome.Replaced, result.BootCopy);
        Assert.Equal(Boot, result.BootPath);
        Assert.Null(result.BootCopyError);

        Assert.Equal(NewModule, server.Stored[Boot]);
        Assert.Equal(OldModule, server.Stored[Boot + SprxUpdate.BackupSuffix]);
        Assert.False(server.Stored.ContainsKey(Boot + SprxUpdate.TempSuffix));

        // The live load is unchanged: the scratch copy, loaded into the slot asked for.
        Assert.Equal(NewModule, server.Stored[WebManLoader.RemotePath]);
        var (url, bootAtLoad) = Assert.Single(webMan.Requests);
        Assert.Equal("http://127.0.0.1/vshplugin.ps3mapi?prx=%2Fdev_hdd0%2Ftmp%2Fqwark.sprx&load_slot=5", url);
        Assert.Equal(NewModule, bootAtLoad);

        // The list is only read: the very bytes that were there, not a copy the client wrote back.
        Assert.Same(list, server.Stored[BootList]);

        // Written beside it, read back, the old .old cleared, the copy moved aside, the new one in.
        await BootInstallTests.WaitForQuitAsync(server);
        var commands = server.Commands.Where(c => !c.StartsWith("PASV", StringComparison.Ordinal)).ToList();
        int stored = commands.IndexOf($"STOR {Boot}.new");
        Assert.True(stored >= 0, string.Join(" | ", commands));
        Assert.Equal(
            new[]
            {
                $"STOR {Boot}.new",
                $"RETR {Boot}.new",
                $"DELE {Boot}.old",
                $"RNFR {Boot}",
                $"RNTO {Boot}.old",
                $"RNFR {Boot}.new",
                $"RNTO {Boot}",
            },
            commands.Skip(stored).Take(7));
        Assert.DoesNotContain($"STOR {Boot}", commands);
        Assert.DoesNotContain($"STOR {BootList}", commands);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AConsoleWhoseListNamesNoQwarkIsLoadedLiveAndNothingElseIsWritten(bool hasList)
    {
        var list = Encoding.UTF8.GetBytes($"{WebManLine}\n");
        var webManPlugin = new byte[] { 42, 42 };

        using var server = new FakeFtpServer();
        if (hasList) server.Stored[BootList] = list;
        server.Stored[WebManLine] = webManPlugin;
        server.Start();

        var webMan = new WebManStub(() => null);
        var loader = new WebManLoader(new HttpClient(webMan)) { FtpPort = server.Port };

        var result = await loader.LoadAsync("127.0.0.1", BootInstallTests.FakeRelease(null), 5);

        Assert.Equal(BootCopyOutcome.NotBooted, result.BootCopy);
        Assert.Null(result.BootCopyError);
        Assert.Single(webMan.Requests);
        Assert.Equal(NewModule, server.Stored[WebManLoader.RemotePath]);

        // Nothing new in /dev_hdd0/plugins, and the list exactly as it was, or still absent.
        var plugins = server.Stored.Keys.Where(k => k.StartsWith(WebManLoader.PluginsDirectory + "/", StringComparison.Ordinal));
        Assert.Equal(new[] { WebManLine }, plugins);
        Assert.Same(webManPlugin, server.Stored[WebManLine]);
        if (hasList) Assert.Same(list, server.Stored[BootList]);
        else Assert.False(server.Stored.ContainsKey(BootList));

        await BootInstallTests.WaitForQuitAsync(server);
        Assert.DoesNotContain(server.Commands, c => c.Contains(WebManLoader.PluginsDirectory, StringComparison.Ordinal));
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("STOR " + BootList, StringComparison.Ordinal));
    }

    /// <summary>
    /// The upload of the boot copy breaks off half way. The half-written file is the <c>.new</c>,
    /// never the boot path: the old boot copy is untouched and nothing was moved, the module still
    /// loads live, and the user gets the one toast that says the reboot will load the old one.
    /// </summary>
    [Fact]
    public async Task AnUploadCutShortLeavesTheOldBootCopyWholeStillLoadsAndSaysSoOnce()
    {
        using var server = new FakeFtpServer();
        server.Stored[BootList] = Encoding.UTF8.GetBytes($"{Boot}\n");
        server.Stored[Boot] = OldModule;
        server.StorAborts[Boot + SprxUpdate.TempSuffix] = "451 Transfer aborted";
        server.Start();

        var webMan = new WebManStub(() => null);
        using var state = new AppState(new Settings())
        {
            WebMan = new WebManLoader(new HttpClient(webMan)) { FtpPort = server.Port },
        };

        var result = await state.LoadThroughWebManAsync("127.0.0.1", BootInstallTests.FakeRelease(null), 5);

        Assert.Equal(BootCopyOutcome.Failed, result.BootCopy);
        Assert.Contains("451", result.BootCopyError, StringComparison.Ordinal);

        Assert.Same(OldModule, server.Stored[Boot]);
        Assert.False(server.Stored.ContainsKey(Boot + SprxUpdate.BackupSuffix));
        Assert.Single(webMan.Requests);
        Assert.Equal(NewModule, server.Stored[WebManLoader.RemotePath]);

        state.Tick(1f / 60f);
        var toast = Assert.Single(state.Toasts, t => t.Kind == ToastKind.Error);
        Assert.Equal(AppState.BootCopyFailedToast(result.BootCopyError!), toast.Text);
        Assert.Contains("old one", toast.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The swap's last move is refused, the moment the console has no module at the boot path: the
    /// old copy is put back where it was, the load still happens, and the result says why.
    /// </summary>
    [Fact]
    public async Task ASwapRefusedHalfWayPutsTheOldBootCopyBack()
    {
        using var server = new FakeFtpServer();
        server.Stored[BootList] = Encoding.UTF8.GetBytes($"{Boot}\n");
        server.Stored[Boot] = OldModule;
        server.RntoRefused.Add(Boot);
        server.Start();

        var webMan = new WebManStub(() => null);
        var loader = new WebManLoader(new HttpClient(webMan)) { FtpPort = server.Port };

        var result = await loader.LoadAsync("127.0.0.1", BootInstallTests.FakeRelease(null), 5);

        Assert.Equal(BootCopyOutcome.Failed, result.BootCopy);
        Assert.StartsWith("putting it in place", result.BootCopyError, StringComparison.Ordinal);
        Assert.Contains("553", result.BootCopyError, StringComparison.Ordinal);
        Assert.Same(OldModule, server.Stored[Boot]);
        Assert.Single(webMan.Requests);
    }

    /// <summary>
    /// A list naming qwark somewhere else is the copy that is replaced; a qwark.sprx in
    /// /dev_hdd0/plugins that the list does not name is not the console's boot copy and is left be.
    /// </summary>
    [Fact]
    public async Task ACustomBootPathOnTheListIsTheOneReplaced()
    {
        const string custom = "/dev_hdd0/qwark/qwark.sprx";
        var leftover = new byte[] { 7, 7, 7 };

        using var server = new FakeFtpServer();
        server.Stored[BootList] = Encoding.UTF8.GetBytes($"{WebManLine}\r\n{custom}\r\n");
        server.Stored[custom] = OldModule;
        server.Stored[Boot] = leftover;
        server.Start();

        var loader = new WebManLoader(new HttpClient(new WebManStub(() => null))) { FtpPort = server.Port };
        var result = await loader.LoadAsync("127.0.0.1", BootInstallTests.FakeRelease(null), 5);

        Assert.Equal(BootCopyOutcome.Replaced, result.BootCopy);
        Assert.Equal(custom, result.BootPath);
        Assert.Equal(NewModule, server.Stored[custom]);
        Assert.Equal(OldModule, server.Stored[custom + SprxUpdate.BackupSuffix]);
        Assert.Same(leftover, server.Stored[Boot]);
        Assert.False(server.Stored.ContainsKey(Boot + SprxUpdate.BackupSuffix));
    }

    /// <summary>A list that boots the scratch copy itself has had it written by the live upload: once, and no swap.</summary>
    [Fact]
    public async Task ABootPathThatIsTheLiveUploadIsWrittenOnce()
    {
        using var server = new FakeFtpServer();
        server.Stored[BootList] = Encoding.UTF8.GetBytes($"{WebManLoader.RemotePath}\n");
        server.Start();

        var loader = new WebManLoader(new HttpClient(new WebManStub(() => null))) { FtpPort = server.Port };
        var result = await loader.LoadAsync("127.0.0.1", BootInstallTests.FakeRelease(null), 5);

        Assert.Equal(BootCopyOutcome.SameAsLive, result.BootCopy);
        Assert.Equal(NewModule, server.Stored[WebManLoader.RemotePath]);

        await BootInstallTests.WaitForQuitAsync(server);
        Assert.Single(server.Commands, c => c.StartsWith("STOR ", StringComparison.Ordinal));
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("RNFR ", StringComparison.Ordinal));
    }

    /// <summary>
    /// A game module an earlier standalone update staged as <c>rac2.sprx.new</c> is another build's,
    /// and the core would put it over the module the load has just written the next time RaC2
    /// boots, without a look at its build. The load takes it away; a module it did not send keeps its own.
    /// </summary>
    [Fact]
    public async Task AStagedGameModuleFromAnEarlierUpdateGoesWhenTheLoadWritesThatModule()
    {
        var staleTwo = new byte[] { 2, 2, 2 };
        var staleFour = new byte[] { 4, 4, 4 };
        var two = new byte[100];
        GameModule.SelfMagic.CopyTo(two);

        string sprx = BootInstallTests.FakeRelease(null);
        File.WriteAllBytes(GameModule.BesideSprx(sprx, GameId.Rac2), two);

        using var server = new FakeFtpServer();
        server.Stored[GameModule.StagedPath(GameId.Rac2)] = staleTwo;
        server.Stored[GameModule.StagedPath(GameId.Rac4)] = staleFour;
        server.Start();

        var loader = new WebManLoader(new HttpClient(new WebManStub(() => null))) { FtpPort = server.Port };
        await loader.LoadAsync("127.0.0.1", sprx, 5);

        Assert.Equal(two, server.Stored[GameModule.ConsolePath(GameId.Rac2)]);
        Assert.False(server.Stored.ContainsKey(GameModule.StagedPath(GameId.Rac2)));
        Assert.Same(staleFour, server.Stored[GameModule.StagedPath(GameId.Rac4)]);
    }
}
