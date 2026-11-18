using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Revision 1.34 (qwark build 59): a combo for each page of the in-game Trainer. The module lists
/// them after the first seven in COMBO_LIST and names them after its entries; the Combos panel
/// shows them as one row whose dropdown picks the page. These are the wire, the dropdown's model and
/// the requests against the fake console; the capture itself, which is the panel's static state, is
/// <see cref="ClientTests"/>'.
/// </summary>
public class TrainerPageCombosTests
{
    private const uint Mask = (uint)PadButton.Select | (uint)PadButton.Up;
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

    // ------------------------------------------------------------------ the wire

    [Fact]
    public void AModuleBeforeTheRevisionListsSevenCombosAndNoPage()
    {
        var entries = Enumerable.Range(0, 7).Select(a => ((byte)a, a == 5 ? ComboDefaults.OpenConsoleMenu : 0u)).ToArray();
        var parsed = ComboEntry.ParseList(ComboList(entries, null));

        Assert.Equal(7, parsed.Length);
        Assert.Empty(CombosPanel.TrainerPages(parsed));
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
        var pages = CombosPanel.TrainerPages(parsed);
        Assert.Equal(Pages, pages.Select(p => p.Page));
        Assert.Equal(Enumerable.Range(7, 7).Select(a => (ComboAction)a), pages.Select(p => p.Action));
        Assert.Equal(Mask, pages.Single(p => p.Page == "Planets").Mask);
    }

    [Fact]
    public void ANameCutShortOrForAnActionTheListHasNotNamesNothing()
    {
        var entries = Enumerable.Range(0, 8).Select(a => ((byte)a, 0u)).ToArray();
        var full = ComboList(entries, new[] { ((byte)7, "Game"), ((byte)20, "Nowhere") });
        var parsed = ComboEntry.ParseList(full);
        Assert.Equal("Game", Assert.Single(CombosPanel.TrainerPages(parsed)).Page);

        // The same reply cut in the middle of the first name: the entries stand, no page is named.
        var cut = ComboEntry.ParseList(full.AsSpan(0, 1 + (8 * 8) + 1 + 2 + 2));
        Assert.Equal(8, cut.Length);
        Assert.Empty(CombosPanel.TrainerPages(cut));
    }

    [Fact]
    public async Task TheFakeConsoleListsAndNamesThePagesAndAnOlderOneDoesNot()
    {
        var (server, client) = await ConnectAsync(s => s.Session = s.Session with { QwarkVersion = 59 });
        using (server)
        using (client)
        {
            var combos = await client.ComboListAsync();
            Assert.Equal(14, combos.Length);
            Assert.Equal(Pages, CombosPanel.TrainerPages(combos).Select(p => p.Page));
            Assert.All(CombosPanel.TrainerPages(combos), p => Assert.Equal(0u, p.Mask));
            Assert.True(CombosPanel.ShowsPagesRow(server.Session, combos));
        }

        var (old, oldClient) = await ConnectAsync(s =>
        {
            s.ComboPages.Clear();
            s.Session = s.Session with { QwarkVersion = 58 };
        });
        using (old)
        using (oldClient)
        {
            var combos = await oldClient.ComboListAsync();
            Assert.Equal(7, combos.Length);
            Assert.False(CombosPanel.ShowsPagesRow(old.Session, combos));
            await Assert.ThrowsAsync<QwarkStatusException>(() => oldClient.ComboSetAsync((ComboAction)8, Mask));
        }
    }

    // ------------------------------------------------------------------ the chosen page's requests

    [Fact]
    public async Task SettingAndClearingThePickedPageTouchesThatPageAlone()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var pages = CombosPanel.TrainerPages(await client.ComboListAsync());
            var cheats = CombosPanel.ChosenPage(pages, pages.Single(p => p.Page == "Cheats").Action)!.Value;

            await client.ComboSetAsync(cheats.Action, Mask);
            var after = CombosPanel.TrainerPages(await client.ComboListAsync());
            Assert.Equal(Mask, after.Single(p => p.Page == "Cheats").Mask);
            Assert.All(after.Where(p => p.Page != "Cheats"), p => Assert.Equal(0u, p.Mask));
            Assert.Equal(Mask, server.Combos[cheats.Action]);

