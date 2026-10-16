using System.Diagnostics;

using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// MANIP_LIST, section 5.14 (revision 1.21): the reply read and written field by field, and every
/// reply that stops short of its own counts refused rather than half read.
/// </summary>
public class ManipListParsingTests
{
    private static ManipList Sample() => new(
        Selected: 2,
        AutoReset: false,
        Remember: true,
        EventSeq: 0x1234,
        EventKind: ManipEventKind.RefusedChallengeFile,
        EventGroup: 3,
        Groups: new[]
        {
            new ManipGroup(1, ManipGroupFlags.NgPlus | ManipGroupFlags.PerRun, "NG+ no IMG", new[] { "NG+ no IMG" }),
            new ManipGroup(3, ManipGroupFlags.PerRun, "Any%", new[] { "Any%", "All Platinum Bolts" }),
            new ManipGroup(6,
                ManipGroupFlags.NgPlus | ManipGroupFlags.Extension | ManipGroupFlags.PerRun | ManipGroupFlags.NoChallengeCheck,
                "NG+ No Specials", new[] { "NG+ No Specials" }),
        },
        Headers: new[]
        {
            new ManipHeader("Fixes", new ushort[] { 0x0021, 0x0102 }),
            new ManipHeader("Trophies", new ushort[] { 7 }),
            new ManipHeader("Empty", Array.Empty<ushort>()),
        });

    private static void AssertSame(ManipList expected, ManipList actual)
    {
        Assert.Equal(expected.Selected, actual.Selected);
        Assert.Equal(expected.AutoReset, actual.AutoReset);
        Assert.Equal(expected.Remember, actual.Remember);
        Assert.Equal(expected.EventSeq, actual.EventSeq);
        Assert.Equal(expected.EventKind, actual.EventKind);
        Assert.Equal(expected.EventGroup, actual.EventGroup);
        Assert.Equal(expected.Headers.Select(h => h.Name), actual.Headers.Select(h => h.Name));
        for (int i = 0; i < expected.Headers.Length; i++)
        {
            Assert.Equal(expected.Headers[i].Features, actual.Headers[i].Features);
        }

        Assert.Equal(expected.Groups.Length, actual.Groups.Length);
        for (int i = 0; i < expected.Groups.Length; i++)
        {
            Assert.Equal(expected.Groups[i].Id, actual.Groups[i].Id);
            Assert.Equal(expected.Groups[i].Flags, actual.Groups[i].Flags);
            Assert.Equal(expected.Groups[i].Name, actual.Groups[i].Name);
            Assert.Equal(expected.Groups[i].Categories, actual.Groups[i].Categories);
        }
    }

    [Fact]
    public void AListRoundTrips()
    {
        var list = Sample();
        AssertSame(list, ManipList.Parse(list.ToBytes()));
    }

    [Fact]
    public void TheBytesAreLaidOutAsTheContractSpellsThem()
    {
        var bytes = new byte[]
        {
            2, 0, 1, 1,                     // selected 2, auto-reset off, remember on, one group
            0x12, 0x34, 4, 2,               // event_seq 0x1234 big-endian, kind 4, group 2
            2, 0x05, 3, (byte)'N', (byte)'G', (byte)'+',   // id 2, NGPLUS|PER_RUN, "NG+"
            3, (byte)'a', (byte)'\n', (byte)'b',           // categories "a" and "b"
            2,                                             // two headers
            5, (byte)'F', (byte)'i', (byte)'x', (byte)'e', (byte)'s',
            2, 0x00, 0x21, 0x01, 0x02,                     // "Fixes": 0x0021 and 0x0102
            1, (byte)'T',
            1, 0x00, 0x07,                                 // "T": 7
        };

        var list = ManipList.Parse(bytes);

        Assert.Equal(2, list.Selected);
        Assert.False(list.AutoReset);
        Assert.True(list.Remember);
        Assert.Equal(0x1234, list.EventSeq);
        Assert.Equal(ManipEventKind.RefusedChallengeFile, list.EventKind);
        Assert.Equal(2, list.EventGroup);

        var group = Assert.Single(list.Groups);
        Assert.Equal(2, group.Id);
        Assert.Equal("NG+", group.Name);
        Assert.True(group.IsNgPlus);
        Assert.True(group.HasPerRun);
        Assert.False(group.IsExtension);
        Assert.False(group.SkipsChallengeCheck);
        Assert.Equal(new[] { "a", "b" }, group.Categories);

        Assert.Equal(new[] { "Fixes", "T" }, list.Headers.Select(h => h.Name));
        Assert.Equal(new ushort[] { 0x0021, 0x0102 }, list.Headers[0].Features);
        Assert.Equal(new ushort[] { 7 }, list.Headers[1].Features);

        // And the writer the fake console uses spells it the same way.
        Assert.Equal(bytes, list.ToBytes());
    }

    [Fact]
    public void EveryTruncatedReplyThrows()
    {
        var bytes = Sample().ToBytes();
        for (int length = 0; length < bytes.Length; length++)
        {
            int cut = length;
            Assert.Throws<ProtocolException>(() => ManipList.Parse(bytes.AsSpan(0, cut)));
        }

        // The whole reply is fine, and so is one with bytes a later revision may add after it.
        ManipList.Parse(bytes);
        AssertSame(Sample(), ManipList.Parse(bytes.Concat(new byte[] { 9, 9, 9 }).ToArray()));
    }

    [Fact]
    public void AReplyWithNoGroupsAndNoHeadersIsNineBytes()
    {
        var empty = new ManipList(0, true, false, 0, ManipEventKind.None, 0,
            Array.Empty<ManipGroup>(), Array.Empty<ManipHeader>());

        var bytes = empty.ToBytes();
        Assert.Equal(ManipList.HeaderSize + 1, bytes.Length);
        AssertSame(empty, ManipList.Parse(bytes));

        // The eight fixed bytes alone are missing the header count, which the contract always carries.
        Assert.Throws<ProtocolException>(() => ManipList.Parse(bytes.AsSpan(0, ManipList.HeaderSize)));
    }

