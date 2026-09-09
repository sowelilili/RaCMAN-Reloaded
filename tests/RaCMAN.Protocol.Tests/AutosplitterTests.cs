using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Stands in for LiveSplit's built-in TCP server: it records the commands it is given, answers the
/// get* queries out of a scripted split list, and moves its own phase and split index the way
/// LiveSplit would, so the engine's "ask again after every command" behaviour is exercised.
/// </summary>
internal sealed class FakeLiveSplitServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _commands = new();
    private readonly object _gate = new();

    public FakeLiveSplitServer(params string[] splits)
    {
        Splits = splits.Length > 0 ? splits : new[] { "Aranos", "Oozla", "Maktar" };
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    public string[] Splits { get; set; }

    public int SplitIndex { get; set; }

    public string Phase { get; set; } = "NotRunning";

    public string Version { get; set; } = "1.8.29";

    /// <summary>Everything received, queries included, in order.</summary>
    public string[] Commands
    {
        get { lock (_gate) return _commands.ToArray(); }
    }

    /// <summary>Just the commands that do something: the queries are noise for most assertions.</summary>
    public string[] Actions
    {
        get { lock (_gate) return _commands.Where(c => !LiveSplitClient.IsQuery(c)).ToArray(); }
    }

    public void ClearCommands()
    {
        lock (_gate) _commands.Clear();
    }

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };

                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line is null) return;

                    string command = line.Trim();
                    lock (_gate) _commands.Add(command);

                    var reply = Handle(command);
                    if (reply is not null) await writer.WriteLineAsync(reply);
                }
            }
        }
        catch (Exception)
        {
            // The client went away.
        }
    }

    private string? Handle(string command)
    {
        lock (_gate)
        {
            switch (command)
            {
                case LiveSplitClient.StartTimer:
                    Phase = "Running";
                    SplitIndex = 0;
                    return null;

                case LiveSplitClient.StartOrSplit:
                    if (Phase == "Running") SplitIndex++;
                    else Phase = "Running";
                    return null;

                case LiveSplitClient.Split:
                case LiveSplitClient.SkipSplit:
                    if (Phase == "Running") SplitIndex++;
                    return null;

                case LiveSplitClient.Unsplit:
                    SplitIndex = Math.Max(0, SplitIndex - 1);
                    return null;

                case LiveSplitClient.Reset:
                    Phase = "NotRunning";
                    SplitIndex = 0;
                    return null;

                case LiveSplitClient.Pause:
                    Phase = "Paused";
                    return null;

                case LiveSplitClient.Resume:
                    Phase = "Running";
                    return null;

                case LiveSplitClient.GetCurrentTimerPhase:
                    return Phase;

                case LiveSplitClient.GetLiveSplitVersion:
                    return Version;

                case LiveSplitClient.GetSplitIndex:
                    return SplitIndex.ToString();

                case LiveSplitClient.GetCurrentSplitName:
                    return NameAt(SplitIndex);

                case LiveSplitClient.GetUpcomingSplitName:
                    return NameAt(SplitIndex + 1);

                case LiveSplitClient.GetPreviousSplitName:
                    return NameAt(SplitIndex - 1);

                case LiveSplitClient.Ping:
                    return "pong";

                default:
                    return null;
            }
        }
    }

    private string NameAt(int index) => index >= 0 && index < Splits.Length ? Splits[index] : string.Empty;

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        _cts.Dispose();
    }
}

/// <summary>
/// The decision engine: which events reach LiveSplit, as which command, and why. The console's
/// half is not involved here — events are handed straight to the engine, which is exactly what
/// QwarkClient does when a push or a poll turns one up.
/// </summary>
public class AutosplitterTests
{
    private static readonly AutosplitEventDesc PlanetEntered = new(
        1, AutosplitKind.Split, AutosplitEventFlags.EnabledByDefault | AutosplitEventFlags.PlanetRoute,
        "Planet entered");

    private static readonly AutosplitEventDesc BossDefeated = new(
        2, AutosplitKind.Split, AutosplitEventFlags.EnabledByDefault, "Protopet defeated");

    private static readonly AutosplitEventDesc ArenaEntered = new(
        3, AutosplitKind.Split, AutosplitEventFlags.None, "Maktar arena");

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

