using System.Diagnostics;
using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// DEBUG_TEXT (qwark revision 1.29, PROTOCOL.md 5.16): a game's read-only debug pages, ToD's level
/// cache. The wire layouts, the fake console's answers, and what the client asks for by itself:
/// the list when the game is described, never a SLOW page.
/// </summary>
public class DebugPagesTests
{
    [Fact]
    public void TheOpHasItsNumberAndIsAGameOp()
    {
        Assert.Equal(0x00E8, (int)Opcode.DebugText);
        Assert.True(FakeQwarkServer.IsGameOp(Opcode.DebugText));
    }

    [Fact]
    public void TheListIsACountThenAFlagAndATwentyThreeByteTitleEach()
    {
        var wire = new byte[1 + 2 * 24];
        wire[0] = 2;
        wire[1] = 0;
        "Now"u8.CopyTo(wire.AsSpan(2));
        wire[25] = 1;
        "Cache"u8.CopyTo(wire.AsSpan(26));

        var pages = DebugText.ParseList(wire);
        Assert.Equal(new[] { new DebugPage(0, false, "Now"), new DebugPage(1, true, "Cache") }, pages);
        Assert.Equal(wire, DebugText.ListBytes(pages));

        Assert.Throws<ProtocolException>(() => DebugText.ParseList(wire.AsSpan(0, 30)));
        Assert.Throws<ProtocolException>(() => DebugText.ParseList(ReadOnlySpan<byte>.Empty));
        Assert.Empty(DebugText.ParseList(new byte[] { 0 }));
    }

    [Fact]
    public void APageIsItsLengthAPadAndTheText()
    {
        byte[] wire = { 0x00, 0x05, 0x00, 0x00, (byte)'a', (byte)'\n', (byte)'b', (byte)'c', (byte)'\n' };
        Assert.Equal("a\nbc\n", DebugText.ParseText(wire));
        Assert.Equal(wire, DebugText.TextBytes("a\nbc\n"));
        Assert.Throws<ProtocolException>(() => DebugText.ParseText(wire.AsSpan(0, 8)));
        Assert.Throws<ProtocolException>(() => DebugText.ParseText(wire.AsSpan(0, 3)));
        Assert.Equal(string.Empty, DebugText.ParseText(new byte[4]));
    }

    private static async Task<QwarkClient> ConnectAsync(FakeQwarkServer server)
    {
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return client;
    }

    [Fact]
    public async Task ToDListsItsPagesAndAnswersEach()
    {
        using var server = new FakeQwarkServer();
        server.UseTod();
        server.Start();
        using var client = await ConnectAsync(server);

        var pages = await client.DebugPagesAsync();
        Assert.Equal(new[] { "Now", "Cache" }, pages.Select(p => p.Title));
        Assert.False(pages[0].Slow);
        Assert.True(pages[1].Slow);
        Assert.Equal(server.DebugTexts[0], await client.DebugTextAsync(0));
        Assert.Equal(server.DebugTexts[1], await client.DebugTextAsync(1));
        Assert.Equal(Status.BadArg, (await Assert.ThrowsAsync<QwarkStatusException>(() => client.DebugTextAsync(2))).Status);
    }

    [Fact]
    public async Task AnotherGameAnswersUnsupported()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var client = await ConnectAsync(server);

        Assert.Equal(Status.Unsupported, (await Assert.ThrowsAsync<QwarkStatusException>(() => client.DebugPagesAsync())).Status);
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
    public async Task TheClientReadsTheListButNoSlowPageByItself()
    {
        using var server = new FakeQwarkServer();
        server.UseTod();
        server.Start();

        using var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.DebugPages.Count == 2));
        Assert.True(state.HasDebugBox);
        Assert.Contains(GameLayout.DebugSection, GamePanel.SubPagesWithContent(state));
        Assert.Equal(new byte[] { DebugText.ListRequest }, server.DebugTextRequests.Distinct());

        // A page is read only when asked for; its text and when are kept for the box.
        state.RefreshDebugText(1);
        Assert.True(await PumpAsync(state, () => state.DebugTextOf(1)?.Text is not null));
        Assert.Equal(server.DebugTexts[1], state.DebugTextOf(1)!.Text);
        Assert.Null(state.DebugTextOf(1)!.Refusal);
        Assert.StartsWith("Read at ", DebugPagesBox.StatusLine(state.DebugTextOf(1), slow: true));

        // A refusal is kept beside the last text, never swallowed.
        server.DebugTextUnknown = true;
        state.RefreshDebugText(1);
        Assert.True(await PumpAsync(state, () => state.DebugTextOf(1)?.Refusal is not null));
        Assert.Equal(Status.UnknownOp, state.DebugTextOf(1)!.Refusal);
        Assert.Equal(server.DebugTexts[1], state.DebugTextOf(1)!.Text);
        Assert.Contains("UNKNOWN_OP", DebugPagesBox.StatusLine(state.DebugTextOf(1), slow: true));
        Assert.Contains("older", DebugPagesBox.StatusLine(state.DebugTextOf(1), slow: true));
        Assert.Contains("waits for Read", DebugPagesBox.StatusLine(null, slow: true));
        Assert.Null(DebugPagesBox.StatusLine(null, slow: false));
    }

    [Fact]
    public async Task AModuleWithoutTheOpHasNoPages()
    {
        using var server = new FakeQwarkServer { DebugTextUnknown = true };
        server.UseTod();
        server.Start();

        using var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.HasLua && server.RequestLog().Contains(Opcode.DebugText)));
        await Task.Delay(100);
        state.Tick(1f / 60f);
        Assert.Empty(state.DebugPages);
        Assert.True(state.HasDebugBox);   // the Lua console still is
    }
}