    [Fact]
    public void AHeaderThatStopsShortThrowsWhereverItStops()
    {
        // One header, "Fixes", that names two features and carries one.
        var shortFeatures = new byte[]
        {
            0, 1, 0, 0, 0, 0, 0, 0,
            1, 5, (byte)'F', (byte)'i', (byte)'x', (byte)'e', (byte)'s', 2, 0x00, 0x01,
        };
        var ex = Assert.Throws<ProtocolException>(() => ManipList.Parse(shortFeatures));
        Assert.Contains("header 0", ex.Message);

        // Two headers counted, one there.
        var missingHeader = new byte[] { 0, 1, 0, 0, 0, 0, 0, 0, 2, 1, (byte)'T', 0 };
        Assert.Throws<ProtocolException>(() => ManipList.Parse(missingHeader));

        // A name longer than what is left.
        var shortName = new byte[] { 0, 1, 0, 0, 0, 0, 0, 0, 1, 9, (byte)'T' };
        Assert.Throws<ProtocolException>(() => ManipList.Parse(shortName));
    }

    [Fact]
    public void CategoriesAreOnePerLine()
    {
        var list = new ManipList(0, true, false, 0, ManipEventKind.None, 0,
            new[] { new ManipGroup(4, ManipGroupFlags.PerRun, "100%", new[] { "Max%", "100%" }) },
            Array.Empty<ManipHeader>());

        var bytes = list.ToBytes();

        // "Max%\n100%" on the wire, nothing else between the two names.
        string cats = System.Text.Encoding.UTF8.GetString(bytes, ManipList.HeaderSize + 3 + 4 + 1, 9);
        Assert.Equal("Max%\n100%", cats);

        // A trailing or doubled line feed is not an empty category.
        var sloppy = new byte[]
        {
            0, 1, 0, 1, 0, 0, 0, 0,
            4, 0x04, 1, (byte)'x', 7, (byte)'a', (byte)'\n', (byte)'\n', (byte)'b', (byte)'\r', (byte)'\n', (byte)'\n',
            0,
        };
        Assert.Equal(new[] { "a", "b" }, ManipList.Parse(sloppy).Groups[0].Categories);
    }

    [Fact]
    public void TheGroupIsLookedUpByIdNotByPosition()
    {
        var list = Sample();

        Assert.Null(list.SelectedGroup);   // 2 is not one of this list's ids
        Assert.Equal("Any%", list.Group(3)!.Name);
        Assert.Equal(ManipList.NoneName, list.GroupName(ManipList.None));
        Assert.Equal("Group 9", list.GroupName(9));
        Assert.Null(list.Group(ManipList.None));

        var picked = list with { Selected = 6 };
        Assert.Equal("NG+ No Specials", picked.SelectedGroup!.Name);
        Assert.True(picked.SelectedGroup.SkipsChallengeCheck);
        Assert.True(picked.SelectedGroup.IsExtension);
    }

    [Fact]
    public void OnlyAHeaderIdThatCanNameAFeatureIsOne()
    {
        // DESCRIBE ids are a byte; anything wider names nothing the panel could draw. A feature
        // two headers list counts once, for the first of them.
        var list = Sample() with
        {
            Headers = new[]
            {
                new ManipHeader("Fixes", new ushort[] { 12, 0x0102, 11, 12 }),
                new ManipHeader("Trophies", new ushort[] { 11, 30 }),
            },
        };

        Assert.Equal(new byte[] { 12, 11 }, list.Headers[0].FeatureIds);
        Assert.Equal(new byte[] { 12, 11, 30 }, list.HeaderFeatureIds);
    }

    [Fact]
    public void TheOpcodesAndBitsAreTheContractsNumbers()
    {
        Assert.Equal(0x00D0, (ushort)Opcode.ManipList);
        Assert.Equal(0x00D1, (ushort)Opcode.ManipSelect);
        Assert.Equal(0x00D2, (ushort)Opcode.ManipOptions);
        Assert.Equal(0x00D3, (ushort)Opcode.ManipApply);

        Assert.Equal(0x01, (byte)ManipGroupFlags.NgPlus);
        Assert.Equal(0x02, (byte)ManipGroupFlags.Extension);
        Assert.Equal(0x04, (byte)ManipGroupFlags.PerRun);
        Assert.Equal(0x08, (byte)ManipGroupFlags.NoChallengeCheck);

        Assert.Equal(1, (byte)ManipEventKind.Activated);
        Assert.Equal(2, (byte)ManipEventKind.PerRunApplied);
        Assert.Equal(3, (byte)ManipEventKind.RefusedNotChallengeFile);
        Assert.Equal(4, (byte)ManipEventKind.RefusedChallengeFile);
        Assert.Equal(5, (byte)ManipEventKind.RefusedNoFile);
    }
}

