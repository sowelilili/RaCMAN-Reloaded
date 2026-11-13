using System.Net;
using System.Net.Sockets;
using System.Text;

using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The FTP client against a loopback server that answers the way RFC 959 says a server may,
/// multi-line replies included. This is the only half of WebManLoader that can be tested here:
/// the webMAN HTTP endpoints and the console itself cannot.
/// </summary>
public class FtpTests
{
    [Theory]
    [InlineData("227 Entering Passive Mode (192,168,1,50,195,80)", "192.168.1.50", 50000)]
    [InlineData("227 =192,168,1,50,4,1", "192.168.1.50", 1025)]
    [InlineData("227 Entering Passive Mode ( 10,0,0,2, 8,0 )", "10.0.0.2", 2048)]
    public void PassiveRepliesAreParsedIntoAHostAndPort(string reply, string host, int port)
    {
        var parsed = FtpSession.ParsePassive(reply);
        Assert.Equal(host, parsed.Host);
        Assert.Equal(port, parsed.Port);
    }

    [Fact]
    public void APassiveReplyWithoutSixNumbersIsRejected()
    {
        Assert.Throws<IOException>(() => FtpSession.ParsePassive("227 Entering Passive Mode (1,2,3)"));
    }

    [Fact]
    public async Task StoreFollowsPasvThenTheTransferCommandThenTheCompletionReply()
    {
        using var server = new FakeFtpServer();
        server.Start();

        var payload = new byte[70000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;

        using (var ftp = new FtpSession("127.0.0.1", server.Port))
        {
            await ftp.ConnectAsync();
            await ftp.StoreAsync("/dev_hdd0/tmp/qwark.sprx", payload);
        }

        Assert.Equal(payload, server.Stored["/dev_hdd0/tmp/qwark.sprx"]);

        // Login, binary mode, then PASV before the transfer command: RFC 959 passive sequencing.
        Assert.Equal(
            new[] { "USER anonymous", "PASS anonymous@", "TYPE I", "PASV", "STOR /dev_hdd0/tmp/qwark.sprx" },
            server.Commands.Take(5));
    }

    [Fact]
    public async Task RetrieveReadsTheFileAndAMissingOneComesBackNull()
    {
        using var server = new FakeFtpServer();
        server.Stored["/dev_hdd0/boot_plugins.txt"] = "/dev_hdd0/plugins/other.sprx\n"u8.ToArray();
        server.Start();

        using var ftp = new FtpSession("127.0.0.1", server.Port);
        await ftp.ConnectAsync();

        var bytes = await ftp.RetrieveAsync("/dev_hdd0/boot_plugins.txt");
        Assert.Equal("/dev_hdd0/plugins/other.sprx\n", Encoding.UTF8.GetString(bytes!));

        Assert.Null(await ftp.RetrieveAsync("/dev_hdd0/not-there.txt"));
    }

    [Fact]
    public async Task AMultiLineGreetingDoesNotConfuseTheReplyReader()
    {
        // The greeting's middle line starts with three digits, which a naive reader would take
        // for the end of the reply and then answer the wrong command.
        using var server = new FakeFtpServer
        {
            Greeting = new[] { "220-webMAN MOD FTP", "220 1.47.44 ready", },
        };

        server.Start();

        using var ftp = new FtpSession("127.0.0.1", server.Port);
        await ftp.ConnectAsync();
        await ftp.MakeDirectoryAsync("/dev_hdd0/plugins");

        Assert.Equal(new[] { "USER anonymous", "PASS anonymous@", "TYPE I", "MKD /dev_hdd0/plugins" }, server.Commands);
    }

    [Fact]
    public async Task MakeDirectoryTreatsAnExistingFolderAsSuccess()
    {
        using var server = new FakeFtpServer { MkdReply = "550 Already exists" };
        server.Start();

        using var ftp = new FtpSession("127.0.0.1", server.Port);
        await ftp.ConnectAsync();
        await ftp.MakeDirectoryAsync("/dev_hdd0/plugins");
    }

    /// <summary>
    /// The webMAN load sends ui.pak in the same session as the module, into qwark's folder, and
    /// only then asks webMAN to load the module, so the module finds its pak as it starts. webMAN's
    /// HTTP side is a stub here: what it records is what the FTP server held when the load was asked.
    /// </summary>
    [Fact]
    public async Task TheWebManLoadSendsUiPakBeforeAskingForTheLoad()
    {
        var pak = FakeQwarkServer.UiPakFor(QwarkClient.ExpectedQwarkBuild);

        using var server = new FakeFtpServer();
        server.Start();

        var webMan = new WebManStub(() => server.Stored.TryGetValue(UiPak.ConsolePath, out var held) ? held : null);
        var loader = new WebManLoader(new HttpClient(webMan)) { FtpPort = server.Port };

        var said = new List<string>();
        bool sent = (await loader.LoadAsync("127.0.0.1", BootInstallTests.FakeRelease(pak), 5,
            new InlineProgress<string>(said.Add))).UiPakSent;

        Assert.True(sent);
        var (url, pakAtLoad) = Assert.Single(webMan.Requests);
        Assert.Equal("http://127.0.0.1/vshplugin.ps3mapi?prx=%2Fdev_hdd0%2Ftmp%2Fqwark.sprx&load_slot=5", url);
        Assert.Equal(pak, pakAtLoad);

        await BootInstallTests.WaitForQuitAsync(server);
        var commands = server.Commands.Where(c => !c.StartsWith("PASV", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            new[]
            {
                $"STOR {WebManLoader.RemotePath}",
                $"MKD {UiPak.ConsoleDirectory}",
                $"STOR {UiPak.ConsolePath}",

                // The boot list is read to find a boot copy; this console has none, so that is all.
                $"RETR {WebManLoader.BootPluginsPath}",
                "QUIT",
            },
            commands.Skip(3));
    }

    [Fact]
    public async Task WithoutUiPakTheWebManLoadStillLoadsTheModuleAndSaysSo()
    {
        using var server = new FakeFtpServer();
        server.Start();

        var webMan = new WebManStub(() => null);
        var loader = new WebManLoader(new HttpClient(webMan)) { FtpPort = server.Port };

        var said = new List<string>();
        bool sent = (await loader.LoadAsync("127.0.0.1", BootInstallTests.FakeRelease(null), 5,
            new InlineProgress<string>(said.Add))).UiPakSent;

        Assert.False(sent);
        Assert.Single(webMan.Requests);
        Assert.Contains(UiPak.MissingNote, said);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, server.Stored[WebManLoader.RemotePath]);

        await BootInstallTests.WaitForQuitAsync(server);
        Assert.DoesNotContain($"MKD {UiPak.ConsoleDirectory}", server.Commands);
        Assert.False(server.Stored.ContainsKey(UiPak.ConsolePath));
    }

    /// <summary>
    /// A console that refuses qwark's folder or the pak still gets its module: the trainer does not
    /// wait on the menu's file, and the refusal is said with the console's reply.
    /// </summary>
    [Fact]
    public async Task ARefusedUiPakStillLoadsTheModuleAndSaysWhy()
    {
        var pak = FakeQwarkServer.UiPakFor(QwarkClient.ExpectedQwarkBuild);

        using var server = new FakeFtpServer { MkdReply = "553 Permission denied" };
        server.Start();

        var webMan = new WebManStub(() => null);
        var loader = new WebManLoader(new HttpClient(webMan)) { FtpPort = server.Port };

        var said = new List<string>();
        bool sent = (await loader.LoadAsync("127.0.0.1", BootInstallTests.FakeRelease(pak), 5,
            new InlineProgress<string>(said.Add))).UiPakSent;

        Assert.False(sent);
        Assert.Single(webMan.Requests);
        Assert.Contains(said, line => line.StartsWith(UiPak.SendFailedNote, StringComparison.Ordinal) && line.Contains("553"));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, server.Stored[WebManLoader.RemotePath]);
        Assert.False(server.Stored.ContainsKey(UiPak.ConsolePath));
    }

