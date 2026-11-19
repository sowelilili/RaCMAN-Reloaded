using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Tools of Destruction's cutscene cache (qwark revision 1.29, PROTOCOL.md section 5.17): Cache
/// cutscenes' status word read field by field, the one line under its button for each state, the
/// word told apart from UYA's trophy word by the game, and the three rows on ToD's Manips page. qwark
/// caches three planets' sound, Cobalia first, then Stratus City, then Fastoon.
/// </summary>
public class CutsceneCacheTests
{
    private static readonly Feature Cache =
        new(19, FeatureKind.Action, 0, 0, FeatureFlags.None, 3, 0, 0, "Cache cutscenes");

    private static SessionInfo SessionWith(GameId game, int index, uint word)
    {
        var readouts = new uint[SessionInfo.ReadoutCount];
        readouts[index] = word;
        return SessionInfo.Empty with { State = SessionState.Ingame, Game = game, Readout = readouts };
    }

    [Fact]
    public void TheWordIsLaidOutAsTheContractSpellsIt()
    {
        // Caching Stratus City, 42%, 17 s in, two runs ended before.
        var caching = CutsceneCacheStatus.Parse(0x222A1102u);
        Assert.Equal(CutsceneCacheState.Caching, caching.State);
        Assert.Equal("Stratus City", caching.Planet);
        Assert.Equal(42, caching.Percent);
        Assert.Equal(17, caching.Seconds);
        Assert.Equal(2, caching.Seq);
        Assert.Equal(CutsceneCacheFailure.None, caching.Failure);

        Assert.Equal(CutsceneCacheWait.Warmer, CutsceneCacheStatus.Parse(0x12000000u).Wait);
        Assert.Equal(CutsceneCacheFailure.Disabled, CutsceneCacheStatus.Parse(0x44000003u).Failure);
        Assert.Null(CutsceneCacheStatus.Parse(0x31640A01u).Planet);
        Assert.Equal(CutsceneCacheState.None, CutsceneCacheStatus.Parse(0).State);

        var round = new CutsceneCacheStatus(CutsceneCacheState.Done, 0, 100, 255, 9);
        Assert.Equal(round, CutsceneCacheStatus.Parse(round.ToWord()));

        // Done: bits 16..23 are the read rate in tenths of a MiB/s, 0 for nothing read.
        Assert.Equal(7.4, CutsceneCacheStatus.Parse(0x304A1001u).MiBPerSecond);
        Assert.False(CutsceneCacheStatus.Parse(0x304A1001u).AlreadyCached);
        Assert.Null(CutsceneCacheStatus.Parse(0x30000001u).MiBPerSecond);
        Assert.True(CutsceneCacheStatus.Parse(0x30000001u).AlreadyCached);
        Assert.Null(CutsceneCacheStatus.Parse(0x222A1102u).MiBPerSecond);
        Assert.Equal(CutsceneCacheFailure.TooLateStratusCity, CutsceneCacheStatus.Parse(0x47000001u).Failure);
        Assert.Equal(CutsceneCacheFailure.TooLateFastoon, CutsceneCacheStatus.Parse(0x48000001u).Failure);
        Assert.Equal(CutsceneCacheFailure.TooLateCobalia, CutsceneCacheStatus.Parse(0x49000001u).Failure);

        // Caching detail 3 is Cobalia, the first of the three; 1 Fastoon, 2 Stratus City.
        Assert.Equal("Cobalia", CutsceneCacheStatus.Parse(0x23140500u).Planet);
        Assert.Equal("Fastoon", CutsceneCacheStatus.Parse(0x21140500u).Planet);
        Assert.Null(CutsceneCacheStatus.Parse(0x24140500u).Planet);
    }

