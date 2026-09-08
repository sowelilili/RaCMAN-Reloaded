using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace RaCMAN.Protocol;

/// <summary>
/// Gets qwark.sprx onto the console the way racman-official does it: FTP the file into
/// /dev_hdd0/tmp/ and load it through webMAN's vshplugin.ps3mapi endpoint.
///
/// None of this can be tested here: it needs a console running webMAN MOD with its FTP server
/// up. The FTP client below is checked against RFC 959 rather than against hardware.
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

    private readonly HttpClient _http;

    public WebManLoader(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public static string RemotePath => $"{RemoteDirectory}/{SprxName}";

    public static string BootPath => $"{PluginsDirectory}/{SprxName}";

    /// <summary>True when webMAN's plugin page already lists a plugin with this file name.</summary>
    public async Task<bool> IsLoadedAsync(string ip, string sprxName = SprxName, CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await _http.GetStringAsync($"http://{ip}/home.ps3mapi", cancellationToken).ConfigureAwait(false);
            return page.Contains(sprxName, StringComparison.OrdinalIgnoreCase);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
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

        using (var ftp = new FtpSession(ip))
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
    /// Puts the SPRX in /dev_hdd0/plugins and appends that path to /dev_hdd0/boot_plugins.txt so
    /// the console loads it at boot. Only ever called from an explicit user action: a VSH plugin
    /// that crashes at boot is recovered only by disabling plugins.
    /// </summary>
    public async Task<bool> InstallToBootAsync(
        string ip,
        string localSprxPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localSprxPath)) throw new FileNotFoundException($"{SprxName} not found", localSprxPath);
        var bytes = await File.ReadAllBytesAsync(localSprxPath, cancellationToken).ConfigureAwait(false);

        using var ftp = new FtpSession(ip);
        await ftp.ConnectAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report($"Uploading {SprxName} to {BootPath}");
        await ftp.MakeDirectoryAsync(PluginsDirectory, cancellationToken).ConfigureAwait(false);
        await ftp.StoreAsync(BootPath, bytes, cancellationToken).ConfigureAwait(false);

        var existing = await ftp.RetrieveAsync(BootPluginsPath, cancellationToken).ConfigureAwait(false);
        var text = existing is null ? string.Empty : Encoding.UTF8.GetString(existing);

        if (text.Split('\n').Any(line => line.Trim().Equals(BootPath, StringComparison.OrdinalIgnoreCase)))
        {
            progress?.Report($"{BootPluginsPath} already lists {BootPath}");
            return false;
        }

        var builder = new StringBuilder(text);
        if (builder.Length > 0 && builder[^1] is not ('\n' or '\r')) builder.Append('\n');
        builder.Append(BootPath).Append('\n');

        await ftp.StoreAsync(BootPluginsPath, Encoding.UTF8.GetBytes(builder.ToString()), cancellationToken).ConfigureAwait(false);
        progress?.Report($"Added {BootPath} to {BootPluginsPath}");
        return true;
    }

    /// <summary>Drops every boot_plugins.txt line naming qwark.sprx, wherever it points.</summary>
    public async Task<bool> RemoveFromBootAsync(string ip, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        using var ftp = new FtpSession(ip);
        await ftp.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var existing = await ftp.RetrieveAsync(BootPluginsPath, cancellationToken).ConfigureAwait(false);
        if (existing is null) return false;

        var lines = Encoding.UTF8.GetString(existing).Split('\n');
        var kept = lines.Where(l => !l.Trim().EndsWith(SprxName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (kept.Length == lines.Length) return false;

        await ftp.StoreAsync(BootPluginsPath, Encoding.UTF8.GetBytes(string.Join('\n', kept)), cancellationToken).ConfigureAwait(false);
        progress?.Report($"Removed {SprxName} from {BootPluginsPath}");
        return true;
    }
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
