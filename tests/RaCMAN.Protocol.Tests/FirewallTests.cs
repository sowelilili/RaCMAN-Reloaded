using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Whether the console's telemetry can reach this client, and whether to ask the user about it.
/// <para>
/// The bug these are here for: the client used to remember one boolean, "the firewall offer has
/// been shown", and never looked at the firewall again. A firewall rule names one executable by its
/// full path, and that path moves — the installer replaces its <c>current\</c> folder on every
/// update, a portable copy gets dragged elsewhere, a second copy's run of the helper deletes the
/// first copy's rule by name. None of that was ever noticed, so the user was left with UDP blocked,
/// no offer, and a client quietly on the slow TCP path.
/// </para>
/// <para>
/// Nothing here touches a firewall. Reading the rule list is a thin shim
/// (<see cref="FirewallRules"/>); everything that decides anything is pure, and that is what is
/// tested. The paths are built under the temp folder so they are absolute on every platform, and
/// none of them has to exist.
/// </para>
/// </summary>
public class FirewallTests
{
    /// <summary>An absolute path on whichever platform is running, of a file that need not exist.</summary>
    private static string At(params string[] parts) =>
        Path.Combine(new[] { Path.GetTempPath() }.Concat(parts).ToArray());

    /// <summary>Where the installer puts the copy that owns the telemetry socket.</summary>
    private static string Installed => At("RaCMANReloaded", "current", "RaCMAN.App.exe");

    private static FirewallRule Allow(string program, int protocol = FirewallRule.Udp) =>
        new(program, Inbound: true, Allow: true, Enabled: true, Protocol: protocol);

    private static FirewallMarker Never => new(Asked: false, Granted: false, Executable: string.Empty);

    private static FirewallOffer Decide(FirewallState state, FirewallMarker marker) =>
        FirewallHelper.Decide(supported: true, scriptPresent: true, state, marker, Installed);

    // --- which executable a rule has to name ---------------------------------------------------

    [Fact]
    public void TheRuleNamesTheRunningProcessWhenThatIsTheAppItself()
    {
        // The installed copy: the stub launcher in the folder above starts this one, and this one
        // owns the UDP socket, so this one is what a rule must name.
        var executable = FirewallHelper.Executable(Installed, At("RaCMANReloaded", "current"));

        Assert.True(FirewallHelper.SamePath(Installed, executable));
    }

    [Fact]
    public void TheRuleFallsBackToTheApplicationFolderWhenTheProcessIsAHost()
    {
        // "dotnet run", or a debugger: the process is not the app, so the app in the application
        // folder is the best answer there is.
        string folder = At("racman-build");
        var executable = FirewallHelper.Executable(At("dotnet", "dotnet.exe"), folder);

        Assert.True(FirewallHelper.SamePath(Path.Combine(folder, "RaCMAN.App.exe"), executable));
    }

    [Fact]
    public void AProcessPathThatIsMissingOrBlankFallsBackTooRatherThanThrowing()
    {
        string folder = At("racman-build");
        string expected = Path.Combine(folder, "RaCMAN.App.exe");

        Assert.True(FirewallHelper.SamePath(expected, FirewallHelper.Executable(null, folder)));
        Assert.True(FirewallHelper.SamePath(expected, FirewallHelper.Executable("   ", folder)));
    }

    // --- comparing the path in a rule against the path being asked about -----------------------

    [Fact]
    public void PathsAreComparedWithoutRegardToCase()
    {
        // Windows' own Allow-access prompt writes the path lower-cased; our helper writes it as the
        // folder is really spelled. They name the same file.
        Assert.True(FirewallHelper.SamePath(Installed, Installed.ToUpperInvariant()));
        Assert.True(FirewallHelper.SamePath(Installed.ToLowerInvariant(), Installed));
    }

    [Fact]
    public void PathsAreComparedWithTheirRelativeStepsCollapsed()
    {
        string round = Path.Combine(At("RaCMANReloaded", "current"), "..", "current", "RaCMAN.App.exe");

        Assert.True(FirewallHelper.SamePath(round, Installed));
    }

    [Fact]
    public void ATrailingSeparatorAndSurroundingQuotesDoNotChangeAPath()
    {
        string folder = At("RaCMANReloaded", "current");

        Assert.True(FirewallHelper.SamePath(folder + Path.DirectorySeparatorChar, folder));
        Assert.True(FirewallHelper.SamePath($"\"{Installed}\"", Installed));
        Assert.True(FirewallHelper.SamePath($"  {Installed}  ", Installed));
    }