    /// <summary>
    /// Qwark revision 1.34 (D19): the line under Cache cutscenes is the module's (ACTION_STATUS), for
    /// the word the readout carries; the client keeps no layout of ToD's word. It asks once a word,
    /// draws what it is told, asks again as the word moves, and against a module that does not know
    /// the op (UNKNOWN_OP, before build 59) draws nothing and stops asking.
    /// </summary>
    [Fact]
    public async Task TheLineIsTheModulesForTheWordTheReadoutCarries()
    {
        using var server = new FakeQwarkServer();
        server.UseTod();
        server.Start();
        using var state = await ConnectedStateAsync(server);
        Assert.True(await PumpAsync(state, () => state.Describe.Game == GameId.Tod));
        var cache = state.Describe.Features.Single(f => f.Label == "Cache cutscenes");

        SetWord(server, 0x30640C01u);
        Assert.True(await PumpAsync(state, () => state.ActionLine(cache) is not null
                                                             && state.Session.ReadoutAt(3) == 0x30640C01u));
        Assert.Equal(new ActionStatusLine(StatusTone.Good, "Cutscenes cached (12 s, 10.0 MiB/s)"), state.ActionLine(cache));
        int asks = server.ActionStatusAsks;
        await PumpForAsync(state, 300, () => state.ActionLine(cache));
        Assert.Equal(asks, server.ActionStatusAsks);

        SetWord(server, 0x23400200u);
        Assert.True(await PumpAsync(state, () => state.ActionLine(cache)?.Tone == StatusTone.Busy));
        Assert.Equal("Caching cutscenes: Cobalia 64%", state.ActionLine(cache)!.Text);

        SetWord(server, 0);
        Assert.True(await PumpAsync(state, () => state.Session.ReadoutAt(3) == 0 && server.ActionStatusAsks > asks + 1,
            () => state.ActionLine(cache)));
        await PumpForAsync(state, 100, () => state.ActionLine(cache));
        Assert.Null(state.ActionLine(cache));
    }

    [Fact]
    public async Task AnOlderModuleDrawsNoLineAndIsAskedOnce()
    {
        using var server = new FakeQwarkServer();
        server.UseTod();
        server.Describe = FakeQwarkServer.TodDescribe58();
        server.ActionStatusUnknown = true;
        server.Start();
        using var state = await ConnectedStateAsync(server);
        Assert.True(await PumpAsync(state, () => state.Describe.Game == GameId.Tod));
        var cache = state.Describe.Features.Single(f => f.Label == "Cache cutscenes");

        SetWord(server, 0x30640C01u);
        Assert.True(await PumpAsync(state, () => state.Session.ReadoutAt(3) == 0x30640C01u
                                                             && server.ActionStatusAsks == 1, () => state.ActionLine(cache)));
        SetWord(server, 0x23400200u);
        await PumpForAsync(state, 300, () => state.ActionLine(cache));
        Assert.Null(state.ActionLine(cache));
        Assert.Equal(1, server.ActionStatusAsks);
    }

    private static async Task<AppState> ConnectedStateAsync(FakeQwarkServer server)
    {
        var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);
        return state;
    }

    /// <summary>Frames until the condition holds; rame is what a panel draws each frame.</summary>
    private static async Task<bool> PumpAsync(AppState state, Func<bool> condition, Action? frame = null, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            state.Tick(1f / 60f);
            frame?.Invoke();
            if (condition()) return true;
            await Task.Delay(16);
        }

        state.Tick(1f / 60f);
        return condition();
    }

    private static async Task PumpForAsync(AppState state, int milliseconds, Action? frame = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            state.Tick(1f / 60f);
            frame?.Invoke();
            await Task.Delay(16);
        }
    }

    private static void SetWord(FakeQwarkServer server, uint word)
    {
        var readouts = (uint[])server.Session.Readout.Clone();
        readouts[3] = word;
        server.Session = server.Session with { Readout = readouts };
    }
    [Fact]
    public void TheThreeRowsAreOnToDsManipsPageUnderOneHeading()
    {
        var d = FakeQwarkServer.TodDescribe();
        string title = FakeQwarkServer.TodTitleId;
        var sections = GamePanel.SectionsFor(title, GameId.Tod, d);
        var manips = sections[GameLayout.ManipsSection].Select(f => f.Label).ToArray();
        foreach (var label in new[] { "Remove cutscenes", "Cache cutscenes", "Cache cutscenes on reset", "Disable cutscene caching" })
        {
            Assert.Contains(label, manips);
            Assert.DoesNotContain(sections["Cheats"], f => f.Label == label);
        }

        var blocks = GameLayout.Blocks(title, GameId.Tod, GameLayout.ManipsSection, sections[GameLayout.ManipsSection]);
        var cache = blocks.Single(b => b.Heading == "Cutscene cache");
        Assert.Equal(new[] { "Cache cutscenes", "Cache cutscenes on reset", "Disable cutscene caching" },
            cache.Features.Select(f => f.Label));
        Assert.Contains(blocks, b => b.Heading is null && b.Features.Any(f => f.Label == "Remove cutscenes"));

        // The action names its status readout; the two toggles are not LIVE, so they have "on boot".
        var row = d.Features.Single(f => f.Label == "Cache cutscenes");
        Assert.Equal((byte?)3, row.StatusReadout);
        Assert.Equal("Cutscene cache", d.Readouts[3]);
        Assert.All(d.Features.Where(f => f.Label.Contains("cutscene caching") || f.Label.EndsWith("on reset")),
            f => Assert.False(f.IsLive));
    }
}