/// <summary>The four MANIP ops on the wire, against the fake console.</summary>
public class ManipClientTests
{
    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync()
    {
        var server = new FakeQwarkServer();
        server.Start();
        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    [Fact]
    public async Task SelectSendsTheGroupIdAsOneByte()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.ManipSelectAsync(3);
            Assert.Equal(new byte[] { 3 }, server.LastPayload(Opcode.ManipSelect));
            Assert.Equal(3, server.ManipOptionsFor(GameId.Rac1).Selected);

            await client.ManipSelectAsync(ManipList.None);
            Assert.Equal(new byte[] { 0 }, server.LastPayload(Opcode.ManipSelect));
            Assert.Equal(ManipList.None, server.ManipOptionsFor(GameId.Rac1).Selected);
        }
    }

    [Fact]
    public async Task OptionsSendAutoResetThenRemember()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.ManipOptionsAsync(autoReset: false, remember: true);
            Assert.Equal(new byte[] { 0, 1 }, server.LastPayload(Opcode.ManipOptions));
            Assert.Equal((false, true), (server.ManipOptionsFor(GameId.Rac1).AutoReset, server.ManipOptionsFor(GameId.Rac1).Remember));

            await client.ManipOptionsAsync(autoReset: true, remember: false);
            Assert.Equal(new byte[] { 1, 0 }, server.LastPayload(Opcode.ManipOptions));

            var list = await client.ManipListAsync();
            Assert.True(list.AutoReset);
            Assert.False(list.Remember);
        }
    }

    [Fact]
    public async Task ListAndApplyCarryNoPayload()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.ChallengeCounter = 1;
            await client.ManipSelectAsync(1);
            await client.ManipApplyAsync();
            var list = await client.ManipListAsync();

            Assert.Empty(server.LastPayload(Opcode.ManipApply)!);
            Assert.Empty(server.LastPayload(Opcode.ManipList)!);
            Assert.Equal(1, server.ManipApplyCount);
            Assert.Equal(1, list.Selected);
        }
    }

    [Fact]
    public async Task TheListIsTheConsolesTableInItsOrder()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var list = await client.ManipListAsync();

            Assert.Equal(FakeQwarkServer.ManipGroupsFor(GameId.Rac1).Select(g => g.Name), list.Groups.Select(g => g.Name));
            Assert.Equal(ManipList.None, list.Selected);
            Assert.True(list.AutoReset);     // qwark's default
            Assert.False(list.Remember);     // and this one's
            Assert.Empty(list.Headers);      // only Deadlocked and UYA have headings
            Assert.True(list.Groups.Single(g => g.Name == "NG+ GBSP").IsExtension);
        }
    }

    [Fact]
    public async Task UyaHasTwoNgPlusGroupsAndATrophiesHeading()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.Describe = FakeQwarkServer.WithManipHeaderFeatures(server.Describe with { Game = GameId.Rac3 }, GameId.Rac3);
            server.Session = server.Session with { Game = GameId.Rac3, TitleId = "NPEA00387" };

            var list = await client.ManipListAsync();

            Assert.Equal(new[] { "NG+ no QE", "NG+", "NG+ CC Early" }, list.Groups.Select(g => g.Name));
            Assert.Equal(new byte[] { 1, 2, 3 }, list.Groups.Select(g => g.Id));

            var trophies = Assert.Single(list.Headers);
            Assert.Equal("Trophies", trophies.Name);

            // The reset button and its toggle, by id, in that order: qwark's 21 and 39.
            Assert.Equal(new ushort[] { 21, 39 }, trophies.Features);
            var labels = trophies.Features.Select(id => server.Describe.Features.Single(f => f.Id == id)).ToArray();
            Assert.Equal(new[] { FeatureKind.Action, FeatureKind.Toggle }, labels.Select(f => f.Kind));
            Assert.Equal(FakeQwarkServer.TrophyResetLabel, labels[0].Label);
            Assert.Equal(FakeQwarkServer.TrophyAutoResetLabel, labels[1].Label);
            Assert.All(labels, f => Assert.Equal(FeatureFlags.None, f.Flags));
        }
    }

    [Fact]
    public async Task AnUnknownGroupIsBadArg()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.ManipSelectAsync(42));
            Assert.Equal(Status.BadArg, refused.Status);
        }
    }

    [Fact]
    public async Task AGameWithNoGroupsAnswersUnsupported()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.ManipsSupported = false;

            Assert.Equal(Status.Unsupported, (await Assert.ThrowsAsync<QwarkStatusException>(() => client.ManipListAsync())).Status);
            Assert.Equal(Status.Unsupported, (await Assert.ThrowsAsync<QwarkStatusException>(() => client.ManipSelectAsync(1))).Status);
            Assert.Equal(Status.Unsupported, (await Assert.ThrowsAsync<QwarkStatusException>(() => client.ManipOptionsAsync(true, true))).Status);
            Assert.Equal(Status.Unsupported, (await Assert.ThrowsAsync<QwarkStatusException>(() => client.ManipApplyAsync())).Status);
        }
    }

    [Fact]
    public async Task ARefusedApplyIsBusyAndTheEventSaysWhy()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            // An NG+ group on a fresh file: the check fails, nothing is written.
            server.ChallengeCounter = 0;
            await client.ManipSelectAsync(1);
            var before = await client.ManipListAsync();

            var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.ManipApplyAsync());
            Assert.Equal(Status.Busy, refused.Status);

            var after = await client.ManipListAsync();
            Assert.NotEqual(before.EventSeq, after.EventSeq);
            Assert.Equal(ManipEventKind.RefusedNotChallengeFile, after.EventKind);
            Assert.Equal(1, after.EventGroup);
        }
    }

    // ------------------------------------------- the fake behaves as qwark's manips.c does

    /// <summary>Moves the fake to UYA, whose groups all have setup writes.</summary>
    private static void MakeUya(FakeQwarkServer server)
    {
        server.Describe = FakeQwarkServer.WithManipHeaderFeatures(server.Describe with { Game = GameId.Rac3 }, GameId.Rac3);
        server.Session = server.Session with { Game = GameId.Rac3, TitleId = "NPEA00387" };
    }

    [Fact]
    public async Task PickingASetupGroupOnAFileItDoesNotSuitIsRefusedAtOnceAndStillAnsweredOk()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            MakeUya(server);
            server.ChallengeCounter = 0;

            // NG+ no QE is a setup alone. With a file in play the setup goes in straight away,
            // after the check, and a fresh file is refused there; the answer is OK all the same.
            await client.ManipSelectAsync(1);
            var list = await client.ManipListAsync();
            Assert.Equal(1, list.Selected);
            Assert.Equal(ManipEventKind.RefusedNotChallengeFile, list.EventKind);
            Assert.Equal(1, list.EventGroup);

            // On a challenge-mode file the NG+ group's setup goes in, and its first time this boot
            // is announced.
            server.ChallengeCounter = 1;
            await client.ManipSelectAsync(2);
            Assert.Equal(ManipEventKind.Activated, (await client.ManipListAsync()).EventKind);
        }
    }

    [Fact]
    public async Task PickingAGroupWithNoFileInPlayOrOneWithNoSetupRaisesNothing()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            // RaC1's groups are per-run writes alone: nothing is due when one is picked.
            server.ChallengeCounter = 0;
            ushort seq = (await client.ManipListAsync()).EventSeq;
            await client.ManipSelectAsync(1);
            Assert.Equal(seq, (await client.ManipListAsync()).EventSeq);

            // UYA's are setups, but at the title screen there is no file to check or write.
            MakeUya(server);
            server.ManipFileLoaded = false;
            await client.ManipSelectAsync(1);
            Assert.Equal(seq, (await client.ManipListAsync()).EventSeq);
        }
    }

    [Fact]
    public async Task ASetupPutInAgainIsAppliedNotActivated()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            MakeUya(server);
            server.ChallengeCounter = 1;

            await client.ManipSelectAsync(1);
            Assert.Equal(ManipEventKind.Activated, (await client.ManipListAsync()).EventKind);

            // Apply now on a group that is a setup alone writes it again: kind 2, a new number.
            ushort seq = (await client.ManipListAsync()).EventSeq;
            await client.ManipApplyAsync();
            var applied = await client.ManipListAsync();
            Assert.Equal(ManipEventKind.PerRunApplied, applied.EventKind);
            Assert.NotEqual(seq, applied.EventSeq);

            // Another group, then this one again: the setup is due again and goes in at once, and
            // this group was announced this boot already.
            await client.ManipSelectAsync(2);
            Assert.Equal(ManipEventKind.Activated, (await client.ManipListAsync()).EventKind);
            await client.ManipSelectAsync(1);
            var again = await client.ManipListAsync();
            Assert.Equal(ManipEventKind.PerRunApplied, again.EventKind);
            Assert.Equal(1, again.EventGroup);

            // Picking the group already picked is nothing at all.
            await client.ManipSelectAsync(1);
            Assert.Equal(again.EventSeq, (await client.ManipListAsync()).EventSeq);
        }
    }

    [Fact]
    public async Task AGroupsFirstApplicationInABootIsAnnouncedWhateverItWrote()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            // RaC1's NG+ (poki skip) has per-run writes alone. Its first application this boot,
            // a run's start here, is "activated"; the next one is "applied".
            server.ChallengeCounter = 1;
            await client.ManipSelectAsync(1);

            server.ManipRunStart();
            Assert.Equal(ManipEventKind.Activated, (await client.ManipListAsync()).EventKind);

            server.ManipRunStart();
            Assert.Equal(ManipEventKind.PerRunApplied, (await client.ManipListAsync()).EventKind);

            // With auto-reset off a run's start has nothing due for a group with no setup.
            await client.ManipOptionsAsync(autoReset: false, remember: false);
            ushort seq = (await client.ManipListAsync()).EventSeq;
            server.ManipRunStart();
            Assert.Equal(seq, (await client.ManipListAsync()).EventSeq);
        }
    }

    [Fact]
    public async Task ANewBootClearsTheEventsKindAndGroupButNotItsNumber()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            ushort seq = server.RaiseManipEvent(ManipEventKind.RefusedChallengeFile, 3);
            Assert.Equal(ManipEventKind.RefusedChallengeFile, (await client.ManipListAsync()).EventKind);

            server.Session = server.Session with { Generation = server.Session.Generation + 1 };

            var list = await client.ManipListAsync();
            Assert.Equal(ManipEventKind.None, list.EventKind);
            Assert.Equal(0, list.EventGroup);
            Assert.Equal(seq, list.EventSeq);
        }
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(0, 2)]
    [InlineData(0xFF, 1)]
    public async Task AnOptionThatIsNeitherZeroNorOneIsBadArg(byte autoReset, byte remember)
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.RequestAsync(Opcode.ManipOptions, new[] { autoReset, remember }));
            Assert.Equal(Status.BadArg, refused.Status);

            // Nothing changed.
            Assert.Equal((true, false), (server.ManipOptionsFor(GameId.Rac1).AutoReset, server.ManipOptionsFor(GameId.Rac1).Remember));
        }
    }

    [Fact]
    public async Task AModuleOlderThanTheOpsAnswersUnknownOp()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.ManipsUnknown = true;

            Assert.Equal(Status.UnknownOp, (await Assert.ThrowsAsync<QwarkStatusException>(() => client.ManipListAsync())).Status);
            Assert.Equal(Status.UnknownOp, (await Assert.ThrowsAsync<QwarkStatusException>(() => client.ManipSelectAsync(1))).Status);
            Assert.Equal(Status.UnknownOp, (await Assert.ThrowsAsync<QwarkStatusException>(() => client.ManipOptionsAsync(true, true))).Status);
            Assert.Equal(Status.UnknownOp, (await Assert.ThrowsAsync<QwarkStatusException>(() => client.ManipApplyAsync())).Status);
        }
    }
}

