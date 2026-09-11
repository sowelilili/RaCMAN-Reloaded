using System.Net.Sockets;

using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The order the Connect button does things in against a console: qwark first, webMAN only when
/// nothing answered, and a failure that names the step it stopped at. Every step is a delegate, so
/// none of this reaches the network.
/// </summary>
public class Ps3ConnectTests
{
    private const string Ip = "192.168.1.50";

    /// <summary>Records what the sequence asked for, and fails whichever steps the test wants failed.</summary>
    private sealed class FakeConsole
    {
        public List<string> Calls { get; } = new();

        public List<Ps3ConnectStep> Said { get; } = new();

        public List<TimeSpan> Waits { get; } = new();

        /// <summary>How many connect attempts fail before one succeeds; the default never answers.</summary>
        public int RefuseConnects { get; set; } = int.MaxValue;

        public bool Loaded { get; set; }

        public Exception? AskThrows { get; set; }

        public Exception? LoadThrows { get; set; }

        public int Connects { get; private set; }

        public Task<Ps3ConnectOutcome> RunAsync() => RunAsync(webMan: true);

        public Task<Ps3ConnectOutcome> RunAsync(bool webMan) => Ps3Connect.RunAsync(
            webMan,
            Ip,
            connect: () =>
            {
                Calls.Add("connect");
                if (Connects++ < RefuseConnects) throw new SocketException((int)SocketError.ConnectionRefused);
                return Task.CompletedTask;
            },
            isLoaded: () =>
            {
                Calls.Add("ask");
                if (AskThrows is { } error) throw error;
                return Task.FromResult(Loaded);
            },
            load: () =>
            {
                Calls.Add("load");
                if (LoadThrows is { } error) throw error;
                return Task.CompletedTask;
            },
            wait: delay =>
            {
                Waits.Add(delay);
                return Task.CompletedTask;
            },
            say: (step, _) => Said.Add(step));
    }

    [Fact]
    public async Task AConsoleThatAnswersIsNeverAskedAboutWebMan()
    {
        var console = new FakeConsole { RefuseConnects = 0 };

        var outcome = await console.RunAsync();

        Assert.True(outcome.Connected);
        Assert.Equal(Ps3ConnectStep.Connect, outcome.Step);
        Assert.Equal(new[] { "connect" }, console.Calls);
        Assert.Empty(console.Waits);
    }

    [Fact]
    public async Task NothingListeningLoadsThroughWebManAndConnectsAgain()
    {
        // Refused once, so the load happens; the attempt after it is the one that lands.
        var console = new FakeConsole { RefuseConnects = 1, Loaded = false };

        var outcome = await console.RunAsync();

        Assert.True(outcome.Connected);
        Assert.Equal(Ps3ConnectStep.Reconnect, outcome.Step);
        Assert.Equal(new[] { "connect", "ask", "load", "connect" }, console.Calls);
        Assert.Equal(new[] { Ps3Connect.LoadWait }, console.Waits);

        // Every step said what it was about to do, in the order it did it.
        Assert.Equal(
            new[] { Ps3ConnectStep.Connect, Ps3ConnectStep.Ask, Ps3ConnectStep.Load, Ps3ConnectStep.Reconnect },
            console.Said);
    }

    [Fact]
    public async Task AModuleWebManAlreadyListsIsNotSentAgain()
    {
        var console = new FakeConsole { RefuseConnects = 1, Loaded = true };

        var outcome = await console.RunAsync();

        Assert.True(outcome.Connected);
        Assert.Equal(new[] { "connect", "ask", "connect" }, console.Calls);
        Assert.Equal(new[] { Ps3Connect.LoadWait }, console.Waits);
    }

