using System.Diagnostics;
using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Revision 1.28 (pending): LEVELFLAGS_NAMES and LEVELFLAGS_BIT, the layout, the fake console's ToD
/// flags (one region for every planet, named and grouped by planet), and the named list the Level
/// flags panel draws from them. The fake listens on loopback only.
/// </summary>
public class LevelFlagNamesTests
{
    [Fact]
    public void TheTwoOpsHaveTheirNumbersAndAreGameOps()
    {
        Assert.Equal(0x0056, (int)Opcode.LevelFlagsBit);
        Assert.Equal(0x0057, (int)Opcode.LevelFlagsNames);
        Assert.True(FakeQwarkServer.IsGameOp(Opcode.LevelFlagsBit));
        Assert.True(FakeQwarkServer.IsGameOp(Opcode.LevelFlagsNames));
    }

    [Fact]
    public void TheReplyIsGroupsThenLengthPrefixedNames()
    {
        var names = new LevelFlagNames(
            new[] { new LevelFlagGroup(1, "Cobalia"), new LevelFlagGroup(LevelFlagGroup.NoPlanet, "Other") },
            new[] { new LevelFlagName(31, 39, 0, "LVL_COBALIA_BRIDGE_EXTENDED"), new LevelFlagName(0x3B, 287, 1, string.Empty) });

        var wire = names.ToBytes();
        Assert.Equal(1 + 2 * 24 + 2 + (6 + 27) + 6, wire.Length);
        Assert.Equal(2, wire[0]);
        Assert.Equal(1, wire[1]);                       // the first group's planet
        Assert.Equal((byte)'C', wire[2]);
        Assert.Equal(0xFF, wire[1 + 24]);               // Other is no planet's
        Assert.Equal(new byte[] { 0, 2 }, wire[49..51]);
        Assert.Equal(new byte[] { 0, 31, 0, 39, 0, 27 }, wire[51..57]);

        var back = LevelFlagNames.Parse(wire);
        Assert.Equal(names.Groups, back.Groups);
        Assert.Equal(names.Flags, back.Flags);
        Assert.True(back.Groups[0].IsPlanet);
        Assert.False(back.Groups[1].IsPlanet);
        Assert.Equal("Group 7", back.GroupName(7));

        Assert.Throws<ProtocolException>(() => LevelFlagNames.Parse(wire.AsSpan(0, wire.Length - 1)));
    }

    [Fact]
    public void ABitIsAByteAndAMaskOfTheRegion()
    {
        var flag = new LevelFlagName(31, 39, 1, "LVL_COBALIA_BRIDGE_EXTENDED");
        Assert.Equal(3, flag.Offset);
        Assert.Equal(0x80, flag.Mask);

        var region = new byte[40];
        Assert.False(flag.IsSetIn(region));
        region[3] = 0x80;
        Assert.True(flag.IsSetIn(region));
        Assert.False(flag.IsSetIn(region.AsSpan(0, 3)));

        Assert.Equal("Flag 287", new LevelFlagName(0x3B, 287, 19, string.Empty).Label);
        Assert.Equal("RYNO_HAS1", new LevelFlagName(0, 33, 19, "RYNO_HAS1").Label);
    }

    private static async Task<QwarkClient> ConnectAsync(FakeQwarkServer server)
    {
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return client;
    }

