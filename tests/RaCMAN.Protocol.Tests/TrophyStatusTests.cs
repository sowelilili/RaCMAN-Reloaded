using System.Diagnostics;

using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// UYA's trophy reset status (revision 1.21, PROTOCOL.md sections 5.3 and 5.14): the word read
/// field by field, found through the reset's row rather than by a readout index or name, and the
/// one short line the Manips sub-page shows under the Trophies heading for each state.
/// </summary>
public class TrophyStatusParsingTests
{
    [Fact]
    public void TheWordIsLaidOutAsTheContractSpellsIt()
    {
        // Running, registering, the second registration, 9 s in, the fifth reset to end.
        var running = TrophyResetStatus.Parse(0x13200905u);
        Assert.Equal(TrophyResetState.Running, running.State);
        Assert.Equal(TrophyResetStep.Registering, running.Step);
        Assert.Equal(2, running.Registrations);
        Assert.Equal(9, running.Seconds);
        Assert.Equal(5, running.Seq);
        Assert.Equal(TrophyResetFailure.None, running.Failure);

        // Ready in 13 s.
        var ready = TrophyResetStatus.Parse(0x20000D06u);
        Assert.Equal(TrophyResetState.Ready, ready.State);
        Assert.Equal(13, ready.Seconds);
        Assert.Equal(TrophyResetStep.None, ready.Step);

        // Refused with NOT_FOUND (7) in the arg nibble.
        var refused = TrophyResetStatus.Parse(0x37700007u);
        Assert.Equal(TrophyResetFailure.Refused, refused.Failure);
        Assert.Equal(Status.NotFound, refused.RefusedWith);

        // Ten minutes without an answer: 600 s needs the field's twelve bits.
        var slow = TrophyResetStatus.Parse(0x33025808u);
        Assert.Equal(TrophyResetFailure.NoAnswer, slow.Failure);
        Assert.Equal(600, slow.Seconds);

        Assert.Equal(TrophyResetState.None, TrophyResetStatus.Parse(0).State);
    }

    [Fact]
    public void AStatusRoundTrips()
    {
        var statuses = new[]
        {
            TrophyResetStatus.Running(TrophyResetStep.Deleting, seconds: 1, seq: 200),
            TrophyResetStatus.Running(TrophyResetStep.Registering, 3, 12, 255),
            TrophyResetStatus.Ready(4095, 1),
            TrophyResetStatus.Failed(TrophyResetFailure.Refused, Status.Unsupported, seq: 9),
            TrophyResetStatus.Failed(TrophyResetFailure.Stopped, seconds: 77),
        };

        foreach (var status in statuses) Assert.Equal(status, TrophyResetStatus.Parse(status.ToWord()));
        Assert.Equal(0x13200905u, TrophyResetStatus.Running(TrophyResetStep.Registering, 2, 9, 5).ToWord());
    }

    [Fact]
    public void OnlyAnActionNamesAStatusReadout()
    {
        var reset = new Feature(21, FeatureKind.Action, 5, 0, FeatureFlags.None, 12, 0, 0, "Reset trophies");
        Assert.Equal((byte?)12, reset.StatusReadout);

        // A client before revision 1.21 took a readout only from these three kinds, so it reads
        // the reset's row as it always did.
        Assert.Null(reset.MirrorReadout);

        Assert.Null((reset with { Readout = SessionInfo.NoReadout }).StatusReadout);
        Assert.Null((reset with { Readout = 16 }).StatusReadout);
        Assert.Null((reset with { Kind = FeatureKind.Value }).StatusReadout);
        Assert.Null((reset with { Kind = FeatureKind.Toggle }).StatusReadout);
    }
}

/// <summary>The line under the Trophies heading, one per state, and nothing where there is nothing to say.</summary>
public class TrophyStatusLineTests
{
    private static readonly Feature Reset =
        new(21, FeatureKind.Action, 5, 0, FeatureFlags.None, 12, 0, 0, "Reset trophies");

