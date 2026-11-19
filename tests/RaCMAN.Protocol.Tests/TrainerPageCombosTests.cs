using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Revision 1.34 (qwark build 59): a combo for each page of the in-game Trainer. The module lists
/// them after the first seven in COMBO_LIST and names them after its entries; the Combos panel
/// gives them a table of their own under the action combos', a row per page, with only the pages
/// that have a combo showing until "More...". These are the wire, the table's model and the
/// requests a row sends against the fake console; the capture itself, which is the panel's static
/// state, is <see cref="ClientTests"/>'.
/// </summary>
public class TrainerPageCombosTests
{
    private const uint Mask = (uint)PadButton.Select | (uint)PadButton.Up;
    private const uint Other = (uint)PadButton.L1 | (uint)PadButton.Select;
    private const uint Own = (uint)PadButton.Select | (uint)PadButton.Down;

    private static readonly string[] Pages = { "Game", "Cheats", "Planets", "Manips", "Unlocks", "Mods", "Save files" };

    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync(Action<FakeQwarkServer>? setup = null)
    {
        var server = new FakeQwarkServer { ProfilesSupported = true };
        setup?.Invoke(server);
        server.Start();
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    /// <summary>COMBO_LIST as a module lays it out: the entries, then the names when there are any.</summary>
    private static byte[] ComboList((byte Action, uint Mask)[] entries, (byte Action, string Name)[]? names)
    {
        var bytes = new List<byte> { (byte)entries.Length };
        foreach (var (action, mask) in entries)
        {
            bytes.AddRange(new byte[] { action, 0, 0, 0, (byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask });
        }

        if (names is not null)
        {
            bytes.Add((byte)names.Length);
            foreach (var (action, name) in names)
            {
                bytes.Add(action);
                bytes.Add((byte)name.Length);
                bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(name));
            }
        }

        return bytes.ToArray();
    }

    /// <summary>The page the module calls <paramref name="name"/>, out of a list.</summary>
    private static ComboEntry PageNamed(IEnumerable<ComboEntry> pages, string name) => pages.Single(p => p.Page == name);

    /// <summary>The fake console's pages as the panel reads them, re-listed.</summary>
    private static async Task<IReadOnlyList<ComboEntry>> PagesAsync(QwarkClient client) =>
        TrainerPageTable.Pages(await client.ComboListAsync());

    // ------------------------------------------------------------------ the wire

    [Fact]
    public void AModuleBeforeTheRevisionListsSevenCombosAndNoPage()
    {
        var entries = Enumerable.Range(0, 7).Select(a => ((byte)a, a == 5 ? ComboDefaults.OpenConsoleMenu : 0u)).ToArray();
        var parsed = ComboEntry.ParseList(ComboList(entries, null));

        Assert.Equal(7, parsed.Length);
        Assert.Empty(TrainerPageTable.Pages(parsed));
        Assert.Equal(ComboDefaults.OpenConsoleMenu, parsed[5].Mask);
    }

    [Fact]
    public void TheNamesAfterTheEntriesMakeTheirCombosTrainerPages()
    {
        var entries = Enumerable.Range(0, 14).Select(a => ((byte)a, a == 9 ? Mask : 0u)).ToArray();
        var names = Pages.Select((name, i) => ((byte)(7 + i), name)).ToArray();
        var parsed = ComboEntry.ParseList(ComboList(entries, names));

        Assert.Equal(14, parsed.Length);
        Assert.All(parsed.Take(7), entry => Assert.False(entry.IsTrainerPage));
        var pages = TrainerPageTable.Pages(parsed);
        Assert.Equal(Pages, pages.Select(p => p.Page));
        Assert.Equal(Enumerable.Range(7, 7).Select(a => (ComboAction)a), pages.Select(p => p.Action));
        Assert.Equal(Mask, PageNamed(pages, "Planets").Mask);
    }

    [Fact]
    public void ANameCutShortOrForAnActionTheListHasNotNamesNothing()
    {
        var entries = Enumerable.Range(0, 8).Select(a => ((byte)a, 0u)).ToArray();
        var full = ComboList(entries, new[] { ((byte)7, "Game"), ((byte)20, "Nowhere") });
        var parsed = ComboEntry.ParseList(full);
        Assert.Equal("Game", Assert.Single(TrainerPageTable.Pages(parsed)).Page);

        // The same reply cut in the middle of the first name: the entries stand, no page is named.
        var cut = ComboEntry.ParseList(full.AsSpan(0, 1 + (8 * 8) + 1 + 2 + 2));
        Assert.Equal(8, cut.Length);
        Assert.Empty(TrainerPageTable.Pages(cut));
    }

    // ------------------------------------------------------------------ the table: what shows

    [Fact]
    public async Task TheTableStartsHiddenWithOnlyTheBoundPagesShowing()
    {
        var (server, client) = await ConnectAsync(s => s.Session = s.Session with { QwarkVersion = 59 });
        using (server)
        using (client)
        {
            // Nothing bound: the panel has the table, with no row yet, and the button to show them.
            var table = new TrainerPageTable();
            var combos = await client.ComboListAsync();
            Assert.True(TrainerPageTable.Shows(combos));
            var pages = TrainerPageTable.Pages(combos);
            Assert.Equal(Pages, pages.Select(p => p.Page));
            Assert.Empty(table.Rows(pages));
            Assert.Equal(TrainerPageTable.MoreLabel, table.ButtonLabel(pages));

            // Two shared combos and one of the signed-in user's own, on a page with no shared one:
            // those three show, in the module's order, and the other four wait behind the button.
            await client.ComboSetAsync(PageNamed(pages, "Planets").Action, Mask);
            await client.ComboSetAsync(PageNamed(pages, "Cheats").Action, Other);
            await client.ProfileEnableAsync(true, ProfileSetup.Fresh);
            await client.ComboOverrideAsync(PageNamed(pages, "Save files").Action, Own);

            pages = await PagesAsync(client);
            var rows = table.Rows(pages);
            Assert.Equal(new[] { "Cheats", "Planets", "Save files" }, rows.Select(p => p.Page));
            Assert.True(PageNamed(rows, "Save files").Overridden);
            Assert.False(table.Expanded);
            Assert.Equal(TrainerPageTable.MoreLabel, table.ButtonLabel(pages));
        }
    }

    [Fact]
    public void MoreShowsEveryPageInTheModulesOrderAndFewerHidesThemAgain()
    {
        var pages = PageList(("Game", 0, false), ("Cheats", Mask, false), ("Planets", 0, false), ("Manips", Own, true));
        var table = new TrainerPageTable();
        Assert.Equal(new[] { "Cheats", "Manips" }, table.Rows(pages).Select(p => p.Page));

        table.Toggle();
        Assert.True(table.Expanded);
        Assert.Equal(new[] { "Game", "Cheats", "Planets", "Manips" }, table.Rows(pages).Select(p => p.Page));
        Assert.Equal(TrainerPageTable.FewerLabel, table.ButtonLabel(pages));

        table.Toggle();
        Assert.Equal(new[] { "Cheats", "Manips" }, table.Rows(pages).Select(p => p.Page));
        Assert.Equal(TrainerPageTable.MoreLabel, table.ButtonLabel(pages));
    }

    [Fact]
    public void LeavingThePanelHidesTheUnboundPagesAgain()
    {
        var pages = PageList(("Game", 0, false), ("Cheats", Mask, false));
        var table = new TrainerPageTable();
        table.Toggle();

        // Frames on the Combos panel keep every page showing.
        table.Observe(PanelNav.Combos);
        table.Observe(PanelNav.Combos);
        Assert.Equal(2, table.Rows(pages).Count);

        // Any other panel, Settings included, hides them; coming back starts hidden.
        for (int panel = 0; panel <= PanelNav.Settings; panel++)
        {
            if (panel == PanelNav.Combos) continue;
            Assert.True(table.Expanded);
            table.Observe(panel);
            Assert.False(table.Expanded);
            table.Observe(PanelNav.Combos);
            Assert.Equal("Cheats", Assert.Single(table.Rows(pages)).Page);
            Assert.Equal(TrainerPageTable.MoreLabel, table.ButtonLabel(pages));
            table.Toggle();
        }
    }

    [Fact]
    public void WithEveryPageBoundThereIsNoButton()
    {
        var all = PageList(("Game", Mask, false), ("Cheats", Own, true));
        var table = new TrainerPageTable();
        Assert.Equal(2, table.Rows(all).Count);
        Assert.Null(table.ButtonLabel(all));

        // Still none once shown: Fewer would hide nothing.
        table.Toggle();
        Assert.Null(table.ButtonLabel(all));

        // And it comes back as soon as one is cleared.
        var oneCleared = PageList(("Game", 0, false), ("Cheats", Own, true));
        Assert.Equal(TrainerPageTable.FewerLabel, table.ButtonLabel(oneCleared));
    }

    [Fact]
    public void ACaptureKeepsItsRowWhileTheUnboundPagesAreHidden()
    {
        var pages = PageList(("Game", 0, false), ("Cheats", Mask, false), ("Planets", 0, false));
        var table = new TrainerPageTable();

        Assert.Equal(new[] { "Cheats", "Planets" }, table.Rows(pages, pages[2].Action).Select(p => p.Page));
        // A capture on an action combo's row adds nothing here.
        Assert.Equal(new[] { "Cheats" }, table.Rows(pages, ComboAction.Die).Select(p => p.Page));
    }

    [Fact]
    public async Task AnOlderModuleShowsNeitherTheTableNorTheButton()
    {
        var (old, client) = await ConnectAsync(s =>
        {
            s.ComboPages.Clear();
            s.Session = s.Session with { QwarkVersion = 58 };
        });
        using (old)
        using (client)
        {
            var combos = await client.ComboListAsync();
            Assert.Equal(7, combos.Length);
            Assert.False(TrainerPageTable.Shows(combos));

            // Even with the table shown from an earlier, newer console: nothing to draw.
            var table = new TrainerPageTable();
            table.Toggle();
            var pages = TrainerPageTable.Pages(combos);
            Assert.Empty(table.Rows(pages));
            Assert.Null(table.ButtonLabel(pages));
            await Assert.ThrowsAsync<QwarkStatusException>(() => client.ComboSetAsync((ComboAction)8, Mask));
        }
    }

    [Fact]
    public async Task ToolsOfDestructionShowsTheTableAndTheButton()
    {
        var (server, client) = await ConnectAsync(s =>
        {
            s.UseTod();
            s.Session = s.Session with { QwarkVersion = 59 };
        });
        using (server)
        using (client)
        {
            var session = (await client.GetStateAsync()).Session;
            Assert.Equal(GameId.Tod, session.Game);
            var describe = await client.DescribeAsync();
            var combos = await client.ComboListAsync();

            Assert.True(TrainerPageTable.Shows(combos));
            var pages = TrainerPageTable.Pages(combos);
            Assert.Equal(Pages, pages.Select(p => p.Page));
            Assert.Equal(TrainerPageTable.MoreLabel, new TrainerPageTable().ButtonLabel(pages));
            Assert.False(TrainerPageTable.Greyed(session, describe));
            Assert.True(CombosPanel.ShowsRow(ComboAction.OpenConsoleMenu, session));
        }
    }

    [Fact]
    public async Task ToolsOfDestructionUnderRpcs3IsNotGreyed()
    {
        var (server, client) = await ConnectAsync(s =>
        {
            s.UseTod();
            s.Emulator = true;
            s.NoCodePatches = true;
            s.Session = s.Session with { QwarkVersion = 59 };
        });
        using (server)
        using (client)
        {
            var session = (await client.GetStateAsync()).Session;
            Assert.True(session.IsEmulator);
            var describe = await client.DescribeAsync();
            Assert.True(describe.Extension!.MenuHere);

            Assert.True(TrainerPageTable.Shows(await client.ComboListAsync()));
            Assert.False(TrainerPageTable.Greyed(session, describe));
            Assert.False(CombosPanel.MenuComboBlocked(ComboAction.OpenConsoleMenu, session, describe));
        }
    }

    [Fact]
    public async Task AnOriginalGameUnderRpcs3IsGreyedAsItsModuleSays()
    {
        // RaC1's build 59 module under RPCS3: its Trainer is code written into the game, so the
        // module says it cannot come up there.
        var (server, client) = await ConnectAsync(s =>
        {
            s.Emulator = true;
            s.NoCodePatches = true;
            s.Session = s.Session with { QwarkVersion = 59 };
            s.Describe = s.Describe with { Extension = new DescribeExtension(PlanetFlags.None, false, "Help") };
        });
        using (server)
        using (client)
        {
            var session = (await client.GetStateAsync()).Session;
            Assert.Equal(GameId.Rac1, session.Game);
            var describe = await client.DescribeAsync();

            Assert.True(TrainerPageTable.Shows(await client.ComboListAsync()));
            Assert.True(TrainerPageTable.Greyed(session, describe));
            Assert.True(CombosPanel.MenuComboBlocked(ComboAction.OpenConsoleMenu, session, describe));
            Assert.False(CombosPanel.MenuComboBlocked(ComboAction.Die, session, describe));

            // It is the module's word, not the platform's: the same session with a module that
            // says the Trainer comes up is not greyed, and a console whose module says it cannot is.
            var here = describe with { Extension = describe.Extension! with { MenuHere = true } };
            Assert.False(TrainerPageTable.Greyed(session, here));
            var console = session with { Flags = session.Flags & ~(SessionFlags.Emulator | SessionFlags.NoCodePatches) };
            Assert.True(TrainerPageTable.Greyed(console, describe));
        }
    }

    // ------------------------------------------------------------------ the table: what it says

    [Fact]
    public async Task TheRowsAndTooltipsNameThePagesAsTheModuleDoes()
    {
        // A module whose pages are not the ones this client has seen: the names are the module's.
        string[] names = { "Game", "Gadgets", "Save files" };
        var (server, client) = await ConnectAsync(s =>
        {
            s.ComboPages.Clear();
            s.ComboPages.AddRange(names);
        });
        using (server)
        using (client)
        {
            var table = new TrainerPageTable();
            table.Toggle();
            var rows = table.Rows(await PagesAsync(client));

            Assert.Equal(names, rows.Select(TrainerPageTable.RowLabel));
            var tips = rows.Select(TrainerPageTable.Tooltip).ToList();
            Assert.Equal(names.Length, tips.Distinct().Count());
            for (int i = 0; i < names.Length; i++)
            {
                // The sentence around the module's name, and that name alone.
                Assert.Equal(names[i], Between(tips[i], "Opens the Trainer menu directly to the ", " page."));
            }

            Assert.Equal("Page", TrainerPageTable.Header);
        }
    }

    private static string Between(string text, string before, string after)
    {
        Assert.StartsWith(before, text, StringComparison.Ordinal);
        Assert.EndsWith(after, text, StringComparison.Ordinal);
        return text[before.Length..^after.Length];
    }

    // ------------------------------------------------------------------ a row's requests

    [Fact]
    public async Task ARowsSetAndClearTouchItsOwnPageAlone()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var table = new TrainerPageTable();
            table.Toggle();
            var rows = table.Rows(await PagesAsync(client));
            var cheats = PageNamed(rows, "Cheats");
            var planets = PageNamed(rows, "Planets");

            await client.ComboSetAsync(cheats.Action, Mask);
            await client.ComboSetAsync(planets.Action, Other);
            Assert.Equal(Mask, server.Combos[cheats.Action]);
            Assert.Equal(Other, server.Combos[planets.Action]);

            // Hidden again, both bound rows show; Clear on Planets' row takes Planets' line out
            // (COMBO_SET 0) and leaves Cheats, and Planets drops out of the hidden table.
            table.Toggle();
            rows = table.Rows(await PagesAsync(client));
            Assert.Equal(new[] { "Cheats", "Planets" }, rows.Select(p => p.Page));
            await CombosPanel.ClearAsync(client, server.Session, PageNamed(rows, "Planets"));

            Assert.False(server.Combos.ContainsKey(planets.Action));
            Assert.Equal(Mask, server.Combos[cheats.Action]);
            Assert.Equal("Cheats", Assert.Single(table.Rows(await PagesAsync(client))).Page);
        }
    }

    [Fact]
    public async Task AnOverriddenRowClearsTheOverrideThenTheSharedCombo()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var pages = await PagesAsync(client);
            var planets = PageNamed(pages, "Planets").Action;
            var cheats = PageNamed(pages, "Cheats").Action;
            await client.ComboSetAsync(planets, Mask);
            await client.ComboSetAsync(cheats, Other);
            await client.ProfileEnableAsync(true, ProfileSetup.Fresh);
            var session = (await client.GetStateAsync()).Session;
            Assert.True(CombosPanel.ShowsOverride(session));

            await client.ComboOverrideAsync(planets, Own);
            var table = new TrainerPageTable();
            var row = PageNamed(table.Rows(await PagesAsync(client)), "Planets");
            Assert.Equal(Own, row.Mask);
            Assert.True(CombosPanel.IsOverridden(session, row));
            Assert.Equal(Own, server.ComboOverridesFor(server.SignedInUser)[planets]);

            // First Clear: this user's override of Planets goes, the shared combo shows again.
            await CombosPanel.ClearAsync(client, session, row);
            row = PageNamed(table.Rows(await PagesAsync(client)), "Planets");
            Assert.Equal(new ComboEntry(planets, Mask, ComboEntryFlags.None, "Planets"), row);
            Assert.Empty(server.ComboOverridesFor(server.SignedInUser));
            Assert.Equal(Other, server.Combos[cheats]);

            // Second: the shared one, and the row is gone from the hidden table.
            await CombosPanel.ClearAsync(client, session, row);
            Assert.False(server.Combos.ContainsKey(planets));
            Assert.Equal("Cheats", Assert.Single(table.Rows(await PagesAsync(client))).Page);
        }
    }

    // ------------------------------------------------------------------ the toasts

    [Fact]
    public void APageComboIsNamedForItsPageInTheToasts()
    {
        var combos = new[] { new ComboEntry(ComboAction.Die, 0), new ComboEntry((ComboAction)9, Mask, ComboEntryFlags.None, "Planets") };

        Assert.Equal("Open Planets page", CombosPanel.Label(combos, (ComboAction)9));
        Assert.Equal("Die", CombosPanel.Label(combos, ComboAction.Die));
        // An action the list does not name falls back to its number rather than another's name.
        Assert.Equal("10", CombosPanel.Label(combos, (ComboAction)10));
    }

    private static IReadOnlyList<ComboEntry> PageList(params (string Page, uint Mask, bool Own)[] pages) =>
        pages.Select((p, i) => new ComboEntry((ComboAction)(7 + i), p.Mask,
            p.Own ? ComboEntryFlags.Overridden : ComboEntryFlags.None, p.Page)).ToList();
}