            // Clear sends COMBO_SET 0, which takes the page's line out: no mask at all on the console.
            Assert.Equal(CombosPanel.ClearRequest.ClearShared, CombosPanel.ClearFor(server.Session, after.Single(p => p.Page == "Cheats")));
            await client.ComboSetAsync(cheats.Action, 0);
            Assert.False(server.Combos.ContainsKey(cheats.Action));
            Assert.All(CombosPanel.TrainerPages(await client.ComboListAsync()), p => Assert.Equal(0u, p.Mask));
        }
    }

    [Fact]
    public async Task APageCanBeOverriddenForTheSignedInUserAndClearedBack()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var planets = CombosPanel.TrainerPages(await client.ComboListAsync()).Single(p => p.Page == "Planets").Action;
            await client.ComboSetAsync(planets, Mask);
            await client.ProfileEnableAsync(true, ProfileSetup.Fresh);
            var session = (await client.GetStateAsync()).Session;

            await client.ComboOverrideAsync(planets, Own);
            var row = CombosPanel.TrainerPages(await client.ComboListAsync()).Single(p => p.Action == planets);
            Assert.Equal(Own, row.Mask);
            Assert.True(row.Overridden);
            Assert.Equal("Planets", row.Page);
            Assert.Equal(CombosPanel.ClearRequest.RemoveOverride, CombosPanel.ClearFor(session, row));
            Assert.Equal(Mask, server.Combos[planets]);

            await client.ComboOverrideClearAsync(planets);
            row = CombosPanel.TrainerPages(await client.ComboListAsync()).Single(p => p.Action == planets);
            Assert.Equal(new ComboEntry(planets, Mask, ComboEntryFlags.None, "Planets"), row);
        }
    }

    // ------------------------------------------------------------------ the dropdown

    private static IReadOnlyList<ComboEntry> PageList(params (string Page, uint Mask, bool Own)[] pages) =>
        pages.Select((p, i) => new ComboEntry((ComboAction)(7 + i), p.Mask,
            p.Own ? ComboEntryFlags.Overridden : ComboEntryFlags.None, p.Page)).ToList();

    [Fact]
    public void TheDropdownKeepsThePickedPageElseShowsTheFirstBoundOneElseTheFirst()
    {
        var pages = PageList(("Game", 0, false), ("Cheats", 0, false), ("Planets", Mask, false), ("Manips", Mask, false));

        Assert.Equal("Planets", CombosPanel.ChosenPage(pages, null)!.Value.Page);
        Assert.Equal("Cheats", CombosPanel.ChosenPage(pages, (ComboAction)8)!.Value.Page);
        // A page the module no longer lists is not kept.
        Assert.Equal("Planets", CombosPanel.ChosenPage(pages, (ComboAction)13)!.Value.Page);
        Assert.Equal("Game", CombosPanel.ChosenPage(PageList(("Game", 0, false), ("Cheats", 0, false)), null)!.Value.Page);
        Assert.Null(CombosPanel.ChosenPage(Array.Empty<ComboEntry>(), null));
    }

    [Fact]
    public void TheDropdownMarksEveryPageThatHasACombo()
    {
        var profiles = SessionInfo.Empty with
        {
            ProfileFlags = ProfileFlags.Supported | ProfileFlags.On | ProfileFlags.Active,
        };
        var pages = PageList(("Game", 0, false), ("Cheats", Mask, false), ("Planets", Own, true));

        Assert.Equal("Game", CombosPanel.PageItem(profiles, pages[0]));
        Assert.Equal($"Cheats: {PadButtons.Describe(Mask)}", CombosPanel.PageItem(profiles, pages[1]));
        Assert.Equal($"Planets: {PadButtons.Describe(Own)} (yours)", CombosPanel.PageItem(profiles, pages[2]));
        Assert.Equal("Open Cheats", CombosPanel.PagePreview(pages[1]));

        string tip = CombosPanel.PagesTooltip(profiles, pages);
        Assert.Contains("Cheats: ", tip);
        Assert.Contains("Planets: ", tip);
        Assert.DoesNotContain("Game", tip);
        Assert.Contains("No page has a combo yet", CombosPanel.PagesTooltip(profiles, PageList(("Game", 0, false))));
    }

    [Fact]
    public void APageComboIsNamedForItsPageInTheToasts()
    {
        var combos = new[] { new ComboEntry(ComboAction.Die, 0), new ComboEntry((ComboAction)9, Mask, ComboEntryFlags.None, "Planets") };

        Assert.Equal("Open Planets page", CombosPanel.Label(combos, (ComboAction)9));
        Assert.Equal("Die", CombosPanel.Label(combos, ComboAction.Die));
        // An action the list does not name falls back to its number rather than another's name.
        Assert.Equal("10", CombosPanel.Label(combos, (ComboAction)10));
    }
}
