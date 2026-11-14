using System.Diagnostics;
using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The Settings page's tabs: the side nav it puts in place of the panels (Back and Esc), which rows
/// each tab has (the second round's RaCMAN, IPC, Console and Advanced), the IPC rows' resets, the
/// Console tab's boot plugin look and Installation row under Standalone, Apply toggles on game boot
/// and its question (qwark revision 1.33), and the Input display rate, the console's telemetry rate
/// of qwark revision 1.32 (TELEMETRY_RATE, SessionInfo flags3 bits 2 to 4), against the fake console.
/// </summary>
[Collection(SettingsPanelStatics.Name)]
public class SettingsPageTests
{
    private static readonly SessionInfo Capable = SessionInfo.Empty with
    {
        QwarkVersion = QwarkClient.ExpectedQwarkBuild,
        State = SessionState.Ingame,
        Game = GameId.Rac1,
        ProfileFlags = ProfileFlags.Supported,
        Flags3 = SessionFlags3.RunWarnings | SessionFlags3.Settings133 | SessionInfo.TelemetryRateBits(60),
    };

    /// <summary>A module from before revision 1.32: no telemetry rate bits, so no switch, and none of 1.33's.</summary>
    private static readonly SessionInfo OlderModule = Capable with { Flags3 = SessionFlags3.RunWarnings };

    private static SettingsContext Context(SessionInfo session, bool connected, InputRateMemory? memory = null,
        bool standalone = false) =>
        new(session, connected, memory ?? new InputRateMemory(), standalone);

    // ---------------------------------------------------------------- the nav

    [Fact]
    public void SettingsTakesTheNavAndBackReturnsToThePanelItWasOpenedFrom()
    {
        var nav = new SettingsNav();
        Assert.False(nav.Open);

        Assert.Equal(PanelNav.Settings, nav.Enter(PanelNav.Mods));
        Assert.True(nav.Open);

        Assert.Equal(PanelNav.Mods, nav.Back());
        Assert.False(nav.Open);

        // Opened again from somewhere else, Back goes there instead.
        nav.Enter(PanelNav.InputDisplay);
        Assert.Equal(PanelNav.InputDisplay, nav.Back());
    }

    [Fact]
    public void PressingSettingsWhileOnSettingsKeepsTheWayBack()
    {
        var nav = new SettingsNav();
        nav.Enter(PanelNav.Combos);
        nav.Enter(PanelNav.Settings);

        Assert.Equal(PanelNav.Combos, nav.Back());
    }

    [Fact]
    public void TheTabIsKeptWhileThePageIsClosed()
    {
        var nav = new SettingsNav();
        nav.Enter(PanelNav.Game);
        Assert.Equal(SettingsTab.RaCMAN, nav.Tab);

        nav.Select(SettingsTab.Console);
        nav.Back();
        nav.Enter(PanelNav.Game);

        Assert.Equal(SettingsTab.Console, nav.Tab);
    }

    [Fact]
    public void EscGoesBackOnlyWhenNothingElseWantsIt()
    {
        var nav = new SettingsNav();
        Assert.False(nav.EscapeGoesBack(escapePressed: true, typing: false, popupOpen: false));

        nav.Enter(PanelNav.Positions);
        Assert.True(nav.EscapeGoesBack(escapePressed: true, typing: false, popupOpen: false));
        Assert.False(nav.EscapeGoesBack(escapePressed: false, typing: false, popupOpen: false));

        // A box being typed in, and a dropdown or modal that is open, take Esc for themselves.
        Assert.False(nav.EscapeGoesBack(escapePressed: true, typing: true, popupOpen: false));
        Assert.False(nav.EscapeGoesBack(escapePressed: true, typing: false, popupOpen: true));
    }

    [Fact]
    public void ThePanelMovingAwayClosesTheTabsAndAStartOnSettingsOpensThem()
    {
        var nav = new SettingsNav();
        nav.Enter(PanelNav.Unlocks);

        // The window sent elsewhere (a --game-section route, a panel that went away).
        nav.Follow(PanelNav.Game);
        Assert.False(nav.Open);

        // --panel 11 starts on Settings without a press.
        nav.Follow(PanelNav.Settings);
        Assert.True(nav.Open);
    }

