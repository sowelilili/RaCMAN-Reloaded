using System.Diagnostics;

namespace RaCMAN.App;

/// <summary>What the firewall was found to say about this copy of the client.</summary>
public enum FirewallState
{
    /// <summary>The rule list could not be read: not Windows, or the COM API refused. Not an answer.</summary>
    Unknown,

    /// <summary>An enabled inbound allow rule names this executable and covers the telemetry protocol.</summary>
    Allowed,

    /// <summary>The rule list was read and nothing in it lets the console's UDP reach this executable.</summary>
    Missing,
}

/// <summary>What, if anything, to put in front of the user about the firewall this start.</summary>
public enum FirewallOffer
{
    /// <summary>Say nothing: it already works, or the user has settled it for this copy.</summary>
    None,

    /// <summary>Never asked for this copy. The first-run wording.</summary>
    FirstRun,

    /// <summary>Asked before and allowed, but no rule covers the executable that is running now.</summary>
    Again,
}

/// <summary>
/// What the settings file remembers about the firewall. Not "did we ask" but "what was granted, and
/// for which executable": an answer given for a copy in one folder says nothing about a copy in
/// another, and a grant that is no longer backed by a rule has to be noticed rather than assumed.
/// </summary>
public readonly record struct FirewallMarker(bool Asked, bool Granted, string Executable);

/// <summary>
/// One inbound firewall rule, reduced to the five things that decide whether the console's telemetry
/// reaches this client. Built from the Windows rule list by <see cref="FirewallRules"/>; a plain
/// record so the decision below can be tested without a firewall.
/// </summary>
public readonly record struct FirewallRule(string Program, bool Inbound, bool Allow, bool Enabled, int Protocol)
{
    public const int Tcp = 6;

    public const int Udp = 17;

    /// <summary>Windows' "any protocol" rule, which the Allow-access prompt writes.</summary>
    public const int AnyProtocol = 256;

    public bool Covers(int protocol) => Protocol == protocol || Protocol == AnyProtocol;
}

/// <summary>
/// Whether the console's telemetry can reach this client, and the bundled helper that makes it so.
/// <para>
/// qwark streams live state to the PC over UDP on a port the client picks per connection, so the
/// rule has to be a program rule rather than a port rule, and it has to name the executable that
/// owns the socket. Under the installer that is <c>...\current\RaCMAN.App.exe</c>, not the stub
/// launcher in the folder above it, and not the folder the release happened to be staged in when
/// the user last pressed the button — which is why <see cref="Executable"/> asks the process rather
/// than guessing from the application folder, and why the answer is stored with the grant.
/// </para>
/// <para>
/// Reading the rule list needs no administrator rights (see <see cref="FirewallRules"/>); only
/// adding a rule does, which is the one thing that still costs a UAC prompt, and it is asked for
/// only when the rules really are missing.
/// </para>
/// </summary>
public static class FirewallHelper
{
    /// <summary>The name the rule must carry. The stub launcher beside the install shares it.</summary>
    public const string ExecutableName = "RaCMAN.App.exe";

    public const string UdpRuleName = "RaCMAN Reloaded (UDP-In)";