    [Fact]
    public async Task ToDsFlagsAreOneRegionSetABitAtATimeAndResetAPlanetAtATime()
    {
        using var server = new FakeQwarkServer();
        server.UseTod();
        server.Start();
        using var client = await ConnectAsync(server);

        var names = await client.LevelFlagNamesAsync();
        Assert.Equal(20, names.Groups.Length);
        Assert.Equal(new LevelFlagGroup(0, "Metropolis"), names.Groups[0]);
        Assert.Equal(new LevelFlagGroup(LevelFlagGroup.NoPlanet, "Other"), names.Groups[19]);
        var bridge = Assert.Single(names.Flags, f => f.Number == 39);
        Assert.Equal("LVL_COBALIA_BRIDGE_EXTENDED", bridge.Name);

        var region = await client.LevelFlagsGetAsync(0);
        Assert.Equal(40, region.Length);
        Assert.True(bridge.IsSetIn(region));

        // One bit, through the console: the byte's other bits stay, and every planet reads the same table.
        var grind = Assert.Single(names.Flags, f => f.Number == 38);
        await client.LevelFlagsBitAsync(0, grind.Bit, on: true);
        region = await client.LevelFlagsGetAsync(5);
        Assert.True(grind.IsSetIn(region));
        Assert.True(bridge.IsSetIn(region));
        await client.LevelFlagsBitAsync(0, grind.Bit, on: false);
        Assert.False(grind.IsSetIn(await client.LevelFlagsGetAsync(0)));

        var past = await Assert.ThrowsAsync<QwarkStatusException>(() => client.LevelFlagsBitAsync(0, 40 * 8, on: true));
        Assert.Equal(Status.BadArg, past.Status);

        // Cobalia's reset takes Cobalia's flags and leaves Metropolis's.
        await client.LevelFlagsBitAsync(0, grind.Bit, on: true);
        await client.LevelFlagsResetAsync(1);
        region = await client.LevelFlagsGetAsync(0);
        Assert.False(bridge.IsSetIn(region));
        Assert.True(grind.IsSetIn(region));
    }

    [Fact]
    public async Task AGameWhoseFlagsHaveNoNamesAnswersUnsupported()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var client = await ConnectAsync(server);

        var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.LevelFlagNamesAsync());
        Assert.Equal(Status.Unsupported, refused.Status);

        // LEVELFLAGS_BIT works on any game's region.
        await client.LevelFlagsBitAsync(2, 5 * 8 + 1, on: false);
        Assert.Equal(server.LevelFlags[2][5], (await client.LevelFlagsGetAsync(2))[5]);
        Assert.Equal(0, server.LevelFlags[2][5] & 0x02);
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

    [Fact]
    public async Task TheClientReadsToDsNamesWithItsDescription()
    {
        using var server = new FakeQwarkServer();
        server.UseTod();
        server.Start();

        using var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.LevelFlagNames != null));
        Assert.Equal(20, state.LevelFlagNames!.Groups.Length);
        Assert.False(state.LevelFlagsUnsupported);
    }

    [Fact]
    public async Task AClassicGameKeepsTheByteView()
    {
        using var server = new FakeQwarkServer();
        server.Start();

        using var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => server.RequestLog().Contains(Opcode.LevelFlagsNames)));
        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0));
        Assert.Null(state.LevelFlagNames);
    }

    [Fact]
    public void TheListGoesByGroupAndTheFilterMatchesNamesAndNumbers()
    {
        var names = FakeQwarkServer.TodLevelFlagNames();
        var groups = LevelFlagsPanel.GroupsInOrder(names);

        Assert.Equal(new[] { 0, 1, 2, 19 }, groups.Select(g => g.Index));
        Assert.Equal(new ushort[] { 39, 40, 186 }, groups[1].Flags.Select(f => f.Number));
        Assert.Equal(names.Flags.Length, groups.Sum(g => g.Flags.Count));

        var bridge = names.Flags.Single(f => f.Number == 39);
        Assert.True(LevelFlagsPanel.Matches(bridge, string.Empty));
        Assert.True(LevelFlagsPanel.Matches(bridge, "bridge"));
        Assert.True(LevelFlagsPanel.Matches(bridge, "39"));
        Assert.False(LevelFlagsPanel.Matches(bridge, "3"));
        Assert.False(LevelFlagsPanel.Matches(bridge, "fastoon"));
        Assert.True(LevelFlagsPanel.Matches(names.Flags.Single(f => f.Number == 287), "flag 287"));
    }
}