    [Theory]
    [InlineData("console", SettingsTab.Console)]
    [InlineData("IPC", SettingsTab.Ipc)]
    [InlineData("Advanced", SettingsTab.Advanced)]
    [InlineData("0", SettingsTab.RaCMAN)]
    [InlineData("2", SettingsTab.Console)]
    [InlineData("3", SettingsTab.Advanced)]
    // The first round's two tabs are both the Console tab now.
    [InlineData("qwark", SettingsTab.Console)]
    [InlineData("connection", SettingsTab.Console)]
    public void TheCommandLineNamesATabByNameOrNumber(string text, SettingsTab tab)
    {
        Assert.Equal(tab, SettingsNav.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData("ports")]
    public void AnythingElseNamesNoTab(string text)
    {
        Assert.Null(SettingsNav.Parse(text));
    }

    // ---------------------------------------------------------------- the rows

    /// <summary>
    /// The client's own settings are on their tabs with or without a console, in the second round's
    /// order: RaCMAN's General has the theme alone, Save files sits between Updates and Files, and
    /// Advanced has what was General's debug information and game layout, and the qwark.sprx path.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheClientsOwnSettingsAreOnTheirTabsWithOrWithoutAConsole(bool connected)
    {
        var context = Context(Capable, connected);

        var racman = SettingsLayout.Rows(SettingsTab.RaCMAN, context);
        Assert.Equal(
            new[]
            {
                SettingRow.Theme,
                SettingRow.Version, SettingRow.CheckForUpdates,
                SettingRow.MirrorSaves, SettingRow.SortSaveFiles,
                SettingRow.Folders,
                SettingRow.ImportFromRaCMAN,
            },
            racman);
        Assert.Equal(new[] { "General", "Updates", "Save files", "Files", "Import" },
            racman.Select(SettingsLayout.Group).Distinct());
        Assert.Equal(new[] { SettingRow.Rpcs3Port, SettingRow.LiveSplit }, SettingsLayout.Rows(SettingsTab.Ipc, context));
        Assert.Equal(new[] { SettingRow.DebugInformation, SettingRow.GameLayout, SettingRow.SprxPath },
            SettingsLayout.Rows(SettingsTab.Advanced, context));
    }

    /// <summary>
    /// Every row on exactly one tab, the tab it says it is on, and every row the page knows on some
    /// tab against a standalone console that has it all.
    /// </summary>
    [Fact]
    public void EveryRowIsOnExactlyOneTabAndEachIsOnTheTabItSaysItIsOn()
    {
        var memory = new InputRateMemory();
        var context = Context(Capable, true, memory, standalone: true);
        var seen = new HashSet<SettingRow>();

        foreach (var tab in SettingsNav.Tabs)
        {
            foreach (var row in SettingsLayout.Rows(tab, context))
            {
                Assert.True(seen.Add(row), $"{row} is listed twice");
                Assert.Equal(tab, SettingsLayout.TabOf(row));
            }
        }

        Assert.Empty(Enum.GetValues<SettingRow>().Where(row => !seen.Contains(row)));
        Assert.Equal(new[] { SettingsTab.RaCMAN, SettingsTab.Ipc, SettingsTab.Console, SettingsTab.Advanced }, SettingsNav.Tabs);
    }

    /// <summary>The Console tab is qwark's settings first, then the connection's, each a group of its own.</summary>
    [Fact]
    public void TheConsoleTabIsQwarksSettingsFirstThenTheConnection()
    {
        var rows = SettingsLayout.Rows(SettingsTab.Console, Context(Capable, true, standalone: true));
        Assert.Equal(
            new[]
            {
                SettingRow.InGameMenus, SettingRow.CheatWarnings, SettingRow.BootApply, SettingRow.PerUserSettings,
                SettingRow.InputDisplayRate,
                SettingRow.ConnectionMode, SettingRow.Installation, SettingRow.TableRefresh,
            },
            rows);

        var groups = rows.Select(SettingsLayout.Group).ToArray();
        int lastQwark = Array.LastIndexOf(groups, SettingsLayout.QwarkGroup);
        int firstConnection = Array.IndexOf(groups, SettingsLayout.ConnectionGroup);
        Assert.True(lastQwark >= 0 && firstConnection > lastQwark, string.Join(", ", groups));
        Assert.Equal(new[] { SettingsLayout.QwarkGroup, SettingsLayout.ConnectionGroup }, groups.Distinct());
    }

    [Fact]
    public void TheConsoleTabsQwarkRowsFollowTheSessionAndGoWithoutAConsole()
    {
        Assert.Equal(new[] { SettingRow.ConnectionMode, SettingRow.TableRefresh },
            SettingsLayout.Rows(SettingsTab.Console, Context(Capable, false)));

        // A game with no console menu has no In-game menus row; RPCS3 has no users.
        var noMenu = SettingsLayout.Rows(SettingsTab.Console, Context(Capable with { Flags2 = SessionFlags2.NoMenu }, true));
        Assert.DoesNotContain(SettingRow.InGameMenus, noMenu);
        Assert.Contains(SettingRow.PerUserSettings, noMenu);
        var rpcs3 = Capable with { Flags = SessionFlags.Emulator | SessionFlags.NoCodePatches };
        Assert.DoesNotContain(SettingRow.PerUserSettings, SettingsLayout.Rows(SettingsTab.Console, Context(rpcs3, true)));

        // A module before revision 1.33: Cheat warnings without its third value, no Apply toggles
        // on game boot; before 1.30 no Cheat warnings at all.
        var older = SettingsLayout.Rows(SettingsTab.Console, Context(OlderModule, true));
        Assert.Contains(SettingRow.CheatWarnings, older);
        Assert.DoesNotContain(SettingRow.BootApply, older);
        Assert.DoesNotContain(SettingRow.CheatWarnings,
            SettingsLayout.Rows(SettingsTab.Console, Context(Capable with { Flags3 = SessionFlags3.None }, true)));
    }

    /// <summary>The Installation row is the standalone mode's: under Connection mode then, and nowhere in webMAN mode.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheInstallationRowIsThereOnlyUnderStandalone(bool connected)
    {
        var standalone = SettingsLayout.Rows(SettingsTab.Console, Context(Capable, connected, standalone: true)).ToList();
        int mode = standalone.IndexOf(SettingRow.ConnectionMode);
        Assert.Equal(mode + 1, standalone.IndexOf(SettingRow.Installation));

        foreach (var tab in SettingsNav.Tabs)
        {
            Assert.DoesNotContain(SettingRow.Installation, SettingsLayout.Rows(tab, Context(Capable, connected, standalone: false)));
        }
    }

    // ---------------------------------------------------------------- the Console tab's heading

    [Fact]
    public void TheHeadingIsTheUsersWordsAndThePerUserNoteSaysWhichRowsAreWhose()
    {
        Assert.Equal("Editing console settings. These are saved persistently on your console and apply whenever qwark is loaded.",
            QwarkSettings.ConsoleHeading);

        var user1 = new ProfileInfo(ProfileFlags.Supported, ProfileKind.User, 0, 1, "user-00000001", string.Empty);
        Assert.Null(QwarkSettings.PerUserNote(Capable, user1));

        var on = Capable with { ProfileFlags = ProfileFlags.Supported | ProfileFlags.On | ProfileFlags.Active };
        string note = QwarkSettings.PerUserNote(on, user1)!;
        Assert.Contains("user 1", note, StringComparison.Ordinal);
        Assert.Contains("Cheat warnings", note, StringComparison.Ordinal);
        Assert.Contains("shared", note, StringComparison.Ordinal);

        var waiting = Capable with { ProfileFlags = ProfileFlags.Supported | ProfileFlags.On };
        Assert.Contains("once one signs in", QwarkSettings.PerUserNote(waiting, user1), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- IPC's resets

    [Fact]
    public void TheRpcs3ResetPutsBack28012AndSavesIt()
    {
        string path = Path.Combine(Path.GetTempPath(), $"racman-settings-{Guid.NewGuid():N}.json");
        try
        {
            var settings = Settings.Load(path);
            settings.Rpcs3.PinePort = 28100;
            settings.Save();
            using var state = new AppState(settings);
            Assert.False(SettingsPanel.Rpcs3PortIsDefault(settings));

            SettingsPanel.ResetRpcs3Port(state);
            Assert.Equal(28012, settings.Rpcs3.PinePort);
            Assert.True(SettingsPanel.Rpcs3PortIsDefault(settings));
            Assert.Equal(28012, Settings.Load(path).Rpcs3.PinePort);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheLiveSplitResetPutsBackTheDefaultEndpointAndSavesIt()
    {
        string path = Path.Combine(Path.GetTempPath(), $"racman-settings-{Guid.NewGuid():N}.json");
        try
        {
            var settings = Settings.Load(path);
            settings.Autosplit.Host = "192.168.1.20";
            settings.Autosplit.Port = 16900;
            settings.Save();
            using var state = new AppState(settings);
            Assert.False(SettingsPanel.LiveSplitIsDefault(settings.Autosplit));

            // Nothing is connected or switched on, so the reset saves and starts nothing.
            Assert.False(state.LiveSplit.Enabled);
            SettingsPanel.ResetLiveSplit(state);
            Assert.True(SettingsPanel.LiveSplitIsDefault(settings.Autosplit));
            Assert.False(state.LiveSplit.Enabled);

            var reloaded = Settings.Load(path);
            Assert.Equal("127.0.0.1", reloaded.Autosplit.Host);
            Assert.Equal(16834, reloaded.Autosplit.Port);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- the qwark.sprx path

    [Theory]
    [InlineData("", true, "detected beside RaCMAN", false)]
    [InlineData("qwark.sprx", true, "detected beside RaCMAN", false)]
    [InlineData("  ", false, "no qwark.sprx beside RaCMAN; enter the path to one", true)]
    [InlineData("D:\\qwark\\qwark.sprx", true, "using your file", false)]
    [InlineData("D:\\qwark\\qwark.sprx", false, "your file isn't there; empty the box to use the one beside RaCMAN", true)]
    public void TheSprxPathSaysWhatIsInUse(string configured, bool exists, string text, bool warning)
    {
        Assert.Equal((text, warning), SettingsPanel.SprxPathHint(configured, exists));
    }

    /// <summary>An emptied box is detection again: the settings' own bare name, which the box shows as empty.</summary>
    [Fact]
    public void EmptyingTheSprxPathIsDetectionAgain()
    {
        string path = Path.Combine(Path.GetTempPath(), $"racman-settings-{Guid.NewGuid():N}.json");
        try
        {
            using var state = new AppState(Settings.Load(path));
            SettingsPanel.SetSprxPath(state, "  D:\\qwark\\qwark.sprx ");
            Assert.Equal("D:\\qwark\\qwark.sprx", Settings.Load(path).Ps3.SprxPath);
            Assert.Equal("D:\\qwark\\qwark.sprx", Ps3Connect.ResolveSprx(state.Settings.Ps3.SprxPath));

            SettingsPanel.SetSprxPath(state, "   ");
            Assert.Equal(Ps3Settings.DefaultSprxPath, Settings.Load(path).Ps3.SprxPath);
            Assert.Equal(string.Empty, SettingsPanel.SprxPathShown(state.Settings.Ps3.SprxPath));
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "qwark.sprx"), Ps3Connect.ResolveSprx(state.Settings.Ps3.SprxPath));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- the boot plugin look

    [Theory]
    [InlineData("/dev_hdd0/plugins/qwark.sprx\n/dev_hdd0/plugins/webftp_server.sprx\n", BootPluginState.Listed)]
    [InlineData("/dev_hdd0/plugins/webftp_server.sprx\r\n/dev_hdd0/tmp/qwark.sprx\r\n", BootPluginState.Listed)]
    [InlineData("/dev_hdd0/plugins/webftp_server.sprx\n", BootPluginState.NotListed)]
    [InlineData("", BootPluginState.NotListed)]
    [InlineData("qwark.sprx\n", BootPluginState.NotListed)]
    public void AListWithoutQwarkIsNotInstalled(string text, BootPluginState want)
    {
        Assert.Equal(want, BootPluginCheck.Judge(text).State);
    }

    [Fact]
    public void OnlyAListThatWasReadWarnsAndOnlyInStandalone()
    {
        var check = new BootPluginCheck();
        Assert.Null(SettingsPanel.BootPluginLine(true, check));

        Assert.True(check.Begin("192.168.1.5"));
        Assert.False(check.Begin("192.168.1.5"));
        Assert.Null(SettingsPanel.BootPluginLine(true, check));

        check.Complete("/dev_hdd0/plugins/webftp_server.sprx\n");
        Assert.True(check.WarnNotInstalled);
        var line = SettingsPanel.BootPluginLine(true, check);
        Assert.True(line is { Warning: true } && line.Value.Text.StartsWith(SettingsPanel.NotInstalledWarning, StringComparison.Ordinal));
        Assert.Null(SettingsPanel.BootPluginLine(false, check));

        Assert.True(check.Begin("192.168.1.5"));
        check.Fail("no answer");
        Assert.True(SettingsPanel.BootPluginLine(true, check) is { Warning: false });

        Assert.True(check.Begin("192.168.1.5"));
        check.Complete("/dev_hdd0/plugins/qwark.sprx\n");
        Assert.Null(SettingsPanel.BootPluginLine(true, check));
        Assert.Equal("/dev_hdd0/plugins/qwark.sprx", check.ListedPath);
    }

    /// <summary>
    /// The look against a console with no connection goes over webMAN's FTP, reads boot_plugins.txt
    /// and writes nothing; a list without qwark is the warning, and the list an install writes
    /// (qwark's line first) reads as installed.
    /// </summary>
    [Fact]
    public async Task TheLookReadsTheListOverFtpAndWarnsWhenQwarkIsMissing()
    {
        using (var ftp = new FakeFtpServer())
        {
            ftp.Stored[WebManLoader.BootPluginsPath] = System.Text.Encoding.UTF8.GetBytes("/dev_hdd0/plugins/webftp_server.sprx\n");
            ftp.Start();

            using var state = StandaloneState(ftp);
            Assert.True(state.CheckBootPlugins());
            Assert.True(await PumpAsync(state, () => state.BootPlugins.State is not BootPluginState.Checking));
            Assert.Equal(BootPluginState.NotListed, state.BootPlugins.State);
            Assert.True(SettingsPanel.BootPluginLine(true, state.BootPlugins) is { Warning: true });
            Assert.Contains($"RETR {WebManLoader.BootPluginsPath}", ftp.Commands);
            Assert.DoesNotContain(ftp.Commands, c => c.StartsWith("STOR", StringComparison.Ordinal));
        }

        using (var ftp = new FakeFtpServer())
        {
            ftp.Stored[WebManLoader.BootPluginsPath] =
                System.Text.Encoding.UTF8.GetBytes($"{WebManLoader.BootPath}\n/dev_hdd0/plugins/webftp_server.sprx\n");
            ftp.Start();

            using var state = StandaloneState(ftp);
            Assert.True(state.CheckBootPlugins());
            Assert.True(await PumpAsync(state, () => state.BootPlugins.State is not BootPluginState.Checking));
            Assert.Equal(BootPluginState.Listed, state.BootPlugins.State);
            Assert.Equal(WebManLoader.BootPath, state.BootPlugins.ListedPath);
            Assert.Null(SettingsPanel.BootPluginLine(true, state.BootPlugins));
        }
    }

    [Fact]
    public async Task AConsoleWithNoListIsNotInstalledAndNoServerIsAFailureNotAWarning()
    {
        using var ftp = new FakeFtpServer();
        ftp.Start();
        using (var state = StandaloneState(ftp))
        {
            Assert.True(state.CheckBootPlugins());
            Assert.True(await PumpAsync(state, () => state.BootPlugins.State is not BootPluginState.Checking));
            Assert.Equal(BootPluginState.NotListed, state.BootPlugins.State);
        }

        // A port nothing listens on: webMAN not answering.
        using var nobody = new AppState(new Settings { Ps3 = { AutoReconnect = false, Host = "127.0.0.1" } })
        {
            WebMan = new WebManLoader { FtpPort = FreePort() },
        };
        nobody.Settings.Ps3.StandaloneConnection = true;
        Assert.True(nobody.CheckBootPlugins());
        Assert.True(await PumpAsync(nobody, () => nobody.BootPlugins.State is not BootPluginState.Checking, 12000));
        Assert.Equal(BootPluginState.Failed, nobody.BootPlugins.State);
        Assert.False(nobody.BootPlugins.WarnNotInstalled);
    }

    /// <summary>
    /// The look is asked for, not polled: only in standalone mode on the console target, once at a
    /// time, and on opening the Console tab rather than every frame it is drawn.
    /// </summary>
    [Fact]
    public async Task TheLookRunsWhenAskedAndNeverEveryFrame()
    {
        using var ftp = new FakeFtpServer();
        ftp.Start();
        using var state = StandaloneState(ftp);

        state.Settings.Ps3.StandaloneConnection = false;
        Assert.False(state.CheckBootPlugins());
        state.Settings.Ps3.StandaloneConnection = true;
        state.Settings.Rpcs3Target = true;
        Assert.False(state.CheckBootPlugins());
        state.Settings.Rpcs3Target = false;

        Assert.True(state.CheckBootPlugins());
        Assert.False(state.CheckBootPlugins());   // one under way already
        Assert.True(await PumpAsync(state, () => state.BootPlugins.State is not BootPluginState.Checking));
        Assert.Equal(1, state.BootPlugins.Started);

        // Opening the Console tab is a look; drawing it frame after frame is not, and coming back to
        // it after another tab is one again.
        Assert.True(SettingsPanel.ConsoleTabOpened(SettingsTab.Console, 1000));
        for (int frame = 1001; frame < 1100; frame++) Assert.False(SettingsPanel.ConsoleTabOpened(SettingsTab.Console, frame));
        Assert.False(SettingsPanel.ConsoleTabOpened(SettingsTab.Ipc, 1100));
        Assert.True(SettingsPanel.ConsoleTabOpened(SettingsTab.Console, 1101 + 5));
    }

    /// <summary>Connected to a standalone console, the look goes through qwark's own file ops, not webMAN.</summary>
    [Fact]
    public async Task ConnectedTheLookGoesThroughQwarkAndRunsOnConnecting()
    {
        using var server = StartServer();
        server.Files[WebManLoader.BootPluginsPath] = System.Text.Encoding.UTF8.GetBytes("/dev_hdd0/plugins/qwark.sprx\n");
        var settings = new Settings { Ps3 = { AutoReconnect = false } };
        settings.Ps3.StandaloneConnection = true;
        using var state = new AppState(settings) { WebMan = new WebManLoader { FtpPort = FreePort() } };
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.BootPlugins.State == BootPluginState.Listed));
        Assert.Equal(1, state.BootPlugins.Started);
        Assert.Contains(Opcode.FileRead, server.RequestLog());
    }

    // ---------------------------------------------------------------- Apply toggles on game boot (1.33)

    [Fact]
    public async Task BootApplyIsReadOnConnectingAndAPickSendsIt()
    {
        using var server = StartServer();
        server.BootApply = BootApplyMode.Never;
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.BootApply is not null));
        Assert.Equal(BootApplyMode.Never, state.BootApply);

        Assert.True(state.SetBootApply(BootApplyMode.Ask));
        Assert.True(await PumpAsync(state, () => state.BootApply == BootApplyMode.Ask));
        Assert.Equal(BootApplyMode.Ask, server.BootApply);
        Assert.Empty(state.Toasts.Where(t => t.Kind == ToastKind.Error));
    }

    [Fact]
    public async Task AnOlderModuleIsNeverAskedForBootApply()
    {
        using var server = StartServer();
        server.ReportsSettings133 = false;
        using var state = await ConnectedStateAsync(server);
        Assert.True(await PumpAsync(state, () => state.Session.QwarkVersion != 0));
        await PumpForAsync(state, 200);

        Assert.Null(state.BootApply);
        Assert.False(state.SetBootApply(BootApplyMode.Never));
        await PumpForAsync(state, 100);
        Assert.Equal(0, Count(server, Opcode.BootApply));
    }

    /// <summary>
    /// A boot under Ask with this client connected: the question goes up once, lists the on-boot
    /// toggles, and the answer is one BOOT_ASK; the console's bit going down takes the question away.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABootsQuestionIsAskedOnceAndAnsweredWithOneRequest(bool apply)
    {
        using var server = StartServer();
        using var state = await ConnectedStateAsync(server);
        Assert.True(await PumpAsync(state, () => state.Session.QwarkVersion != 0));
        Assert.False(state.BootAskRequested);

        var ghost = state.Session.Game == GameId.None ? 0 : 3;
        server.Session = server.Session with { ToggleAuto = 1UL << ghost };
        server.BootAskPending = true;
        Assert.True(await PumpAsync(state, () => state.BootAskRequested));
        Assert.Single(BootAskModal.Items(state).Toggles);

        state.AnswerBootAsk(apply);
        Assert.False(state.BootAskRequested);
        Assert.True(await PumpAsync(state, () => !state.Session.BootAskPending));
        Assert.Equal(new[] { apply }, server.BootAskAnswers);
        Assert.Equal(apply, (state.Session.ToggleState & (1UL << ghost)) != 0);

        // Nothing asks again for the same boot.
        await PumpForAsync(state, 200);
        Assert.False(state.BootAskRequested);
    }

    [Fact]
    public async Task AQuestionAnsweredElsewhereGoesAway()
    {
        using var server = StartServer();
        using var state = await ConnectedStateAsync(server);
        server.BootAskPending = true;
        Assert.True(await PumpAsync(state, () => state.BootAskRequested));

        server.BootAskPending = false;
        Assert.True(await PumpAsync(state, () => !state.BootAskRequested));
        Assert.Empty(server.BootAskAnswers);
    }

    // ---------------------------------------------------------------- the rate on the wire

    [Theory]
    [InlineData(30, 0)]
    [InlineData(60, 1)]
    [InlineData(120, 2)]
    public void Flags3CarriesTheRateInBitsThreeAndFour(int hz, int code)
    {
        var bytes = new byte[SessionInfo.Size];
        bytes[0] = 1;
        bytes[31] = (byte)(0x04 | (code << 3));

        var info = SessionInfo.Parse(bytes);
        Assert.True(info.HasTelemetryRate);
        Assert.Equal(hz, info.TelemetryHz);
        Assert.Equal(bytes[31], SessionInfo.Parse(info.ToBytes()).ToBytes()[31]);
    }

    /// <summary>
    /// A module without the switch always sent at 30 Hz, so that is what it reads as, whatever its
    /// other bits; revision 1.33's 60 Hz default is the console's to report in bits 3-4.
    /// </summary>
    [Fact]
    public void AModuleWithoutTheSwitchIsThirtyHertzWhateverTheBitsAbove()
    {
        var bytes = new byte[SessionInfo.Size];
        bytes[0] = 1;
        bytes[31] = 0x01 | (2 << 3);
        var info = SessionInfo.Parse(bytes);
        Assert.False(info.HasTelemetryRate);
        Assert.Equal(30, info.TelemetryHz);

        // The code no module sends.
        bytes[31] = 0x04 | (3 << 3);
        Assert.Equal(30, SessionInfo.Parse(bytes).TelemetryHz);
    }

    // ---------------------------------------------------------------- the row's gating

    [Fact]
    public void TheRateRowIsThereOnlyForAModuleWithTheSwitch()
    {
        var memory = new InputRateMemory();
        memory.Observe(OlderModule, connected: true);
        Assert.DoesNotContain(SettingRow.InputDisplayRate,
            SettingsLayout.Rows(SettingsTab.Console, Context(OlderModule, true, memory)));

        memory.Observe(Capable, connected: true);
        Assert.Contains(SettingRow.InputDisplayRate,
            SettingsLayout.Rows(SettingsTab.Console, Context(Capable, true, memory)));
        Assert.True(InputRateMemory.Enabled(connected: true));
    }

    [Fact]
    public void WithoutAConsoleTheRowStaysGreyedAtTheLastRateSeen()
    {
        var memory = new InputRateMemory();

        // Nothing seen in this run: nothing to show, and 60 Hz, the default since qwark revision
        // 1.33, is what it would show.
        Assert.False(memory.Shows(SessionInfo.Empty, connected: false));
        Assert.Equal(60, memory.Shown(SessionInfo.Empty, connected: false));

        var at120 = Capable with { Flags3 = SessionInfo.TelemetryRateBits(120) };
        memory.Observe(at120, connected: true);
        memory.Observe(SessionInfo.Empty, connected: false);

        Assert.True(memory.Shows(SessionInfo.Empty, connected: false));
        Assert.False(InputRateMemory.Enabled(connected: false));
        Assert.Equal(120, memory.Shown(SessionInfo.Empty, connected: false));

        // The next console is an older module: the row goes, with or without a connection after.
        memory.Observe(OlderModule, connected: true);
        Assert.False(memory.Shows(OlderModule, connected: true));
        Assert.False(memory.Shows(SessionInfo.Empty, connected: false));
    }

    // ---------------------------------------------------------------- the dropdown against the fake

    [Fact]
    public async Task ThePickSendsTelemetryRateAndTheSessionComesBackWithIt()
    {
        using var server = StartServer();
        using var state = await ConnectedStateAsync(server);
        Assert.True(await PumpAsync(state, () => state.Session.HasTelemetryRate));
        Assert.Equal(60, state.Session.TelemetryHz);

        Assert.True(SettingsPanel.SetInputRate(state, 30));
        Assert.True(await PumpAsync(state, () => state.Session.TelemetryHz == 30));
        Assert.Equal(30, server.TelemetryHz);
        Assert.Equal(1, Count(server, Opcode.TelemetryRate));

        Assert.True(SettingsPanel.SetInputRate(state, 120));
        Assert.True(await PumpAsync(state, () => state.Session.TelemetryHz == 120));
        Assert.Empty(state.Toasts.Where(t => t.Kind == ToastKind.Error));
    }

    [Fact]
    public async Task ARateTheConsoleDoesNotTakeIsRefusedAndShown()
    {
        using var server = StartServer();
        using var state = await ConnectedStateAsync(server);
        Assert.True(await PumpAsync(state, () => state.Session.HasTelemetryRate));

        var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => state.Client.TelemetryRateAsync(45));
        Assert.Equal(Status.BadArg, refused.Status);
        Assert.Equal(60, server.TelemetryHz);

        // The panel's own path toasts the console's answer rather than swallowing it.
        Assert.True(SettingsPanel.SetInputRate(state, 90));
        Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Kind == ToastKind.Error)));
        Assert.Equal(60, server.TelemetryHz);
    }

    [Fact]
    public async Task AnOlderModuleIsNeverSentTheRate()
    {
        using var server = StartServer();
        server.ReportsTelemetryRate = false;
        using var state = await ConnectedStateAsync(server);
        Assert.True(await PumpAsync(state, () => state.Session.QwarkVersion != 0));

        Assert.False(state.Session.HasTelemetryRate);
        Assert.False(SettingsPanel.SetInputRate(state, 60));
        await PumpForAsync(state, 200);
        Assert.Equal(0, Count(server, Opcode.TelemetryRate));

        var unknown = await Assert.ThrowsAsync<QwarkStatusException>(() => state.Client.TelemetryRateAsync(60));
        Assert.Equal(Status.UnknownOp, unknown.Status);
    }

    [Fact]
    public void WithoutAConnectionNothingIsSent()
    {
        using var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        Assert.False(SettingsPanel.SetInputRate(state, 60));
    }

    /// <summary>
    /// At 120 Hz the packets keep coming over UDP: the client takes them all (it keeps the latest),
    /// does not fall back to TCP, and its handler, the one the OBS overlay is fed from, sees more of
    /// them than it does at 30 Hz.
    /// </summary>
    [Fact]
    public async Task AtAHigherRateTheTelemetryKeepsArrivingOverUdp()
    {
        using var server = StartServer();
        using var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);

        int received = 0;
        client.TelemetryReceived += _ => Interlocked.Increment(ref received);

        async Task<int> PacketsInAboutASecond()
        {
            await Task.Delay(200);
            int start = Volatile.Read(ref received);
            await Task.Delay(1000);
            return Volatile.Read(ref received) - start;
        }

        await client.TelemetryRateAsync(30);
        int at30 = await PacketsInAboutASecond();
        await client.TelemetryRateAsync(120);
        int at120 = await PacketsInAboutASecond();

        Assert.True(at30 >= 15, $"{at30} packets a second at 30 Hz");
        Assert.True(at120 > at30 * 3 / 2, $"{at120} packets a second at 120 Hz, {at30} at 30 Hz");
        Assert.False(client.TelemetryViaTcp);
        Assert.True(client.LatestTelemetry is { Session.TelemetryHz: 120 });
        Assert.InRange(client.TelemetryAgeMs, 0, 500);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<AppState> ConnectedStateAsync(FakeQwarkServer server)
    {
        var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);
        return state;
    }

    private static async Task<bool> PumpAsync(AppState state, Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            state.Tick(1f / 60f);
            if (condition()) return true;
            await Task.Delay(16);
        }

        state.Tick(1f / 60f);
        return condition();
    }

    private static Task PumpForAsync(AppState state, int milliseconds) => PumpAsync(state, () => false, milliseconds);

    private static int Count(FakeQwarkServer server, Opcode opcode) => server.RequestLog().Count(op => op == opcode);

    private static FakeQwarkServer StartServer()
    {
        var server = new FakeQwarkServer();
        server.Start();
        return server;
    }

    /// <summary>A client set up as standalone, aimed at 127.0.0.1, whose webMAN is the fake FTP server.</summary>
    private static AppState StandaloneState(FakeFtpServer ftp)
    {
        var settings = new Settings { Ps3 = { AutoReconnect = false, Host = "127.0.0.1" } };
        settings.Ps3.StandaloneConnection = true;
        ConnectionPanel.UseHost("127.0.0.1");
        return new AppState(settings) { WebMan = new WebManLoader { FtpPort = ftp.Port } };
    }


    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>The tests that drive SettingsPanel's and ConnectionPanel's statics run one at a time.</summary>
[CollectionDefinition(Name)]
public sealed class SettingsPanelStatics
{
    public const string Name = "Settings panel statics";
}
