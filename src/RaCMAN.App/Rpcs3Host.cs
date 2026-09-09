using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>
/// The RPCS3 side of the client: qwark's core compiled for the PC, talking to RPCS3 through its
/// IPC (PINE) server and serving this client on the usual qwark port. It is an ordinary child
/// process — started hidden, its output kept for the Connection panel, stopped by writing "exit"
/// on its stdin the way the simulator has always been driven.
/// <para>
/// Nothing here knows the protocol: once the helper is up, <see cref="QwarkClient"/> connects to
/// 127.0.0.1 exactly as it would to a console.
/// </para>
/// </summary>
public sealed class Rpcs3Host : IDisposable
{
    public const string ExeName = "qwark-rpcs3.exe";

    /// <summary>RPCS3's own default IPC port.</summary>
    public const int DefaultPinePort = 28012;

    /// <summary>How much of the helper's output the panel keeps.</summary>
    public const int MaxLines = 50;

    /// <summary>How long a helper gets to leave on its own after "exit" before it is killed.</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly List<string> _lines = new();

    private Process? _process;
    private int _pid;
    private int? _exitCode;
    private string? _lastPine;

    public Rpcs3Host(string? baseDirectory = null, int qwarkPort = QwarkClient.DefaultPort)
    {
        BaseDirectory = baseDirectory ?? AppContext.BaseDirectory;
        QwarkPort = qwarkPort;
    }

    /// <summary>Where the search for the helper starts: the folder this client runs from.</summary>
    public string BaseDirectory { get; }

    /// <summary>The port the helper serves this client on, and the one an existing server is looked for on.</summary>
    public int QwarkPort { get; }