    [Fact]
    public void NothingIsNotAPathAndMatchesNothing()
    {
        Assert.Equal(string.Empty, FirewallHelper.Normalise(null));
        Assert.Equal(string.Empty, FirewallHelper.Normalise("   "));
        Assert.Equal(string.Empty, FirewallHelper.Normalise("\"\""));
        Assert.False(FirewallHelper.SamePath(null, null));
        Assert.False(FirewallHelper.SamePath(string.Empty, Installed));
    }

    // --- reading an answer out of the rule list ------------------------------------------------

    [Fact]
    public void AnEnabledInboundAllowForThisExecutableIsWhatCounts()
    {
        var rules = new[] { Allow(Installed) };

        Assert.True(FirewallHelper.Allows(rules, Installed, FirewallRule.Udp));
        Assert.Equal(FirewallState.Allowed, FirewallHelper.StateOf(rules, Installed));
    }

    [Fact]
    public void AProtocolAnyRuleCountsForUdpToo()
    {
        // What Windows' own Allow-access prompt writes when the user answers it. A user who has
        // already said yes to Windows must never be asked again by us.
        var rules = new[] { Allow(Installed, FirewallRule.AnyProtocol) };

        Assert.Equal(FirewallState.Allowed, FirewallHelper.StateOf(rules, Installed));
    }

    [Fact]
    public void ARuleThatIsOffOrOutboundOrABlockOrForTcpOnlyDoesNotCount()
    {
        var rules = new[]
        {
            Allow(Installed) with { Enabled = false },
            Allow(Installed) with { Inbound = false },
            Allow(Installed) with { Allow = false },
            Allow(Installed, FirewallRule.Tcp),
        };

        Assert.Equal(FirewallState.Missing, FirewallHelper.StateOf(rules, Installed));
    }

    [Fact]
    public void ARuleForAnotherCopyOfTheAppDoesNotCount()
    {
        // The shape of the reported bug: a rule exists, it is even named for RaCMAN, and it points
        // at the folder the client used to live in.
        var rules = new[] { Allow(At("RaCMANReloaded", "app-1.0.1", "RaCMAN.App.exe")) };

        Assert.Equal(FirewallState.Missing, FirewallHelper.StateOf(rules, Installed));
    }

    [Fact]
    public void ARuleListThatCouldNotBeReadIsNotAnAnswer()
    {
        Assert.Equal(FirewallState.Unknown, FirewallHelper.StateOf(null, Installed));
    }

    // --- what to put in front of the user ------------------------------------------------------

    [Fact]
    public void ABuildWithNoHelperBesideItOffersNothing()
    {
        Assert.Equal(FirewallOffer.None,
            FirewallHelper.Decide(supported: true, scriptPresent: false, FirewallState.Missing, Never, Installed));
        Assert.Equal(FirewallOffer.None,
            FirewallHelper.Decide(supported: false, scriptPresent: true, FirewallState.Missing, Never, Installed));
    }

    [Fact]
    public void ARuleThatIsAlreadyThereEndsTheQuestionWhateverWasAskedBefore()
    {
        Assert.Equal(FirewallOffer.None, Decide(FirewallState.Allowed, Never));
        Assert.Equal(FirewallOffer.None,
            Decide(FirewallState.Allowed, new FirewallMarker(Asked: true, Granted: false, Installed)));
        Assert.Equal(FirewallOffer.None,
            Decide(FirewallState.Allowed, new FirewallMarker(Asked: true, Granted: true, Installed)));
    }

    [Fact]
    public void NeverAskedAndNoRuleIsTheFirstRunOffer()
    {
        Assert.Equal(FirewallOffer.FirstRun, Decide(FirewallState.Missing, Never));
    }

    [Fact]
    public void GrantedForThisExecutableAndNowGoneIsOfferedAgain()
    {
        // It was allowed; the rule has gone. This is what an update used to leave behind in silence.
        var marker = new FirewallMarker(Asked: true, Granted: true, Installed);

        Assert.Equal(FirewallOffer.Again, Decide(FirewallState.Missing, marker));
    }

    [Fact]
    public void GrantedForSomewhereElseIsOfferedAgainForHere()
    {
        // An update that moves the executable, and the dragged-portable-copy case: the grant was
        // real, it simply was not about this executable.
        var marker = new FirewallMarker(Asked: true, Granted: true,
            At("RaCMANReloaded", "app-1.0.1", "RaCMAN.App.exe"));

        Assert.Equal(FirewallOffer.Again, Decide(FirewallState.Missing, marker));
    }

