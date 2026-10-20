using RaCMAN.App;
using Velopack;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The parts of the auto-updater that are decisions rather than downloads: which of two versions is
/// newer, whether a start is one that should look at all, and what the banner shows of the release
/// notes. Nothing here builds an UpdateManager or reaches GitHub; that is the point of keeping the
/// rules pure and handing the service its answers.
/// </summary>
public class UpdateTests
{
    [Fact]
    public void HigherNumbersAreNewer()
    {
        Assert.True(AppVersion.IsNewer("1.2.4", "1.2.3"));
        Assert.True(AppVersion.IsNewer("1.3.0", "1.2.99"));
        Assert.True(AppVersion.IsNewer("2.0.0", "1.99.99"));
        Assert.False(AppVersion.IsNewer("1.2.3", "1.2.3"));
        Assert.False(AppVersion.IsNewer("1.2.3", "1.2.4"));
    }

    [Fact]
    public void AMissingPartCountsAsZero()
    {
        Assert.False(AppVersion.IsNewer("1.2", "1.2.0"));
        Assert.True(AppVersion.IsNewer("1.2.1", "1.2"));
        Assert.False(AppVersion.IsNewer("1.2.0.0", "1.2"));
    }

    [Fact]
    public void ABuildStampIsNotPartOfTheVersion()
    {
        Assert.Equal("1.2.3", AppVersion.Clean("1.2.3+9f1c2ab"));
        Assert.False(AppVersion.IsNewer("1.2.3+9f1c2ab", "1.2.3"));
    }

    [Fact]
    public void ATagIsToleratedSoARawGitTagStillCompares()
    {
        Assert.True(AppVersion.IsNewer("v1.3.0", "1.2.0"));
    }

    [Fact]
    public void AReleaseIsAheadOfItsOwnPrereleases()
    {
        Assert.True(AppVersion.IsNewer("1.2.0", "1.2.0-beta.1"));
        Assert.False(AppVersion.IsNewer("1.2.0-beta.1", "1.2.0"));
        Assert.True(AppVersion.IsNewer("1.2.0-beta.2", "1.2.0-beta.1"));
    }

    [Fact]
    public void NonsenseSortsAsZeroRatherThanThrowing()
    {
        Assert.False(AppVersion.IsNewer("not-a-version", "1.0.0"));
        Assert.False(AppVersion.IsNewer(null, null));
        Assert.True(AppVersion.IsNewer("0.0.1", string.Empty));
    }

    [Fact]
    public void ThisBuildKnowsWhatVersionItIs()
    {
        // Whatever the csproj or the release workflow set, it has to parse as a version.
        Assert.True(AppVersion.IsNewer(AppVersion.Current, "0.0.0"));
        Assert.DoesNotContain('+', AppVersion.Current);
    }

    // ---------------------------------------------------------------- the startup gate

