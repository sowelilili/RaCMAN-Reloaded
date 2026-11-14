using System.Diagnostics;
using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The Settings page's tabs: the side nav it puts in place of the panels (Back and Esc), which rows
/// each tab has, and the Connection tab's Input display rate, the console's telemetry rate of qwark
/// revision 1.32 (TELEMETRY_RATE, SessionInfo flags3 bits 2 to 4), against the fake console.
/// </summary>
public class SettingsPageTests
{
    private static readonly SessionInfo Capable = SessionInfo.Empty with
    {
        QwarkVersion = QwarkClient.ExpectedQwarkBuild,
        State = SessionState.Ingame,
        Game = GameId.Rac1,
        ProfileFlags = ProfileFlags.Supported,
        Flags3 = SessionFlags3.RunWarnings | SessionInfo.TelemetryRateBits(30),
    };

    /// <summary>A module from before revision 1.32: no telemetry rate bits, so no switch.</summary>
    private static readonly SessionInfo OlderModule = Capable with { Flags3 = SessionFlags3.RunWarnings };

    private static SettingsContext Context(SessionInfo session, bool connected, InputRateMemory? memory = null) =>
        new(session, connected, memory ?? new InputRateMemory());

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

        nav.Select(SettingsTab.Qwark);
        nav.Back();
        nav.Enter(PanelNav.Game);

        Assert.Equal(SettingsTab.Qwark, nav.Tab);
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
    [InlineData("qwark", SettingsTab.Qwark)]
    [InlineData("IPC", SettingsTab.Ipc)]
    [InlineData("connection", SettingsTab.Connection)]
    [InlineData("0", SettingsTab.RaCMAN)]
    [InlineData("3", SettingsTab.Qwark)]
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
    /// Every setting the page had before its tabs is still on it, on the tab the design gives it,
    /// with or without a console: none of them is the console's.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheClientsOwnSettingsAreOnTheirTabsWithOrWithoutAConsole(bool connected)
    {
        var context = Context(Capable, connected);

        Assert.Equal(
            new[]
            {
                SettingRow.Theme, SettingRow.DebugInformation, SettingRow.GameLayout,
                SettingRow.Version, SettingRow.CheckForUpdates,
                SettingRow.Folders, SettingRow.MirrorSaves, SettingRow.SortSaveFiles,
                SettingRow.ImportFromRaCMAN,
            },
            SettingsLayout.Rows(SettingsTab.RaCMAN, context));
        Assert.Equal(new[] { SettingRow.Rpcs3Port, SettingRow.LiveSplit }, SettingsLayout.Rows(SettingsTab.Ipc, context));

        var connection = SettingsLayout.Rows(SettingsTab.Connection, context);
        Assert.Equal(new[] { SettingRow.ConnectionMode, SettingRow.TableRefresh }, connection.Take(2));
    }

    [Fact]
    public void NoRowIsOnTwoTabsAndEachIsOnTheTabItSaysItIsOn()
    {
        var memory = new InputRateMemory();
        var context = Context(Capable, true, memory);
        var seen = new HashSet<SettingRow>();

        foreach (var tab in SettingsNav.Tabs)
        {
            foreach (var row in SettingsLayout.Rows(tab, context))
            {
                Assert.True(seen.Add(row), $"{row} is listed twice");
                Assert.Equal(tab, SettingsLayout.TabOf(row));
            }
        }

        // Everything but the hidden Run warnings switch, against a console that has it all.
        var missing = Enum.GetValues<SettingRow>().Where(row => !seen.Contains(row)).ToArray();
        Assert.Equal(new[] { SettingRow.RunWarnings }, missing);
    }

    [Fact]
    public void TheQwarkTabHasTheConsolesSettingsAndNothingWithoutAConsole()
    {
        Assert.Equal(new[] { SettingRow.InGameMenus, SettingRow.PerUserSettings },
            SettingsLayout.Rows(SettingsTab.Qwark, Context(Capable, true)));
        Assert.Empty(SettingsLayout.Rows(SettingsTab.Qwark, Context(Capable, false)));

        // A game with no console menu has no In-game menus row; RPCS3 has no users.
        Assert.Equal(new[] { SettingRow.PerUserSettings },
            SettingsLayout.Rows(SettingsTab.Qwark, Context(Capable with { Flags2 = SessionFlags2.NoMenu }, true)));
        var rpcs3 = Capable with { Flags = SessionFlags.Emulator | SessionFlags.NoCodePatches };
        Assert.Equal(new[] { SettingRow.InGameMenus }, SettingsLayout.Rows(SettingsTab.Qwark, Context(rpcs3, true)));

        // Before build 55 there was no MENU_HELP to send.
        Assert.Equal(new[] { SettingRow.PerUserSettings },
            SettingsLayout.Rows(SettingsTab.Qwark, Context(Capable with { QwarkVersion = 54 }, true)));
    }

    /// <summary>The Run warnings row stays hidden (the user's call after build 57) though the module has the switch.</summary>
    [Fact]
    public void TheRunWarningsRowStaysHidden()
    {
        Assert.True(Capable.HasRunWarnings);
        Assert.DoesNotContain(SettingRow.RunWarnings, SettingsLayout.Rows(SettingsTab.Qwark, Context(Capable, true)));
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
            SettingsLayout.Rows(SettingsTab.Connection, Context(OlderModule, true, memory)));

        memory.Observe(Capable, connected: true);
        Assert.Contains(SettingRow.InputDisplayRate,
            SettingsLayout.Rows(SettingsTab.Connection, Context(Capable, true, memory)));
        Assert.True(InputRateMemory.Enabled(connected: true));
    }

    [Fact]
    public void WithoutAConsoleTheRowStaysGreyedAtTheLastRateSeen()
    {
        var memory = new InputRateMemory();

        // Nothing seen in this run: nothing to show.
        Assert.False(memory.Shows(SessionInfo.Empty, connected: false));

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
        Assert.Equal(30, state.Session.TelemetryHz);

        Assert.True(SettingsPanel.SetInputRate(state, 60));
        Assert.True(await PumpAsync(state, () => state.Session.TelemetryHz == 60));
        Assert.Equal(60, server.TelemetryHz);
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
        Assert.Equal(30, server.TelemetryHz);

        // The panel's own path toasts the console's answer rather than swallowing it.
        Assert.True(SettingsPanel.SetInputRate(state, 90));
        Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Kind == ToastKind.Error)));
        Assert.Equal(30, server.TelemetryHz);
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
}
