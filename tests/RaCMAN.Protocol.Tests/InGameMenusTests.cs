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

    /// <summary>
    /// Qwark revision 1.29: RPCS3 has no console menu in the four games, but Tools of Destruction's
    /// is its own pause menu and works there, so qwark-rpcs3 stores the setting as a console does.
    /// </summary>
    [Fact]
    public async Task MenuHelpIsTakenUnderRpcs3ForToD()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.Emulator = true;
            server.NoCodePatches = true;

            await client.MenuHelpAsync(false);

            Assert.False(server.MenuHelp);
        }
    }

    /// <summary>
    /// The box draws itself from the session alone: hidden against a module before build 55, greyed
    /// under RPCS3, and saying in words which way the console has it.
    /// </summary>
    [Fact]
    public void TheBoxIsGatedOnTheBuildAndGreyedUnderRpcs3()
    {
        Assert.False(QwarkSettings.ShowsInGameMenus(54));
        Assert.True(QwarkSettings.ShowsInGameMenus(55));
        Assert.True(QwarkSettings.ShowsInGameMenus(QwarkClient.ExpectedQwarkBuild));

        var console = SessionInfo.Empty with { QwarkVersion = 55 };
        var off = console with { Flags = SessionFlags.MenuHelpOff };
        var rpcs3 = console with { Flags = SessionFlags.Emulator | SessionFlags.NoCodePatches };

        var none = DescribeResult.Empty;
        Assert.False(QwarkSettings.InGameMenusBlocked(console, none));
        Assert.True(QwarkSettings.InGameMenusBlocked(rpcs3, none));
        Assert.Equal("Trainer replaces Help in the pause menu", QwarkSettings.InGameMenusStatus(console, none));
        Assert.Equal("the game's default pause menu", QwarkSettings.InGameMenusStatus(off, none));
        Assert.Equal("not under RPCS3", QwarkSettings.InGameMenusStatus(rpcs3, none));

        // Qwark revision 1.34 (D19): the module says whether its Trainer can come up here and which
        // pause row it takes; the client keeps neither. ToD's build 59 module: under RPCS3 too, in
        // place of Inventory. A game's module that says it cannot (the four under RPCS3) greys it.
        var tod = FakeQwarkServer.TodDescribe();
        var todConsole = console with { Game = GameId.Tod };
        Assert.False(QwarkSettings.InGameMenusBlocked(rpcs3 with { Game = GameId.Tod }, tod));
        Assert.Equal("Trainer replaces Inventory in the pause menu", QwarkSettings.InGameMenusStatus(todConsole, tod));
        Assert.Contains("in place of the Inventory menu", QwarkSettings.InGameMenusTooltip(todConsole, tod));
        Assert.Equal("the game's default pause menu", QwarkSettings.InGameMenusStatus(off with { Game = GameId.Tod }, tod));
        var other = tod with { Game = GameId.Rac2, Extension = new DescribeExtension(PlanetFlags.None, false, "Help") };
        Assert.True(QwarkSettings.InGameMenusBlocked(rpcs3 with { Game = GameId.Rac2 }, other));
        Assert.False(QwarkSettings.InGameMenusBlocked(console with { Game = GameId.Rac2 },
            other with { Extension = other.Extension! with { MenuHere = true } }));

        // Another game's description, or one from a module before the revision, says nothing: the
        // build 58 ToD module under RPCS3 reads as having no menu, and the row as Help.
        Assert.True(QwarkSettings.InGameMenusBlocked(rpcs3 with { Game = GameId.Rac1 }, tod));
        var tod58 = FakeQwarkServer.TodDescribe58();
        Assert.True(QwarkSettings.InGameMenusBlocked(rpcs3 with { Game = GameId.Tod }, tod58));
        Assert.Equal("Trainer replaces Help in the pause menu", QwarkSettings.InGameMenusStatus(todConsole, tod58));

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

            // Revision 1.34: the Trainer's page combos follow it; an action past them is BAD_ARG.
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.ComboSetAsync((ComboAction)combos.Length, CaptureMask));
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

        // ToD's Fast loads is a code patch since qwark revision 1.29: under RPCS3 its combo needs
        // the code switches, as the other games' do. A Fast loads that were a data write would not.
        var tod = FakeQwarkServer.TodDescribe();
        Assert.True(CombosPanel.FastLoadsComboBlocked(ComboAction.ToggleFastLoads, rpcs3, tod));
        Assert.False(CombosPanel.FastLoadsComboBlocked(ComboAction.ToggleFastLoads, patched, tod));
        var dataWrite = tod with
        {
            Features = tod.Features.Select(f => f.Label == "Fast loads" ? f with { Flags = FeatureFlags.None } : f).ToArray(),
        };
        Assert.False(CombosPanel.FastLoadsComboBlocked(ComboAction.ToggleFastLoads, rpcs3, dataWrite));
        Assert.True(CombosPanel.FastLoadsComboBlocked(ComboAction.ToggleFastLoads, rpcs3, DescribeResult.Empty));
    }

    /// <summary>
    /// Revision 1.28: a game with no console menu (flags2 bit7 NO_MENU; ToD until qwark revision 1.29) has neither
    /// the menu's combo row nor the In-game menus box; every other combo row stays.
    /// </summary>
    [Fact]
    public void AGameWithNoConsoleMenuHasNeitherTheMenuComboNorTheBox()
    {
        var console = SessionInfo.Empty with { QwarkVersion = QwarkClient.ExpectedQwarkBuild };
        var tod = console with { Flags2 = SessionFlags2.NoMenu };

        Assert.True(CombosPanel.ShowsRow(ComboAction.OpenConsoleMenu, console));
        Assert.True(QwarkSettings.ShowsInGameMenus(console));
        Assert.False(CombosPanel.ShowsRow(ComboAction.OpenConsoleMenu, tod));
        Assert.False(QwarkSettings.ShowsInGameMenus(tod));
        Assert.True(CombosPanel.ShowsRow(ComboAction.ToggleFastLoads, tod));
        Assert.True(CombosPanel.ShowsRow(ComboAction.SavePosition, tod));

        // The bit crosses the wire, and an older module never sends it.
        Assert.True(SessionInfo.Parse(tod.ToBytes()).NoConsoleMenu);
        Assert.False(SessionInfo.Parse(console.ToBytes()).NoConsoleMenu);
    }

    /// <summary>Qwark revision 1.29: ToD's console menu is its own pause menu, so the fake ToD has one.</summary>
    [Fact]
    public async Task TheFakeToDConsoleHasAConsoleMenu()
    {
        using var server = new FakeQwarkServer();
        server.UseTod();
        server.Start();
        using var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        Assert.False((await client.HelloAsync()).NoConsoleMenu);
    }
}
