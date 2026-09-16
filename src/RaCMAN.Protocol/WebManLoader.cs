using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace RaCMAN.Protocol;

/// <summary>What an install did to boot_plugins.txt, for the one message the user sees.</summary>
public enum BootInstallOutcome
{
    /// <summary>The list did not name qwark at all; its line is now the first one.</summary>
    Added,

    /// <summary>The list named qwark lower down, more than once, or at an older path; it names it once, first.</summary>
    Moved,

    /// <summary>The list already began with the boot path, so it was left exactly as it was.</summary>
    AlreadyFirst,
}

/// <summary>
/// What an install did and how many lines the list holds afterwards, which is the other thing the
/// user has to be told: see <see cref="BeyondHenLimit"/>.
/// </summary>
public readonly record struct BootInstallResult(BootInstallOutcome Outcome, int Lines)
{
    /// <summary>
    /// Whether the console reads past the end of the list. qwark is the first line either way, so
    /// this is a warning and never a failure: what falls off the end is somebody else's plugin, and
    /// only the user can decide which line to drop.
    /// </summary>
    public bool BeyondHenLimit => Lines > WebManLoader.HenPluginLimit;
}

/// <summary>
/// Gets qwark.sprx onto the console the way racman-official does it: FTP the file into
/// /dev_hdd0/tmp/ and load it through webMAN's vshplugin.ps3mapi endpoint.
///
/// The console side of this cannot be tested here: it needs a console running webMAN MOD with its
/// FTP server up. The FTP client below is checked against RFC 959 rather than against hardware, and
/// the boot_plugins.txt rewrite against a loopback FTP server.
/// </summary>
public sealed class WebManLoader
{
    public const string SprxName = "qwark.sprx";

    /// <summary>Where the runtime load puts the SPRX, as Ratchetron and racman-official do.</summary>
    public const string RemoteDirectory = "/dev_hdd0/tmp";

    /// <summary>
    /// Where a boot install puts it. /dev_hdd0/tmp is a scratch directory the console and webMAN
    /// both clear, so a boot_plugins.txt line pointing there stops working at some point; a boot
    /// plugin has to live somewhere stable.
    /// </summary>
    public const string PluginsDirectory = "/dev_hdd0/plugins";

    public const string BootPluginsPath = "/dev_hdd0/boot_plugins.txt";
    public const int DefaultSlot = 5;

    /// <summary>
    /// How many lines of boot_plugins.txt PS3HEN loads. A longer list is not an error and no line
    /// of it is ever dropped here, but the console stops reading after the sixth.
    /// </summary>
    public const int HenPluginLimit = 6;

    private readonly HttpClient _http;

    public WebManLoader(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>
    /// The console's FTP port. webMAN's server is always on 21; the tests point this at a loopback
    /// server, so the boot list rewrite runs against a real transfer rather than a copy of itself.
    /// </summary>
    public int FtpPort { get; init; } = 21;

    public static string RemotePath => $"{RemoteDirectory}/{SprxName}";

    public static string BootPath => $"{PluginsDirectory}/{SprxName}";

    /// <summary>
    /// How long the plugin page gets to answer. webMAN on a console that is up answers in
    /// milliseconds, and this probe now runs before the connect attempt, so a console that is off
    /// must not hold the sequence up for the whole of <see cref="HttpClient"/>'s timeout.
    /// </summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// What webMAN's VSH plugin page says about a plugin file: whether it is in a slot, and which.
    /// </summary>
    public async Task<VshPluginStatus> PluginStatusAsync(
        string ip, string sprxName = SprxName, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProbeTimeout);

        try
        {
            var page = await _http.GetStringAsync($"http://{ip}/vshplugin.ps3mapi", cts.Token).ConfigureAwait(false);
            return VshPluginStatus.Parse(page, sprxName);
        }
        catch (HttpRequestException)
        {
            return VshPluginStatus.Unknown;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own probe deadline, not the caller giving up: a console that does not answer
            // its web server in four seconds has told us nothing either way.
            return VshPluginStatus.Unknown;
        }
    }

