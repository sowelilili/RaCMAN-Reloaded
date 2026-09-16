using System.Text;

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
