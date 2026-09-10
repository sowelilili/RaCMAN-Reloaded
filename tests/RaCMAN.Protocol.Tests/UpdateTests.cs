using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The parts of the auto-updater that are decisions rather than downloads: which of two versions is
/// newer, and whether a start is one that should look at all. Nothing here builds an UpdateManager
/// or reaches GitHub; that is the point of keeping both rules pure.
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
}