/// <summary>The warning under the controls, and the one word in it that depends on the group.</summary>
public class ManipWarningTests
{
    private static readonly ManipGroup NgPlus = new(2, ManipGroupFlags.NgPlus | ManipGroupFlags.PerRun, "NG+", new[] { "NG+" });
    private static readonly ManipGroup AnyPercent = new(3, ManipGroupFlags.PerRun, "Any%", new[] { "Any%", "All Platinum Bolts" });
    private static readonly ManipGroup SetupOnly = new(1, ManipGroupFlags.NgPlus, "NG+ no QE", new[] { "NG+ no QE" });

    private static ManipList With(byte selected, bool autoReset) =>
        new(selected, autoReset, false, 0, ManipEventKind.None, 0,
            new[] { SetupOnly, NgPlus, AnyPercent }, Array.Empty<ManipHeader>());

    [Fact]
    public void AnNgPlusGroupCanBeResetEveryRun()
    {
        Assert.Equal(
            "NG+ has manips that can be reset every run. Turn on auto-reset to apply them automatically.",
            ManipsArea.Warning(With(2, autoReset: false)));
    }

    [Fact]
    public void ANewGameGroupMustBe()
    {
        Assert.Equal(
            "Any% has manips that must be reset every run. Turn on auto-reset to apply them automatically.",
            ManipsArea.Warning(With(3, autoReset: false)));
    }

    [Fact]
    public void NothingToWarnAboutWithAutoResetOn()
    {
        Assert.Null(ManipsArea.Warning(With(2, autoReset: true)));
        Assert.Null(ManipsArea.Warning(With(3, autoReset: true)));
    }

    [Fact]
    public void NothingToWarnAboutForAGroupWithoutPerRunWritesOrForNone()
    {
        Assert.Null(ManipsArea.Warning(With(1, autoReset: false)));
        Assert.Null(ManipsArea.Warning(With(ManipList.None, autoReset: false)));
    }
}

/// <summary>
/// The console's last event, toasted once per sequence number per session, in words for each kind.
/// </summary>
public class ManipEventTests
{
    private static readonly ManipGroup[] Groups =
    {
        new(1, ManipGroupFlags.NgPlus | ManipGroupFlags.PerRun, "NG+ no IMG", new[] { "NG+ no IMG" }),
        new(3, ManipGroupFlags.PerRun, "Any%", new[] { "Any%" }),
    };

    private static ManipList Event(ushort seq, ManipEventKind kind, byte group = 1) =>
        new(1, true, false, seq, kind, group, Groups, Array.Empty<ManipHeader>());