    [Fact]
    public void DeclinedForThisExecutableIsRespectedAndNotAskedAgain()
    {
        var marker = new FirewallMarker(Asked: true, Granted: false, Installed);

        Assert.Equal(FirewallOffer.None, Decide(FirewallState.Missing, marker));
    }

    [Fact]
    public void DeclinedForSomewhereElseSaysNothingAboutThisCopy()
    {
        var marker = new FirewallMarker(Asked: true, Granted: false, At("unzipped", "RaCMAN.App.exe"));

        Assert.Equal(FirewallOffer.FirstRun, Decide(FirewallState.Missing, marker));
    }

    [Fact]
    public void ARuleListThatCouldNotBeReadNagsNobodyWhoHasAlreadyBeenAsked()
    {
        Assert.Equal(FirewallOffer.None,
            Decide(FirewallState.Unknown, new FirewallMarker(Asked: true, Granted: true, Installed)));
        Assert.Equal(FirewallOffer.None,
            Decide(FirewallState.Unknown, new FirewallMarker(Asked: true, Granted: false, Installed)));

        // Never asked at all is still worth one offer: the worst it costs is a dialog.
        Assert.Equal(FirewallOffer.FirstRun, Decide(FirewallState.Unknown, Never));
    }

    // --- the marker, and where it lives ---------------------------------------------------------

    [Fact]
    public void TheMarkerIsInTheDataFolderAndNotBesideTheExecutable()
    {
        // The whole point: the updater replaces the application folder wholesale, so a marker kept
        // there would reset on every update and the offer would come back every single time.
        Assert.StartsWith(AppPaths.Root, Settings.DefaultPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(Settings.DefaultPath.StartsWith(AppPaths.Application, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheMarkerSurvivesTheApplicationFolderBeingReplaced()
    {
        var folder = TempFolder();

        // The build that was running when the user granted it.
        string file = Path.Combine(folder, AppPaths.SettingsFileName);
        var before = Settings.Load(file);
        before.RecordFirewall(granted: true, Installed);

        // The next build, out of a brand new application folder, reading the same data folder.
        var after = Settings.Load(file);

        Assert.True(after.Firewall.Asked);
        Assert.True(after.Firewall.Granted);
        Assert.True(FirewallHelper.SamePath(Installed, after.Firewall.Executable));
        Assert.Equal(FirewallOffer.None,
            FirewallHelper.Decide(true, true, FirewallState.Allowed, after.Firewall.Marker, Installed));
    }

    [Fact]
    public void RecordingTheSameAnswerTwiceChangesNothing()
    {
        var settings = new Settings();

        Assert.True(settings.Firewall.Record(granted: true, Installed));
        Assert.False(settings.Firewall.Record(granted: true, Installed));
        Assert.True(settings.Firewall.Record(granted: false, Installed));
    }

    [Fact]
    public void TheRecordedPathIsStoredNormalised()
    {
        var settings = new Settings();
        settings.Firewall.Record(granted: true, $"  \"{Installed}\"  ");

        Assert.Equal(FirewallHelper.Normalise(Installed), settings.Firewall.Executable);
    }

    [Fact]
    public void TheOldFirewallOfferedFlagBecomesAskedButNotGranted()
    {
        // A settings file from a build that only ever recorded "the offer was shown".
        string file = Path.Combine(TempFolder(), AppPaths.SettingsFileName);
        File.WriteAllText(file, "{\"firewallOffered\":true}");

        var loaded = Settings.Load(file);

        Assert.True(loaded.Firewall.Asked);
        Assert.False(loaded.Firewall.Granted);
        Assert.Equal(string.Empty, loaded.Firewall.Executable);

        // Which means: no rule, and no idea what was answered about, so ask once about this copy.
        Assert.Equal(FirewallOffer.FirstRun,
            FirewallHelper.Decide(true, true, FirewallState.Missing, loaded.Firewall.Marker, Installed));
    }

    [Fact]
    public void AFileWithNoFirewallKeyAtAllLoadsAsNeverAsked()
    {
        string file = Path.Combine(TempFolder(), AppPaths.SettingsFileName);
        File.WriteAllText(file, "{\"lastHost\":\"192.168.1.50\"}");

        var loaded = Settings.Load(file);

        Assert.False(loaded.Firewall.Asked);
        Assert.Equal(FirewallOffer.FirstRun,
            FirewallHelper.Decide(true, true, FirewallState.Missing, loaded.Firewall.Marker, Installed));
    }

    /// <summary>A folder of this run's own, inside the data folder the whole run is confined to.</summary>
    private static string TempFolder()
    {
        var folder = Path.Combine(TestDataFolder.Root, Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        return folder;
    }
}