    private sealed class Harness : IDisposable
    {
        public Harness(GameId game, string[] splits, params AutosplitEventDesc[] descriptors)
        {
            Server = new FakeLiveSplitServer(splits);
            Settings = new Settings();
            Settings.Autosplit.Enabled = true;
            LiveSplit = new LiveSplitClient();

            // The app hands results to the render thread; a test applies them where they happen.
            Engine = new Autosplitter(Settings, LiveSplit, action => action())
            {
                Game = game,
                Descriptors = descriptors,
                PlanetNames = new[] { "Aranos", "Oozla", "Maktar", "Endako" },
            };

            LiveSplit.Start(LiveSplitClient.DefaultHost, Server.Port);
        }

        public FakeLiveSplitServer Server { get; }

        public Settings Settings { get; }

        public LiveSplitClient LiveSplit { get; }

        public Autosplitter Engine { get; }

        public AutosplitGameSettings Options => Settings.Autosplit.For(Engine.Game);

        public async Task ReadyAsync()
        {
            Assert.True(await WaitFor(() => LiveSplit.IsConnected), "the fake LiveSplit never accepted the client");
            await Engine.RefreshAsync();
            Server.ClearCommands();
        }

        /// <summary>Waits for the engine's fire-and-forget send to reach the server.</summary>
        public Task<bool> Sent(string command) => WaitFor(() => Server.Actions.Contains(command));

        public void Dispose()
        {
            LiveSplit.Dispose();
            Server.Dispose();
        }
    }

    private static AutosplitEvent Split(byte code, uint arg = 0, uint seq = 1) =>
        new(seq, 1200, AutosplitKind.Split, code, arg);

    // ---------------------------------------------------------------- which events split