    private static SessionInfo SessionWith(int index, uint word)
    {
        var readouts = new uint[SessionInfo.ReadoutCount];
        readouts[index] = word;
        return SessionInfo.Empty with { State = SessionState.Ingame, Game = GameId.Rac3, Readout = readouts };
    }

    [Fact]
    public void NothingIsShownBeforeTheFirstResetOfABoot()
    {
        Assert.Null(ManipsArea.TrophyLine(default));
        Assert.Null(ManipsArea.StatusLine(SessionWith(12, 0), Reset));
    }

    [Fact]
    public void ARunningResetSaysWhereItIs()
    {
        Assert.Equal("Trophies: resetting...", ManipsArea.TrophyLine(TrophyResetStatus.Running(TrophyResetStep.Starting)));
        Assert.Equal("Trophies: resetting... (deleting)",
            ManipsArea.TrophyLine(TrophyResetStatus.Running(TrophyResetStep.Deleting)));

        // Before the first registration and during it, no attempt number: the first after the
        // delete fails on every console so far, so the second is the one worth naming.
        Assert.Equal("Trophies: resetting... (registering)",
            ManipsArea.TrophyLine(TrophyResetStatus.Running(TrophyResetStep.Registering, 0)));
        Assert.Equal("Trophies: resetting... (registering)",
            ManipsArea.TrophyLine(TrophyResetStatus.Running(TrophyResetStep.Registering, 1)));
        Assert.Equal("Trophies: resetting... (registering, attempt 2)",
            ManipsArea.TrophyLine(TrophyResetStatus.Running(TrophyResetStep.Registering, 2)));

        // A step this client does not know is still a reset under way.
        Assert.Equal("Trophies: resetting...", ManipsArea.TrophyLine(new TrophyResetStatus(TrophyResetState.Running, 9, 0, 0, 0)));
    }

    [Fact]
    public void AReadyResetSaysHowLongItTook()
    {
        Assert.Equal("Trophies ready (13 s)", ManipsArea.TrophyLine(TrophyResetStatus.Ready(13)));
        Assert.Equal("Trophies ready (0 s)", ManipsArea.TrophyLine(TrophyResetStatus.Ready(0)));
    }

    [Theory]
    [InlineData(TrophyResetFailure.DeleteStopped, "Trophy reset failed: a trophy file could not be deleted")]
    [InlineData(TrophyResetFailure.Unregistered, "Trophy reset failed: the game could not re-register its trophies")]
    [InlineData(TrophyResetFailure.NoAnswer, "Trophy reset failed: the console did not answer, restart the game")]
    [InlineData(TrophyResetFailure.LoadEnded, "Trophy reset failed: the Veldin load ended first")]
    [InlineData(TrophyResetFailure.GameRefused, "Trophy reset failed: the game refused it")]
    [InlineData(TrophyResetFailure.NotTaken, "Trophy reset failed: the game did not respond")]
    [InlineData(TrophyResetFailure.Stopped, "Trophy reset failed: the game changed under it")]
    [InlineData((TrophyResetFailure)12, "Trophy reset failed: reason 12")]
    public void AFailedResetSaysWhy(TrophyResetFailure failure, string line)
    {
        Assert.Equal(line, ManipsArea.TrophyLine(TrophyResetStatus.Failed(failure, seconds: 13)));
    }

    [Fact]
    public void ARefusalSaysWhatItWasRefusedWith()
    {
        Assert.Equal("Trophy reset failed: trophy folder not found",
            ManipsArea.TrophyLine(TrophyResetStatus.Failed(TrophyResetFailure.Refused, Status.NotFound)));
        Assert.Equal("Trophy reset failed: refused (Unsupported)",
            ManipsArea.TrophyLine(TrophyResetStatus.Failed(TrophyResetFailure.Refused, Status.Unsupported)));
        Assert.Equal("Trophy reset failed: refused (IoError)",
            ManipsArea.TrophyLine(TrophyResetStatus.Failed(TrophyResetFailure.Refused, Status.IoError)));
    }