    [Fact]
    public void EachNewEventIsToastedOnce()
    {
        var events = new ManipEvents();

        // The first answer of a connection is the starting point: whatever it carries happened
        // before this client looked.
        Assert.Null(events.Observe(Event(7, ManipEventKind.Activated)));

        var refused = events.Observe(Event(8, ManipEventKind.RefusedNotChallengeFile));
        Assert.Equal(ToastKind.Error, refused?.Toast);
        Assert.Equal("NG+ no IMG wasn't applied: this file isn't a challenge-mode file", refused?.Text);

        // The same event read again, however many times, is not new.
        Assert.Null(events.Observe(Event(8, ManipEventKind.RefusedNotChallengeFile)));
        Assert.Null(events.Observe(Event(8, ManipEventKind.RefusedNotChallengeFile)));

        // Another event of the same kind is.
        Assert.NotNull(events.Observe(Event(9, ManipEventKind.RefusedNotChallengeFile)));
    }

    [Fact]
    public void TheNumberMayWrapAndANewConnectionStartsAgain()
    {
        var events = new ManipEvents();
        Assert.Null(events.Observe(Event(ushort.MaxValue, ManipEventKind.Activated)));
        Assert.NotNull(events.Observe(Event(0, ManipEventKind.Activated)));

        // A new connection may have reached a restarted module, whose numbers mean nothing next
        // to the old one's: its first answer is a starting point, whatever number it carries, so
        // an event from before it is not told again...
        events.Forget();
        Assert.Null(events.Observe(Event(3, ManipEventKind.RefusedNoFile)));

        // ...and the next one is told even when its number is one the old module had used.
        events.Forget();
        Assert.Null(events.Observe(Event(0, ManipEventKind.None)));
        Assert.NotNull(events.Observe(Event(1, ManipEventKind.RefusedNoFile)));
    }

    [Fact]
    public void ARebootDoesNotStartAgainBecauseTheModulesNumberDoesNot()
    {
        // qwark's number belongs to the module: a game boot clears the event's kind and group and
        // leaves the number, so an event the new boot raises before this client's first read of
        // it is still a new number, and is still told.
        var events = new ManipEvents();
        Assert.Null(events.Observe(Event(4, ManipEventKind.Activated)));
        Assert.Null(events.Observe(Event(4, ManipEventKind.None)));

        var refused = events.Observe(Event(5, ManipEventKind.RefusedChallengeFile));
        Assert.Equal(ToastKind.Error, refused?.Toast);
    }

    [Theory]
    [InlineData(ManipEventKind.Activated, "NG+ no IMG manips activated", ToastKind.Info)]
    [InlineData(ManipEventKind.RefusedNotChallengeFile, "NG+ no IMG wasn't applied: this file isn't a challenge-mode file", ToastKind.Error)]
    [InlineData(ManipEventKind.RefusedChallengeFile, "NG+ no IMG wasn't applied: this file is a challenge-mode file", ToastKind.Error)]
    [InlineData(ManipEventKind.RefusedNoFile, "NG+ no IMG wasn't applied: load a file first", ToastKind.Error)]
    public void EachKindHasItsWords(ManipEventKind kind, string text, ToastKind toast)
    {
        var notice = ManipEvents.NoticeFor(kind, "NG+ no IMG");
        Assert.Equal(text, notice?.Text);
        Assert.Equal(toast, notice?.Toast);
    }

    [Fact]
    public void ALaterApplicationIsQuietAndNothingIsNothing()
    {
        var events = new ManipEvents();
        Assert.Null(events.Observe(Event(1, ManipEventKind.None)));

        // A new event, but one that only goes on the status line. Kind 2 is the per-run writes at
        // a run's start or a setup put in again, so it does not say "per-run".
        var quiet = events.Observe(Event(2, ManipEventKind.PerRunApplied));
        Assert.NotNull(quiet);
        Assert.Null(quiet?.Toast);
        Assert.Equal("NG+ no IMG manips applied", quiet?.Text);
        Assert.Equal("Last: NG+ no IMG manips applied", ManipsArea.LastEventLine(Event(2, ManipEventKind.PerRunApplied)));

        Assert.Null(events.Observe(Event(3, ManipEventKind.None)));
        Assert.Null(ManipsArea.LastEventLine(Event(3, ManipEventKind.None)));
        Assert.Null(ManipEvents.NoticeFor((ManipEventKind)9, "x"));
    }

    [Fact]
    public void TheEventNamesItsOwnGroupNotTheSelection()
    {
        var notice = ManipEvents.NoticeFor(Event(2, ManipEventKind.RefusedChallengeFile, group: 3));
        Assert.Equal("Any% wasn't applied: this file is a challenge-mode file", notice?.Text);
    }
}

/// <summary>
/// The manips area as <see cref="AppState"/> drives it against the fake console: hidden for a game
/// with no groups, its events toasted once, and Apply now answered in words rather than in BUSY.
/// </summary>
public class ManipStateTests
{
    /// <summary>A title no layout entry is keyed by, so the sorting is the groups' and the manips'.</summary>
    private const string UnknownTitle = "ZZZZ99999";

    private static async Task<AppState> ConnectedStateAsync(FakeQwarkServer server)
    {
        var state = new AppState(new Settings { Ps3 = { AutoReconnect = false } });
        state.Client.AutoReconnect = false;
        await state.Client.ConnectAsync("127.0.0.1", server.Port);
        return state;
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

    private static async Task PumpForAsync(AppState state, int milliseconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            state.Tick(1f / 60f);
            await Task.Delay(16);
        }
    }

    private static int ToastsSaying(AppState state, string text) => state.Toasts.Count(t => t.Text == text);

    private static bool HasManipsSection(AppState state) =>
        GamePanel.SectionsFor(UnknownTitle, GameId.None, state.Describe, state.Manips)
            .ContainsKey(GameLayout.ManipsSection);

