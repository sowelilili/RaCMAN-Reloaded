using System.Diagnostics;
using System.Globalization;

using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// VSH_MEMORY and flags.VSH_MEMORY (protocol revision 1.22, qwark build 54) on the client's side:
/// the 28-byte reply, the request against the fake console, the timed read <see cref="AppState"/>
/// makes only while the bit is set, the line the Connection panel draws from it, and the status
/// line's "low memory".
/// </summary>
public class VshMemoryTests
{
    /// <summary>1,234 KB free of 5,678 KB, lowest 1,100 KB at a game start in Deadlocked, 1,800 KB at module start.</summary>
    private static readonly VshMemoryInfo Figures = new(
        Free: 1234 * 1024,
        Total: 5678 * 1024,
        Lowest: 1100 * 1024,
        Start: 1800 * 1024,
        AgeSeconds: 4,
        LowestAgeSeconds: 12 * 60,
        LowestAt: VshMemoryReading.Game,
        LowestGame: GameId.Rac4);

    private static int Asked(FakeQwarkServer server) => server.RequestLog().Count(op => op == Opcode.VshMemory);

    private static async Task<AppState> ConnectedStateAsync(FakeQwarkServer server)
    {
        var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
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

    // ================================================================ the wire

    [Fact]
    public void VshMemoryIsOpcode0x0007AndBit5()
    {
        Assert.Equal(0x0007, (ushort)Opcode.VshMemory);
        Assert.Equal(0x20, (byte)SessionFlags.VshMemory);

        var flagged = SessionInfo.Parse((SessionInfo.Empty with { Flags = SessionFlags.VshMemory | SessionFlags.CombosOff }).ToBytes());
        Assert.True(flagged.VshMemory);
        Assert.True(flagged.CombosOff);
        Assert.False(flagged.CodeSwitches);

        Assert.False(SessionInfo.Parse((SessionInfo.Empty with { Flags = SessionFlags.CodeSwitches }).ToBytes()).VshMemory);
    }

    [Fact]
    public void TheReplyParsesFromItsExactBytes()
    {
        byte[] wire =
        {
            0x00, 0x13, 0x48, 0x00,   // free          1,263,616 = 1,234 KB
            0x00, 0x58, 0xB8, 0x00,   // total         5,814,272 = 5,678 KB
            0x00, 0x11, 0x30, 0x00,   // lowest        1,126,400 = 1,100 KB
            0x00, 0x1C, 0x20, 0x00,   // start         1,843,200 = 1,800 KB
            0x00, 0x00, 0x00, 0x04,   // age_s         4
            0x00, 0x00, 0x02, 0xD0,   // lowest_age_s  720
            0x03,                     // lowest_at     GAME
            0x04,                     // lowest_game   RAC4
            0x00, 0x00,               // pad
        };

        Assert.Equal(VshMemoryInfo.WireSize, wire.Length);

        var info = VshMemoryInfo.Parse(wire);
        Assert.Equal(Figures, info);
        Assert.Equal(1234u, info.FreeKb);
        Assert.Equal(5678u, info.TotalKb);
        Assert.Equal(1100u, info.LowestKb);
        Assert.Equal(1800u, info.StartKb);
        Assert.True(info.HasLowest);
        Assert.True(info.HasStart);

        // And the fake console sends exactly these bytes for the same figures.
        Assert.Equal(wire, Figures.ToBytes());
    }

    [Fact]
    public void ALongerReplyParsesFromItsFirst28Bytes()
    {
        var longer = Figures.ToBytes().Concat(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 }).ToArray();
        Assert.Equal(Figures, VshMemoryInfo.Parse(longer));
    }