    [Fact]
    public void AStateThisClientDoesNotKnowSaysNothing()
    {
        Assert.Null(ManipsArea.TrophyLine(TrophyResetStatus.Parse(0x40000D01u)));
        Assert.Null(ManipsArea.TrophyLine(TrophyResetStatus.Parse(0xF0000000u)));
    }

    [Fact]
    public void TheLineIsReadThroughTheResetsOwnRow()
    {
        var ready = TrophyResetStatus.Ready(13, 4).ToWord();

        var line = ManipsArea.StatusLine(SessionWith(12, ready), Reset);
        Assert.Equal<(string, TrophyResetState)?>(("Trophies ready (13 s)", TrophyResetState.Ready), line);

        // The row says where the word is, whatever index that is.
        Assert.Equal("Trophies ready (13 s)",
            ManipsArea.StatusLine(SessionWith(3, ready), Reset with { Readout = 3 })?.Text);

        // An older qwark names no status readout: nothing, whatever the readouts hold.
        Assert.Null(ManipsArea.StatusLine(SessionWith(12, ready), Reset with { Readout = SessionInfo.NoReadout }));

        // The toggle beside it is not a status, and neither is a VALUE that mirrors a readout.
        var toggle = new Feature(39, FeatureKind.Toggle, 5, 0, FeatureFlags.None, 12, 0, 0, "Reset trophies on Veldin load");
        Assert.Null(ManipsArea.StatusLine(SessionWith(12, ready), toggle));
        var value = new Feature(3, FeatureKind.Value, 0, 0, FeatureFlags.None, 12, 0, 0, "Bolts");
        Assert.Null(ManipsArea.StatusLine(SessionWith(12, ready), value));

        var failed = TrophyResetStatus.Failed(TrophyResetFailure.DeleteStopped).ToWord();
        Assert.Equal(TrophyResetState.Failed, ManipsArea.StatusLine(SessionWith(12, failed), Reset)?.State);
    }

    [Fact]
    public void TheFakeScriptNamesEachState()
    {
        static TrophyResetStatus? Step(string step) => FakeScript.TrophyStep(step.Split(':'));

        Assert.Equal(default(TrophyResetStatus), Step("trophies:none"));
        Assert.Equal(TrophyResetStep.Starting, Step("trophies:starting")?.Step);
        Assert.Equal(TrophyResetStep.Deleting, Step("trophies:deleting")?.Step);
        Assert.Equal((int?)2, Step("trophies:registering:2")?.Registrations);
        Assert.Equal((ushort?)13, Step("trophies:ready")?.Seconds);
        Assert.Equal((ushort?)9, Step("trophies:ready:9")?.Seconds);
        Assert.Equal(TrophyResetFailure.Unregistered, Step("trophies:failed")?.Failure);
        Assert.Equal(TrophyResetFailure.LoadEnded, Step("trophies:failed:4")?.Failure);
        Assert.Null(Step("trophies:sideways"));
    }
}

/// <summary>The fake console's trophy reset and its status readout, and the line against it end to end.</summary>
public class TrophyStatusFakeTests
{
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

    private static void MoveToUya(FakeQwarkServer server)
    {
        server.Describe = FakeQwarkServer.WithManipHeaderFeatures(server.Describe with { Game = GameId.Rac3 }, GameId.Rac3);
        server.Session = server.Session with
        {
            Game = GameId.Rac3,
            TitleId = "NPEA00387",
            Generation = server.Session.Generation + 1,
        };
    }