    [Fact]
    public async Task TheAreaIsThereForAGameWithGroups()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0 && state.Manips is not null));

        Assert.Equal(FakeQwarkServer.ManipGroupsFor(GameId.Rac1).Length, state.Manips!.Groups.Length);
        Assert.True(HasManipsSection(state));
        Assert.Contains("selected=None", ManipsArea.Summary(state));

        using var idle = new AppState(new Settings());
        Assert.Equal("hidden", ManipsArea.Summary(idle));
    }

    [Fact]
    public async Task TheAreaIsHiddenWhenTheGameHasNoGroups()
    {
        using var server = new FakeQwarkServer();
        server.ManipsSupported = false;
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0
                                                 && server.RequestLog().Contains(Opcode.ManipList)));
        await PumpForAsync(state, 300);

        Assert.Null(state.Manips);
        Assert.False(HasManipsSection(state));
        Assert.Equal("hidden", ManipsArea.Summary(state));

        // UNSUPPORTED is the normal answer for such a game, not something to report.
        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);
    }

    [Fact]
    public async Task AnAreaOnScreenGoesWhenTheConsoleStopsOfferingIt()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Manips is not null));

        server.ManipsSupported = false;
        state.RefreshManips();

        Assert.True(await PumpAsync(state, () => state.Manips is null));
        Assert.False(HasManipsSection(state));
    }

    [Fact]
    public async Task AnEventIsToastedOnceHoweverOftenTheListIsRead()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Manips is not null));

        server.RaiseManipEvent(ManipEventKind.RefusedNotChallengeFile, 2);
        const string text = "NG+ (no poki skip) wasn't applied: this file isn't a challenge-mode file";

        state.RefreshManips();
        Assert.True(await PumpAsync(state, () => ToastsSaying(state, text) == 1));

        for (int i = 0; i < 4; i++)
        {
            state.RefreshManips();
            await PumpForAsync(state, 80);
        }

        Assert.Equal(1, ToastsSaying(state, text));
        Assert.Equal(1, state.ManipToasts);
        Assert.Equal(ToastKind.Error, state.Toasts.Single(t => t.Text == text).Kind);

        // A second event of the same kind is a second toast.
        server.RaiseManipEvent(ManipEventKind.RefusedNotChallengeFile, 2);
        state.RefreshManips();
        Assert.True(await PumpAsync(state, () => ToastsSaying(state, text) == 2));
    }

    [Fact]
    public async Task AnEventFromBeforeTheClientLookedIsNotToasted()
    {
        using var server = new FakeQwarkServer();
        server.RaiseManipEvent(ManipEventKind.RefusedNoFile, 1);
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Manips is not null));
        await PumpForAsync(state, 200);

        Assert.Equal(ManipEventKind.RefusedNoFile, state.Manips!.EventKind);
        Assert.Equal(0, state.ManipToasts);
        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);
    }

    [Fact]
    public async Task APlanetChangeReadsTheListSoALoadsEventIsToastedOnAnyPanel()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Manips is not null && state.Positions.Slots.Length > 0));
        await PumpForAsync(state, 200);

        // A file load: the console records its event and the planet moves. Nobody has the
        // manips area open, and nobody asks.
        server.RaiseManipEvent(ManipEventKind.RefusedChallengeFile, 3);
        server.Session = server.Session with { CurrentPlanet = (byte)(server.Session.CurrentPlanet + 1) };

        Assert.True(await PumpAsync(state,
            () => ToastsSaying(state, "All Missions wasn't applied: this file is a challenge-mode file") == 1));
    }

    [Fact]
    public async Task ARefusedApplyIsToldByItsEventNotAsBusy()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Manips is not null));

        server.ManipFileLoaded = false;
        state.SelectManipGroup(1);
        Assert.True(await PumpAsync(state, () => state.Manips?.Selected == 1));

        state.ApplyManips();
        const string text = "NG+ (poki skip) wasn't applied: load a file first";
        Assert.True(await PumpAsync(state, () => ToastsSaying(state, text) == 1));
        await PumpForAsync(state, 200);

        Assert.Equal(1, ToastsSaying(state, text));
        Assert.DoesNotContain(state.Toasts, toast => toast.Text.Contains("Busy"));
        Assert.Equal(1, server.ManipApplyCount);
    }

    [Fact]
    public async Task ApplyNowSaysWhatItDid()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Manips is not null));

        // A challenge-mode file is loaded. RaC1's groups are per-run writes alone, so picking one
        // writes nothing yet; Apply now is its first application this boot, which is announced.
        server.ChallengeCounter = 1;
        state.SelectManipGroup(1);
        Assert.True(await PumpAsync(state, () => state.Manips?.Selected == 1));
        Assert.Equal(0, state.ManipToasts);

        state.ApplyManips();
        Assert.True(await PumpAsync(state, () => ToastsSaying(state, "NG+ (poki skip) manips activated") == 1));
        Assert.Equal(ToastKind.Info, state.Toasts.Single(t => t.Text == "NG+ (poki skip) manips activated").Kind);

        // Apply now again is a later application, which the console reports quietly; the button
        // still says it went through.
        state.ApplyManips();
        Assert.True(await PumpAsync(state, () => ToastsSaying(state, "NG+ (poki skip) manips applied") == 1));
        Assert.Equal(ManipEventKind.PerRunApplied, state.Manips!.EventKind);
        Assert.Equal(ToastKind.Success, state.Toasts.Single(t => t.Text == "NG+ (poki skip) manips applied").Kind);
    }

    [Fact]
    public async Task PickingASetupGroupOnTheWrongFileIsToldAtOnce()
    {
        using var server = new FakeQwarkServer();
        server.Describe = FakeQwarkServer.WithManipHeaderFeatures(server.Describe with { Game = GameId.Rac3 }, GameId.Rac3);
        server.Session = server.Session with { Game = GameId.Rac3, TitleId = "NPEA00387" };
        server.ChallengeCounter = 0;
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Manips is not null));

        // UYA's NG+ no QE is a setup, which goes in at once with a file in play: on a fresh file the
        // console refuses, and the select's own OK is not the end of it.
        state.SelectManipGroup(1);
        Assert.True(await PumpAsync(state,
            () => ToastsSaying(state, "NG+ no QE wasn't applied: this file isn't a challenge-mode file") == 1));
    }

    /// <summary>
    /// The case the planet-change read missed: a save loaded on the first planet starts a run
    /// without the planet number moving, and nobody has the manips area open. The timed read finds
    /// the event all the same.
    /// </summary>
    [Fact]
    public async Task ARunStartedWithoutThePlanetMovingIsToastedOnAnyPanel()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Manips is not null));
        server.ChallengeCounter = 0;
        state.SelectManipGroup(1);
        Assert.True(await PumpAsync(state, () => state.Manips?.Selected == 1));
        await PumpForAsync(state, 200);

        byte planet = server.Session.CurrentPlanet;
        server.ManipRunStart();

        const string text = "NG+ (poki skip) wasn't applied: this file isn't a challenge-mode file";
        Assert.True(await PumpAsync(state, () => ToastsSaying(state, text) == 1, timeoutMs: 6000));
        Assert.Equal(planet, server.Session.CurrentPlanet);

        // And once only, however many timed reads follow.
        await PumpForAsync(state, (int)(AppState.ManipPollSeconds * 1000 * 2) + 200);
        Assert.Equal(1, ToastsSaying(state, text));
    }

    [Fact]
    public async Task AModuleWithoutTheOpsHasNoAreaAndIsNotAskedAgain()
    {
        using var server = new FakeQwarkServer();
        server.ManipsUnknown = true;
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0
                                                 && server.RequestLog().Contains(Opcode.ManipList)));
        await PumpForAsync(state, 300);
        server.ClearRequestLog();

        // An older module is a game with no manips as far as the page is concerned: no area, no
        // toast, and no timed reads of an op it does not know.
        await PumpForAsync(state, (int)(AppState.ManipPollSeconds * 1000 * 2) + 300);

        Assert.Null(state.Manips);
        Assert.False(HasManipsSection(state));
        Assert.DoesNotContain(Opcode.ManipList, server.RequestLog());
        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);
    }

    /// <summary>
    /// A connection to a module that restarted: its event numbers start again and can meet the old
    /// module's by chance. Its first answer is a starting point, so an event from before it is not
    /// replayed, and the next event is told even when the old module had used its number.
    /// </summary>
    [Fact]
    public async Task AReconnectToARestartedModuleNeitherReplaysNorSwallows()
    {
        using var first = new FakeQwarkServer();
        first.Start();
        using var state = await ConnectedStateAsync(first);

        Assert.True(await PumpAsync(state, () => state.Manips is not null));
        await PumpForAsync(state, 200);

        // One event on the first module: number 1, told once.
        first.RaiseManipEvent(ManipEventKind.RefusedNotChallengeFile, 2);
        const string told = "NG+ (no poki skip) wasn't applied: this file isn't a challenge-mode file";
        state.RefreshManips();
        Assert.True(await PumpAsync(state, () => state.ManipToasts == 1));
        Assert.Equal(1, ToastsSaying(state, told));

        // The module restarts. Before the client is back, the new one raises two events of its own
        // (numbers 1 and 2), both shown on the console while no PC was looking.
        using var second = new FakeQwarkServer();
        second.RaiseManipEvent(ManipEventKind.RefusedNotChallengeFile, 2);
        second.RaiseManipEvent(ManipEventKind.RefusedChallengeFile, 3);
        second.Start();

        await state.Client.ConnectAsync("127.0.0.1", second.Port);
        Assert.True(await PumpAsync(state, () => state.Manips?.EventSeq == 2));
        await PumpForAsync(state, 300);

        Assert.Equal(1, state.ManipToasts);
        Assert.Equal(0, ToastsSaying(state, "All Missions wasn't applied: this file is a challenge-mode file"));

        // A third module restart, reached with nothing raised yet; its first event is number 1,
        // which the first module had used too. It is told.
        using var third = new FakeQwarkServer();
        third.Start();
        await state.Client.ConnectAsync("127.0.0.1", third.Port);
        Assert.True(await PumpAsync(state, () => state.Manips is { EventSeq: 0 }));
        await PumpForAsync(state, 200);

        third.RaiseManipEvent(ManipEventKind.RefusedNotChallengeFile, 2);
        state.RefreshManips();
        Assert.True(await PumpAsync(state, () => state.ManipToasts == 2));
        Assert.Contains(state.Toasts, toast => toast.Text == told);
    }

    [Fact]
    public async Task TheCheckboxesShowWhatTheConsoleHolds()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Manips is not null));
        Assert.True(state.Manips!.AutoReset);
        Assert.False(state.Manips.Remember);

        state.SetManipOptions(autoReset: false, remember: true);
        Assert.True(await PumpAsync(state, () => state.Manips is { AutoReset: false, Remember: true }));
        Assert.Equal(new byte[] { 0, 1 }, server.LastPayload(Opcode.ManipOptions));

        // With auto-reset off and a per-run group picked, the warning shows.
        state.SelectManipGroup(3);
        Assert.True(await PumpAsync(state, () => state.Manips?.Selected == 3));
        Assert.Equal(
            "All Missions has manips that must be reset every run. Turn on auto-reset to apply them automatically.",
            ManipsArea.Warning(state.Manips!));
    }

    [Fact]
    public async Task NothingIsAskedOfAConsoleThatIsLaunchingOrAtTheXmb()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Manips is not null));

        server.Quitting = true;
        Assert.True(await PumpAsync(state, () => state.ConsoleBusy));
        server.ClearRequestLog();

        state.RefreshManips();
        await PumpForAsync(state, 200);
        Assert.DoesNotContain(Opcode.ManipList, server.RequestLog());

        // And the area keeps what it had through the quit, greyed out rather than gone.
        Assert.NotNull(state.Manips);
    }
}

