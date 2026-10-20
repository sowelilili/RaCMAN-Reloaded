using System.Text;

using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The boot install against the loopback FTP server: what /dev_hdd0/boot_plugins.txt holds
/// afterwards and what the user is told about it.
/// <para>
/// PS3HEN loads that list in order, so qwark's line has to be the first one — a console that loaded
/// webMAN before it hung the XMB. The rewrite is written with \n endings and no byte-order mark, so
/// a list a hand editor left with CRLF comes back normalised, and blank lines do not survive it; a
/// list that already begins with the boot path is not rewritten at all.
/// </para>
/// </summary>
public class BootInstallTests
{
    private const string Qwark = "/dev_hdd0/plugins/qwark.sprx";
    private const string WebMan = "/dev_hdd0/plugins/webftp_server.sprx";
    private const string BootList = WebManLoader.BootPluginsPath;

    [Fact]
    public async Task AConsoleWithNoListGetsOneWithTheSingleLine()
    {
        using var server = new FakeFtpServer();
        server.Start();

        var result = await Loader(server).InstallToBootAsync("127.0.0.1", FakeSprx());

        Assert.Equal(BootInstallOutcome.Added, result.Outcome);
        Assert.Equal(1, result.Lines);
        Assert.False(result.BeyondHenLimit);
        Assert.Equal($"{Qwark}\n", Text(server, BootList));

        // And the module itself is where that line says it is, not in the scratch directory.
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, server.Stored[WebManLoader.BootPath]);
    }

    [Fact]
    public async Task QwarkGoesAboveWebManAndWebManKeepsItsLine()
    {
        using var server = new FakeFtpServer();
        server.Stored[BootList] = Encoding.UTF8.GetBytes($"{WebMan}\n");
        server.Start();

        var result = await Loader(server).InstallToBootAsync("127.0.0.1", FakeSprx());

        Assert.Equal(BootInstallOutcome.Added, result.Outcome);
        Assert.Equal(2, result.Lines);
        Assert.Equal($"{Qwark}\n{WebMan}\n", Text(server, BootList));
    }

    [Fact]
    public async Task AListThatAlreadyStartsWithItIsNotTouched()
    {
        var before = Encoding.UTF8.GetBytes($"{Qwark}\n{WebMan}\n");

        using var server = new FakeFtpServer();
        server.Stored[BootList] = before;
        server.Start();

        var result = await Loader(server).InstallToBootAsync("127.0.0.1", FakeSprx());

        Assert.Equal(BootInstallOutcome.AlreadyFirst, result.Outcome);
        Assert.Equal(2, result.Lines);

        // The very bytes that were there: a STOR would have left the server holding its own copy.
        Assert.Same(before, server.Stored[BootList]);
    }

    [Fact]
    public async Task AQwarkLineFurtherDownIsMovedToTheTop()
    {
        using var server = new FakeFtpServer();
        server.Stored[BootList] = Encoding.UTF8.GetBytes(
            $"{WebMan}\n/dev_hdd0/plugins/rebug_toolbox.sprx\n{Qwark}\n");

        server.Start();

        var result = await Loader(server).InstallToBootAsync("127.0.0.1", FakeSprx());

        Assert.Equal(BootInstallOutcome.Moved, result.Outcome);
        Assert.Equal(3, result.Lines);
        Assert.Equal(
            $"{Qwark}\n{WebMan}\n/dev_hdd0/plugins/rebug_toolbox.sprx\n",
            Text(server, BootList));
    }

    [Fact]
    public async Task EveryOtherLineNamingTheModuleGoesIncludingTheOldScratchPath()
    {
        using var server = new FakeFtpServer();
        server.Stored[BootList] = Encoding.UTF8.GetBytes(
            $"/dev_hdd0/tmp/qwark.sprx\n{WebMan}\n{Qwark}\n{Qwark}\n");

        server.Start();

        var result = await Loader(server).InstallToBootAsync("127.0.0.1", FakeSprx());

        Assert.Equal(BootInstallOutcome.Moved, result.Outcome);
        Assert.Equal(2, result.Lines);
        Assert.Equal($"{Qwark}\n{WebMan}\n", Text(server, BootList));
    }

    [Fact]
    public async Task AListTooLongForHenIsStillWrittenWholeAndReported()
    {
        var others = Enumerable.Range(1, 6).Select(i => $"/dev_hdd0/plugins/plugin{i}.sprx").ToArray();

        using var server = new FakeFtpServer();
        server.Stored[BootList] = Encoding.UTF8.GetBytes(string.Join('\n', others) + '\n');
        server.Start();

        var result = await Loader(server).InstallToBootAsync("127.0.0.1", FakeSprx());

        Assert.Equal(BootInstallOutcome.Added, result.Outcome);
        Assert.Equal(7, result.Lines);
        Assert.True(result.BeyondHenLimit);

        // Nobody's line is dropped to make room: the console reads the first six, and which of the
        // rest to give up is the user's call.
        Assert.Equal(string.Join('\n', others.Prepend(Qwark)) + '\n', Text(server, BootList));
    }

    [Fact]
    public async Task WindowsEndingsAreNormalisedToNewlines()
    {
        using var server = new FakeFtpServer();
        server.Stored[BootList] = Encoding.UTF8.GetBytes($"{WebMan}\r\n/dev_hdd0/plugins/other.sprx\r\n");
        server.Start();

        var result = await Loader(server).InstallToBootAsync("127.0.0.1", FakeSprx());

        Assert.Equal(BootInstallOutcome.Added, result.Outcome);
        Assert.Equal($"{Qwark}\n{WebMan}\n/dev_hdd0/plugins/other.sprx\n", Text(server, BootList));
        Assert.DoesNotContain("\r", Text(server, BootList));
    }

    [Fact]
    public async Task BlankLinesDoNotBecomeALeadingEmptyLine()
    {
        using var server = new FakeFtpServer();
        server.Stored[BootList] = Encoding.UTF8.GetBytes($"\n{WebMan}\n\n");
        server.Start();

        var result = await Loader(server).InstallToBootAsync("127.0.0.1", FakeSprx());

        Assert.Equal(2, result.Lines);
        Assert.Equal($"{Qwark}\n{WebMan}\n", Text(server, BootList));
    }

    [Fact]
    public async Task TheStandaloneUpdateStillFindsTheBootPathOnTheFirstLine()
    {
        using var server = new FakeFtpServer();
        server.Stored[BootList] = Encoding.UTF8.GetBytes($"{WebMan}\n/dev_hdd0/tmp/qwark.sprx\n");
        server.Start();

        await Loader(server).InstallToBootAsync("127.0.0.1", FakeSprx());

        // The list the install writes is the one SprxUpdate reads to find the file it replaces.
        Assert.Equal(WebManLoader.BootPath, SprxUpdate.BootPathFrom(Text(server, BootList)));
    }

    [Fact]
    public async Task RemoveDropsEveryQwarkLineAndLeavesTheRestWellFormed()
    {
        using var server = new FakeFtpServer();
        server.Stored[BootList] = Encoding.UTF8.GetBytes(
            $"{Qwark}\n{WebMan}\n/dev_hdd0/tmp/qwark.sprx\n/dev_hdd0/plugins/other.sprx\n");

        server.Start();

        Assert.True(await Loader(server).RemoveFromBootAsync("127.0.0.1"));
        Assert.Equal($"{WebMan}\n/dev_hdd0/plugins/other.sprx\n", Text(server, BootList));
    }

    [Fact]
    public async Task RemoveSaysSoWhenTheListNeverNamedTheModule()
    {
        var before = Encoding.UTF8.GetBytes($"{WebMan}\n");

        using var server = new FakeFtpServer();
        server.Stored[BootList] = before;
        server.Start();

        Assert.False(await Loader(server).RemoveFromBootAsync("127.0.0.1"));
        Assert.Same(before, server.Stored[BootList]);
    }

    [Fact]
    public async Task RemoveEmptiesAListThatNamedNothingElse()
    {
        using var server = new FakeFtpServer();
        server.Stored[BootList] = Encoding.UTF8.GetBytes($"{Qwark}\n");
        server.Start();

        Assert.True(await Loader(server).RemoveFromBootAsync("127.0.0.1"));
        Assert.Equal(string.Empty, Text(server, BootList));
    }

    // ------------------------------------------------------------------ ui.pak

    /// <summary>
    /// The console menu's ui.pak goes with the module, to the folder qwark reads it from. The
    /// folder is made first: a console qwark has never started on has none.
    /// </summary>
    [Fact]
    public async Task TheInstallPutsUiPakInQwarksFolderBesideTheModule()
    {
        var pak = FakeQwarkServer.UiPakFor(QwarkClient.ExpectedQwarkBuild);

        using var server = new FakeFtpServer();
        server.Start();

        var said = new List<string>();
        var result = await Loader(server).InstallToBootAsync(
            "127.0.0.1", FakeRelease(pak), new InlineProgress<string>(said.Add));

        Assert.True(result.UiPakSent);
        Assert.Equal(pak, server.Stored[UiPak.ConsolePath]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, server.Stored[WebManLoader.BootPath]);
        Assert.Equal($"{Qwark}\n", Text(server, BootList));
        Assert.Contains($"Uploading ui.pak to {UiPak.ConsolePath}", said);

        await WaitForQuitAsync(server);
        var commands = server.Commands.Where(c => !c.StartsWith("PASV", StringComparison.Ordinal)).ToList();
        int module = commands.IndexOf($"STOR {WebManLoader.BootPath}");
        int folder = commands.IndexOf($"MKD {UiPak.ConsoleDirectory}");
        int stored = commands.IndexOf($"STOR {UiPak.ConsolePath}");
        Assert.True(module >= 0 && module < folder && folder < stored, string.Join(" | ", commands));

        Assert.Contains("Sent ui.pak to /dev_hdd0/qwark.", ConnectionPanel.BootInstallMessage(result));
    }

    /// <summary>No ui.pak beside the module: the module is installed all the same, and the client says the pak is missing.</summary>
    [Fact]
    public async Task WithoutUiPakTheModuleIsInstalledAndTheClientSaysSo()
    {
        using var server = new FakeFtpServer();
        server.Start();

        var said = new List<string>();
        var result = await Loader(server).InstallToBootAsync(
            "127.0.0.1", FakeRelease(null), new InlineProgress<string>(said.Add));

        Assert.False(result.UiPakSent);
        Assert.Equal(BootInstallOutcome.Added, result.Outcome);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, server.Stored[WebManLoader.BootPath]);
        Assert.False(server.Stored.ContainsKey(UiPak.ConsolePath));
        Assert.Contains(UiPak.MissingNote, said);

        await WaitForQuitAsync(server);
        Assert.DoesNotContain($"MKD {UiPak.ConsoleDirectory}", server.Commands);

        Assert.Contains("No ui.pak was sent.", ConnectionPanel.BootInstallMessage(result));
    }

    /// <summary>The FTP session's last word is QUIT, so once the server has it the command list is complete.</summary>
    internal static async Task WaitForQuitAsync(FakeFtpServer server)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!server.Commands.Contains("QUIT") && sw.ElapsedMilliseconds < 5000) await Task.Delay(10);
        Assert.Contains("QUIT", server.Commands);
    }

    /// <summary>A qwark.sprx in a folder of its own, with a ui.pak beside it when one is given.</summary>
    internal static string FakeRelease(byte[]? pak)
    {
        string folder = Path.Combine(TestDataFolder.Root, $"release-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        if (pak is not null) File.WriteAllBytes(Path.Combine(folder, UiPak.FileName), pak);

        string path = Path.Combine(folder, WebManLoader.SprxName);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        return path;
    }

    private static WebManLoader Loader(FakeFtpServer server) => new() { FtpPort = server.Port };

    private static string Text(FakeFtpServer server, string path) => Encoding.UTF8.GetString(server.Stored[path]);

    /// <summary>A file for the upload half to send; nothing here reads what is in it.</summary>
    private static string FakeSprx()
    {
        string path = Path.Combine(TestDataFolder.Root, $"qwark-{Guid.NewGuid():N}.sprx");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        return path;
    }
}