    [Fact]
    public void AShorterReplyIsAProtocolError()
    {
        var whole = Figures.ToBytes();

        var cut = Assert.Throws<ProtocolException>(() => VshMemoryInfo.Parse(whole[..^1]));
        Assert.Contains("VSH_MEMORY", cut.Message);
        Assert.Contains("27", cut.Message);

        Assert.Throws<ProtocolException>(() => VshMemoryInfo.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void KbIsRoundedDown()
    {
        Assert.Equal(1u, (Figures with { Free = 2047 }).FreeKb);
        Assert.Equal(0u, (Figures with { Free = 1023 }).FreeKb);
        Assert.False((Figures with { Start = 0 }).HasStart);
    }

    // ================================================================ the request against the fake

    [Fact]
    public async Task TheClientReadsTheFiguresTheFakeHoldsAndEverySessionBlockCarriesTheBit()
    {
        using var server = new FakeQwarkServer { VshMemory = Figures };
        server.Start();
        using var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);

        // HELLO, GET_STATE and telemetry all say the console can read it.
        Assert.True(client.LatestSession!.VshMemory);
        Assert.True((await client.GetStateAsync()).Session.VshMemory);
        var sw = Stopwatch.StartNew();
        while (client.LatestTelemetry is null && sw.ElapsedMilliseconds < 5000) await Task.Delay(20);
        Assert.True(client.LatestTelemetry!.Session.VshMemory);

        Assert.Equal(Figures, await client.VshMemoryAsync());
        Assert.Empty(server.LastPayload(Opcode.VshMemory)!);
    }

    [Fact]
    public async Task WithoutAFigureTheBitIsClearAndTheOpIsUnsupported()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);

        Assert.False(client.LatestSession!.VshMemory);
        Assert.False((await client.GetStateAsync()).Session.VshMemory);