/// <summary>
/// The headers of MANIP_LIST: the features each one lists are drawn under it in the manips area,
/// whatever their kind, and not in their own group; the Manips section is there for a game with
/// groups even with no feature in it.
/// </summary>
public class ManipHeaderTests
{
    private const string UnknownTitle = "ZZZZ99999";

    private static readonly string[] Groups = { "Cheats", "Player", "Progress" };

    private static Feature Toggle(byte id, string label, byte group = 0) =>
        new(id, FeatureKind.Toggle, group, 0, FeatureFlags.None, 0xFF, 0, 0, label);

    private static Feature Action(byte id, string label, byte group = 2) =>
        new(id, FeatureKind.Action, group, 0, FeatureFlags.None, 0xFF, 0, 0, label);

    private static readonly DescribeResult Deadlocked = new(GameId.None, Groups, Array.Empty<string>(), new[]
    {
        Toggle(0, "Infinite ammo"),
        Toggle(11, "Crash patches"),
        Toggle(12, "Fix reset softlocks"),
    });

    /// <summary>UYA's shape: a button in Progress and the new toggle for it, both under Trophies.</summary>
    private static readonly DescribeResult Uya = new(GameId.None, Groups, Array.Empty<string>(), new[]
    {
        Toggle(0, "Infinite ammo"),
        Action(21, "Reset trophies"),
        Action(22, "Unlock all skill points"),
        Toggle(39, "Reset trophies on Veldin load", group: 2),
    });

    private static ManipList List(params ManipHeader[] headers) =>
        new(0, true, false, 0, ManipEventKind.None, 0,
            new[] { new ManipGroup(1, ManipGroupFlags.NgPlus, "NG+", new[] { "NG+ no LC", "NG+" }) }, headers);

