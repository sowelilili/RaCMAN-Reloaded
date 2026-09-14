using System.Diagnostics;

using RaCMAN.App;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// PROTOCOL.md section 1.1, from the client's side. While a game is starting or ending qwark has no
/// request buffers to spare, so everything but HELLO, HEARTBEAT, SUBSCRIBE, UNSUBSCRIBE and
/// GET_STATE is answered BUSY. The client's half of that contract is three things: it does not send
/// the requests, it does not report the refusals it does collect, and it reads back afterwards
/// whatever the launch cost it.
/// </summary>
public class BootQuietTests
{
    private static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }

        return condition();
    }

    private static async Task<AppState> ConnectedStateAsync(FakeQwarkServer server)
    {
        var state = new AppState(new Settings { AutoReconnect = false });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);
        return state;
    }

    /// <summary>Runs frames until the condition holds, the way the render loop would.</summary>
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

    /// <summary>Frames for a fixed stretch, for the tests whose point is that nothing happens.</summary>
    private static async Task PumpForAsync(AppState state, int milliseconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            state.Tick(1f / 60f);
            await Task.Delay(16);
        }
    }

    // ---------------------------------------------------------------- the decision itself

    [Fact]
    public void BackgroundWorkNeverReportsALaunchRefusal()
    {
        Assert.Null(StatusToast.For(Status.Busy, Opcode.WatchList, SessionState.Booting, quiet: true, debug: false));
        Assert.Null(StatusToast.For(Status.Busy, Opcode.WatchList, SessionState.Quitting, quiet: true, debug: false));

        // Debug information changes what a toast says, never whether there is one.
        Assert.Null(StatusToast.For(Status.Busy, Opcode.WatchList, SessionState.Booting, quiet: true, debug: true));

        // The three that were already quiet stay quiet.
        Assert.Null(StatusToast.For(Status.NotIngame, Opcode.PosList, SessionState.Xmb, quiet: true, debug: false));
        Assert.Null(StatusToast.For(Status.Unsupported, Opcode.UnlockList, SessionState.Ingame, quiet: true, debug: false));
        Assert.Null(StatusToast.For(Status.UnknownOp, Opcode.AutosplitEvents, SessionState.Ingame, quiet: true, debug: false));

        // Everything else a background read can collect is still a failure worth showing.
        Assert.Equal("The console refused that: IoError",
            StatusToast.For(Status.IoError, Opcode.FileRead, SessionState.Ingame, quiet: true, debug: false));
    }

    [Fact]
    public void AButtonRefusedDuringALaunchIsToldWhy()
    {
        Assert.Equal(StatusToast.WhileBusy(SessionState.Booting),
            StatusToast.For(Status.Busy, Opcode.ModList, SessionState.Booting, quiet: false, debug: false));

        Assert.Equal(StatusToast.WhileBusy(SessionState.Quitting),
            StatusToast.For(Status.Busy, Opcode.ModList, SessionState.Quitting, quiet: false, debug: false));

        // A sentence rather than a status code, and not the same sentence for both.
        Assert.Contains("starting a game", StatusToast.WhileBusy(SessionState.Booting));
        Assert.NotEqual(StatusToast.WhileBusy(SessionState.Booting), StatusToast.WhileBusy(SessionState.Quitting));

        // INGAME, BUSY keeps the meaning it has there: a save transfer in flight, or a full ring.
        Assert.Equal("The console refused that: Busy",
            StatusToast.For(Status.Busy, Opcode.SaveFileRead, SessionState.Ingame, quiet: false, debug: false));

        // And the five ops a launch does not stop are not part of any of this.
        Assert.True(FakeQwarkServer.IsControlOp(Opcode.GetState));
        Assert.False(FakeQwarkServer.IsControlOp(Opcode.WatchList));
    }

    // ---------------------------------------------------------------- the fake console's half

    [Fact]
    public async Task ALaunchingConsoleAnswersBusyToEverythingButTheControlOps()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        server.Booting = true;

        using var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);

        // HELLO and SUBSCRIBE got through, which is why the connection is up at all.
        Assert.True(client.IsConnected);
        Assert.Equal(SessionState.Booting, client.LatestSession!.State);

        await client.HeartbeatAsync();
        var packet = await client.GetStateAsync();
        Assert.Equal(SessionState.Booting, packet.Session.State);

        var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.WatchListAsync());
        Assert.Equal(Status.Busy, refused.Status);
        Assert.Equal(Status.Busy, (await Assert.ThrowsAsync<QwarkStatusException>(() => client.DescribeAsync())).Status);

        server.Booting = false;
        Assert.Equal(SessionState.Ingame, server.Session.State);
        await client.WatchListAsync();
    }

    // ---------------------------------------------------------------- (a) and (c), the toasts

    [Fact]
    public async Task ABackgroundReadRefusedByALaunchSaysNothing()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));

        server.Booting = true;
        Assert.True(await PumpAsync(state, () => state.ConsoleBusy));

        // The background reads the session change fires, plus one sent by hand so that a refusal is
        // certain to come back rather than merely to have been held.
        state.RefreshAll();
        state.RefreshLive();
        state.RefreshAutosplitEvents();
        state.RefreshSaveFileInfo();
        state.RunQuiet(() => state.Client.WatchListAsync());

        Assert.True(await PumpAsync(state, () => server.BusyCount > 0));
        await PumpForAsync(state, 300);

        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);
        Assert.DoesNotContain(state.Toasts, toast => toast.Text.Contains("Busy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AButtonPressedDuringALaunchGetsOneFriendlyToast()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));

        server.Booting = true;
        Assert.True(await PumpAsync(state, () => state.ConsoleBusy));

        // What a control does: one request, and the status is the user's to see.
        state.Run(() => state.Client.ModListAsync());

        Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Kind == ToastKind.Error)));
        await PumpForAsync(state, 300);

        var errors = state.Toasts.Where(t => t.Kind == ToastKind.Error).ToArray();
        Assert.Single(errors);
        Assert.Equal(StatusToast.WhileBusy(SessionState.Booting), errors[0].Text);

        // "Re-read everything" is eleven requests, so it says it once and sends none of them.
        int refused = server.BusyCount;
        state.ForceRefresh();
        await PumpForAsync(state, 200);
        Assert.Equal(refused, server.BusyCount);
        Assert.Contains(state.Toasts, t => t.Text == StatusToast.WhileBusy(SessionState.Booting));
    }

    // ---------------------------------------------------------------- (b), the silence itself

    [Fact]
    public async Task NoBulkRequestGoesOutWhileTheConsoleIsLaunching()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));
        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0));

        // The user's Deadlocked session: a quit, the XMB, and the same game on its way back.
        server.Quitting = true;
        Assert.True(await PumpAsync(state, () => state.ConsoleBusy));

        server.ClearRequestLog();
        int refused = server.BusyCount;

        // Everything a launch used to catch: the session change's own re-reads, the panels' timers
        // and the probes, all asked for while the console is refusing.
        await PumpForAsync(state, 400);
        server.Booting = true;
        state.RefreshUnlocks(quiet: true);
        state.RefreshWatches(quiet: true);
        state.RefreshFreezes(quiet: true);
        state.RefreshPatches(quiet: true);
        state.RefreshCombos(quiet: true);
        state.RefreshMods(quiet: true);
        state.RefreshPositions(quiet: true);
        state.RefreshAutosplitEvents();
        state.RefreshSaveFileInfo();
        state.RefreshAll();
        await PumpForAsync(state, 800);

        // The link is quiet, not dead: the five ops section 1.1 keeps answering still work.
        await state.Client.HeartbeatAsync();
        await state.Client.GetStateAsync();

        var sent = server.RequestLog();
        Assert.Contains(Opcode.Heartbeat, sent);
        Assert.All(sent, opcode => Assert.True(FakeQwarkServer.IsControlOp(opcode), $"{opcode} was sent during a launch"));
        Assert.Equal(refused, server.BusyCount);
        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);

        // And back in: what was held is read, without anybody pressing anything.
        server.ClearRequestLog();
        server.Booting = false;

        // The values, and the two probes RefreshLive does not carry but the launch stopped. They go
        // out together and answer in whatever order they please, so this waits for all of them.
        var owed = new[]
        {
            Opcode.PosList, Opcode.WatchList, Opcode.ModList, Opcode.AutosplitDescribe, Opcode.SaveFileInfo,
        };

        Assert.True(await PumpAsync(state, () => owed.All(server.RequestLog().Contains)),
            $"never asked for {string.Join(", ", owed.Except(server.RequestLog()))} after INGAME");

        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));

        // The description itself is this game's and is not read a second time.
        Assert.DoesNotContain(Opcode.Describe, server.RequestLog());
        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);
    }

    // ---------------------------------------------------------------- (d), the events survive

    [Fact]
    public async Task TheAutosplitCatchUpStillDeliversEventsFromAroundAReboot()
    {
        using var server = new FakeQwarkServer();
        server.Start();

        var received = new List<AutosplitEvent>();
        using var client = new QwarkClient { AutoReconnect = false };
        client.AutosplitEventReceived += ev =>
        {
            lock (received) received.Add(ev);
        };

        await client.ConnectAsync("127.0.0.1", server.Port);

        // One event before the quit, delivered the usual way, so the client has a baseline.
        server.EmitAutosplitEvent(AutosplitKind.Split, code: 1, arg: 2);
        Assert.True(await WaitFor(() =>
        {
            lock (received) return received.Count == 1;
        }));

        // Deadlocked quits to the XMB and comes back. The datagrams stop with the game, so the
        // quit's PAUSE and the reboot's RESUME only exist in the console's ring.
        server.Quitting = true;
        Assert.True(await WaitFor(() => client.LatestSession?.State == SessionState.Quitting));

        server.ClearRequestLog();
        int refused = server.BusyCount;
        var pause = server.EmitAutosplitEvent(AutosplitKind.Pause, code: 9, timeMs: 1000);
        server.Booting = true;
        var resume = server.EmitAutosplitEvent(AutosplitKind.Resume, code: 9, timeMs: 15_800);

        // A second and a half is longer than the safety poll's own interval: nothing was asked for
        // because the client knows better than to ask, not because it had no chance to.
        await Task.Delay(1500);
        var duringTheLaunch = server.RequestLog();
        Assert.DoesNotContain(Opcode.AutosplitEvents, duringTheLaunch);
        Assert.All(duringTheLaunch, opcode => Assert.True(FakeQwarkServer.IsControlOp(opcode), $"{opcode} was sent during a launch"));
        Assert.Equal(refused, server.BusyCount);

        lock (received) Assert.Single(received);

        server.Booting = false;

        Assert.True(await WaitFor(() =>
        {
            lock (received) return received.Count == 3;
        }));

        lock (received)
        {
            Assert.Equal(new[] { 1u, pause.Seq, resume.Seq }, received.Select(e => e.Seq).ToArray());
            Assert.Equal(AutosplitKind.Pause, received[1].Kind);
            Assert.Equal(AutosplitKind.Resume, received[2].Kind);
            Assert.Equal(1000u, received[1].TimeMs);
            Assert.Equal(15_800u, received[2].TimeMs);
        }
    }

    /// <summary>
    /// The same, one layer up: the autosplitter is what acts on those two, and a quit it never
    /// heard about would leave LiveSplit running through the reboot.
    /// </summary>
    [Fact]
    public async Task TheAutosplitterActsOnTheEventsTheCatchUpDelivers()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        // After the state is built, so nothing goes looking for a LiveSplit to talk to: the switch
        // is read every frame, and all this test needs from it is the safety poll the catch-up
        // rides on. Every event is logged as "ignored: LiveSplit is not connected", which is the
        // truth here and still proves the event reached the engine.
        state.Settings.Autosplit.Enabled = true;

        Assert.True(await PumpAsync(state, () => state.AutosplitEvents.Length > 0));
        int before = state.Autosplitter.Received;

        server.Quitting = true;
        Assert.True(await PumpAsync(state, () => state.ConsoleBusy));

        server.EmitAutosplitEvent(AutosplitKind.Pause, code: 9, timeMs: 1000);
        server.Booting = true;
        server.EmitAutosplitEvent(AutosplitKind.Resume, code: 9, timeMs: 15_800);

        await PumpForAsync(state, 600);
        Assert.Equal(before, state.Autosplitter.Received);

        server.Booting = false;
        Assert.True(await PumpAsync(state, () => state.Autosplitter.Received == before + 2));

        var log = state.Autosplitter.Log().TakeLast(2).ToArray();
        Assert.Equal(AutosplitKind.Pause, log[0].Kind);
        Assert.Equal(AutosplitKind.Resume, log[1].Kind);
        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);
    }
}