    [Fact]
    public async Task ARefusedStoreIsReportedRatherThanSwallowed()
    {
        using var server = new FakeFtpServer { StorReply = "553 Permission denied" };
        server.Start();

        using var ftp = new FtpSession("127.0.0.1", server.Port);
        await ftp.ConnectAsync();

        var ex = await Assert.ThrowsAsync<IOException>(() => ftp.StoreAsync("/dev_hdd0/tmp/x", new byte[] { 1 }));
        Assert.Contains("553", ex.Message);
    }

    [Fact]
    public async Task AConsoleThatAcceptsAndSaysNothingTimesOut()
    {
        using var server = new FakeFtpServer { Greeting = Array.Empty<string>() };
        server.Start();

        using var ftp = new FtpSession("127.0.0.1", server.Port) { Timeout = TimeSpan.FromMilliseconds(300) };

        var ex = await Assert.ThrowsAsync<IOException>(() => Within10Seconds(Wrap(ftp.ConnectAsync())));
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task AConsoleThatStallsAfterAnUploadTimesOut()
    {
        using var server = new FakeFtpServer { SilentAfterStor = true };
        server.Start();

        using var ftp = new FtpSession("127.0.0.1", server.Port) { Timeout = TimeSpan.FromMilliseconds(300) };
        await ftp.ConnectAsync();

        var ex = await Assert.ThrowsAsync<IOException>(() => Within10Seconds(Wrap(ftp.StoreAsync("/dev_hdd0/tmp/x", new byte[] { 1, 2, 3 }))));
        Assert.Contains("timed out", ex.Message);

        // The bytes did arrive; only the completion reply was missing.
        Assert.Equal(new byte[] { 1, 2, 3 }, server.Stored["/dev_hdd0/tmp/x"]);
    }

    [Fact]
    public async Task ACallersCancellationStaysACancellation()
    {
        using var server = new FakeFtpServer { Greeting = Array.Empty<string>() };
        server.Start();

        using var ftp = new FtpSession("127.0.0.1", server.Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ftp.ConnectAsync(cts.Token));
    }

    /// <summary>The task's own outcome, or a failed test if it is still running after ten seconds.</summary>
    private static async Task<T> Within10Seconds<T>(Task<T> task)
    {
        var first = await Task.WhenAny(task, Task.Delay(10000));
        Assert.True(ReferenceEquals(first, task), "still waiting after 10 s: the FTP timeout did not apply");
        return await task;
    }

    /// <summary>A task with no result as one with a result, so <see cref="Within10Seconds{T}"/> can take it.</summary>
    private static async Task<bool> Wrap(Task task)
    {
        await task;
        return true;
    }
}

/// <summary>
/// webMAN's load endpoint, answered here: it records each request and what the FTP server held at
/// that moment (whatever the function given picks out of it).
/// </summary>
internal sealed class WebManStub : HttpMessageHandler
{
    private readonly Func<byte[]?> _pakOnConsole;