    private static ManipHeader Header(string name, params ushort[] ids) => new(name, ids);

    [Fact]
    public void TheListedFeaturesLeaveTheirGroupForTheManipsSection()
    {
        var sections = GamePanel.SectionsFor(UnknownTitle, GameId.None, Deadlocked, List(Header("Fixes", 11, 12)));

        Assert.Equal(new[] { "Crash patches", "Fix reset softlocks" },
            sections[GameLayout.ManipsSection].Select(f => f.Label));
        Assert.Equal(new[] { "Infinite ammo" }, sections["Cheats"].Select(f => f.Label));
    }

    [Fact]
    public void AButtonAndAToggleGoUnderTheirHeadingTogether()
    {
        var list = List(Header("Trophies", 21, 39));
        var sections = GamePanel.SectionsFor(UnknownTitle, GameId.None, Uya, list);

        // Both leave Progress, where the button and the toggle were described, and the button that
        // no header lists stays there.
        Assert.Equal(new[] { "Unlock all skill points" }, sections["Progress"].Select(f => f.Label));

        var block = Assert.Single(ManipsArea.HeaderBlocks(list, sections[GameLayout.ManipsSection]));
        Assert.Equal("Trophies", block.Heading);
        Assert.Equal(new[] { "Reset trophies", "Reset trophies on Veldin load" }, block.Features.Select(f => f.Label));
        Assert.Equal(new[] { FeatureKind.Action, FeatureKind.Toggle }, block.Features.Select(f => f.Kind));
    }

    [Fact]
    public void EachHeadingKeepsItsOwnFeaturesInTheHeadersOrder()
    {
        var both = new DescribeResult(GameId.None, Groups, Array.Empty<string>(),
            Deadlocked.Features.Concat(Uya.Features.Skip(1)).ToArray());
        var list = List(Header("Trophies", 39, 21), Header("Fixes", 12, 11));

        var blocks = ManipsArea.HeaderBlocks(list, both.Features);

        Assert.Equal(new[] { "Trophies", "Fixes" }, blocks.Select(b => b.Heading));
        Assert.Equal(new[] { "Reset trophies on Veldin load", "Reset trophies" }, blocks[0].Features.Select(f => f.Label));
        Assert.Equal(new[] { "Fix reset softlocks", "Crash patches" }, blocks[1].Features.Select(f => f.Label));
    }

    [Fact]
    public void AFeatureTwoHeadersListIsDrawnOnceAndAnEmptyHeadingNotAtAll()
    {
        var list = List(Header("Fixes", 11, 12), Header("Again", 12), Header("Nothing", 77));

        var blocks = ManipsArea.HeaderBlocks(list, Deadlocked.Features);

        var fixes = Assert.Single(blocks);
        Assert.Equal("Fixes", fixes.Heading);
        Assert.Equal(new[] { "Crash patches", "Fix reset softlocks" }, fixes.Features.Select(f => f.Label));
    }

    [Fact]
    public void WithoutAListTheyStayWhereTheirGroupPutsThem()
    {
        var sections = GamePanel.SectionsFor(UnknownTitle, GameId.None, Deadlocked);

        Assert.Equal(new[] { "Infinite ammo", "Crash patches", "Fix reset softlocks" },
            sections["Cheats"].Select(f => f.Label));
        Assert.False(sections.ContainsKey(GameLayout.ManipsSection));
    }

    [Fact]
    public void AGameWithGroupsAndNoHeadersStillHasTheSection()
    {
        var sections = GamePanel.SectionsFor(UnknownTitle, GameId.None, Deadlocked, List());

        Assert.Empty(sections[GameLayout.ManipsSection]);
        Assert.Equal(3, sections["Cheats"].Count);
    }

    [Fact]
    public void AnIdThatNamesNoFeatureMovesNothing()
    {
        var sections = GamePanel.SectionsFor(UnknownTitle, GameId.None, Deadlocked,
            List(Header("Fixes", 12, 40, 0x0100 + 11)));

        Assert.Equal(new[] { "Fix reset softlocks" }, sections[GameLayout.ManipsSection].Select(f => f.Label));
        Assert.Equal(new[] { "Infinite ammo", "Crash patches" }, sections["Cheats"].Select(f => f.Label));
    }

    [Fact]
    public void TheFakeConsoleAddsEachGamesHeadingFeaturesOnce()
    {
        var plain = new DescribeResult(GameId.Rac4, Groups, Array.Empty<string>(), new[] { Toggle(0, "Infinite ammo") });

        var deadlocked = FakeQwarkServer.WithManipHeaderFeatures(plain, GameId.Rac4);
        Assert.Equal(new[] { "Infinite ammo", "Crash patches", "Fix reset softlocks" }, deadlocked.Features.Select(f => f.Label));
        Assert.True(deadlocked.Features.Single(f => f.Label == "Crash patches").WritesCode);
        Assert.Equal(3, FakeQwarkServer.WithManipHeaderFeatures(deadlocked, GameId.Rac4).Features.Length);

        var uya = FakeQwarkServer.WithManipHeaderFeatures(plain with { Game = GameId.Rac3 }, GameId.Rac3);
        var reset = uya.Features.Single(f => f.Label == FakeQwarkServer.TrophyResetLabel);
        var auto = uya.Features.Single(f => f.Label == FakeQwarkServer.TrophyAutoResetLabel);
        Assert.Equal((FeatureKind.Action, (byte)21), (reset.Kind, reset.Id));
        Assert.Equal((FeatureKind.Toggle, (byte)39), (auto.Kind, auto.Id));
        Assert.Equal(FeatureFlags.None, reset.Flags);
        Assert.Equal(FeatureFlags.None, auto.Flags);

        // An id the table already uses is not taken twice: the next free one is.
        var crowded = plain with { Game = GameId.Rac3, Features = new[] { Toggle(21, "Something else") } };
        var moved = FakeQwarkServer.WithManipHeaderFeatures(crowded, GameId.Rac3);
        Assert.Equal(22, moved.Features.Single(f => f.Label == FakeQwarkServer.TrophyResetLabel).Id);
        Assert.Equal(39, moved.Features.Single(f => f.Label == FakeQwarkServer.TrophyAutoResetLabel).Id);

        // RaC1 and RaC2 have no headings, so nothing is added.
        Assert.Single(FakeQwarkServer.WithManipHeaderFeatures(plain, GameId.Rac2).Features);
    }
}
