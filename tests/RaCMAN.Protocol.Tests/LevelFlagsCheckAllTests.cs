using System.Buffers.Binary;
using System.Diagnostics;

using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The Level flags panel's "Check all": LEVELFLAGS_SET of FF for every byte LEVELFLAGS_GET
/// reported, one request per byte, from one task. It writes the planet it was asked about and no
/// other, it stops at the first refusal and says so, and it re-reads either way.
/// </summary>
public class LevelFlagsCheckAllTests
{
    private const byte Planet = 2;

    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync()
    {
        var server = new FakeQwarkServer();
        server.Start();

        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    private static async Task<AppState> ConnectedStateAsync(FakeQwarkServer server)
    {
        var state = new AppState(new Settings { AutoReconnect = false, ObsPadEnabled = false });
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

    /// <summary>The offset a LEVELFLAGS_SET payload carries: `u8 planet, u8 value, u16 offset`.</summary>
    private static ushort OffsetOf(byte[] payload) => BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2));

    private static int SetCount(FakeQwarkServer server) =>
        server.RequestLog().Count(opcode => opcode == Opcode.LevelFlagsSet);

    [Fact]
    public async Task CheckAllSetsEveryByteOfThePlanetAndTouchesNoOther()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var others = server.LevelFlags.Where(p => p.Key != Planet)
                .ToDictionary(p => p.Key, p => (byte[])p.Value.Clone());

            var before = await client.LevelFlagsGetAsync(Planet);
            Assert.Equal(64, before.Length);
            Assert.Contains(before, b => b != LevelFlagsPanel.AllBits);

            server.ClearRequestLog();
            await LevelFlagsPanel.CheckAllAsync(client, Planet, before.Length);

            // One request per byte, the last of them at the last offset, with every bit set.
            Assert.Equal(before.Length, SetCount(server));
            var last = server.LastPayload(Opcode.LevelFlagsSet)!;
            Assert.Equal(Planet, last[0]);
            Assert.Equal(LevelFlagsPanel.AllBits, last[1]);
            Assert.Equal(before.Length - 1, OffsetOf(last));

            var after = await client.LevelFlagsGetAsync(Planet);
            Assert.Equal(before.Length, after.Length);
            Assert.All(after, b => Assert.Equal(LevelFlagsPanel.AllBits, b));

            foreach (var (planet, flags) in others) Assert.Equal(flags, server.LevelFlags[planet]);
        }
    }

    [Fact]
    public async Task ARefusalPartWayStopsTheLoopAndIsThrown()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            int reported = (await client.LevelFlagsGetAsync(Planet)).Length;

            // The region the console answers for is now shorter than the one the table was read
            // from, so the write at offset 10 is out of range.
            const int shorter = 10;
            server.LevelFlags[Planet] = new byte[shorter];
            server.ClearRequestLog();

            var refused = await Assert.ThrowsAsync<QwarkStatusException>(
                () => LevelFlagsPanel.CheckAllAsync(client, Planet, reported));
            Assert.Equal(Status.BadArg, refused.Status);
            Assert.Equal(Opcode.LevelFlagsSet, refused.Opcode);

            // Everything before the refusal was written; nothing was sent after it.
            Assert.Equal(shorter + 1, SetCount(server));
            Assert.Equal(shorter, OffsetOf(server.LastPayload(Opcode.LevelFlagsSet)!));
            Assert.All(server.LevelFlags[Planet], b => Assert.Equal(LevelFlagsPanel.AllBits, b));
        }
    }

    [Fact]
    public async Task ACheckAllThatFinishesToastsOnceAndReReads()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        int rereads = 0;
        string done = LevelFlagsPanel.CheckAllToast(Planet);
        LevelFlagsPanel.RunCheckAll(state, Planet, 64, () => rereads++);

        Assert.True(await PumpAsync(state, () => rereads == 1 && state.Toasts.Any(t => t.Text == done)));
        Assert.Equal("Level flags all set on planet 2", done);

        var toast = Assert.Single(state.Toasts, t => t.Text == done);
        Assert.Equal(ToastKind.Success, toast.Kind);
        Assert.DoesNotContain(state.Toasts, t => t.Kind == ToastKind.Error);
        Assert.All(server.LevelFlags[Planet], b => Assert.Equal(LevelFlagsPanel.AllBits, b));
    }

    [Fact]
    public async Task ACheckAllThatIsRefusedSaysSoAndStillReReads()
    {
        using var server = new FakeQwarkServer();

        // Shortened before anything connects, so the change cannot race the client's own reads.
        const int shorter = 10;
        server.LevelFlags[Planet] = new byte[shorter];
        server.Start();
        using var state = await ConnectedStateAsync(server);

        int rereads = 0;
        LevelFlagsPanel.RunCheckAll(state, Planet, 64, () => rereads++);

        Assert.True(await PumpAsync(state, () => rereads == 1 && state.Toasts.Any(t => t.Kind == ToastKind.Error)));

        var error = Assert.Single(state.Toasts, t => t.Kind == ToastKind.Error);
        Assert.Contains(nameof(Status.BadArg), error.Text);
        Assert.DoesNotContain(state.Toasts, t => t.Text == LevelFlagsPanel.CheckAllToast(Planet));
        Assert.All(server.LevelFlags[Planet], b => Assert.Equal(LevelFlagsPanel.AllBits, b));
    }
}
