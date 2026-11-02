using System.Diagnostics;
using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Level-flag names: ToD's come from the client's own data/levelflags/tod.json (LEVELFLAGS_NAMES is
/// retired in revision 1.29), the values from LEVELFLAGS_GET and LEVELFLAGS_BIT as before; the fake
/// console's ToD flags (one region for every planet, reset a planet at a time), and the named list
/// the Level flags panel draws from them. A game without a names file keeps the byte view. The fake
/// listens on loopback only.
/// </summary>
public class LevelFlagNamesTests
{
    /// <summary>The source tree's data/levelflags, or the copy the build put beside the test binary.</summary>
    public static string LevelFlagsDataFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "src", "RaCMAN.App", "data", "levelflags");
            if (Directory.Exists(candidate)) return candidate;
        }

        return Path.Combine(AppContext.BaseDirectory, "data", "levelflags");
    }

    /// <summary>ToD's names as the source tree's data file has them.</summary>
    public static LevelFlagNames TodNames()
    {
        var all = LevelFlagNameFiles.Load(LevelFlagsDataFolder(), out var problems);
        Assert.Empty(problems);
        return all[GameId.Tod];
    }

    [Fact]
    public void TheBitOpHasItsNumberAndTheNamesOpIsRetired()
    {
        Assert.Equal(0x0056, (int)Opcode.LevelFlagsBit);
        Assert.True(FakeQwarkServer.IsGameOp(Opcode.LevelFlagsBit));
        Assert.False(Enum.IsDefined(typeof(Opcode), (ushort)0x0057));
        Assert.False(Enum.IsDefined(typeof(Opcode), (ushort)0x00E8));
    }

    [Fact]
    public void ToDsDataFileHasEveryFlagInItsPlanetsGroup()
    {
        var names = TodNames();

        Assert.Equal(20, names.Groups.Length);
        Assert.Equal(292, names.Flags.Length);
        Assert.Equal(Enumerable.Range(0, 19).Select(i => (byte)i).Append(LevelFlagGroup.NoPlanet),
            names.Groups.Select(g => g.Planet));
        Assert.Equal(new LevelFlagGroup(0, "Kerwan"), names.Groups[0]);
        Assert.Equal(new LevelFlagGroup(1, "Cobalia"), names.Groups[1]);
        // Each planet's group has the name PLANET_LIST gives that planet (the ship's names, revision 1.29).
        Assert.Equal(FakeQwarkServer.TodPlanets, names.Groups.Take(19).Select(g => g.Name));
        Assert.Equal(new LevelFlagGroup(LevelFlagGroup.NoPlanet, "Other"), names.Groups[19]);

        // Flag n is bit n & 63 of big-endian word n >> 6 of LEVELFLAGS_GET's 40 bytes.
        var tachyon = Assert.Single(names.Flags, f => f.Number == 185);
        Assert.Equal("MOVIE_METROPOLIS_TACHYON", tachyon.Name);
        Assert.Equal(0, tachyon.Group);
        Assert.Equal(129, tachyon.Bit);
        Assert.Equal(16, tachyon.Offset);
        Assert.Equal(0x02, tachyon.Mask);

        Assert.All(names.Flags, f =>
        {
            Assert.True(f.Bit < 40 * 8, f.Name);
            Assert.Equal(FakeQwarkServer.TodFlagBit(f.Number), f.Bit);
            Assert.True(f.Group < names.Groups.Length, f.Name);
            Assert.False(string.IsNullOrEmpty(f.Name));
        });
        Assert.Equal(names.Flags.Length, names.Flags.Select(f => f.Bit).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 292).Select(n => (ushort)n), names.Flags.Select(f => f.Number).Order());

        // In the order qwark sent them: by group, then by number.
        Assert.Equal(names.Flags.OrderBy(f => f.Group).ThenBy(f => f.Number), names.Flags);
    }

    [Fact]
    public void OnlyToDHasANamesFile()
    {
        var all = LevelFlagNameFiles.Load(LevelFlagsDataFolder(), out _);
        Assert.Equal(new[] { GameId.Tod }, all.Keys);
        Assert.False(all.ContainsKey(GameId.Rac1));

        // The application's own lookup reads the copy the build put beside the binary.
        Assert.Equal(292, LevelFlagNameFiles.For(GameId.Tod)?.Flags.Length);
        Assert.Null(LevelFlagNameFiles.For(GameId.Rac1));
        Assert.Null(LevelFlagNameFiles.For(GameId.None));
    }

    [Fact]
    public void AMissingOrBrokenFileGivesNoNamesAndTheRestStillLoad()
    {
        var none = LevelFlagNameFiles.Load(Path.Combine(Path.GetTempPath(), "racman-no-such-levelflags-folder"), out var missing);
        Assert.Empty(none);
        Assert.Single(missing);

        string folder = Path.Combine(Path.GetTempPath(), $"racman-levelflags-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "a-broken.json"), "{ \"game\": 1, \"groups\": [");
            File.WriteAllText(Path.Combine(folder, "b-bad-group.json"),
                "{ \"game\": 2, \"groups\": [ { \"planet\": 0, \"name\": \"Oozla\" } ],"
                + " \"flags\": [ { \"number\": 1, \"group\": 3, \"bit\": 1, \"name\": \"X\" } ] }");
            File.WriteAllText(Path.Combine(folder, "c-good.json"),
                "{ \"game\": 3, \"groups\": [ { \"planet\": 255, \"name\": \"Other\" } ],"
                + " \"flags\": [ { \"number\": 7, \"group\": 0, \"bit\": 9, \"name\": \"SEVEN\" } ] }");

            var loaded = LevelFlagNameFiles.Load(folder, out var problems);
            Assert.Equal(new[] { GameId.Rac3 }, loaded.Keys);
            Assert.Equal(new LevelFlagName(9, 7, 0, "SEVEN"), Assert.Single(loaded[GameId.Rac3].Flags));
            Assert.Equal(2, problems.Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
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
        Assert.Equal("Group 27", TodNames().GroupName(27));
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

        var names = TodNames();
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

        // Cobalia's reset (qwark's, by planet) takes Cobalia's flags and leaves Metropolis's.
        await client.LevelFlagsBitAsync(0, grind.Bit, on: true);
        await client.LevelFlagsResetAsync(1);
        region = await client.LevelFlagsGetAsync(0);
        Assert.False(bridge.IsSetIn(region));
        Assert.True(grind.IsSetIn(region));
    }

    [Fact]
    public async Task TheRetiredNamesOpIsNotAnsweredAndBitsStillWorkOnAnyGame()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var client = await ConnectAsync(server);

        var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.RequestAsync((Opcode)0x0057));
        Assert.NotEqual(Status.Ok, refused.Status);

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
    public async Task TheClientTakesToDsNamesFromItsDataFileWithItsDescription()
    {
        using var server = new FakeQwarkServer();
        server.UseTod();
        server.Start();

        using var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.LevelFlagNames != null));
        Assert.Equal(20, state.LevelFlagNames!.Groups.Length);
        Assert.Equal(292, state.LevelFlagNames.Flags.Length);
        Assert.False(state.LevelFlagsUnsupported);

        // Nothing asked the console for them.
        Assert.DoesNotContain((Opcode)0x0057, server.RequestLog());
    }

    [Fact]
    public async Task AGameWithoutANamesFileKeepsTheByteView()
    {
        using var server = new FakeQwarkServer();
        server.Start();

        using var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0 && state.Planets.Length > 0));
        Assert.Equal(GameId.Rac1, state.DescribedGame);
        Assert.Null(state.LevelFlagNames);
        Assert.DoesNotContain((Opcode)0x0057, server.RequestLog());
    }

    [Fact]
    public void TheListGoesByGroupAndTheFilterMatchesNamesAndNumbers()
    {
        var names = TodNames();
        var groups = LevelFlagsPanel.GroupsInOrder(names);

        Assert.Equal(Enumerable.Range(0, 20), groups.Select(g => g.Index));
        Assert.Equal(names.Flags.Length, groups.Sum(g => g.Flags.Count));
        var cobalia = groups[1].Flags.Select(f => f.Number).ToArray();
        Assert.Contains((ushort)39, cobalia);
        Assert.Contains((ushort)186, cobalia);
        Assert.DoesNotContain((ushort)38, cobalia);
        Assert.Contains(groups[0].Flags, f => f.Number == 185);

        var bridge = names.Flags.Single(f => f.Number == 39);
        Assert.True(LevelFlagsPanel.Matches(bridge, string.Empty));
        Assert.True(LevelFlagsPanel.Matches(bridge, "bridge"));
        Assert.True(LevelFlagsPanel.Matches(bridge, "39"));
        Assert.False(LevelFlagsPanel.Matches(bridge, "3"));
        Assert.False(LevelFlagsPanel.Matches(bridge, "fastoon"));
        Assert.True(LevelFlagsPanel.Matches(new LevelFlagName(0x3B, 287, 19, string.Empty), "flag 287"));
    }
}