    private static bool Gate(bool allowed = true, bool installed = true, bool enabled = true,
        DateTime? last = null, DateTime? now = null) =>
        UpdateService.ShouldCheckAtStartup(allowed, installed, enabled, last, now ?? new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void AFirstStartChecks()
    {
        Assert.True(Gate());
    }

    [Fact]
    public void ARunThatMayNotTouchTheNetworkNeverChecks()
    {
        // --exit-after, --fake-server, --fake-rpcs3 and --no-update-check all land here.
        Assert.False(Gate(allowed: false));
    }

    [Fact]
    public void ACopyTheInstallerDidNotPutHereNeverChecks()
    {
        Assert.False(Gate(installed: false));
    }

    [Fact]
    public void TheSettingTurnsItOff()
    {
        Assert.False(Gate(enabled: false));
    }

    [Fact]
    public void OnceADayAndNoMore()
    {
        var now = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(Gate(last: now - TimeSpan.FromHours(23), now: now));
        Assert.True(Gate(last: now - TimeSpan.FromHours(25), now: now));
        Assert.True(Gate(last: now - UpdateService.CheckInterval, now: now));
    }

    [Fact]
    public void AStampInTheFutureIsAClockThatMovedRatherThanNeverAgain()
    {
        var now = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(Gate(last: now + TimeSpan.FromDays(400), now: now));
    }

    // ---------------------------------------------------------------- the service, offline

    [Fact]
    public void AServiceThatIsNotAllowedSaysSoAndStaysIdle()
    {
        var service = new UpdateService(new Settings(), action => action(), (_, _) => { }, allowed: false);

        Assert.True(service.IsInert);
        Assert.NotNull(service.InertReason);
        Assert.False(service.HasBanner);
        Assert.Equal(UpdateStage.Idle, service.Stage);

        // Both entry points are gated on the same flag, so neither can reach the network.
        service.StartupCheck();
        service.CheckNow();
        service.Download();
        service.RestartAndApply();

        Assert.Equal(UpdateStage.Idle, service.Stage);
    }

    [Fact]
    public void TheBannerNeedsSomethingToActOn()
    {
        var service = new UpdateService(new Settings(), action => action(), (_, _) => { }, allowed: false);

        Assert.False(service.HasBanner);
        service.Dismiss();
        Assert.True(service.Dismissed);
    }

    // ---------------------------------------------------------------- the notes on the banner
    //
    // A check's answer is handed to the service as Velopack would hand it over, on a service that
    // is not allowed to touch the network: it can show what it was given and can do nothing else.

    private static string Newer => AppVersion.IsNewer("99.0.0", AppVersion.Current) ? "99.0.0" : "9999.0.0";

    private static UpdateInfo Offer(string version) => new(new VelopackAsset
    {
        PackageId = "RaCMANReloaded",
        Version = SemanticVersion.Parse(version),
        Type = VelopackAssetType.Full,
        FileName = $"RaCMANReloaded-{version}-full.nupkg",
    }, false);

    private static ReleaseNote[] NotesFor(params string[] versions) =>
        versions.Select(v => new ReleaseNote { Version = v, Markdown = $"## {v}\n- fixed" }).ToArray();

    /// <summary>Settings of the test's own, so a save lands in a file nobody else is writing.</summary>
    private static Settings OwnSettings() =>
        Settings.Load(Path.Combine(TestDataFolder.Root, $"updates-{Guid.NewGuid():N}.settings.json"));

    private static UpdateService Offline(Settings? settings = null) =>
        new(settings ?? OwnSettings(), action => action(), (_, _) => { }, allowed: false);

    [Fact]
    public void AnOfferWithNotesHasAWhatsNewThatOpensAndCloses()
    {
        var service = Offline();
        service.Found(Offer(Newer), NotesFor(Newer), announce: false);

        Assert.True(service.HasBanner);
        Assert.Equal(UpdateStage.Available, service.Stage);
        Assert.True(service.HasNotes);
        Assert.False(service.NotesOpen);

        service.OpenNotes();
        Assert.True(service.NotesOpen);

        // The window is over the banner, not instead of it.
        Assert.True(service.HasBanner);

        service.CloseNotes();
        Assert.False(service.NotesOpen);
        Assert.True(service.HasBanner);
    }

    [Fact]
    public void AnOfferWithoutNotesIsThePlainBanner()
    {
        var service = Offline();
        service.Found(Offer(Newer), Array.Empty<ReleaseNote>(), announce: false);

        Assert.True(service.HasBanner);
        Assert.False(service.HasNotes);

        service.OpenNotes();
        Assert.False(service.NotesOpen);
    }

    [Fact]
    public void SkippedReleasesAreAllOnTheBanner()
    {
        var service = Offline();
        service.Found(Offer(Newer), ReleaseNotes.FromGithubReleases(
            """
            [{"tag_name":"v1.0.1","body":"older"},{"tag_name":"v1.0.3","body":"newest"},{"tag_name":"v1.0.2","body":"middle"}]
            """, "1.0.0", Newer), announce: false);

        Assert.Equal(new[] { "1.0.3", "1.0.2", "1.0.1" }, service.Notes.Select(n => n.Version));
    }

    [Fact]
    public void LaterClosesTheNotesWithTheBanner()
    {
        var service = Offline();
        service.Found(Offer(Newer), NotesFor(Newer), announce: false);
        service.OpenNotes();

        service.Dismiss();

        Assert.False(service.HasBanner);
        Assert.False(service.NotesOpen);
    }

    [Fact]
    public void ACheckThatFindsNothingTakesTheNotesAway()
    {
        var service = Offline();
        service.Found(Offer(Newer), NotesFor(Newer), announce: false);
        service.OpenNotes();

        service.Found(null, NotesFor(Newer), announce: false);

        Assert.Equal(UpdateStage.UpToDate, service.Stage);
        Assert.False(service.HasBanner);
        Assert.False(service.HasNotes);
        Assert.False(service.NotesOpen);
    }

    [Fact]
    public void NotesDoNotLetAServiceThatIsNotAllowedDownload()
    {
        var service = Offline();
        service.Found(Offer(Newer), NotesFor(Newer), announce: false);

        service.Download();
        service.RestartAndApply();

        Assert.Equal(UpdateStage.Available, service.Stage);
    }

    // ---------------------------------------------------------------- after the restart

    [Fact]
    public void ADownloadKeepsItsNotesForTheNewVersion()
    {
        var settings = OwnSettings();
        var service = Offline(settings);
        service.Found(Offer(Newer), NotesFor(Newer), announce: false);

        service.KeepNotesForAfterTheUpdate();

        var kept = Settings.Load(settings.Path).Updates.PendingNotes;
        Assert.NotNull(kept);
        Assert.Equal(Newer, kept!.Version);
        Assert.Equal("## " + Newer + "\n- fixed", Assert.Single(kept.Releases).Markdown);
    }

    [Fact]
    public void ADownloadWithoutNotesClearsWhatAnEarlierOneKept()
    {
        var settings = OwnSettings();
        settings.Updates.PendingNotes = new UpdateNotesRecord { Version = "0.0.1", Releases = NotesFor("0.0.1").ToList() };
        var service = Offline(settings);
        service.Found(Offer(Newer), Array.Empty<ReleaseNote>(), announce: false);

        service.KeepNotesForAfterTheUpdate();

        Assert.Null(Settings.Load(settings.Path).Updates.PendingNotes);
    }

    [Fact]
    public void TheFirstStartOfTheNewVersionShowsItsNotesOnce()
    {
        var settings = OwnSettings();
        settings.Updates.PendingNotes = new UpdateNotesRecord
        {
            Version = AppVersion.Current,
            Releases = NotesFor(AppVersion.Current).ToList(),
        };
        settings.Save();

        // Allowed, and safe: showing the notes reads the settings and nothing else, and this test
        // calls nothing that would build the update manager.
        var service = new UpdateService(settings, action => action(), (_, _) => { }, allowed: true);
        service.ShowInstalledNotes();

        Assert.Equal(AppVersion.Current, service.UpdatedTo);
        Assert.True(service.HasBanner);
        Assert.True(service.HasNotes);
        Assert.Null(Settings.Load(settings.Path).Updates.PendingNotes);

        service.Dismiss();
        Assert.False(service.HasBanner);

        // The next start has nothing to show.
        var again = new UpdateService(Settings.Load(settings.Path), action => action(), (_, _) => { }, allowed: true);
        again.ShowInstalledNotes();
        Assert.Null(again.UpdatedTo);
        Assert.False(again.HasBanner);
    }

    [Fact]
    public void ARunThatIsNotAllowedLeavesTheNotesForTheNextOne()
    {
        var settings = OwnSettings();
        settings.Updates.PendingNotes = new UpdateNotesRecord
        {
            Version = AppVersion.Current,
            Releases = NotesFor(AppVersion.Current).ToList(),
        };
        settings.Save();

        var service = Offline(settings);
        service.ShowInstalledNotes();

        Assert.Null(service.UpdatedTo);
        Assert.False(service.HasBanner);
        Assert.NotNull(Settings.Load(settings.Path).Updates.PendingNotes);
    }

    [Fact]
    public void ANewOfferReplacesTheInstalledVersionsNotes()
    {
        var settings = OwnSettings();
        settings.Updates.PendingNotes = new UpdateNotesRecord
        {
            Version = AppVersion.Current,
            Releases = NotesFor(AppVersion.Current).ToList(),
        };

        var service = new UpdateService(settings, action => action(), (_, _) => { }, allowed: true);
        service.ShowInstalledNotes();

        // Nothing newer: the installed version's notes stay on its banner.
        service.Found(null, Array.Empty<ReleaseNote>(), announce: false);
        Assert.Equal(AppVersion.Current, service.UpdatedTo);
        Assert.True(service.HasNotes);

        service.Found(Offer(Newer), NotesFor(Newer), announce: false);
        Assert.Null(service.UpdatedTo);
        Assert.Equal(Newer, Assert.Single(service.Notes).Version);
    }
}
