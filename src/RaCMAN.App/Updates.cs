using System.Globalization;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace RaCMAN.App;

/// <summary>
/// The version this build calls itself, out of the assembly's informational version. The release
/// workflow sets it from the tag with <c>-p:Version=</c>, so a local build is whatever the csproj
/// says (1.0.0) and a release is the tag without its "v".
/// </summary>
public static class AppVersion
{
    /// <summary>What the window title and the Settings panel show.</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) informational = assembly.GetName().Version?.ToString(3);
        return Clean(informational);
    }

    /// <summary>
    /// The version without its build metadata: a build stamped by SourceLink calls itself
    /// "1.2.3+9f1c2ab", and the commit is not something to put in a window title.
    /// </summary>
    public static string Clean(string? version)
    {
        var text = (version ?? string.Empty).Trim();
        if (text.Length == 0) return "0.0.0";

        int plus = text.IndexOf('+');
        return plus < 0 ? text : text[..plus];
    }

    /// <summary>True when <paramref name="candidate"/> is a version worth offering over <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    /// <summary>
    /// Orders two versions the way semver does, as far as this client needs it: the numeric parts
    /// first, and then a release ahead of any prerelease of the same numbers, so 1.2.0 beats
    /// 1.2.0-beta.1 and 1.2.0-beta.2 beats 1.2.0-beta.1. Anything that will not parse as numbers
    /// sorts as 0.0.0 rather than throwing, because the answer only decides what a banner says.
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        var (leftNumbers, leftTag) = Split(left);
        var (rightNumbers, rightTag) = Split(right);

        for (int i = 0; i < Math.Max(leftNumbers.Length, rightNumbers.Length); i++)
        {
            int a = i < leftNumbers.Length ? leftNumbers[i] : 0;
            int b = i < rightNumbers.Length ? rightNumbers[i] : 0;
            if (a != b) return a.CompareTo(b);
        }

        if (leftTag.Length == 0 && rightTag.Length == 0) return 0;

        // A release is ahead of every prerelease that carries the same numbers.
        if (leftTag.Length == 0) return 1;
        if (rightTag.Length == 0) return -1;

        return string.Compare(leftTag, rightTag, StringComparison.OrdinalIgnoreCase);
    }

    private static (int[] Numbers, string Tag) Split(string? version)
    {
        var text = Clean(version);
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

        int dash = text.IndexOf('-');
        string tag = dash < 0 ? string.Empty : text[(dash + 1)..];
        string core = dash < 0 ? text : text[..dash];

        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            numbers[i] = int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? Math.Max(value, 0)
                : 0;
        }

        return (numbers, tag);
    }
}

/// <summary>Where the update service has got to. The banner and the Settings panel read it.</summary>
public enum UpdateStage
{
    /// <summary>Nothing has been asked yet, or the answer was "no update" a while ago.</summary>
    Idle,

    Checking,

    /// <summary>The last check found nothing, and the user asked for it, so say so.</summary>
    UpToDate,

    /// <summary>There is a newer release, not downloaded yet.</summary>
    Available,

    Downloading,

    /// <summary>Downloaded and staged; a restart applies it.</summary>
    Ready,

    /// <summary>The last thing that was asked for did not work. <see cref="UpdateService.Problem"/> says why.</summary>
    Failed,
}