        var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.VshMemoryAsync());
        Assert.Equal(Status.Unsupported, refused.Status);
        Assert.Equal(Opcode.VshMemory, refused.Opcode);

        // A module from before the op does not know it at all.
        server.RefusedOps[Opcode.VshMemory] = Status.UnknownOp;
        Assert.Equal(Status.UnknownOp, (await Assert.ThrowsAsync<QwarkStatusException>(() => client.VshMemoryAsync())).Status);

        // And the figure arriving sets the bit, as qwark-host's vshmem does.
        server.RefusedOps.Remove(Opcode.VshMemory);
        server.VshMemory = Figures;
        Assert.True((await client.GetStateAsync()).Session.VshMemory);
        Assert.Equal(Figures, await client.VshMemoryAsync());

        server.VshMemory = null;
        Assert.False((await client.GetStateAsync()).Session.VshMemory);
    }

    [Fact]
    public async Task TheOpIsAControlOpAndIsAnsweredThroughALaunch()
    {
        Assert.True(FakeQwarkServer.IsControlOp(Opcode.VshMemory));

        using var server = new FakeQwarkServer { VshMemory = Figures };
        server.Start();
        using var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);

        server.Booting = true;
        Assert.Equal(Figures, await client.VshMemoryAsync());
        server.Quitting = true;
        Assert.Equal(Figures, await client.VshMemoryAsync());
        Assert.Equal(0, server.BusyCount);
    }

    // ================================================================ the timed read

    [Fact]
    public async Task NothingIsAskedWhileTheBitIsClear()
    {
        using var server = new FakeQwarkServer();

        // RPCS3, or a module from before the op, which would not even know it.
        server.Emulator = true;
        server.RefusedOps[Opcode.VshMemory] = Status.UnknownOp;
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0));

        // Several intervals' worth of frames.
        for (int i = 0; i < 3; i++)
        {
            state.Tick(AppState.VshMemoryPollSeconds);
            await PumpForAsync(state, 100);
        }

        Assert.Equal(0, Asked(server));
        Assert.Null(state.VshMemory);
        Assert.Null(state.VshMemoryStatus);
        Assert.Null(ConnectionPanel.VshMemoryLine(state.Session.VshMemory, state.VshMemory, state.VshMemoryStatus));
        Assert.False(state.VshMemoryLow);
        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);
    }

    [Fact]
    public async Task ItAsksAsSoonAsTheBitIsSeenAndAgainAfterTheInterval()
    {
        using var server = new FakeQwarkServer { VshMemory = Figures };
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.VshMemory is not null));
        Assert.Equal(Figures, state.VshMemory);
        Assert.Equal(1, Asked(server));

        // Not again inside the interval: a third of a second of frames is nowhere near it.
        await PumpForAsync(state, 300);
        Assert.Equal(1, Asked(server));

        // The interval passes, and the console has a new figure.
        var lower = Figures with { Free = 900 * 1024, Lowest = 900 * 1024, LowestAt = VshMemoryReading.Periodic };
        server.VshMemory = lower;
        state.Tick(AppState.VshMemoryPollSeconds);
        Assert.True(await PumpAsync(state, () => state.VshMemory == lower));
        Assert.Equal(2, Asked(server));
        Assert.Null(state.VshMemoryStatus);

        await PumpForAsync(state, 200);
        Assert.Equal(2, Asked(server));
    }

    [Fact]
    public async Task ABitThatArrivesMidConnectionIsAskedAtOnce()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0));
        await PumpForAsync(state, 200);
        Assert.Equal(0, Asked(server));

        // qwark-host after its vshmem command: the next telemetry packet carries the bit.
        server.VshMemory = Figures;
        Assert.True(await PumpAsync(state, () => state.VshMemory is not null));
        Assert.Equal(1, Asked(server));
    }

    [Fact]
    public async Task TheFiguresGoWithTheConnectionAndANewOneAsksAgain()
    {
        using var server = new FakeQwarkServer { VshMemory = Figures };
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.VshMemory is not null));

        // The Disconnect button: no Disconnected event, and the figures still go.
        await state.Client.DisconnectAsync();
        state.Tick(1f / 60f);
        Assert.Null(state.VshMemory);
        Assert.Null(state.VshMemoryStatus);

        server.ClearRequestLog();
        await state.Client.ConnectAsync("127.0.0.1", server.Port);
        Assert.True(await PumpAsync(state, () => state.VshMemory is not null));
        Assert.Equal(1, Asked(server));

        // A link that drops: the Disconnected event.
        server.DropClients();
        Assert.True(await PumpAsync(state, () => !state.Connected));
        Assert.Null(state.VshMemory);
        Assert.Null(state.VshMemoryStatus);
    }

    [Fact]
    public async Task ARefusalAfterFiguresIsShownAndToastedOnce()
    {
        using var server = new FakeQwarkServer { VshMemory = Figures };
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.VshMemory is not null));

        // A status a background read is not entitled to: it is news, so it is said once.
        server.RefusedOps[Opcode.VshMemory] = Status.IoError;
        state.Tick(AppState.VshMemoryPollSeconds);
        Assert.True(await PumpAsync(state, () => state.VshMemoryStatus == Status.IoError));
        Assert.True(await PumpAsync(state, () => state.Toasts.Any(t => t.Kind == ToastKind.Error)));
        Assert.Single(state.Toasts, t => t.Kind == ToastKind.Error && t.Text.Contains("IoError", StringComparison.Ordinal));

        // The last figures are kept, and the panel shows the status in their place.
        Assert.Equal(Figures, state.VshMemory);
        Assert.Equal(("VSH memory: IoError", true),
            ConnectionPanel.VshMemoryLine(state.Session.VshMemory, state.VshMemory, state.VshMemoryStatus));

        // The same refusal ten seconds later is the panel's line, not another toast. The interval
        // itself has run the first toast out, so any error toast now would be a second one.
        state.Tick(AppState.VshMemoryPollSeconds);
        Assert.True(await PumpAsync(state, () => Asked(server) >= 3));
        await PumpForAsync(state, 200);
        Assert.DoesNotContain(state.Toasts, t => t.Kind == ToastKind.Error);
        Assert.Equal(Status.IoError, state.VshMemoryStatus);

        // And the next read that goes through puts the figures back.
        server.RefusedOps.Remove(Opcode.VshMemory);
        state.Tick(AppState.VshMemoryPollSeconds);
        Assert.True(await PumpAsync(state, () => state.VshMemoryStatus is null));
        Assert.Equal(Figures, state.VshMemory);
    }

    [Fact]
    public async Task AQuietRefusalBeforeAnyFigureSaysNothing()
    {
        using var server = new FakeQwarkServer { VshMemory = Figures };
        server.RefusedOps[Opcode.VshMemory] = Status.Unsupported;
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.VshMemoryStatus == Status.Unsupported));
        await PumpForAsync(state, 200);

        Assert.Null(state.VshMemory);
        Assert.False(state.VshMemoryLow);
        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);
        Assert.Null(ConnectionPanel.VshMemoryLine(state.Session.VshMemory, state.VshMemory, state.VshMemoryStatus));
    }

    [Fact]
    public async Task TheReadGoesOnThroughALaunch()
    {
        using var server = new FakeQwarkServer { VshMemory = Figures };
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.VshMemory is not null && state.Describe.Features.Length > 0));

        server.Booting = true;
        Assert.True(await PumpAsync(state, () => state.ConsoleBusy));

        // Not held the way the bulk reads are, and answered: the figures move while the game boots.
        var booting = Figures with { Free = 1000 * 1024, LowestAt = VshMemoryReading.TickThread };
        server.VshMemory = booting;
        state.Tick(AppState.VshMemoryPollSeconds);
        Assert.True(await PumpAsync(state, () => state.VshMemory == booting));
        Assert.Null(state.VshMemoryStatus);
        Assert.True(state.ConsoleBusy);
    }

    // ================================================================ the status line's "low memory"

    /// <summary>A ten-second reading of <paramref name="freeKb"/> free, with the lowest since module start.</summary>
    private static VshMemoryInfo Reading(uint freeKb, uint lowestKb) =>
        Figures with { Free = freeKb * 1024, Lowest = lowestKb * 1024, LowestAt = VshMemoryReading.Periodic };

    /// <summary>The console's figure moves, the interval passes, and the client has read the new one.</summary>
    private static async Task ReadAsync(AppState state, FakeQwarkServer server, VshMemoryInfo figures)
    {
        int asked = Asked(server);
        server.VshMemory = figures;
        state.Tick(AppState.VshMemoryPollSeconds);
        Assert.True(await PumpAsync(state, () => Asked(server) > asked && state.VshMemory == figures));
    }

    [Fact]
    public void TheMarkIs512KbAndTheWarningGoesOnlyAPageAboveIt()
    {
        const uint mark = AppState.VshMemoryLowBytes;
        const uint clear = mark + AppState.VshMemoryLowMarginBytes;
        Assert.Equal(512u * 1024, mark);
        Assert.Equal(64u * 1024, AppState.VshMemoryLowMarginBytes);

        // On strictly under the mark.
        Assert.True(AppState.VshMemoryLowAfter(false, mark - 1));
        Assert.True(AppState.VshMemoryLowAfter(false, 0));
        Assert.False(AppState.VshMemoryLowAfter(false, mark));
        Assert.False(AppState.VshMemoryLowAfter(false, clear));

        // Once on, it stays on up to the mark plus a page, and goes only above that.
        Assert.True(AppState.VshMemoryLowAfter(true, mark));
        Assert.True(AppState.VshMemoryLowAfter(true, clear));
        Assert.False(AppState.VshMemoryLowAfter(true, clear + 1));
    }

    [Fact]
    public async Task LowMemoryShowsWhileTheLatestReadingIsUnderTheMark()
    {
        // Deadlocked a few seconds after its arrival, as a console reported it: ordinary play.
        using var server = new FakeQwarkServer { VshMemory = Reading(1044, 1044) };
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.VshMemory is not null));
        Assert.False(state.VshMemoryLow);

        // Exactly the mark is not under it.
        await ReadAsync(state, server, Reading(512, 512));
        Assert.False(state.VshMemoryLow);

        await ReadAsync(state, server, Reading(400, 400));
        Assert.True(state.VshMemoryLow);
        Assert.True(ConnectionPanel.VshMemoryLine(state.Session.VshMemory, state.VshMemory, state.VshMemoryStatus)!.Value.Warning);

        // Back at the XMB's figure: gone from the status line. The panel's line keeps the warning
        // colour, because the lowest it shows is still under the mark.
        await ReadAsync(state, server, Reading(2696, 400));
        Assert.False(state.VshMemoryLow);
        Assert.Equal(("VSH memory: 2,696 KB free of 5,678 KB (lowest 400 KB)", true),
            ConnectionPanel.VshMemoryLine(state.Session.VshMemory, state.VshMemory, state.VshMemoryStatus));
    }

    [Fact]
    public async Task LowMemoryGoesOnlyOnceTheReadingIsAPageAboveTheMark()
    {
        using var server = new FakeQwarkServer { VshMemory = Reading(400, 400) };
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.VshMemory is not null));
        Assert.True(state.VshMemoryLow);

        // Over the mark, but not by a page: a figure wobbling about the mark does not flicker it.
        await ReadAsync(state, server, Reading(540, 400));
        Assert.True(state.VshMemoryLow);
        await ReadAsync(state, server, Reading(576, 400));
        Assert.True(state.VshMemoryLow);

        await ReadAsync(state, server, Reading(577, 400));
        Assert.False(state.VshMemoryLow);

        // The same band on the way down does not bring it back; under the mark does.
        await ReadAsync(state, server, Reading(530, 400));
        Assert.False(state.VshMemoryLow);
        await ReadAsync(state, server, Reading(511, 400));
        Assert.True(state.VshMemoryLow);
    }

    [Fact]
    public async Task LowMemoryShowsOnlyWhileTheReadingIsLive()
    {
        using var server = new FakeQwarkServer { VshMemory = Reading(400, 400) };
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.VshMemoryLow));

        // A refused read: the panel keeps the figures and shows the status, but the latest read did
        // not go through, so there is nothing for the status line to warn on.
        server.RefusedOps[Opcode.VshMemory] = Status.IoError;
        state.Tick(AppState.VshMemoryPollSeconds);
        Assert.True(await PumpAsync(state, () => state.VshMemoryStatus == Status.IoError));
        Assert.NotNull(state.VshMemory);
        Assert.False(state.VshMemoryLow);

        // The next one that goes through brings it back.
        server.RefusedOps.Remove(Opcode.VshMemory);
        await ReadAsync(state, server, Reading(420, 400));
        Assert.True(state.VshMemoryLow);

        // The console can no longer read the figure: bit5 goes, and VSH_MEMORY is UNSUPPORTED.
        server.VshMemory = null;
        Assert.True(await PumpAsync(state, () => !state.Session.VshMemory));
        Assert.False(state.VshMemoryLow);

        await ReadAsync(state, server, Reading(410, 400));
        Assert.True(state.VshMemoryLow);

        // Disconnected, it goes with the figures.
        await state.Client.DisconnectAsync();
        state.Tick(1f / 60f);
        Assert.False(state.VshMemoryLow);

        // And a new connection starts from nothing: a reading inside the band above the mark,
        // which would have kept the last connection's warning up, does not put one up.
        server.VshMemory = Reading(540, 400);
        await state.Client.ConnectAsync("127.0.0.1", server.Port);
        Assert.True(await PumpAsync(state, () => state.VshMemory is not null));
        Assert.False(state.VshMemoryLow);
    }

    [Fact]
    public void TheLowMemoryHoverGivesTheFigureAndWhatToDo()
    {
        Assert.Equal("The console's system software (the VSH) is low on memory: 400 KB free of 20,480 KB. "
                     + "Close other plugins or reboot the console if the game becomes unstable.",
            ConnectionPanel.VshMemoryLowTooltip(Figures with { Free = 400 * 1024, Total = 20480 * 1024 }));
    }

    [Fact]
    public void TheFakeScriptsVshmemStepMovesTheFigure()
    {
        using var server = new FakeQwarkServer();

        FakeScript.Apply(server, "vshmem:1044");
        Assert.Equal(1044u, server.VshMemory!.FreeKb);
        Assert.Equal(20480u, server.VshMemory.TotalKb);
        Assert.Equal(1044u, server.VshMemory.LowestKb);
        Assert.True(server.Session.VshMemory);

        // Down, then back up: the lowest stays at the bottom, the total as it was last given.
        FakeScript.Apply(server, "vshmem:400:31744");
        FakeScript.Apply(server, "vshmem:2696");
        Assert.Equal(2696u, server.VshMemory.FreeKb);
        Assert.Equal(31744u, server.VshMemory.TotalKb);
        Assert.Equal(400u, server.VshMemory.LowestKb);
        Assert.Equal(1044u, server.VshMemory.StartKb);

        FakeScript.Apply(server, "vshmem:off");
        Assert.Null(server.VshMemory);
        Assert.False(server.Session.VshMemory);
    }

    // ================================================================ what the panel says

    [Fact]
    public void TheLineIsFreeOfTotalWithTheLowest()
    {
        Assert.Equal(("VSH memory: 1,234 KB free of 5,678 KB (lowest 1,100 KB)", false),
            ConnectionPanel.VshMemoryLine(true, Figures, null));

        // No lowest to report, no lowest in the line.
        Assert.Equal(("VSH memory: 1,234 KB free of 5,678 KB", false),
            ConnectionPanel.VshMemoryLine(true, Figures with { Lowest = 0, LowestAt = VshMemoryReading.None }, null));

        // Thousands separators on every machine, whatever its own culture writes.
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("VSH memory: 1,234 KB free of 5,678 KB (lowest 1,100 KB)",
                ConnectionPanel.VshMemoryLine(true, Figures, null)?.Text);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void TheLineIsAWarningUnderTheLowMark()
    {
        Assert.Equal(512u * 1024, AppState.VshMemoryLowBytes);

        var low = Figures with { Free = 511 * 1024, Lowest = 511 * 1024 };
        Assert.Equal(("VSH memory: 511 KB free of 5,678 KB (lowest 511 KB)", true),
            ConnectionPanel.VshMemoryLine(true, low, null));

        // A lowest under the mark is a warning even with plenty free now.
        var dipped = Figures with { Lowest = 500 * 1024 };
        Assert.Equal(("VSH memory: 1,234 KB free of 5,678 KB (lowest 500 KB)", true),
            ConnectionPanel.VshMemoryLine(true, dipped, null));

        // Exactly the mark is not under it, and the in-game figures of an ordinary session (about a
        // megabyte, qwark's own log mark) are not a warning at all.
        var edge = Figures with { Free = AppState.VshMemoryLowBytes, Lowest = AppState.VshMemoryLowBytes };
        Assert.False(ConnectionPanel.VshMemoryLine(true, edge, null)!.Value.Warning);
        var ordinary = Figures with { Free = 1044 * 1024, Lowest = 1044 * 1024 };
        Assert.False(ConnectionPanel.VshMemoryLine(true, ordinary, null)!.Value.Warning);

        // A lowest the module says it does not have is not a low lowest.
        var none = Figures with { Lowest = 0, LowestAt = VshMemoryReading.None };
        Assert.False(ConnectionPanel.VshMemoryLine(true, none, null)!.Value.Warning);
    }

    [Fact]
    public void ARefusedReadShowsItsStatus()
    {
        Assert.Equal(("VSH memory: IoError", true), ConnectionPanel.VshMemoryLine(true, Figures, Status.IoError));
        Assert.Equal(("VSH memory: Unsupported", true), ConnectionPanel.VshMemoryLine(true, Figures, Status.Unsupported));

        // Before any figure: a status worth a toast is still shown, a quiet one says nothing.
        Assert.Equal(("VSH memory: BadArg", true), ConnectionPanel.VshMemoryLine(true, null, Status.BadArg));
        Assert.Null(ConnectionPanel.VshMemoryLine(true, null, Status.Unsupported));
        Assert.Null(ConnectionPanel.VshMemoryLine(true, null, Status.UnknownOp));
    }

    [Fact]
    public void WithoutTheBitThereIsNoLineAtAll()
    {
        Assert.Null(ConnectionPanel.VshMemoryLine(false, Figures, null));
        Assert.Null(ConnectionPanel.VshMemoryLine(false, null, Status.IoError));
        Assert.Null(ConnectionPanel.VshMemoryLine(false, null, null));

        // With the bit and nothing asked yet, nothing either.
        Assert.Null(ConnectionPanel.VshMemoryLine(true, null, null));
    }

    [Fact]
    public void TheDebugLineSaysWhenAndWhereTheLowestWas()
    {
        Assert.Equal("Lowest 12 min ago at game start in Deadlocked | 1,800 KB free at module start | reading 4 s old",
            ConnectionPanel.VshMemoryDebugLine(Figures));

        // The ages move on from the console's reply.
        Assert.Equal("Lowest 13 min ago at game start in Deadlocked | 1,800 KB free at module start | reading 1 min old",
            ConnectionPanel.VshMemoryDebugLine(Figures, sinceReply: 60));

        // At the XMB there is no game to name, and a failed start reading says so.
        Assert.Equal("Lowest 2 h 5 min ago at a ten-second reading | no reading at module start | reading 0 s old",
            ConnectionPanel.VshMemoryDebugLine(Figures with
            {
                LowestAt = VshMemoryReading.Periodic,
                LowestGame = GameId.None,
                LowestAgeSeconds = 2 * 3600 + 5 * 60 + 7,
                Start = 0,
                AgeSeconds = 0,
            }));

        Assert.Equal("Lowest 30 s ago at console menu install in RaC2 | 1,800 KB free at module start | reading 4 s old",
            ConnectionPanel.VshMemoryDebugLine(Figures with
            {
                LowestAt = VshMemoryReading.Menu,
                LowestGame = GameId.Rac2,
                LowestAgeSeconds = 30,
            }));

        // A reading or a game this client has no name for is shown by its number.
        Assert.Equal("Lowest 1 min ago at reading 9 in game 7 | 1,800 KB free at module start | reading 4 s old",
            ConnectionPanel.VshMemoryDebugLine(Figures with
            {
                LowestAt = (VshMemoryReading)9,
                LowestGame = (GameId)7,
                LowestAgeSeconds = 60,
            }));

        // No lowest at all: the line starts at module start.
        Assert.Equal("1,800 KB free at module start | reading 4 s old",
            ConnectionPanel.VshMemoryDebugLine(Figures with { LowestAt = VshMemoryReading.None }));
    }
}