    [Fact]
    public async Task AFailedLoadStopsAtTheLoadStepAndSaysWhy()
    {
        var console = new FakeConsole { LoadThrows = new IOException("FTP login refused") };

        var outcome = await console.RunAsync();

        Assert.False(outcome.Connected);
        Assert.Equal(Ps3ConnectStep.Load, outcome.Step);
        Assert.Contains("FTP login refused", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "connect", "ask", "load" }, console.Calls);
    }

    [Fact]
    public async Task AWebManThatCannotBeAskedStopsAtTheAskStep()
    {
        var console = new FakeConsole { AskThrows = new HttpRequestException("no route to host") };

        var outcome = await console.RunAsync();

        Assert.False(outcome.Connected);
        Assert.Equal(Ps3ConnectStep.Ask, outcome.Step);
        Assert.Contains("no route to host", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "connect", "ask" }, console.Calls);
    }

    [Fact]
    public async Task AConsoleThatStillDoesNotAnswerAfterTheLoadStopsAtTheSecondAttempt()
    {
        var console = new FakeConsole();

        var outcome = await console.RunAsync();

        Assert.False(outcome.Connected);
        Assert.Equal(Ps3ConnectStep.Reconnect, outcome.Step);
        Assert.Contains(Ip, outcome.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "connect", "ask", "load", "connect" }, console.Calls);
    }

    [Fact]
    public async Task AModuleThatAnsweredAndThenRefusedIsNotAWebManProblem()
    {
        // A protocol or status failure means qwark is there and talking, so the sequence has
        // nothing to add: the error is the caller's to show, and webMAN is never asked.
        var calls = new List<string>();
        var failure = new ProtocolException("HELLO was not understood");

        var thrown = await Assert.ThrowsAsync<ProtocolException>(() => Ps3Connect.RunAsync(
            Ip,
            connect: () =>
            {
                calls.Add("connect");
                throw failure;
            },
            isLoaded: () =>
            {
                calls.Add("ask");
                return Task.FromResult(false);
            },
            load: () =>
            {
                calls.Add("load");
                return Task.CompletedTask;
            },
            wait: _ => Task.CompletedTask,
            say: (_, _) => { }));

        Assert.Same(failure, thrown);
        Assert.Equal(new[] { "connect" }, calls);
    }

    // ---------------------------------------------------------------- standalone mode

    [Fact]
    public async Task StandaloneConnectsAndNeverMentionsWebMan()
    {
        var console = new FakeConsole { RefuseConnects = 0 };

        var outcome = await console.RunAsync(webMan: false);

        Assert.True(outcome.Connected);
        Assert.Equal(Ps3ConnectStep.Connect, outcome.Step);
        Assert.Equal(new[] { "connect" }, console.Calls);
    }

    [Fact]
    public async Task StandaloneStopsAtTheSilentPortRatherThanLoadingAnything()
    {
        // The console that would have sent the webMAN sequence round: in standalone mode one
        // attempt is the whole of it, and webMAN is not asked even whether the module is there.
        var console = new FakeConsole();

        var outcome = await console.RunAsync(webMan: false);

        Assert.False(outcome.Connected);
        Assert.Equal(Ps3ConnectStep.Connect, outcome.Step);
        Assert.Contains(Ip, outcome.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "connect" }, console.Calls);
        Assert.Empty(console.Waits);
        Assert.Equal(new[] { Ps3ConnectStep.Connect }, console.Said);
    }

    [Fact]
    public async Task StandaloneLeavesAModuleErrorToTheCaller()
    {
        // Same rule as the long way round: qwark answered, so what it said is the error to show.
        var failure = new ProtocolException("HELLO was not understood");

        var thrown = await Assert.ThrowsAsync<ProtocolException>(() => Ps3Connect.ConnectOnlyAsync(
            Ip,
            connect: () => throw failure,
            say: (_, _) => { }));

        Assert.Same(failure, thrown);
    }

    // ---------------------------------------------------------------- the reconnect rate limit

    [Fact]
    public void TheFirstReconnectAttemptTakesTheDetour()
    {
        Assert.True(Ps3Connect.ShouldDetour(null, 0));
        Assert.True(Ps3Connect.ShouldDetour(null, 1_000_000));
    }

    [Fact]
    public void OneDetourPerThirtySecondsAndPlainAttemptsInBetween()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), Ps3Connect.DetourInterval);

        long at = 10_000;

        // The attempts the loop makes in the half-minute after a detour are plain reconnects.
        Assert.False(Ps3Connect.ShouldDetour(at, at));
        Assert.False(Ps3Connect.ShouldDetour(at, at + 1_000));
        Assert.False(Ps3Connect.ShouldDetour(at, at + 29_999));

        // Thirty seconds on, the console has had its chance and the detour is worth taking again.
        Assert.True(Ps3Connect.ShouldDetour(at, at + 30_000));
        Assert.True(Ps3Connect.ShouldDetour(at, at + 120_000));
    }

    [Fact]
    public void TheResolvedSprxIsAbsoluteAndDefaultsToTheOneBesideTheClient()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "qwark.sprx");

        Assert.Equal(beside, Ps3Connect.ResolveSprx(null));
        Assert.Equal(beside, Ps3Connect.ResolveSprx("   "));
        Assert.Equal(beside, Ps3Connect.ResolveSprx("qwark.sprx"));

        // A path of the user's own is taken as it is, quotes from a paste-in included.
        string rooted = Path.Combine(Path.GetTempPath(), "other.sprx");
        Assert.Equal(rooted, Ps3Connect.ResolveSprx(rooted));
        Assert.Equal(rooted, Ps3Connect.ResolveSprx($"\"{rooted}\""));
    }

    [Fact]
    public void OnlyASilentPortSendsTheSequenceRoundThroughWebMan()
    {
        Assert.True(Ps3Connect.NothingListening(new SocketException((int)SocketError.ConnectionRefused)));
        Assert.True(Ps3Connect.NothingListening(new SocketException((int)SocketError.TimedOut)));
        Assert.True(Ps3Connect.NothingListening(new TimeoutException()));
        Assert.True(Ps3Connect.NothingListening(new TaskCanceledException()));

        Assert.False(Ps3Connect.NothingListening(new ProtocolException("bad frame")));
        Assert.False(Ps3Connect.NothingListening(new IOException("connection reset")));
    }
}