/// <summary>
/// Auto-update through the GitHub releases of this repository, using Velopack. The releases carry
/// the installer, the self-updating portable build and the update feed, and Velopack replaces the
/// application folder wholesale, which is exactly why the user's files live somewhere else (see
/// <see cref="AppPaths"/>).
/// <para>
/// A copy that Velopack did not install cannot update itself: a <c>dotnet run</c>, a plain unzip of
/// the app folder or a build tree has no update folder beside it, so the service goes inert and the
/// Settings panel says so rather than offering a button that would do nothing.
/// </para>
/// <para>
/// Every network call is behind <see cref="Allowed"/>, which the headless flags turn off, and
/// behind a user's own button. Nothing here is reached by a test.
/// </para>
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/sowelilili/RaCMAN-Reloaded";

    /// <summary>How long a check is good for. One a day is enough for a trainer.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly Settings _settings;
    private readonly Action<Action> _post;
    private readonly Action<string, ToastKind> _toast;

    private UpdateManager? _manager;
    private ReleaseListCapture? _releaseList;
    private UpdateInfo? _pending;
    private bool _managerFailed;

    public UpdateService(Settings settings, Action<Action> post, Action<string, ToastKind> toast, bool allowed)
    {
        _settings = settings;
        _post = post;
        _toast = toast;
        Allowed = allowed;
    }

    /// <summary>
    /// False for a run that must not touch the network: <c>--exit-after</c>, the fake servers and
    /// <c>--no-update-check</c>. It gates the button as well as the startup check, so a headless
    /// run cannot reach GitHub however it is driven.
    /// </summary>
    public bool Allowed { get; }

    public UpdateStage Stage { get; private set; } = UpdateStage.Idle;

    /// <summary>The version the last check found, or null when there is nothing on offer.</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>How far the download has got, 0 to 100.</summary>
    public int Percent { get; private set; }

    /// <summary>Why the last thing asked for failed. Null when nothing has.</summary>
    public string? Problem { get; private set; }

    /// <summary>Set by the banner's Later button, so one run is not nagged twice.</summary>
    public bool Dismissed { get; private set; }

    /// <summary>
    /// What changed, newest release first: the notes of every version between this one and the one
    /// on offer, or, on the first start of a new version, the notes it was downloaded with. Empty
    /// when the releases say nothing, and then the banner is the plain one.
    /// </summary>
    public IReadOnlyList<ReleaseNote> Notes { get; private set; } = Array.Empty<ReleaseNote>();

    public bool HasNotes => Notes.Count > 0;

    /// <summary>Whether the What's new window is open.</summary>
    public bool NotesOpen { get; private set; }

    /// <summary>
    /// The version this start was updated to, while its notes are on show; null otherwise. Set
    /// once, by <see cref="ShowInstalledNotes"/>, on the first start of that version.
    /// </summary>
    public string? UpdatedTo { get; private set; }

    public bool Busy => Stage is UpdateStage.Checking or UpdateStage.Downloading;

    /// <summary>
    /// Why this copy cannot update itself, or null when it can. Checked once: it is a property of
    /// how the client was installed, which does not change while it runs.
    /// </summary>
    public string? InertReason
    {
        get
        {
            if (!Allowed) return "Update checks are switched off for this run.";
            if (Manager is null) return "This copy cannot check for updates.";
            if (!Manager.IsInstalled)
            {
                return "This copy was not installed by the RaCMAN Reloaded installer, so it cannot update itself. "
                       + "Download the latest release from GitHub instead.";
            }

            return null;
        }
    }

    public bool IsInert => InertReason is not null;

    /// <summary>
    /// The banner is drawn for the three states the user can act on, and on the first start of a
    /// new version that came with notes.
    /// </summary>
    public bool HasBanner =>
        !Dismissed && (Stage is UpdateStage.Available or UpdateStage.Downloading or UpdateStage.Ready
                       || UpdatedTo is not null);

    public DateTime? LastCheckUtc => _settings.Updates.LastCheckUtc;

    /// <summary>
    /// Whether the once-a-day check runs at startup. Pure, because it is the whole of the rule:
    /// switched on, able to update, and either never checked or checked long enough ago. A stamp in
    /// the future is a clock that moved and counts as "never", rather than as never again.
    /// </summary>
    public static bool ShouldCheckAtStartup(bool allowed, bool installed, bool enabled, DateTime? lastCheckUtc,
        DateTime nowUtc)
    {
        if (!allowed || !installed || !enabled) return false;
        if (lastCheckUtc is not { } last) return true;
        if (last > nowUtc) return true;

        return nowUtc - last >= CheckInterval;
    }

    /// <summary>The once-a-day check. Silent: a run that finds nothing says nothing.</summary>
    public void StartupCheck()
    {
        if (!ShouldCheckAtStartup(Allowed, Manager?.IsInstalled ?? false, _settings.Updates.Check,
                _settings.Updates.LastCheckUtc, DateTime.UtcNow))
        {
            return;
        }

        Check(announce: false);
    }

    /// <summary>
    /// The first start of a new version: the notes it was downloaded with, shown once on the banner
    /// and then forgotten. Notes for a version that has not been applied yet stay for the start
    /// that applies it. Nothing here reaches the network, and it is still off for a run that is not
    /// <see cref="Allowed"/>, so a headless run on the real data folder cannot use the notes up.
    /// </summary>
    public void ShowInstalledNotes()
    {
        if (!Allowed || _settings.Updates.PendingNotes is not { } stored) return;

        var (show, keep) = ReleaseNotes.AfterUpdate(stored, AppVersion.Current);
        if (!keep)
        {
            _settings.Updates.PendingNotes = null;
            _settings.Save();
        }

        if (show.Count == 0) return;

        UpdatedTo = AppVersion.Clean(stored.Version);
        Notes = show;
        NotesOpen = false;
    }

    /// <summary>The Settings panel's button. Says what it found either way, since it was asked.</summary>
    public void CheckNow() => Check(announce: true);

    private void Check(bool announce)
    {
        // Allowed is tested before anything else on every path out of this object, so a run that
        // must not touch the network does not even build the manager that would.
        if (!Allowed || Busy || Manager is not { } manager || !manager.IsInstalled) return;

        Stage = UpdateStage.Checking;
        Problem = null;

        var releaseList = _releaseList;

        _ = Task.Run(async () =>
        {
            try
            {
                releaseList?.Clear();
                var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
                var notes = NotesFor(update, releaseList?.Last);
                _post(() => Found(update, notes, announce));
            }
            catch (Exception ex)
            {
                _post(() => Failed("Update check failed", ex, announce));
            }
            finally
            {
                // Stamped whatever the answer was, so a PC that is offline asks once a day rather
                // than on every start.
                _post(Stamp);
            }
        });
    }

    /// <summary>
    /// The notes for what a check found, out of the releases list Velopack fetched for that check.
    /// Off the render thread, and never throws: an update whose notes cannot be read is offered
    /// without them.
    /// </summary>
    private static IReadOnlyList<ReleaseNote> NotesFor(UpdateInfo? update, string? releaseList)
    {
        try
        {
            string? target = update?.TargetFullRelease?.Version?.ToString();
            return target is null
                ? Array.Empty<ReleaseNote>()
                : ReleaseNotes.FromGithubReleases(releaseList, AppVersion.Current, target);
        }
        catch (Exception)
        {
            return Array.Empty<ReleaseNote>();
        }
    }

    /// <summary>
    /// What a check's answer does, on the render thread. Public so the tests can hand the service an
    /// answer, notes and all, without a network; a service that is not <see cref="Allowed"/> still
    /// cannot download or apply what it is handed.
    /// </summary>
    public void Found(UpdateInfo? update, IReadOnlyList<ReleaseNote> notes, bool announce)
    {
        _pending = update;
        string? version = update?.TargetFullRelease?.Version?.ToString();

        if (update is null || version is null || !AppVersion.IsNewer(version, AppVersion.Current))
        {
            _pending = null;
            AvailableVersion = null;
            Stage = UpdateStage.UpToDate;

            // The notes of the version just installed stay on its banner; an offer's go with it.
            if (UpdatedTo is null)
            {
                Notes = Array.Empty<ReleaseNote>();
                NotesOpen = false;
            }

            if (announce) _toast($"RaCMAN Reloaded {AppVersion.Current} is the latest version", ToastKind.Success);
            return;
        }

        UpdatedTo = null;
        AvailableVersion = version;
        Notes = notes ?? Array.Empty<ReleaseNote>();
        NotesOpen = NotesOpen && HasNotes;
        Stage = UpdateStage.Available;
        Dismissed = false;
    }

    /// <summary>The banner's What's new button: opens the notes window, when there is something in it.</summary>
    public void OpenNotes() => NotesOpen = HasNotes;

    /// <summary>The notes window's Close button, and the cross on its title bar.</summary>
    public void CloseNotes() => NotesOpen = false;

    /// <summary>Downloads what the last check found. The banner's Download button.</summary>
    public void Download()
    {
        if (!Allowed || Busy || _pending is not { } update || Manager is not { } manager) return;

        Stage = UpdateStage.Downloading;
        Percent = 0;
        Problem = null;

        _ = Task.Run(async () =>
        {
            try
            {
                // Velopack reports progress from its own thread; the render thread owns every field
                // on this object, so the number goes back through the queue like everything else.
                await manager.DownloadUpdatesAsync(update, percent => _post(() => Percent = percent))
                    .ConfigureAwait(false);
                _post(() =>
                {
                    Percent = 100;
                    Stage = UpdateStage.Ready;
                    Dismissed = false;
                    KeepNotesForAfterTheUpdate();
                });
            }
            catch (Exception ex)
            {
                _post(() => Failed("Download failed", ex, announce: true));
            }
        });
    }

    /// <summary>
    /// Applies what was downloaded and comes back on the new version. This does not return: Velopack
    /// hands over to the update process, which waits for this one to leave.
    /// </summary>
    public void RestartAndApply()
    {
        if (!Allowed || Stage != UpdateStage.Ready || _pending is not { } update || Manager is not { } manager) return;

        try
        {
            // Started again the way it was started: a run told to use a different data folder, or
            // to connect to a console, keeps that across the update.
            manager.ApplyUpdatesAndRestart(update.TargetFullRelease, Environment.GetCommandLineArgs().Skip(1).ToArray());
        }
        catch (Exception ex)
        {
            Failed("Update failed", ex, announce: true);
        }
    }

    /// <summary>
    /// Puts the notes of the update just downloaded in the settings, for the first start of the new
    /// version to show once (<see cref="ShowInstalledNotes"/>). Now rather than on Restart to update,
    /// because Velopack also applies a downloaded update by itself on the next start. An update with
    /// no notes clears whatever an earlier download left. Public for the tests, as <see cref="Found"/> is.
    /// </summary>
    public void KeepNotesForAfterTheUpdate()
    {
        var record = HasNotes && AvailableVersion is { } version
            ? new UpdateNotesRecord { Version = version, Releases = Notes.ToList() }
            : null;

        if (record is null && _settings.Updates.PendingNotes is null) return;

        _settings.Updates.PendingNotes = record;
        _settings.Save();
    }

    /// <summary>
    /// The banner's Later button: not this run, and not a word about it again until restart. On the
    /// first start of a new version it is Close, and the notes are done with.
    /// </summary>
    public void Dismiss()
    {
        Dismissed = true;
        NotesOpen = false;
    }

    /// <summary>What the Settings panel prints under the buttons.</summary>
    public string StatusLine() => Stage switch
    {
        UpdateStage.Checking => "Checking...",
        UpdateStage.UpToDate => $"RaCMAN Reloaded {AppVersion.Current} is the latest version.",
        UpdateStage.Available => $"RaCMAN Reloaded {AvailableVersion} is available.",
        UpdateStage.Downloading => $"Downloading RaCMAN Reloaded {AvailableVersion}... {Percent}%",
        UpdateStage.Ready => $"RaCMAN Reloaded {AvailableVersion} is ready; restart to use it.",
        UpdateStage.Failed => Problem ?? "The last update check failed.",
        _ => LastCheckUtc is { } last
            ? $"Last checked {last.ToLocalTime():yyyy-MM-dd HH:mm}."
            : "Not checked yet.",
    };

    private void Failed(string what, Exception ex, bool announce)
    {
        Stage = UpdateStage.Failed;
        Problem = $"{what}: {ex.Message}";
        if (announce) _toast(Problem, ToastKind.Error);
    }

    private void Stamp()
    {
        _settings.Updates.LastCheckUtc = DateTime.UtcNow;
        _settings.Save();
    }

    /// <summary>
    /// The Velopack manager, built once. GithubSource with no token and no prereleases: the
    /// repository is public, and a prerelease is something a maintainer tries by hand rather than
    /// something a player is offered. Its downloader is Velopack's own, wrapped so the releases list
    /// it fetches is kept for the notes. Null when Velopack would not build one at all, which the
    /// Settings panel reports rather than throwing on the render thread.
    /// </summary>
    private UpdateManager? Manager
    {
        get
        {
            if (_manager is not null || _managerFailed) return _manager;

            try
            {
                var releaseList = new ReleaseListCapture(new HttpClientFileDownloader());
                _manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false, releaseList));
                _releaseList = releaseList;
            }
            catch (Exception ex)
            {
                _managerFailed = true;
                Problem = $"Updates unavailable: {ex.Message}";
            }

            return _manager;
        }
    }
}