    public WebManStub(Func<byte[]?> pakOnConsole) => _pakOnConsole = pakOnConsole;

    public List<(string Url, byte[]? PakAtLoad)> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.RequestUri!.AbsoluteUri, _pakOnConsole()));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

/// <summary>
/// Just enough of RFC 959 to exercise FtpSession: anonymous login, TYPE, PASV with its own data
/// listener, STOR, RETR, MKD, DELE, RNFR/RNTO and QUIT. Single connection, one command at a time.
/// A rename onto a file that exists is refused, as cellFs refuses it on the console.
/// </summary>
internal sealed class FakeFtpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _dataListener;

    public FakeFtpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string[] Greeting { get; init; } = { "220 fake ftp ready" };

    public string StorReply { get; init; } = "150 Opening data connection";

    public string MkdReply { get; init; } = "257 Created";

    /// <summary>Takes a STOR's bytes and then never sends the completion reply: a console that stalled mid-upload.</summary>
    public bool SilentAfterStor { get; init; }

    /// <summary>
    /// STORs that break off part way: the server keeps the first half of the bytes under the path
    /// and answers the completion with this reply instead of 226, as a transfer cut short does.
    /// </summary>
    public Dictionary<string, string> StorAborts { get; } = new(StringComparer.Ordinal);

    /// <summary>RNTO destinations the server refuses with 553 the next time, whatever is there; once each.</summary>
    public HashSet<string> RntoRefused { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, byte[]> Stored { get; } = new(StringComparer.Ordinal);

    public List<string> Commands { get; } = new();

    public void Start() => _ = Task.Run(ServeAsync);

    private async Task ServeAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n",
            };

            foreach (var line in Greeting) await writer.WriteLineAsync(line);
            string? renameFrom = null;

            while (!_cts.IsCancellationRequested)
            {
                var command = await reader.ReadLineAsync(_cts.Token);
                if (command is null) return;
                Commands.Add(command);

                if (command.StartsWith("USER", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("331 Password required");
                }
                else if (command.StartsWith("PASS", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync("230 Logged in");
                }
                else if (command == "TYPE I")
                {
                    await writer.WriteLineAsync("200 Type set to I");
                }
                else if (command == "PASV")
                {
                    _dataListener?.Stop();
                    _dataListener = new TcpListener(IPAddress.Loopback, 0);
                    _dataListener.Start();
                    int port = ((IPEndPoint)_dataListener.LocalEndpoint).Port;
                    await writer.WriteLineAsync($"227 Entering Passive Mode (127,0,0,1,{port >> 8},{port & 0xFF})");
                }
                else if (command.StartsWith("STOR ", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync(StorReply);
                    if (!StorReply.StartsWith("15", StringComparison.Ordinal)) continue;

                    using var data = await _dataListener!.AcceptTcpClientAsync(_cts.Token);
                    using var buffer = new MemoryStream();
                    await data.GetStream().CopyToAsync(buffer, _cts.Token);
                    string path = command[5..];
                    if (StorAborts.TryGetValue(path, out var abort))
                    {
                        Stored[path] = buffer.ToArray()[..(int)(buffer.Length / 2)];
                        await writer.WriteLineAsync(abort);
                        continue;
                    }

                    Stored[path] = buffer.ToArray();
                    if (!SilentAfterStor) await writer.WriteLineAsync("226 Transfer complete");
                }
                else if (command.StartsWith("DELE ", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync(Stored.Remove(command[5..]) ? "250 Deleted" : "550 No such file");
                }
                else if (command.StartsWith("RNFR ", StringComparison.Ordinal))
                {
                    renameFrom = Stored.ContainsKey(command[5..]) ? command[5..] : null;
                    await writer.WriteLineAsync(renameFrom is null ? "550 No such file" : "350 Ready for RNTO");
                }
                else if (command.StartsWith("RNTO ", StringComparison.Ordinal))
                {
                    string to = command[5..];
                    if (renameFrom is null) await writer.WriteLineAsync("503 RNFR first");
                    else if (RntoRefused.Remove(to) || Stored.ContainsKey(to)) await writer.WriteLineAsync("553 Rename refused");
                    else
                    {
                        Stored[to] = Stored[renameFrom];
                        Stored.Remove(renameFrom);
                        await writer.WriteLineAsync("250 Renamed");
                    }

                    renameFrom = null;
                }
                else if (command.StartsWith("RETR ", StringComparison.Ordinal))
                {
                    if (!Stored.TryGetValue(command[5..], out var bytes))
                    {
                        await writer.WriteLineAsync("550 No such file");
                        continue;
                    }

                    await writer.WriteLineAsync("150 Opening data connection");
                    using var data = await _dataListener!.AcceptTcpClientAsync(_cts.Token);
                    await data.GetStream().WriteAsync(bytes, _cts.Token);
                    data.Close();
                    await writer.WriteLineAsync("226 Transfer complete");
                }
                else if (command.StartsWith("MKD ", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync(MkdReply);
                }
                else if (command == "QUIT")
                {
                    await writer.WriteLineAsync("221 Bye");
                    return;
                }
                else
                {
                    await writer.WriteLineAsync("500 Unknown command");
                }
            }
        }
        catch (Exception)
        {
            // The client went away, or the test finished first.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _dataListener?.Stop(); } catch { /* already stopped */ }
        try { _listener.Stop(); } catch { /* already stopped */ }
        _cts.Dispose();
    }
}
