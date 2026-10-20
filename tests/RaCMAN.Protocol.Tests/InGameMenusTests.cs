using System.Diagnostics;

using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Revision 1.23 (qwark build 55), the two things the Combos panel gained: the In-game menus box,
/// which is MENU_HELP and flags bit6, and the fast-loads combo, action 6.
/// </summary>
public class InGameMenusTests
{
    private const uint CaptureMask = (uint)PadButton.Select | (uint)PadButton.Triangle;

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

    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync()
    {
        var server = new FakeQwarkServer();
        server.Start();

        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    // ------------------------------------------------------------------ MENU_HELP

    /// <summary>
    /// MENU_HELP is `u8 on` at 0x0092 and nothing comes back: the answer is flags bit6, on the
    /// GET_STATE snapshot and on the telemetry the panel draws from.
    /// </summary>
    [Fact]
    public async Task MenuHelpTurnsTheHelpRowOffAndOnAgain()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            Assert.Equal(0x0092, (int)Opcode.MenuHelp);
            Assert.Equal(0x40, (int)SessionFlags.MenuHelpOff);
            Assert.True(server.MenuHelp);
            Assert.False((await client.GetStateAsync()).Session.MenuHelpOff);

            await client.MenuHelpAsync(false);
            Assert.False(server.MenuHelp);
            Assert.True((await client.GetStateAsync()).Session.MenuHelpOff);
            Assert.True(await WaitFor(() =>
                client.LatestTelemetry is { Session.MenuHelpOff: true } && !client.TelemetryViaTcp));

            await client.MenuHelpAsync(true);
            Assert.True(server.MenuHelp);
            Assert.True(await WaitFor(() =>
                client.LatestTelemetry is { Session.MenuHelpOff: false } && !client.TelemetryViaTcp));
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public async Task MenuHelpRefusesAByteThatIsNeitherOffNorOn(byte value)
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.RequestAsync(Opcode.MenuHelp, new[] { value }));

            Assert.Equal(Status.BadArg, refused.Status);
            Assert.True(server.MenuHelp);
        }
    }

    /// <summary>RPCS3 has no console menu, so qwark-rpcs3 refuses the setting rather than store it.</summary>
    [Fact]
    public async Task MenuHelpIsUnsupportedUnderRpcs3()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.Emulator = true;
            server.NoCodePatches = true;

            var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.MenuHelpAsync(false));

            Assert.Equal(Status.Unsupported, refused.Status);
            Assert.True(server.MenuHelp);
        }
    }

    /// <summary>
    /// The box draws itself from the session alone: hidden against a module before build 55, greyed
    /// under RPCS3, and saying in words which way the console has it.
    /// </summary>
    [Fact]
    public void TheBoxIsGatedOnTheBuildAndGreyedUnderRpcs3()
    {
        Assert.False(CombosPanel.ShowsInGameMenus(54));
        Assert.True(CombosPanel.ShowsInGameMenus(55));
        Assert.True(CombosPanel.ShowsInGameMenus(QwarkClient.ExpectedQwarkBuild));

        var console = SessionInfo.Empty with { QwarkVersion = 55 };
        var off = console with { Flags = SessionFlags.MenuHelpOff };
        var rpcs3 = console with { Flags = SessionFlags.Emulator | SessionFlags.NoCodePatches };

        Assert.False(CombosPanel.InGameMenusBlocked(console));
        Assert.True(CombosPanel.InGameMenusBlocked(rpcs3));
        Assert.Equal("Trainer replaces Help in the pause menu", CombosPanel.InGameMenusStatus(console));
        Assert.Equal("the game's default pause menu", CombosPanel.InGameMenusStatus(off));
        Assert.Equal("not under RPCS3", CombosPanel.InGameMenusStatus(rpcs3));

        var parsed = SessionInfo.Parse(off.ToBytes());
        Assert.True(parsed.MenuHelpOff);
        Assert.False(parsed.CombosOff);
        Assert.False(SessionInfo.Parse(console.ToBytes()).MenuHelpOff);
    }

    // ------------------------------------------------------------------ the fast-loads combo

    [Fact]
    public async Task TheFastLoadsComboIsTheSeventhAndHasNoMaskOutOfTheBox()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            Assert.Equal(6, (int)ComboAction.ToggleFastLoads);

            var before = await client.ComboListAsync();
            Assert.DoesNotContain(before, c => c.Action == ComboAction.ToggleFastLoads && c.Mask != 0);

            await client.ComboSetAsync(ComboAction.ToggleFastLoads, CaptureMask);
            var combos = await client.ComboListAsync();
            Assert.Contains(new ComboEntry(ComboAction.ToggleFastLoads, CaptureMask), combos);

            var refused = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.ComboSetAsync((ComboAction)7, CaptureMask));
            Assert.Equal(Status.BadArg, refused.Status);
        }
    }

    /// <summary>
    /// The hex of a combo's bit-field goes after its buttons only with debug information on: the
    /// console menu's six-button default, with the hex after it, was wider than its column.
    /// </summary>
    [Fact]
    public void TheHexOfAComboShowsOnlyWithDebugInformation()
    {
        uint menu = ComboDefaults.OpenConsoleMenu;
        string buttons = PadButtons.Describe(menu);

        Assert.Equal(buttons, CombosPanel.MaskText(menu, debug: false));
        Assert.Equal($"{buttons}  (0x{menu:X})", CombosPanel.MaskText(menu, debug: true));
        Assert.DoesNotContain("0x", CombosPanel.MaskText(menu, debug: false));

        // A combo that is off says so, with or without the debug detail.
        Assert.Equal("disabled", CombosPanel.MaskText(0, debug: false));
        Assert.Equal("disabled", CombosPanel.MaskText(0, debug: true));
    }

    [Fact]
    public void TheRowIsGatedOnTheBuildAndGreyedWhereTheBoxIs()
    {
        Assert.Equal("Toggle fast loads", CombosPanel.Label(ComboAction.ToggleFastLoads));
        Assert.Null(CombosPanel.Tooltip(ComboAction.ToggleFastLoads));
        Assert.NotNull(CombosPanel.Tooltip(ComboAction.OpenConsoleMenu));
        Assert.False(CombosPanel.ShowsRow(ComboAction.ToggleFastLoads, 54));
        Assert.True(CombosPanel.ShowsRow(ComboAction.ToggleFastLoads, 55));
        Assert.True(CombosPanel.ShowsRow(ComboAction.OpenConsoleMenu, 54));

        var console = SessionInfo.Empty with { QwarkVersion = 55 };
        var rpcs3 = console with { Flags = SessionFlags.Emulator | SessionFlags.NoCodePatches };
        var patched = rpcs3 with { Flags = rpcs3.Flags | SessionFlags.CodeSwitches };

        Assert.False(CombosPanel.FastLoadsComboBlocked(ComboAction.ToggleFastLoads, console));
        Assert.True(CombosPanel.FastLoadsComboBlocked(ComboAction.ToggleFastLoads, rpcs3));
        Assert.False(CombosPanel.FastLoadsComboBlocked(ComboAction.ToggleFastLoads, patched));
        Assert.False(CombosPanel.FastLoadsComboBlocked(ComboAction.Die, rpcs3));
    }
}