    /// <summary>Uploads the SPRX over FTP and asks webMAN to load it into the given VSH slot.</summary>
    public async Task LoadAsync(
        string ip,
        string localSprxPath,
        int slot = DefaultSlot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localSprxPath)) throw new FileNotFoundException($"{SprxName} not found", localSprxPath);
        if (slot is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(slot), "VSH plugin slots are 0 to 7");

        progress?.Report($"Uploading {SprxName} to {RemotePath}");
        var bytes = await File.ReadAllBytesAsync(localSprxPath, cancellationToken).ConfigureAwait(false);

        using (var ftp = new FtpSession(ip, FtpPort))
        {
            await ftp.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await ftp.StoreAsync(RemotePath, bytes, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report($"Loading {SprxName} into slot {slot}");
        string url = $"http://{ip}/vshplugin.ps3mapi?prx={Uri.EscapeDataString(RemotePath)}"
                     + $"&load_slot={slot.ToString(CultureInfo.InvariantCulture)}";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        progress?.Report($"{SprxName} loaded into slot {slot}");
    }

    /// <summary>
    /// Puts the SPRX in /dev_hdd0/plugins and makes that path the first line of
    /// /dev_hdd0/boot_plugins.txt so the console loads it at boot, before anything else on the list.
    /// Only ever called from an explicit user action: a VSH plugin that crashes at boot is recovered
    /// only by disabling plugins.
    /// </summary>
    public async Task<BootInstallResult> InstallToBootAsync(
        string ip,
        string localSprxPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localSprxPath)) throw new FileNotFoundException($"{SprxName} not found", localSprxPath);
        var bytes = await File.ReadAllBytesAsync(localSprxPath, cancellationToken).ConfigureAwait(false);

        using var ftp = new FtpSession(ip, FtpPort);
        await ftp.ConnectAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report($"Uploading {SprxName} to {BootPath}");
        await ftp.MakeDirectoryAsync(PluginsDirectory, cancellationToken).ConfigureAwait(false);
        await ftp.StoreAsync(BootPath, bytes, cancellationToken).ConfigureAwait(false);

        var existing = await ftp.RetrieveAsync(BootPluginsPath, cancellationToken).ConfigureAwait(false);
        var text = existing is null ? string.Empty : Encoding.UTF8.GetString(existing);
        var (rewritten, result) = PlanBootList(text);

        if (result.Outcome is BootInstallOutcome.AlreadyFirst)
        {
            progress?.Report($"{BootPluginsPath} already starts with {BootPath}");
            return result;
        }

        await ftp.StoreAsync(BootPluginsPath, Encoding.UTF8.GetBytes(rewritten), cancellationToken).ConfigureAwait(false);
        progress?.Report(result.Outcome is BootInstallOutcome.Added
            ? $"Added {BootPath} at the top of {BootPluginsPath}"
            : $"Moved {BootPath} to the top of {BootPluginsPath}");
        return result;
    }

    /// <summary>Drops every boot_plugins.txt line naming qwark.sprx, wherever it points.</summary>
    public async Task<bool> RemoveFromBootAsync(string ip, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        using var ftp = new FtpSession(ip, FtpPort);
        await ftp.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var existing = await ftp.RetrieveAsync(BootPluginsPath, cancellationToken).ConfigureAwait(false);
        if (existing is null) return false;

        var lines = BootLines(Encoding.UTF8.GetString(existing));
        var kept = lines.Where(line => !NamesSprx(line)).ToArray();
        if (kept.Length == lines.Count) return false;

        // A list with nothing left on it becomes an empty file rather than a lone newline; one that
        // keeps lines keeps its final newline too, and never gains a blank line at the top.
        var text = kept.Length == 0 ? string.Empty : string.Join('\n', kept) + '\n';
        await ftp.StoreAsync(BootPluginsPath, Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
        progress?.Report($"Removed {SprxName} from {BootPluginsPath}");
        return true;
    }

    /// <summary>
    /// What boot_plugins.txt should hold after an install, and what to tell the user about it.
    /// <para>
    /// PS3HEN loads the list in order, and a console that loads webMAN before qwark has been seen to
    /// hang the XMB, so qwark's line goes first and everything else keeps its order behind it. A
    /// line naming qwark anywhere else is dropped on the way — the old /dev_hdd0/tmp path included —
    /// so the list names the module once and once only.
    /// </para>
    /// <para>
    /// The rewrite is written with \n endings and no byte-order mark, so a list webMAN wrote with
    /// CRLF comes back normalised; blank lines are not plugins and do not survive it. A list that
    /// already begins with the boot path is not rewritten at all, whatever its endings are.
    /// </para>
    /// </summary>
    private static (string Text, BootInstallResult Result) PlanBootList(string existing)
    {
        var lines = BootLines(existing);
        int listed = lines.Count(NamesSprx);

        if (listed == 1 && lines[0].Trim().Equals(BootPath, StringComparison.OrdinalIgnoreCase))
        {
            return (existing, new BootInstallResult(BootInstallOutcome.AlreadyFirst, lines.Count));
        }

        var rewritten = new List<string> { BootPath };
        rewritten.AddRange(lines.Where(line => !NamesSprx(line)));

        var outcome = listed == 0 ? BootInstallOutcome.Added : BootInstallOutcome.Moved;
        return (string.Join('\n', rewritten) + '\n', new BootInstallResult(outcome, rewritten.Count));
    }

    /// <summary>The lines of a boot plugin list: what the console would load, in its order.</summary>
    private static List<string> BootLines(string text) => text
        .Split('\n')
        .Select(line => line.TrimEnd('\r'))
        .Where(line => line.Trim().Length > 0)
        .ToList();

    /// <summary>Whether a line names qwark.sprx, at whichever path it was installed to.</summary>
    private static bool NamesSprx(string line) =>
        line.Trim().EndsWith(SprxName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A very small FTP client: enough for anonymous STOR, RETR and MKD against webMAN's server.
/// FtpWebRequest and WebClient are obsolete in .NET 8, and this keeps the client warning-free.
///
/// RFC 959 shape: log in, TYPE I once for the session, then for every transfer PASV, open the
/// data connection, send the transfer command, move the bytes, close the data connection, read
/// the completion reply off the control connection.
/// </summary>
public sealed class FtpSession : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _password;

    private TcpClient? _control;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public FtpSession(string host, int port = 21, string user = "anonymous", string password = "anonymous@")
    {
        _host = host;
        _port = port;
        _user = user;
        _password = password;
    }

    /// <summary>Applied to both the control and the data socket, so a wedged console cannot hang a request forever.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _control = new TcpClient
        {
            SendTimeout = (int)Timeout.TotalMilliseconds,
            ReceiveTimeout = (int)Timeout.TotalMilliseconds,
        };

        await _control.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
        var stream = _control.GetStream();
        _reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        _writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };

        await ReadReplyAsync(cancellationToken).ConfigureAwait(false);

        var user = await CommandAsync($"USER {_user}", cancellationToken).ConfigureAwait(false);
        if (user.Code == 331) await ExpectAsync($"PASS {_password}", 230, cancellationToken).ConfigureAwait(false);
        else if (user.Code != 230) throw new IOException($"FTP login refused: {user.Text}");

        await ExpectAsync("TYPE I", 200, cancellationToken).ConfigureAwait(false);
    }

    public async Task StoreAsync(string path, byte[] data, CancellationToken cancellationToken = default)
    {
        using var dataClient = await OpenDataConnectionAsync(cancellationToken).ConfigureAwait(false);

        var reply = await CommandAsync($"STOR {path}", cancellationToken).ConfigureAwait(false);
        if (reply.Code is not (125 or 150)) throw new IOException($"FTP STOR refused: {reply.Text}");

        await using (var dataStream = dataClient.GetStream())
        {
            await dataStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await dataStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var done = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
        if (done.Code is not (226 or 250)) throw new IOException($"FTP STOR failed: {done.Text}");
    }

    /// <summary>Returns null when the file does not exist.</summary>
    public async Task<byte[]?> RetrieveAsync(string path, CancellationToken cancellationToken = default)
    {
        using var dataClient = await OpenDataConnectionAsync(cancellationToken).ConfigureAwait(false);

        var reply = await CommandAsync($"RETR {path}", cancellationToken).ConfigureAwait(false);
        if (reply.Code is 550) return null;
        if (reply.Code is not (125 or 150)) throw new IOException($"FTP RETR refused: {reply.Text}");

        using var buffer = new MemoryStream();
        await using (var dataStream = dataClient.GetStream())
        {
            await dataStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        var done = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
        if (done.Code is not (226 or 250)) throw new IOException($"FTP RETR failed: {done.Text}");
        return buffer.ToArray();
    }

    /// <summary>MKD, treating "already there" (521 and 550) as success.</summary>
    public async Task MakeDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var reply = await CommandAsync($"MKD {path}", cancellationToken).ConfigureAwait(false);
        if (reply.Code is 257 or 521 or 550) return;
        throw new IOException($"FTP MKD refused: {reply.Text}");
    }

    private async Task<TcpClient> OpenDataConnectionAsync(CancellationToken cancellationToken)
    {
        var reply = await CommandAsync("PASV", cancellationToken).ConfigureAwait(false);
        if (reply.Code != 227) throw new IOException($"FTP PASV refused: {reply.Text}");

        var (advertised, port) = ParsePassive(reply.Text);

        var client = new TcpClient
        {
            SendTimeout = (int)Timeout.TotalMilliseconds,
            ReceiveTimeout = (int)Timeout.TotalMilliseconds,
        };

        try
        {
            await client.ConnectAsync(advertised, port, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch (SocketException) when (!string.Equals(advertised, _host, StringComparison.Ordinal))
        {
            // A console behind NAT, or one that reports 0.0.0.0, advertises an address that does
            // not resolve from here. The port is still right, so retry against the control host.
            client.Dispose();
            var retry = new TcpClient
            {
                SendTimeout = (int)Timeout.TotalMilliseconds,
                ReceiveTimeout = (int)Timeout.TotalMilliseconds,
            };

            await retry.ConnectAsync(_host, port, cancellationToken).ConfigureAwait(false);
            return retry;
        }
    }

    /// <summary>Pulls the h1,h2,h3,h4,p1,p2 tuple out of a 227 reply, section 4.1.2 of RFC 959.</summary>
    public static (string Host, int Port) ParsePassive(string reply)
    {
        int open = reply.IndexOf('(');
        int close = reply.IndexOf(')', open < 0 ? 0 : open);

        // Some servers omit the parentheses; then the numbers follow the three-digit reply code,
        // which has to come off first or it is read as the first octet.
        string body = open >= 0 && close > open
            ? reply[(open + 1)..close]
            : reply.Length > 4 && char.IsAsciiDigit(reply[0]) ? reply[4..] : reply;
        var parts = body.Split(',')
            .Select(p => new string(p.Where(char.IsAsciiDigit).ToArray()))
            .Where(p => p.Length > 0)
            .ToArray();

        if (parts.Length < 6) throw new IOException($"FTP PASV reply not understood: {reply}");

        var tuple = parts[^6..];
        string host = string.Join('.', tuple[0], tuple[1], tuple[2], tuple[3]);
        int port = (int.Parse(tuple[4], CultureInfo.InvariantCulture) << 8)
                   + int.Parse(tuple[5], CultureInfo.InvariantCulture);

        if (port is <= 0 or > 65535) throw new IOException($"FTP PASV reply not understood: {reply}");
        return (host, port);
    }

    private async Task ExpectAsync(string command, int code, CancellationToken cancellationToken)
    {
        var reply = await CommandAsync(command, cancellationToken).ConfigureAwait(false);
        if (reply.Code != code) throw new IOException($"FTP '{command}' returned {reply.Text}");
    }

    private async Task<(int Code, string Text)> CommandAsync(string command, CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new InvalidOperationException("FTP session is not connected");
        await writer.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
        return await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One reply, multi-line ones included. RFC 959 section 4.2: a reply whose first line has a
    /// '-' in column four runs until a line that starts with the same code followed by a space,
    /// so an intermediate line that happens to start with three digits does not end it early.
    /// </summary>
    private async Task<(int Code, string Text)> ReadReplyAsync(CancellationToken cancellationToken)
    {
        var reader = _reader ?? throw new InvalidOperationException("FTP session is not connected");

        while (true)
        {
            var line = await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false);
            if (line.Length < 4 || !int.TryParse(line.AsSpan(0, 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
            {
                continue;
            }

            if (line[3] != '-') return (code, line);

            string terminator = line[..3] + " ";
            while (true)
            {
                var next = await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false);
                if (next.StartsWith(terminator, StringComparison.Ordinal)) return (code, next);
            }
        }
    }

    private static async Task<string> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        return line ?? throw new IOException("FTP connection closed");
    }

    public void Dispose()
    {
        try
        {
            if (_writer is not null && _control is { Connected: true }) _writer.WriteLine("QUIT");
        }
        catch (IOException)
        {
            // The console drops the control connection often enough; nothing to report.
        }
        catch (ObjectDisposedException)
        {
            // Same.
        }

        _writer?.Dispose();
        _reader?.Dispose();
        _control?.Dispose();
    }
}