    [Fact]
    public async Task ACodeThatIsOnByDefaultSplits()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated, ArenaEntered);
        await h.ReadyAsync();

        h.Engine.Handle(Split(BossDefeated.Code));

        Assert.True(await h.Sent(LiveSplitClient.Split));
        Assert.Equal(1, h.Engine.Acted);

        var entry = h.Engine.Log()[^1];
        Assert.Equal("Protopet defeated", entry.Event);
        Assert.True(entry.Acted);
    }

    [Fact]
    public async Task ACodeThatIsOffByDefaultDoesNothingUntilItIsTicked()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated, ArenaEntered);
        await h.ReadyAsync();

        h.Engine.Handle(Split(ArenaEntered.Code));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.Equal(0, h.Engine.Acted);
        Assert.Contains("switched off", h.Engine.Log()[^1].Action);

        h.Options.SetEvent(ArenaEntered.Label, true);
        h.Engine.Handle(Split(ArenaEntered.Code, seq: 2));

        Assert.True(await h.Sent(LiveSplitClient.Split));
    }

    [Fact]
    public async Task ACodeTheUserSwitchedOffStopsSplitting()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated);
        await h.ReadyAsync();

        h.Options.SetEvent(BossDefeated.Label, false);
        h.Engine.Handle(Split(BossDefeated.Code));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.Equal(1, h.Engine.Received);
        Assert.Equal(0, h.Engine.Acted);
    }

    // ---------------------------------------------------------------- the planet route

    [Fact]
    public async Task WithoutTheRouteEveryPlanetSplits()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla", "Maktar" }, PlanetEntered);
        await h.ReadyAsync();

        // Maktar (planet 2) while the next split says Oozla: no route, so it splits anyway.
        h.Engine.Handle(Split(PlanetEntered.Code, 2));

        Assert.True(await h.Sent(LiveSplitClient.Split));
    }

    [Fact]
    public async Task TheRouteComparesTheNextSplitWhenNamesAreThePlanetYouAreOn()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla", "Maktar" }, PlanetEntered);
        await h.ReadyAsync();
        h.Options.PlanetRoute = true;

        // Split 0 is running, so the next split is "Oozla": entering Maktar is off route.
        h.Engine.Handle(Split(PlanetEntered.Code, 2));
        await Task.Delay(200);
        Assert.Empty(h.Server.Actions);
        Assert.Contains("not on this planet's route", h.Engine.Log()[^1].Action);

        // Oozla is planet 1, and rac2-planets.txt calls it "ozla", which "Oozla" contains.
        h.Engine.Handle(Split(PlanetEntered.Code, 1, seq: 2));
        Assert.True(await h.Sent(LiveSplitClient.Split));
        Assert.Contains("next split", h.Engine.Log()[^1].Action);
    }

    [Fact]
    public async Task TheRouteComparesTheCurrentSplitWhenNamesAreWhereYouAreGoing()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla", "Maktar" }, PlanetEntered);
        await h.ReadyAsync();
        h.Options.PlanetRoute = true;
        h.Options.NamesAreDestination = true;

        h.Server.SplitIndex = 1;
        await h.Engine.RefreshAsync();
        Assert.Equal("Oozla", h.Engine.View.CurrentSplit);

        // Endako (planet 3) is not what the current split names.
        h.Engine.Handle(Split(PlanetEntered.Code, 3));
        await Task.Delay(200);
        Assert.Empty(h.Server.Actions);

        h.Engine.Handle(Split(PlanetEntered.Code, 1, seq: 2));
        Assert.True(await h.Sent(LiveSplitClient.Split));
        Assert.Contains("current split", h.Engine.Log()[^1].Action);
    }

    [Fact]
    public async Task APlanetTheRouteFileDoesNotCoverNeverSplits()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered);
        await h.ReadyAsync();
        h.Options.PlanetRoute = true;

        h.Engine.Handle(Split(PlanetEntered.Code, 250));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.False(h.Engine.Log()[^1].Acted);
    }

    // ---------------------------------------------------------------- start, reset, pause

    [Fact]
    public async Task StartStartsATimerThatIsNotRunningAndLeavesOneThatIs()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered);
        await h.ReadyAsync();

        h.Engine.Handle(new AutosplitEvent(1, 10, AutosplitKind.Start, 0, 0));
        Assert.True(await h.Sent(LiveSplitClient.StartTimer));

        // The engine re-asks after every command, so it now knows the timer is running.
        Assert.True(await WaitFor(() => h.Engine.View.Phase == LiveSplitPhase.Running));
        h.Server.ClearCommands();

        h.Engine.Handle(new AutosplitEvent(2, 20, AutosplitKind.Start, 0, 0));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.Contains("the timer is Running", h.Engine.Log()[^1].Action);
    }

    [Fact]
    public async Task ResetThenStartInTheSameBurstStartsTheNewRun()
    {
        // The console sends RESET and START back to back on a new game. The read-back after the
        // reset is asynchronous, so the engine has to assume the reset landed or the START that
        // follows a millisecond later would still see the old run as Running.
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered);
        await h.ReadyAsync();

        h.Engine.Handle(new AutosplitEvent(1, 10, AutosplitKind.Start, 0, 0));
        Assert.True(await h.Sent(LiveSplitClient.StartTimer));
        Assert.True(await WaitFor(() => h.Engine.View.Phase == LiveSplitPhase.Running));
        h.Server.ClearCommands();

        h.Engine.Handle(new AutosplitEvent(2, 20, AutosplitKind.Reset, 0, 0));
        h.Engine.Handle(new AutosplitEvent(3, 20, AutosplitKind.Start, 0, 0));

        Assert.True(await h.Sent(LiveSplitClient.Reset));
        Assert.True(await h.Sent(LiveSplitClient.StartTimer));
    }

    [Fact]
    public async Task ResetGoesThroughUnlessNeverResetIsOn()
    {
        using var h = new Harness(GameId.Rac4, new[] { "Dread Zone", "Catacrom" }, PlanetEntered);
        await h.ReadyAsync();

        h.Engine.Handle(new AutosplitEvent(1, 10, AutosplitKind.Reset, 0, 0));
        Assert.True(await h.Sent(LiveSplitClient.Reset));

        h.Server.ClearCommands();
        h.Options.NeverReset = true;
        h.Engine.Handle(new AutosplitEvent(2, 20, AutosplitKind.Reset, 0, 0));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.Contains("never reset", h.Engine.Log()[^1].Action);
    }

    [Fact]
    public async Task PauseAndResumeAreSentStraightThrough()
    {
        using var h = new Harness(GameId.Rac4, new[] { "Dread Zone", "Catacrom" });
        await h.ReadyAsync();

        h.Engine.Handle(new AutosplitEvent(1, 10, AutosplitKind.Pause, 0, 0));
        Assert.True(await h.Sent(LiveSplitClient.Pause));

        h.Engine.Handle(new AutosplitEvent(2, 20, AutosplitKind.Resume, 0, 0));
        Assert.True(await h.Sent(LiveSplitClient.Resume));
        Assert.Equal(2, h.Engine.Acted);
    }

    // ---------------------------------------------------------------- when nothing should happen

    [Fact]
    public async Task EventsAreIgnoredAndLoggedWhileTheAutosplitterIsOff()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated);
        await h.ReadyAsync();

        h.Settings.Autosplit.Enabled = false;
        h.Engine.Handle(Split(BossDefeated.Code));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.Equal(1, h.Engine.Received);
        Assert.Equal(0, h.Engine.Acted);
        Assert.Contains("the autosplitter is off", h.Engine.Log()[^1].Action);
    }

    [Fact]
    public async Task EventsAreIgnoredAndLoggedWhileLiveSplitIsDisconnected()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated);
        await h.ReadyAsync();

        h.LiveSplit.Stop();
        h.Engine.Handle(Split(BossDefeated.Code));
        await Task.Delay(200);

        Assert.Empty(h.Server.Actions);
        Assert.Contains("LiveSplit is not connected", h.Engine.Log()[^1].Action);
    }

    [Fact]
    public async Task TheLogKeepsTheLastHundredEventsAndTheirReasons()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" }, PlanetEntered, BossDefeated);
        await h.ReadyAsync();
        h.Settings.Autosplit.Enabled = false;

        for (uint i = 1; i <= Autosplitter.LogLength + 20; i++) h.Engine.Handle(Split(BossDefeated.Code, seq: i));

        var log = h.Engine.Log();
        Assert.Equal(Autosplitter.LogLength, log.Length);
        Assert.Equal(21u, log[0].Seq);
        Assert.Equal((uint)(Autosplitter.LogLength + 20), log[^1].Seq);

        h.Engine.ClearLog();
        Assert.Empty(h.Engine.Log());
    }

    [Fact]
    public async Task TheTestButtonsSendOneCommandAndAreLoggedAsManual()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" });
        await h.ReadyAsync();

        h.Engine.SendManual(LiveSplitClient.Split);
        Assert.True(await h.Sent(LiveSplitClient.Split));

        var entry = h.Engine.Log()[^1];
        Assert.Equal("(manual)", entry.Event);
        Assert.Equal(0, h.Engine.Acted);   // a manual press is not a decision about an event
    }

    [Fact]
    public async Task TheVersionAndPhaseComeBackFromLiveSplitOnConnect()
    {
        using var h = new Harness(GameId.Rac2, new[] { "Aranos", "Oozla" });
        await h.ReadyAsync();

        Assert.True(await WaitFor(() => h.LiveSplit.Version == "1.8.29"));
        Assert.Equal(LiveSplitStatus.Connected, h.LiveSplit.Status);
        Assert.Contains("1.8.29", h.LiveSplit.StatusLine);
        Assert.Equal(LiveSplitPhase.NotRunning, h.Engine.View.Phase);
        Assert.Equal("Aranos", h.Engine.View.CurrentSplit);
        Assert.Equal("Oozla", h.Engine.View.UpcomingSplit);
    }

    // ---------------------------------------------------------------- the route files

    [Fact]
    public void EveryGameShipsARouteFileWithOneLinePerPlanet()
    {
        // The counts are qwark's planet tables: PLANET_LIST index is the line number.
        Assert.Equal(19, AutosplitRoutes.For(GameId.Rac1).Count);
        Assert.Equal(27, AutosplitRoutes.For(GameId.Rac2).Count);
        Assert.Equal(37, AutosplitRoutes.For(GameId.Rac3).Count);
        Assert.Equal(16, AutosplitRoutes.For(GameId.Rac4).Count);
        Assert.Empty(AutosplitRoutes.Problems);
    }

    [Fact]
    public void NullLinesAreUnusedPlanetsAndNeverMatch()
    {
        // Deadlocked's unused indices and RaC3's two placeholders.
        Assert.False(AutosplitRoutes.Knows(GameId.Rac4, 0));
        Assert.False(AutosplitRoutes.Knows(GameId.Rac4, 3));
        Assert.False(AutosplitRoutes.Knows(GameId.Rac3, 0));
        Assert.False(AutosplitRoutes.Knows(GameId.Rac3, 15));

        Assert.False(AutosplitRoutes.Matches(GameId.Rac4, 0, "Dread Zone"));
        Assert.False(AutosplitRoutes.Matches(GameId.Rac2, 1, null));
        Assert.False(AutosplitRoutes.Matches(GameId.Rac2, 99, "Oozla"));
    }

    [Fact]
    public void TheNameTestIsTheOldScriptsSubstringMatch()
    {
        Assert.True(AutosplitRoutes.Matches(GameId.Rac2, 1, "Oozla"));
        Assert.True(AutosplitRoutes.Matches(GameId.Rac2, 2, "Maktar Resort"));
        Assert.True(AutosplitRoutes.Matches(GameId.Rac2, 14, "Aranos 2 (Clank)"));
        Assert.False(AutosplitRoutes.Matches(GameId.Rac2, 5, "Notak"));

        Assert.True(AutosplitRoutes.Matches(GameId.Rac1, 3, "Kerwan"));
        Assert.True(AutosplitRoutes.Matches(GameId.Rac1, 3, "metropolis"));
        Assert.True(AutosplitRoutes.Matches(GameId.Rac1, 17, "Drek's Fleet"));
        Assert.False(AutosplitRoutes.Matches(GameId.Rac1, 1, "Aridia"));

        Assert.True(AutosplitRoutes.Matches(GameId.Rac3, 3, "Starship Phoenix"));
        Assert.True(AutosplitRoutes.Matches(GameId.Rac3, 26, "Metropolis Rangers"));
        // Deadlocked's list is the old dlplanets.txt verbatim, spellings and all: "dreadzone" has
        // no space in it, so a split called "Dread Zone" is matched by "marauder" and friends.
        Assert.True(AutosplitRoutes.Matches(GameId.Rac4, 1, "DreadZone"));
        Assert.True(AutosplitRoutes.Matches(GameId.Rac4, 1, "Marauder"));
        Assert.False(AutosplitRoutes.Matches(GameId.Rac4, 1, "Dread Zone"));
        Assert.True(AutosplitRoutes.Matches(GameId.Rac4, 13, "Maraxus"));
    }

    // ---------------------------------------------------------------- settings

    [Fact]
    public void AutosplitSettingsRoundTripThroughTheFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "racman-autosplit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "racman-reloaded.settings.json");
            var saved = Settings.Load(path);
            saved.Autosplit.Enabled = true;
            saved.Autosplit.Host = "10.0.0.4";
            saved.Autosplit.Port = 16835;

            var rac2 = saved.Autosplit.For(GameId.Rac2);
            rac2.PlanetRoute = true;
            rac2.NamesAreDestination = true;
            rac2.SetEvent("Planet entered", false);
            rac2.SetEvent("Protopet defeated", true);

            saved.Autosplit.For(GameId.Rac4).NeverReset = true;
            saved.Save();

            var loaded = Settings.Load(path);
            Assert.True(loaded.Autosplit.Enabled);
            Assert.Equal("10.0.0.4", loaded.Autosplit.Host);
            Assert.Equal(16835, loaded.Autosplit.Port);

            var back = loaded.Autosplit.For(GameId.Rac2);
            Assert.True(back.PlanetRoute);
            Assert.True(back.NamesAreDestination);
            Assert.False(back.EventEnabled("Planet entered", byDefault: true));
            Assert.True(back.EventEnabled("Protopet defeated", byDefault: false));
            Assert.True(loaded.Autosplit.For(GameId.Rac4).NeverReset);

            // A label the file never mentioned takes the console's default, either way round.
            Assert.True(back.EventEnabled("Maktar arena", byDefault: true));
            Assert.False(back.EventEnabled("Maktar arena", byDefault: false));

            // The per-game entries are keyed by game, and nothing else was invented.
            Assert.Equal(new[] { "rac2", "rac4" }, loaded.Autosplit.Games.Keys.OrderBy(k => k).ToArray());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SettingsFromAnOlderBuildLoadWithTheAutosplitterOff()
    {
        var folder = Path.Combine(Path.GetTempPath(), "racman-autosplit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "old.settings.json");
            File.WriteAllText(path, """{ "lastHost": "192.168.1.50" }""");

            var loaded = Settings.Load(path);
            Assert.False(loaded.Autosplit.Enabled);
            Assert.Equal(LiveSplitClient.DefaultHost, loaded.Autosplit.Host);
            Assert.Equal(LiveSplitClient.DefaultPort, loaded.Autosplit.Port);
            Assert.Empty(loaded.Autosplit.Games);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ALabelThisBuildNoLongerKnowsIsKeptInTheFile()
    {
        var settings = new Settings();
        var game = settings.Autosplit.For(GameId.Rac3);
        game.SetEvent("Something qwark retired", true);
        game.SetEvent("Planet entered", false);

        var json = JsonSerializer.Serialize(settings.Autosplit);
        var back = JsonSerializer.Deserialize<AutosplitSettings>(json)!;

        Assert.Contains("Something qwark retired", back.For(GameId.Rac3).Events.Keys);
        Assert.False(back.For(GameId.Rac3).EventEnabled("Planet entered", byDefault: true));
    }
}