    /// <summary>True while this object owns a running helper.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate) return _process is { HasExited: false };
        }
    }

    /// <summary>The running helper's process id, or null when there is none.</summary>
    public int? Pid
    {
        get
        {
            lock (_gate) return _process is { HasExited: false } ? _pid : null;
        }
    }

    /// <summary>The exit code of the helper this object last started, once it has exited.</summary>
    public int? ExitCode
    {
        get
        {
            lock (_gate) return _exitCode;
        }
    }

    /// <summary>
    /// True when a qwark was already serving <see cref="QwarkPort"/>, so no helper was started and
    /// the client connects to whatever is there: a second copy would only fail to bind the port.
    /// </summary>
    public bool Adopted { get; private set; }

    /// <summary>Why the last <see cref="Ensure"/> could not start a helper. Null when it could.</summary>
    public string? Problem { get; private set; }

    /// <summary>The one-line status the Connection panel shows.</summary>
    public string Status
    {
        get
        {
            lock (_gate)
            {
                if (_process is { HasExited: false }) return $"running (pid {_pid})";
                if (_exitCode is { } code) return $"exited (code {code})";
                return "not started";
            }
        }
    }

    /// <summary>The last "pine: ..." line the helper printed: what it says about RPCS3 itself.</summary>
    public string? LastPineLine
    {
        get
        {
            lock (_gate) return _lastPine;
        }
    }

    /// <summary>The tail of the helper's stdout and stderr, oldest first.</summary>
    public string[] Lines
    {
        get
        {
            lock (_gate) return _lines.ToArray();
        }
    }

    /// <summary>
    /// Where qwark-rpcs3.exe is: beside this executable, which is where the release layout puts
    /// it; else in the sibling qwark repo, the "..\..\..\..\qwark" of a development build, found
    /// by walking up from the build output so a worktree or a different configuration still hits
    /// it; else whatever <paramref name="overridePath"/> (the rpcs3QwarkPath setting) names. Null
    /// when none of those exists.
    /// </summary>
    public static string? Find(string baseDirectory, string? overridePath = null)
    {
        string beside = Path.Combine(baseDirectory, ExeName);
        if (File.Exists(beside)) return Path.GetFullPath(beside);

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            string sibling = Path.Combine(directory.FullName, "qwark", ExeName);
            if (File.Exists(sibling)) return Path.GetFullPath(sibling);
            directory = directory.Parent;
        }

        var trimmed = (overridePath ?? string.Empty).Trim().Trim('"').Trim();
        if (trimmed.Length == 0) return null;

        string configured = Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(baseDirectory, trimmed);
        return File.Exists(configured) ? Path.GetFullPath(configured) : null;
    }

    public string? Find(string? overridePath = null) => Find(BaseDirectory, overridePath);

    /// <summary>True when something on this machine is already listening on <paramref name="port"/>.</summary>
    public static bool PortInUse(int port)
    {
        try
        {
            foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (endpoint.Port == port) return true;
            }
        }
        catch (NetworkInformationException)
        {
            // No listener table on this platform: assume the port is free and let the bind decide.
        }

        return false;
    }

    /// <summary>
    /// Makes sure something is serving the qwark port: does nothing when this object's helper is
    /// already up or when another qwark holds the port, and otherwise starts one. True means the
    /// client can connect; <paramref name="message"/> says what happened, for a toast.
    /// </summary>
    public bool Ensure(string? overridePath, int pinePort, out string message)
    {
        Problem = null;

        if (IsRunning)
        {
            message = $"qwark-rpcs3 is already running (pid {Pid})";
            return true;
        }

        if (PortInUse(QwarkPort))
        {
            Adopted = true;
            message = $"Something is already serving port {QwarkPort}; connecting to that";
            return true;
        }

        Adopted = false;
        string? exe = Find(overridePath);
        if (exe is null)
        {
            Problem = $"{ExeName} was not found beside this client. Build it in ../qwark, or set "
                      + "\"rpcs3QwarkPath\" in the settings file.";
            message = Problem;
            return false;
        }

        try
        {
            Start(exe, pinePort);
            message = $"Started {ExeName} (pid {Pid})";
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or IOException or UnauthorizedAccessException)
        {
            Problem = $"{ExeName} would not start: {ex.Message}";
            message = Problem;
            return false;
        }
    }

    /// <summary>
    /// Waits for something to be listening on the qwark port. A freshly started helper needs a
    /// moment to bind it, and connecting into that gap only produces a refused-connection error
    /// the reconnect loop would then quietly fix: better to wait than to show that. False when the
    /// wait ran out, in which case connecting anyway is still the right thing to try.
    /// </summary>
    public async Task<bool> WaitForPortAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (PortInUse(QwarkPort)) return true;

            // A helper that died on the way up is never going to open the port.
            if (!IsRunning && !Adopted && ExitCode is not null) return false;

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return PortInUse(QwarkPort);
    }

    private void Start(string exe, int pinePort)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? BaseDirectory,
        };

        info.ArgumentList.Add("--pine-port");
        info.ArgumentList.Add(pinePort.ToString(CultureInfo.InvariantCulture));

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Record(e.Data);
        process.ErrorDataReceived += (_, e) => Record(e.Data);
        process.Exited += (_, _) =>
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_process, process)) return;
                try { _exitCode = process.ExitCode; }
                catch (InvalidOperationException) { _exitCode = null; }
            }
        };

        lock (_gate)
        {
            _lines.Clear();
            _lastPine = null;
            _exitCode = null;
        }

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_gate)
        {
            _process = process;
            _pid = process.Id;
        }
    }

    /// <summary>
    /// Keeps one line of the helper's output, and picks out what it says about RPCS3. The helper
    /// stamps its log lines with a time ("[05:37:25] pine: no server on ... yet"), so the status
    /// is taken from "pine:" onwards rather than from the start of the line.
    /// <para>
    /// Called from the process's reader threads, which is why every field it touches is behind
    /// the lock the panel reads through.
    /// </para>
    /// </summary>
    private void Record(string? line)
    {
        if (line is null) return;

        lock (_gate)
        {
            _lines.Add(line);
            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);

            int pine = line.IndexOf("pine:", StringComparison.OrdinalIgnoreCase);
            if (pine >= 0) _lastPine = line[pine..].Trim();
        }
    }

    /// <summary>
    /// Asks the helper to leave the way its stdin loop expects, then kills it if it has not gone
    /// within <see cref="StopGrace"/>. Safe to call when nothing is running.
    /// </summary>
    public void Stop()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.StandardInput.WriteLine("exit");
                    process.StandardInput.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // Its stdin has already gone; the kill below is then the whole of the stop.
                }

                if (!process.WaitForExit((int)StopGrace.TotalMilliseconds))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit((int)StopGrace.TotalMilliseconds);
                }
            }

            lock (_gate)
            {
                try { _exitCode = process.ExitCode; }
                catch (InvalidOperationException) { /* never started, or already reaped */ }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                      or NotSupportedException)
        {
            // Nothing left to stop: the process died between the check and the ask.
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose() => Stop();
}