    [Fact]
    public void TheFakesResetNamesAStatusReadoutOnce()
    {
        var plain = new DescribeResult(GameId.Rac3, new[] { "Cheats" }, new[] { "Bolts", "Health" },
            new[] { new Feature(0, FeatureKind.Toggle, 0, 0, FeatureFlags.None, 0xFF, 0, 0, "Infinite ammo") });

        var uya = FakeQwarkServer.WithManipHeaderFeatures(plain, GameId.Rac3);
        var reset = uya.Features.Single(f => f.Label == FakeQwarkServer.TrophyResetLabel);
        Assert.Equal((byte?)2, reset.StatusReadout);
        Assert.Equal(new[] { "Bolts", "Health", FakeQwarkServer.TrophyStatusReadoutName }, uya.Readouts);
        Assert.Null(uya.Features.Single(f => f.Label == FakeQwarkServer.TrophyAutoResetLabel).StatusReadout);

        // Asked again, it adds nothing.
        var again = FakeQwarkServer.WithManipHeaderFeatures(uya, GameId.Rac3);
        Assert.Equal(uya.Readouts, again.Readouts);
        Assert.Equal(uya.Features.Length, again.Features.Length);

        // Deadlocked's heading has no status readout.
        var deadlocked = FakeQwarkServer.WithManipHeaderFeatures(plain with { Game = GameId.Rac4 }, GameId.Rac4);
        Assert.Equal(plain.Readouts, deadlocked.Readouts);
        Assert.All(deadlocked.Features, f => Assert.Null(f.StatusReadout));
    }

    [Fact]
    public async Task TheLineFollowsTheConsolesWord()
    {
        using var server = new FakeQwarkServer();
        MoveToUya(server);
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Manips is not null
                                                 && state.Describe.Features.Any(f => f.StatusReadout is not null)));

        // Nothing this boot yet: no line.
        Assert.EndsWith("trophies=-", ManipsArea.Summary(state));

        Assert.True(server.SetTrophyStatus(TrophyResetStatus.Running(TrophyResetStep.Registering, 2, 6, 0)));
        Assert.True(await PumpAsync(state, () => ManipsArea.Summary(state).EndsWith(
            "trophies=Trophies:_resetting..._(registering,_attempt_2)", StringComparison.Ordinal)));

        Assert.True(server.SetTrophyStatus(TrophyResetStatus.Ready(13, 1)));
        Assert.True(await PumpAsync(state, () => ManipsArea.Summary(state).EndsWith(
            "trophies=Trophies_ready_(13_s)", StringComparison.Ordinal)));

        Assert.True(server.SetTrophyStatus(TrophyResetStatus.Failed(TrophyResetFailure.LoadEnded, seq: 2)));
        Assert.True(await PumpAsync(state, () => ManipsArea.Summary(state).EndsWith(
            "trophies=Trophy_reset_failed:_the_Veldin_load_ended_first", StringComparison.Ordinal)));

        // A boot puts the word back to 0, and the line goes.
        Assert.True(server.SetTrophyStatus(default));
        Assert.True(await PumpAsync(state, () => ManipsArea.Summary(state).EndsWith("trophies=-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AnOlderQwarkHasNoLine()
    {
        using var server = new FakeQwarkServer();
        MoveToUya(server);

        // Build 53 before revision 1.21's status: the reset names no readout, and a word left in
        // the readouts is nobody's.
        server.Describe = server.Describe with
        {
            Features = server.Describe.Features
                .Select(f => f.Label == FakeQwarkServer.TrophyResetLabel ? f with { Readout = SessionInfo.NoReadout } : f)
                .ToArray(),
        };
        var readouts = (uint[])server.Session.Readout.Clone();
        readouts[5] = TrophyResetStatus.Ready(13).ToWord();
        server.Session = server.Session with { Readout = readouts };
        Assert.False(server.SetTrophyStatus(TrophyResetStatus.Ready(13)));

        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Manips is not null
                                                 && state.Describe.Features.Any(f => f.Label == FakeQwarkServer.TrophyResetLabel)
                                                 && state.Session.ReadoutAt(5) == readouts[5]));
        Assert.EndsWith("trophies=-", ManipsArea.Summary(state));
    }
}
