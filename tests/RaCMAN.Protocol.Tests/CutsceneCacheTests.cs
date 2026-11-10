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

    [Fact]
    public void EachStateHasItsLineAndNoneHasNothing()
    {
        Assert.Null(ManipsArea.CutsceneCacheLine(default));
        Assert.Equal("Cutscene cache: waiting for gameplay...",
            ManipsArea.CutsceneCacheLine(CutsceneCacheStatus.Parse(0x11000000u)));
        Assert.Equal("Cutscene cache: waiting for the game's level caching to start...",
            ManipsArea.CutsceneCacheLine(CutsceneCacheStatus.Parse(0x12000000u)));
        Assert.Equal("Caching cutscenes: Fastoon 7%", ManipsArea.CutsceneCacheLine(CutsceneCacheStatus.Parse(0x21070300u)));
        Assert.Equal("Cutscenes cached (12 s, 10.0 MiB/s)", ManipsArea.CutsceneCacheLine(CutsceneCacheStatus.Parse(0x30640C01u)));
        Assert.Equal("Cutscenes cached (16 s, 7.4 MiB/s)", ManipsArea.CutsceneCacheLine(CutsceneCacheStatus.Parse(0x304A1001u)));
        // Done with rate 0: all three planets were cached already, nothing was read.
        Assert.Equal("Cutscenes already cached (all three planets)", ManipsArea.CutsceneCacheLine(CutsceneCacheStatus.Parse(0x30000001u)));
        Assert.Equal("Cutscene caching stopped: the title screen came before it finished",
            ManipsArea.CutsceneCacheLine(CutsceneCacheStatus.Parse(0x41000501u)));
        Assert.Equal("Caching cutscenes: Cobalia 64%", ManipsArea.CutsceneCacheLine(CutsceneCacheStatus.Parse(0x23400200u)));

        // The too-late reasons did not stop the run: that planet was dropped and the rest cached.
        Assert.Equal("Cutscene cache: too late for Cobalia, the run got there first",
            ManipsArea.CutsceneCacheLine(CutsceneCacheStatus.Parse(0x49000501u)));
        Assert.Equal("Cutscene cache: too late for Stratus City, the run got there first",
            ManipsArea.CutsceneCacheLine(CutsceneCacheStatus.Parse(0x47000501u)));
        Assert.Equal("Cutscene cache: too late for Fastoon, the run got there first",
            ManipsArea.CutsceneCacheLine(CutsceneCacheStatus.Parse(0x48000501u)));
        Assert.Equal("Cutscene caching stopped: unknown reason",
            ManipsArea.CutsceneCacheLine(CutsceneCacheStatus.Parse(0x4A000501u)));
        Assert.Equal("Cutscene caching stopped: Disable cutscene caching is on",
            ManipsArea.CutsceneCacheLine(CutsceneCacheStatus.Parse(0x44000001u)));
        Assert.Null(ManipsArea.CutsceneCacheLine(CutsceneCacheStatus.Parse(0x90000000u)));
    }

    [Fact]
    public void TheGameSaysWhoseWordItIs()
    {
        // ToD's Cache cutscenes: the cache's line, green once done, red when it failed.
        var done = ManipsArea.StatusLine(SessionWith(GameId.Tod, 3, 0x30640C01u), Cache, GameId.Tod);
        Assert.Equal(("Cutscenes cached (12 s, 10.0 MiB/s)", ManipsArea.StatusTone.Good), done);
        var failed = ManipsArea.StatusLine(SessionWith(GameId.Tod, 3, 0x43000000u), Cache, GameId.Tod);
        Assert.Equal(ManipsArea.StatusTone.Bad, failed?.Tone);
        var late = ManipsArea.StatusLine(SessionWith(GameId.Tod, 3, 0x49002001u), Cache, GameId.Tod);
        Assert.Equal(("Cutscene cache: too late for Cobalia, the run got there first", ManipsArea.StatusTone.Bad), late);
        var busy = ManipsArea.StatusLine(SessionWith(GameId.Tod, 3, 0x21320000u), Cache, GameId.Tod);
        Assert.Equal(("Caching cutscenes: Fastoon 50%", ManipsArea.StatusTone.Busy), busy);
        Assert.Null(ManipsArea.StatusLine(SessionWith(GameId.Tod, 3, 0), Cache, GameId.Tod));

        // Another ToD action naming a readout has no layout this client knows: nothing.
        Assert.Null(ManipsArea.StatusLine(SessionWith(GameId.Tod, 3, 0x30640C01u), Cache with { Id = 22 }, GameId.Tod));

        // UYA's trophy word in the same readout is still read as the trophy reset's.
        var reset = new Feature(21, FeatureKind.Action, 5, 0, FeatureFlags.None, 12, 0, 0, "Reset trophies");
        var ready = ManipsArea.StatusLine(SessionWith(GameId.Rac3, 12, 0x20000D06u), reset, GameId.Rac3);
        Assert.Equal(("Trophies ready (13 s)", ManipsArea.StatusTone.Good), ready);
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