    public const string TcpRuleName = "RaCMAN Reloaded (TCP-In)";

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>The script that lives beside the executable in a published build.</summary>
    public static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "windows-firewall.ps1");

    public static bool ScriptPresent => IsSupported && File.Exists(ScriptPath);

    /// <summary>The executable a rule has to name for this run's telemetry to arrive.</summary>
    public static string TargetExecutable => Executable(Environment.ProcessPath, AppContext.BaseDirectory);

    /// <summary>
    /// Which executable owns the telemetry socket. The running process when that is the app itself,
    /// which is what the installer's <c>current\</c> copy and a portable copy both give; otherwise
    /// the app beside the application folder, which is what a <c>dotnet run</c> (process:
    /// <c>dotnet.exe</c>) and a debugger-hosted run give. Pure, so both shapes can be checked.
    /// </summary>
    public static string Executable(string? processPath, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(processPath)
            && string.Equals(Path.GetFileName(processPath), ExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return Normalise(processPath);
        }

        return Normalise(Path.Combine(baseDirectory, ExecutableName));
    }

    /// <summary>
    /// A path in the one spelling both sides of the comparison can agree on. Windows Firewall keeps
    /// what it was given: the Allow-access prompt writes the path lower-cased, our own helper writes
    /// it as the folder is really spelled, and a rule made by hand may carry <c>%LOCALAPPDATA%</c>
    /// or a relative step. So: unquote, expand the environment, make it absolute, drop a trailing
    /// separator. Case is left alone here and ignored by <see cref="SamePath"/>.
    /// </summary>
    public static string Normalise(string? path)
    {
        var text = (path ?? string.Empty).Trim().Trim('"').Trim();
        if (text.Length == 0) return string.Empty;

        try
        {
            text = Environment.ExpandEnvironmentVariables(text);
            text = Path.GetFullPath(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or IOException or System.Security.SecurityException)
        {
            // A path the platform will not parse cannot match a rule either; compare it as written.
        }

        return text.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Whether two paths name the same executable. Always case-insensitive: a firewall rule is a
    /// Windows thing whatever platform happens to be asking, and Windows does not distinguish.
    /// </summary>
    public static bool SamePath(string? left, string? right)
    {
        var a = Normalise(left);
        var b = Normalise(right);
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the rule list lets <paramref name="protocol"/> in for <paramref name="executable"/>.
    /// A rule counts only when it is enabled, inbound, an allow, names this executable and covers
    /// the protocol — which a rule written by Windows' own Allow-access prompt does, so a user who
    /// answered that prompt is never asked again by us.
    /// </summary>
    public static bool Allows(IEnumerable<FirewallRule> rules, string executable, int protocol)
    {
        foreach (var rule in rules)
        {
            if (rule is { Enabled: true, Inbound: true, Allow: true }
                && rule.Covers(protocol)
                && SamePath(rule.Program, executable))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The state the rule list puts this executable in. UDP decides it: the TCP side of the
    /// conversation is a connection this client opens, which a stateful firewall lets back in
    /// whatever the rules say, so a missing TCP rule is not a symptom the user would ever see.
    /// </summary>
    public static FirewallState StateOf(IEnumerable<FirewallRule>? rules, string executable) =>
        rules is null ? FirewallState.Unknown
        : Allows(rules, executable, FirewallRule.Udp) ? FirewallState.Allowed
        : FirewallState.Missing;

    /// <summary>
    /// What to show the user, from the whole of what is known. Pure: this is the rule, and the only
    /// place the rule lives.
    /// <list type="bullet">
    /// <item>A build with no helper beside it, or no Windows, has nothing to offer.</item>
    /// <item>A rule that already covers this executable ends it, whatever was asked before.</item>
    /// <item>Never asked: the first-run offer.</item>
    /// <item>Asked, but the rule list could not be read: say nothing rather than nag on a guess.</item>
    /// <item>Granted before and the rule is gone — the copy moved, or something removed it — say so
    /// and offer to put it back. This is the case an update leaves behind.</item>
    /// <item>Declined for this very executable: respect it. The Connection panel keeps the button.</item>
    /// <item>Declined for a different executable: that answer was about a different copy, so ask.</item>
    /// </list>
    /// </summary>
    public static FirewallOffer Decide(bool supported, bool scriptPresent, FirewallState state,
        FirewallMarker marker, string executable)
    {
        if (!supported || !scriptPresent) return FirewallOffer.None;
        if (state == FirewallState.Allowed) return FirewallOffer.None;
        if (!marker.Asked) return FirewallOffer.FirstRun;
        if (state == FirewallState.Unknown) return FirewallOffer.None;

        if (!SamePath(marker.Executable, executable))
            return marker.Granted ? FirewallOffer.Again : FirewallOffer.FirstRun;

        return marker.Granted ? FirewallOffer.Again : FirewallOffer.None;
    }

    /// <summary>
    /// Kicks off the elevated helper for <see cref="TargetExecutable"/>. Returns a short status for
    /// a toast: the OS approval prompt carries the real outcome, so success here only means the
    /// request was launched.
    /// </summary>
    public static (bool ok, string message) RequestRule() => RequestRule(TargetExecutable);

    /// <summary>The same, for a named executable. The path is passed to the script rather than left
    /// for it to guess, so the rule names exactly what was checked for.</summary>
    public static (bool ok, string message) RequestRule(string executable)
    {
        if (!IsSupported)
            return (false, "The firewall helper is Windows-only.");

        if (!ScriptPresent)
            return (false, "windows-firewall.ps1 isn't next to the app (dev build?). Run it from a published copy.");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{ScriptPath}\" -Program \"{executable}\"",
                UseShellExecute = true,   // required for the UAC "runas" elevation the script does
            };
            Process.Start(psi);
            return (true, "Approve the Windows administrator prompt to allow inbound UDP.");
        }
        catch (Exception ex)
        {
            return (false, $"Couldn't launch the firewall helper: {ex.Message}");
        }
    }
}
