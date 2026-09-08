using System.Net;
using System.Net.Sockets;
using System.Text;

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
}

/// <summary>
/// Just enough of RFC 959 to exercise FtpSession: anonymous login, TYPE, PASV with its own data
/// listener, STOR, RETR, MKD and QUIT. Single connection, one command at a time.
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
                    Stored[command[5..]] = buffer.ToArray();
                    await writer.WriteLineAsync("226 Transfer complete");
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
