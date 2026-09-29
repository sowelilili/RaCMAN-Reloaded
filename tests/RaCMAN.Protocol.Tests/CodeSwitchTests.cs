using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// qwark's code switches (build 50, protocol revision 1.17) on the client's side of the wire:
/// SWITCH_PATCH, flags.CODE_SWITCHES, what the fake console does with both, and the one rule every
/// panel greys a code-patching feature by. The patch file and the Connection panel's status are in
/// <see cref="Rpcs3PatchTests"/>, beside the savefile helper's, and the overlaps with mods in
/// <see cref="Rpcs3ModsTests"/>.
/// </summary>
public class CodeSwitchTests
{
    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync(bool rpcs3 = true)
    {
        var server = new FakeQwarkServer { Emulator = rpcs3, NoCodePatches = rpcs3 };
        server.Start();
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    // ================================================================ the wire

    [Fact]
    public void SwitchPatchIsOpcode0x0025AndCodeSwitchesIsBit4()
    {
        Assert.Equal(0x0025, (ushort)Opcode.SwitchPatch);
        Assert.Equal(0x10, (byte)SessionFlags.CodeSwitches);
    }

    [Theory]
    [InlineData(0x00, false, false)]
    [InlineData(0x06, false, true)]    // RPCS3 without the switches: the code-patching features are out
    [InlineData(0x16, true, false)]    // RPCS3 with them: they work
    [InlineData(0x10, true, false)]    // never sent by a console, and it would change nothing there
    [InlineData(0x1F, true, false)]
    public void TheSessionCarriesTheSwitchesBitOnItsOwn(byte flags, bool switches, bool unavailable)
    {
        var info = SessionInfo.Empty with { Flags = (SessionFlags)flags };
        var parsed = SessionInfo.Parse(info.ToBytes());

        Assert.Equal(switches, parsed.CodeSwitches);
        Assert.Equal(unavailable, parsed.CodeFeaturesUnavailable);
        Assert.Equal((flags & 0x04) != 0, parsed.CodePatchesUnsupported);
        Assert.Equal((flags & 0x08) != 0, parsed.CombosOff);
        Assert.Equal(flags, (byte)parsed.Flags);
    }

    [Fact]
    public async Task TheRequestIsEmptyAndTheReplyIsTheFakesSwitchesForTheGame()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var patch = await client.SwitchPatchAsync();

            Assert.Empty(server.LastPayload(Opcode.SwitchPatch)!);
            var expected = FakeQwarkServer.SwitchPatchFor(GameId.Rac1, server.Describe.Features);
            Assert.Equal(expected.Words, patch.Words);
            Assert.Equal(expected.Bytes, patch.Bytes);
            Assert.Equal(PatchReply.StampFor(patch.Words, patch.Bytes), patch.Stamp);
            Assert.Equal(1, server.SwitchPatchCount);
        }
    }

    [Fact]
    public async Task ATruncatedReplyIsAProtocolErrorNamingTheOp()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var whole = FakeQwarkServer.SwitchPatchFor(GameId.Rac4, server.Describe.Features).ToBytes();
            server.SwitchPatchPayload = whole[..^1];

            var cut = await Assert.ThrowsAsync<ProtocolException>(() => client.SwitchPatchAsync());
            Assert.Contains("SWITCH_PATCH", cut.Message);

            server.SwitchPatchPayload = whole[..(PatchReply.HeaderSize + 4)];
            await Assert.ThrowsAsync<ProtocolException>(() => client.SwitchPatchAsync());

            server.SwitchPatchPayload = whole;
            Assert.Equal(whole, (await client.SwitchPatchAsync()).ToBytes());
        }
    }

    [Fact]
    public async Task SwitchPatchIsRefusedOutsideAGameForAGameWithNoneAndByAnOldModule()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.CodeSwitchesSupported = false;
            var none = await Assert.ThrowsAsync<QwarkStatusException>(() => client.SwitchPatchAsync());
            Assert.Equal(Status.Unsupported, none.Status);
            Assert.Equal(Opcode.SwitchPatch, none.Opcode);

            server.CodeSwitchesSupported = true;
            server.Session = server.Session with { State = SessionState.Xmb };
            var xmb = await Assert.ThrowsAsync<QwarkStatusException>(() => client.SwitchPatchAsync());
            Assert.Equal(Status.NotIngame, xmb.Status);

            server.SwitchPatchUnknown = true;
            var old = await Assert.ThrowsAsync<QwarkStatusException>(() => client.SwitchPatchAsync());
            Assert.Equal(Status.UnknownOp, old.Status);
        }
    }

    [Fact]
    public void EveryGameGetsSwitchesOfItsOwnShapedLikeQwarks()
    {
        var features = new FakeQwarkServer().Describe.Features;
        int patching = features.Count(f => f.WritesCode);

        var rac1 = FakeQwarkServer.SwitchPatchFor(GameId.Rac1, features);
        var rac4 = FakeQwarkServer.SwitchPatchFor(GameId.Rac4, features);

        // Six trampoline words and one site word per feature, then one flag byte each, cleared.
        Assert.Equal(patching * 7, rac1.Words.Length);
        Assert.Equal(patching, rac1.Bytes.Length);
        Assert.All(rac1.Bytes, b => Assert.Equal(0, b.Value));
        Assert.Equal(0x48000000u, rac1.Words[^1].Word & 0xFC000003u);
        Assert.NotEqual(rac1.Words[0].Address, rac4.Words[0].Address);

        // The branch at the site lands on the trampoline.
        var site = rac1.Words[^1];
        Assert.Equal(rac1.Words[0].Address, site.Address + (site.Word & 0x03FFFFFCu));
    }

    // ================================================================ the features behind them

    [Fact]
    public async Task UnderRpcs3ACodePatchingToggleIsRefusedUntilTheSwitchesAreIn()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var jump = Array.Find(server.Describe.Features, f => f.WritesCode)!;

            var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.FeatureSetAsync(jump.Id, 1));
            Assert.Equal(Status.Unsupported, refused.Status);

            // A new boot with the switches applied: qwark-rpcs3 finds them and says so.
            uint generation = server.Session.Generation;
            FakeScript.Apply(server, "switchesin");
            Assert.True(server.CodeSwitches);
            Assert.Equal(generation + 1, server.Session.Generation);

            await client.FeatureSetAsync(jump.Id, 1);
            await client.FeatureSetAutoAsync(jump.Id, true);
            Assert.NotEqual(0ul, server.Session.ToggleState & (1UL << jump.Id));
            Assert.NotEqual(0ul, server.Session.ToggleAuto & (1UL << jump.Id));

            FakeScript.Apply(server, "switchesout");
            Assert.False(server.CodeSwitches);
            await Assert.ThrowsAsync<QwarkStatusException>(() => client.FeatureSetAsync(jump.Id, 0));
        }
    }

    [Fact]
    public void TheGamePanelGreysACodePatchingFeatureOnlyUnderRpcs3WithoutTheSwitches()
    {
        var features = new FakeQwarkServer().Describe.Features;
        var jump = Array.Find(features, f => f.WritesCode)!;
        var ammo = Array.Find(features, f => f.Kind == FeatureKind.Toggle && !f.WritesCode)!;

        var console = SessionInfo.Empty;
        var rpcs3 = SessionInfo.Empty with { Flags = SessionFlags.Emulator | SessionFlags.NoCodePatches };
        var switched = rpcs3 with { Flags = rpcs3.Flags | SessionFlags.CodeSwitches };

        Assert.False(GamePanel.CodeFeatureBlocked(jump, console));
        Assert.True(GamePanel.CodeFeatureBlocked(jump, rpcs3));
        Assert.False(GamePanel.CodeFeatureBlocked(jump, switched));

        // A data cheat is never greyed for this.
        Assert.False(GamePanel.CodeFeatureBlocked(ammo, rpcs3));

        // And the tooltip says where the switches come from, not that RPCS3 cannot do it.
        Assert.Contains("Install them on the Connection panel", Ui.NeedsQwarkPatches);
        Assert.Contains("restart", Ui.NeedsQwarkPatches);
    }

    [Fact]
    public async Task TheClientSeesTheSwitchesArriveAndTheFeatureComeBack()
    {
        using var server = new FakeQwarkServer { Emulator = true, NoCodePatches = true };
        server.Start();
        using var state = new AppState(new Settings { AutoReconnect = false });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0 && state.CodePatchesUnsupported));
        var jump = Array.Find(state.Describe.Features, f => f.WritesCode)!;
        Assert.True(GamePanel.CodeFeatureBlocked(jump, state.Session));

        FakeScript.Apply(server, "switchesin");
        Assert.True(await PumpAsync(state, () => state.CodeSwitches));
        Assert.False(GamePanel.CodeFeatureBlocked(jump, state.Session));

        // The description is the same: the feature is still flagged as patching code.
        Assert.True(jump.WritesCode);
    }

    private static async Task<bool> PumpAsync(AppState state, Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            state.Tick(1f / 60f);
            if (condition()) return true;
            await Task.Delay(16);
        }

        state.Tick(1f / 60f);
        return condition();
    }
}
