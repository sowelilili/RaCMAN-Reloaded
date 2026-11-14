using System.Diagnostics;

using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// Per-user settings, revision 1.24 (qwark build 56): SessionInfo bytes 29 and 30, PROFILE_INFO,
/// PROFILE_ENABLE, COMBO_OVERRIDE and the COMBO_LIST flags byte, the fake console's per-user
/// stores, and what the Combos panel and AppState make of them.
/// </summary>
public class ProfileTests
{
    private const string Mod = "crash-patch";

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

    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync(bool profiles = true)
    {
        var server = new FakeQwarkServer { ProfilesSupported = profiles };
        server.Start();

        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    private static async Task<SessionInfo> SessionAsync(QwarkClient client) => (await client.GetStateAsync()).Session;

    /// <summary>The fake console goes to the XMB, where qwark looks at who is signed in.</summary>
    private static void ToXmb(FakeQwarkServer server) =>
        server.Session = server.Session with { State = SessionState.Xmb, Game = GameId.None, TitleId = string.Empty };

    // ------------------------------------------------------------------ the wire

    [Fact]
    public void SessionInfoCarriesTheProfileFlagsAtByte29AndTheSequenceAtByte30()
    {
        var session = SessionInfo.Empty with
        {
            CurrentPlanet = 7,
            ProfileFlags = ProfileFlags.Supported | ProfileFlags.On | ProfileFlags.Active,
            ProfileSeq = 200,
        };

        var bytes = session.ToBytes();
        Assert.Equal(7, bytes[28]);
        Assert.Equal(0x0E, bytes[29]);
        Assert.Equal(200, bytes[30]);
        Assert.Equal(0, bytes[31]);

        var parsed = SessionInfo.Parse(bytes);
        Assert.True(parsed.ProfilesSupported);
        Assert.True(parsed.ProfilesOn);
        Assert.True(parsed.ProfileActive);
        Assert.Equal(200, parsed.ProfileSeq);

        bytes[29] = (byte)ProfileFlags.Supported;
        var off = SessionInfo.Parse(bytes);
        Assert.True(off.ProfilesSupported);
        Assert.False(off.ProfilesOn);
        Assert.False(off.ProfileActive);
    }

    /// <summary>
    /// Byte 29 is flags2: bit0 is USB_MODS, the profile bits are 1 to 3, and bits 4 to 7 are
    /// reserved. HAS_PROFILE is PROFILE_INFO's bit5 alone.
    /// </summary>
    [Fact]
    public void OnlyBitsOneToThreeAndHasProfileAreNamed()
    {
        Assert.Equal(new[] { 0, 2, 4, 8, 0x20 }, Enum.GetValues<ProfileFlags>().Select(f => (int)f).Order().ToArray());
        Assert.Equal(0x0E, (int)SessionInfo.ProfileBits);
    }

    /// <summary>Byte 29 bit0 is USB_MODS, another feature's: on its own it means no per-user settings at all.</summary>
    [Fact]
    public void Byte29Bit0AloneMeansNoProfiles()
    {
        var bytes = (SessionInfo.Empty with { QwarkVersion = 56 }).ToBytes();
        bytes[29] = 0x01;

        var parsed = SessionInfo.Parse(bytes);
        Assert.Equal(ProfileFlags.None, parsed.ProfileFlags);
        Assert.False(parsed.ProfilesSupported);
        Assert.False(parsed.ProfilesOn);
        Assert.False(parsed.ProfileActive);
        Assert.False(CombosPanel.ShowsProfiles(parsed));
        Assert.False(CombosPanel.ShowsOverride(parsed));

        // And beside the profile bits it changes nothing about them.
        bytes[29] = 0x01 | 0x02 | 0x04;
        parsed = SessionInfo.Parse(bytes);
        Assert.Equal(ProfileFlags.Supported | ProfileFlags.On, parsed.ProfileFlags);
        Assert.False(parsed.ProfileActive);
    }

    /// <summary>
    /// COMBO_LIST's byte after each action was pad; since revision 1.24 its bit0 says the mask is
    /// the active profile's override.
    /// </summary>
    [Fact]
    public void ComboListCarriesTheOverriddenFlagAfterEachAction()
    {
        byte[] payload =
        {
            2,
            (byte)ComboAction.Die, 0x01, 0, 0, 0x00, 0x00, 0x00, 0x44,
            (byte)ComboAction.OpenConsoleMenu, 0x00, 0, 0, 0x00, 0x00, 0x06, 0x0F,
        };

        var list = ComboEntry.ParseList(payload);
        Assert.Equal(new ComboEntry(ComboAction.Die, 0x44, ComboEntryFlags.Overridden), list[0]);
        Assert.True(list[0].Overridden);
        Assert.Equal(new ComboEntry(ComboAction.OpenConsoleMenu, ComboDefaults.OpenConsoleMenu), list[1]);
        Assert.False(list[1].Overridden);
    }

    /// <summary>Bytes 29 to 31 were pad before build 56, so an older module reports no profiles at all.</summary>
    [Fact]
    public void AnOlderModulesPadReadsAsNoProfiles()
    {
        var parsed = SessionInfo.Parse(new SessionInfo(1, 55, SessionState.Ingame, GameId.Rac1, 1, 2, "NPEA00385",
            SessionFlags.None, 0, 0, PlanetFlags.None, 0, 0, 0, 0, 0, new float[4], new uint[SessionInfo.ReadoutCount],
            0, 0, 0, 0, 0, 0).ToBytes());

        Assert.Equal(ProfileFlags.None, parsed.ProfileFlags);
        Assert.Equal(0, parsed.ProfileSeq);
        Assert.False(parsed.ProfilesSupported);
    }

    [Fact]
    public void ProfileInfoIsSixtyFourBigEndianBytes()
    {
        var payload = new byte[ProfileInfo.WireSize];
        payload[0] = (byte)(ProfileFlags.Supported | ProfileFlags.On | ProfileFlags.Active | ProfileFlags.HasProfile);
        Assert.Equal(0x2E, payload[0]);
        payload[1] = 1;
        payload[2] = 9;
        payload[3] = 0xEE; // pad, ignored
        payload[4] = 0x00;
        payload[5] = 0x00;
        payload[6] = 0x01;
        payload[7] = 0x02;
        System.Text.Encoding.ASCII.GetBytes("user-00000258").CopyTo(payload, 8);
        System.Text.Encoding.UTF8.GetBytes("ZoÃ«").CopyTo(payload, 32);

        var info = ProfileInfo.Parse(payload);
        Assert.True(info.Supported);
        Assert.True(info.On);
        Assert.True(info.Active);
        Assert.True(info.HasProfile);
        Assert.Equal(ProfileKind.User, info.Kind);
        Assert.Equal(9, info.Seq);
        Assert.Equal(0x102u, info.UserId);
        Assert.Equal("user-00000258", info.Folder);
        Assert.Equal("ZoÃ«", info.Name);

        // A longer reply is a later revision's; its first 64 bytes are these.
        var longer = payload.Concat(new byte[16]).ToArray();
        Assert.Equal(info, ProfileInfo.Parse(longer));

        // And the fake's own encoding lays it out the same way.
        Assert.Equal(info with { }, ProfileInfo.Parse(info.ToBytes()));
    }

    [Fact]
    public void AShortProfileInfoReplyIsRefused()
    {
        Assert.Throws<ProtocolException>(() => ProfileInfo.Parse(new byte[ProfileInfo.WireSize - 1]));
        Assert.Throws<ProtocolException>(() => ProfileInfo.Parse(Array.Empty<byte>()));
    }

    [Fact]
    public async Task TheThreeOpsAreLaidOutAsTheSpecSays()
    {
        Assert.Equal(0x0093, (int)Opcode.ProfileInfo);
        Assert.Equal(0x0094, (int)Opcode.ProfileEnable);
        Assert.Equal(0x0095, (int)Opcode.ComboOverride);

        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.ProfileEnableAsync(true, ProfileSetup.Fresh);
            Assert.Equal(new byte[] { 1, 2 }, server.LastPayload(Opcode.ProfileEnable));

            await client.ComboOverrideAsync(ComboAction.Die, 0x01020304);
            Assert.Equal(new byte[] { 2, 1, 0, 0, 1, 2, 3, 4 }, server.LastPayload(Opcode.ComboOverride));

            await client.ComboOverrideClearAsync(ComboAction.LoadPlanet);
            Assert.Equal(new byte[] { 3, 0, 0, 0, 0, 0, 0, 0 }, server.LastPayload(Opcode.ComboOverride));

            await client.ProfileInfoAsync();
            Assert.Empty(server.LastPayload(Opcode.ProfileInfo)!);
        }
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, 3)]
    [InlineData(255, 255)]
    public async Task ProfileEnableRefusesAnOnOrSetupOutOfRange(byte on, byte setup)
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.RequestAsync(Opcode.ProfileEnable, new[] { on, setup }));
            Assert.Equal(Status.BadArg, refused.Status);
            Assert.False(server.ProfilesEnabled);
        }
    }

    [Theory]
    [InlineData(7, 1)]
    [InlineData(255, 0)]
    [InlineData(2, 2)]
    public async Task ComboOverrideRefusesAnActionOrSetOutOfRange(byte action, byte set)
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.ProfileEnableAsync(true, ProfileSetup.Fresh);
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.RequestAsync(Opcode.ComboOverride, new byte[] { action, set, 0, 0, 0, 0, 0, 0x44 }));
            Assert.Equal(Status.BadArg, refused.Status);
            Assert.Empty(server.ComboOverridesFor(1));
        }
    }

    // ------------------------------------------------------------------ the switch and the setup

    [Fact]
    public async Task OffTheConsoleReportsTheSignedInUserAndTheSharedSettings()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var session = await SessionAsync(client);
            Assert.True(session.ProfilesSupported);
            Assert.False(session.ProfilesOn);
            Assert.False(session.ProfileActive);

            var info = await client.ProfileInfoAsync();
            Assert.Equal(ProfileFlags.Supported, info.Flags);
            Assert.Equal(ProfileKind.Shared, info.Kind);
            Assert.Equal(1u, info.UserId);
            Assert.Equal("user-00000001", info.Folder);
            Assert.Equal(string.Empty, info.Name);
            Assert.False(info.HasProfile);

            // The name is reserved for named profiles; the fake keeps a hook for that path.
            server.ProfileNames[1] = "Alex";
            Assert.Equal("Alex", (await client.ProfileInfoAsync()).Name);
        }
    }

    [Fact]
    public async Task TurningOnWithCopyStartsTheUserFromTheSharedSettings()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.MenuHelpAsync(false);
            await client.ModSetAutoAsync(Mod, true);
            byte seq = (await SessionAsync(client)).ProfileSeq;

            await client.ProfileEnableAsync(true, ProfileSetup.Copy);

            var session = await SessionAsync(client);
            Assert.True(session.ProfilesOn);
            Assert.True(session.ProfileActive);
            Assert.Equal((byte)(seq + 1), session.ProfileSeq);
            Assert.True(session.MenuHelpOff);

            var info = await client.ProfileInfoAsync();
            Assert.True(info.HasProfile);
            Assert.Equal(ProfileKind.User, info.Kind);
            Assert.True((await client.ModListAsync()).Single(m => m.DirName == Mod).Auto);

            // The user's settings are their own now: a change stays in the profile.
            await client.MenuHelpAsync(true);
            Assert.True(server.MenuHelpFor(1));
            Assert.False(server.MenuHelpFor(0));

            await client.ProfileEnableAsync(false, ProfileSetup.None);
            Assert.True((await SessionAsync(client)).MenuHelpOff);
        }
    }

    [Fact]
    public async Task TurningOnWithDefaultsStartsTheUserFresh()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.MenuHelpAsync(false);
            await client.ModSetAutoAsync(Mod, true);

            await client.ProfileEnableAsync(true, ProfileSetup.Fresh);

            Assert.False((await SessionAsync(client)).MenuHelpOff);
            Assert.False((await client.ModListAsync()).Single(m => m.DirName == Mod).Auto);
            Assert.True(server.HasProfile(1));

            // The shared settings are untouched, and come back with the switch off.
            await client.ProfileEnableAsync(false, ProfileSetup.None);
            Assert.True((await SessionAsync(client)).MenuHelpOff);
            Assert.True((await client.ModListAsync()).Single(m => m.DirName == Mod).Auto);
        }
    }

    /// <summary>A profile that exists is never set up again: HAS_PROFILE survives the switch going off, and a second setup is ignored.</summary>
    [Fact]
    public async Task AProfileThatExistsIsKeptAndNeverOverwritten()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.ProfileEnableAsync(true, ProfileSetup.Fresh);
            await client.MenuHelpAsync(false);
            await client.ProfileEnableAsync(false, ProfileSetup.None);

            var off = await client.ProfileInfoAsync();
            Assert.False(off.On);
            Assert.True(off.HasProfile);
            Assert.False((await SessionAsync(client)).MenuHelpOff);

            // The copy is ignored: the shared menu.help (on) did not replace the profile's (off).
            await client.ProfileEnableAsync(true, ProfileSetup.Copy);
            Assert.True((await SessionAsync(client)).MenuHelpOff);
            Assert.True(server.MenuHelpFor(0));
            Assert.False(server.MenuHelpFor(1));
        }
    }

    [Fact]
    public async Task OnWithNobodySignedInWaitsForAUser()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.SignedInUser = 0;
            await client.ProfileEnableAsync(true, ProfileSetup.Copy);

            var session = await SessionAsync(client);
            Assert.True(session.ProfilesOn);
            Assert.False(session.ProfileActive);

            var info = await client.ProfileInfoAsync();
            Assert.Equal(0u, info.UserId);
            Assert.Equal(string.Empty, info.Folder);
            Assert.Equal(ProfileKind.Shared, info.Kind);
            Assert.False(server.HasProfile(0));
        }
    }

    // ------------------------------------------------------------------ users

    [Fact]
    public async Task ASecondUserAtTheXmbStartsFromTheDefaultsAndMovesTheSequence()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.MenuHelpAsync(false);
            await client.ProfileEnableAsync(true, ProfileSetup.Copy);
            Assert.True((await SessionAsync(client)).MenuHelpOff);

            // In a game qwark does not look: the change waits for the XMB.
            server.SignInAtXmb(2);
            Assert.Equal(1u, server.SignedInUser);
            byte before = (await SessionAsync(client)).ProfileSeq;

            ToXmb(server);
            Assert.Equal(2u, server.SignedInUser);

            var session = await SessionAsync(client);
            Assert.True(session.ProfileActive);
            Assert.Equal((byte)(before + 1), session.ProfileSeq);
            Assert.Equal(0, (int)session.ProfileFlags & 0xF1);

            // No copy for a user who signs in later: the defaults, and no profile until a write.
            Assert.False(session.MenuHelpOff);

            var info = await client.ProfileInfoAsync();
            Assert.Equal(2u, info.UserId);
            Assert.Equal("user-00000002", info.Folder);
            Assert.Equal(string.Empty, info.Name);
            Assert.Equal(ProfileKind.User, info.Kind);
            Assert.False(info.HasProfile);
        }
    }

    /// <summary>The setup acts only as the switch goes on: an off with Copy sets nothing up.</summary>
    [Fact]
    public async Task ASetupWithTheSwitchGoingOffIsIgnored()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.ProfileEnableAsync(false, ProfileSetup.Copy);
            Assert.False(server.HasProfile(1));
            Assert.False((await client.ProfileInfoAsync()).HasProfile);
        }
    }

    [Fact]
    public async Task PerUserKeysAreSeparateBetweenUsers()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.ProfileEnableAsync(true, ProfileSetup.Fresh);
            await client.MenuHelpAsync(false);
            await client.ModSetAutoAsync(Mod, true);

            ToXmb(server);
            server.SignInAtXmb(2);

            Assert.False((await SessionAsync(client)).MenuHelpOff);
            Assert.False((await client.ModListAsync()).Single(m => m.DirName == Mod).Auto);
            await client.ModSetAutoAsync("flight", true);

            server.SignInAtXmb(1);
            Assert.True((await SessionAsync(client)).MenuHelpOff);
            var mods = await client.ModListAsync();
            Assert.True(mods.Single(m => m.DirName == Mod).Auto);
            Assert.False(mods.Single(m => m.DirName == "flight").Auto);

            Assert.True(server.ModAutoFor(2, "flight"));
            Assert.False(server.ModAutoFor(2, Mod));
            Assert.False(server.MenuHelpFor(1));
            Assert.True(server.MenuHelpFor(2));
        }
    }

    /// <summary>A user with no profile starts from the defaults, and the first write creates it: a single key write, so no sequence move.</summary>
    [Fact]
    public async Task AWriteToAUserWithNoProfileCreatesItFromTheDefaults()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.ModSetAutoAsync(Mod, true);
            await client.ProfileEnableAsync(true, ProfileSetup.None);
            Assert.False(server.HasProfile(1));
            Assert.False((await client.ModListAsync()).Single(m => m.DirName == Mod).Auto);

            byte before = (await SessionAsync(client)).ProfileSeq;
            await client.MenuHelpAsync(false);

            Assert.Equal(before, (await SessionAsync(client)).ProfileSeq);
            Assert.True(server.HasProfile(1));
            Assert.True((await client.ProfileInfoAsync()).HasProfile);
            Assert.False(server.ModAutoFor(1, Mod));
            Assert.True(server.ModAutoFor(0, Mod));
        }
    }

    // ------------------------------------------------------------------ combo overrides

    private const uint Shared = (uint)PadButton.L1 | (uint)PadButton.Cross;
    private const uint Own = (uint)PadButton.R1 | (uint)PadButton.Circle;

    private static async Task<ComboEntry> RowAsync(QwarkClient client, ComboAction action) =>
        (await client.ComboListAsync()).Single(c => c.Action == action);

    [Fact]
    public async Task AnOverrideWinsForItsComboAndClearingItBringsTheSharedOneBack()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.ComboSetAsync(ComboAction.Die, Shared);
            await client.ProfileEnableAsync(true, ProfileSetup.Copy);

            // On, and still the shared combos, none flagged.
            Assert.Equal(new ComboEntry(ComboAction.Die, Shared), await RowAsync(client, ComboAction.Die));

            await client.ComboOverrideAsync(ComboAction.Die, Own);
            Assert.Equal(new ComboEntry(ComboAction.Die, Own, ComboEntryFlags.Overridden), await RowAsync(client, ComboAction.Die));

            // The other rows are the shared ones, unflagged.
            Assert.False((await RowAsync(client, ComboAction.OpenConsoleMenu)).Overridden);
            Assert.Equal(Shared, server.Combos[ComboAction.Die]);

            // Cleared: the shared combo is in force again.
            await client.ComboOverrideClearAsync(ComboAction.Die);
            Assert.Equal(new ComboEntry(ComboAction.Die, Shared), await RowAsync(client, ComboAction.Die));

            // Removing an override that is not there is OK.
            await client.ComboOverrideClearAsync(ComboAction.Die);
        }
    }

    [Fact]
    public async Task AnOverrideCanTurnAComboOffOrGiveOneTheSharedSetLacks()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.ProfileEnableAsync(true, ProfileSetup.Fresh);

            await client.ComboOverrideAsync(ComboAction.OpenConsoleMenu, 0);
            Assert.Equal(new ComboEntry(ComboAction.OpenConsoleMenu, 0, ComboEntryFlags.Overridden),
                await RowAsync(client, ComboAction.OpenConsoleMenu));

            Assert.False(server.Combos.ContainsKey(ComboAction.ToggleFastLoads));
            await client.ComboOverrideAsync(ComboAction.ToggleFastLoads, Own);
            Assert.Equal(new ComboEntry(ComboAction.ToggleFastLoads, Own, ComboEntryFlags.Overridden),
                await RowAsync(client, ComboAction.ToggleFastLoads));
        }
    }

    [Fact]
    public async Task OverridesStayInTheProfileWhileTheSwitchIsOff()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.ComboSetAsync(ComboAction.Die, Shared);
            await client.ProfileEnableAsync(true, ProfileSetup.Fresh);
            await client.ComboOverrideAsync(ComboAction.Die, Own);

            await client.ProfileEnableAsync(false, ProfileSetup.None);
            Assert.Equal(new ComboEntry(ComboAction.Die, Shared), await RowAsync(client, ComboAction.Die));
            Assert.Equal(Own, server.ComboOverridesFor(1)[ComboAction.Die]);

            await client.ProfileEnableAsync(true, ProfileSetup.None);
            Assert.Equal(new ComboEntry(ComboAction.Die, Own, ComboEntryFlags.Overridden), await RowAsync(client, ComboAction.Die));
        }
    }

    /// <summary>COMBO_SET always writes the shared mask; under an override the user sees it only once the override goes.</summary>
    [Fact]
    public async Task ComboSetUnderAnOverrideWritesTheSharedMask()
    {
        const uint Later = (uint)PadButton.Select | (uint)PadButton.Square;

        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.ComboSetAsync(ComboAction.Die, Shared);
            await client.ProfileEnableAsync(true, ProfileSetup.Fresh);
            await client.ComboOverrideAsync(ComboAction.Die, Own);

            await client.ComboSetAsync(ComboAction.Die, Later);
            Assert.Equal(new ComboEntry(ComboAction.Die, Own, ComboEntryFlags.Overridden), await RowAsync(client, ComboAction.Die));
            Assert.Equal(Later, server.Combos[ComboAction.Die]);

            await client.ComboOverrideClearAsync(ComboAction.Die);
            Assert.Equal(new ComboEntry(ComboAction.Die, Later), await RowAsync(client, ComboAction.Die));
        }
    }

    /// <summary>The combo switch is the console's: a user's profile does not carry it.</summary>
    [Fact]
    public async Task TheCombosSwitchIsConsoleWide()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.ProfileEnableAsync(true, ProfileSetup.Fresh);
            await client.ComboEnableAsync(false);

            await client.ProfileEnableAsync(false, ProfileSetup.None);
            Assert.True((await SessionAsync(client)).CombosOff);

            ToXmb(server);
            server.SignInAtXmb(2);
            await client.ProfileEnableAsync(true, ProfileSetup.None);
            Assert.True((await SessionAsync(client)).CombosOff);
        }
    }

    [Fact]
    public async Task AnOverrideNeedsAnActiveProfile()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.ComboOverrideAsync(ComboAction.Die, Own));
            Assert.Equal(Status.NotFound, refused.Status);

            server.SignedInUser = 0;
            await client.ProfileEnableAsync(true, ProfileSetup.None);
            refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.ComboOverrideClearAsync(ComboAction.Die));
            Assert.Equal(Status.NotFound, refused.Status);
        }
    }

    /// <summary>An override is a write: it creates a profile that has never been set up, from the defaults.</summary>
    [Fact]
    public async Task AnOverrideCreatesAProfileThatWasNeverSetUp()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.ProfileEnableAsync(true, ProfileSetup.None);
            Assert.False(server.HasProfile(1));

            await client.ComboOverrideAsync(ComboAction.Die, Own);
            Assert.True(server.HasProfile(1));
            Assert.True((await RowAsync(client, ComboAction.Die)).Overridden);
        }
    }

    // ------------------------------------------------------------------ where there are none

    [Fact]
    public async Task UnderRpcs3ThereAreNoProfiles()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.Emulator = true;
            server.NoCodePatches = true;

            var session = await SessionAsync(client);
            Assert.Equal(ProfileFlags.None, session.ProfileFlags);
            Assert.Equal(0, session.ProfileSeq);

            // PROFILE_INFO is answered with zeros, the other two refused.
            Assert.Equal(ProfileInfo.None, await client.ProfileInfoAsync());
            Assert.Equal(Status.Unsupported,
                (await Assert.ThrowsAsync<QwarkStatusException>(() => client.ProfileEnableAsync(true, ProfileSetup.Copy))).Status);
            Assert.Equal(Status.Unsupported,
                (await Assert.ThrowsAsync<QwarkStatusException>(() => client.ComboOverrideAsync(ComboAction.Die, 0))).Status);
            Assert.False(server.ProfilesEnabled);
        }
    }

    [Fact]
    public async Task AModuleWithoutProfilesReportsNone()
    {
        var (server, client) = await ConnectAsync(profiles: false);
        using (server)
        using (client)
        {
            Assert.False((await SessionAsync(client)).ProfilesSupported);
            Assert.Equal(ProfileInfo.None, await client.ProfileInfoAsync());
            Assert.Equal(Status.Unsupported,
                (await Assert.ThrowsAsync<QwarkStatusException>(() => client.ProfileEnableAsync(true, ProfileSetup.None))).Status);
        }
    }

    // ------------------------------------------------------------------ panel text

    private static readonly SessionInfo ConsoleSession = SessionInfo.Empty with { ProfileFlags = ProfileFlags.Supported };

    private static SessionInfo With(ProfileFlags flags) => ConsoleSession with { ProfileFlags = ProfileFlags.Supported | flags };

    /// <summary>User 1 as the real module reports them: the name field is empty for a console user.</summary>
    private static readonly ProfileInfo User1 =
        new(ProfileFlags.Supported, ProfileKind.User, 0, 1, "user-00000001", string.Empty);

    [Fact]
    public void TheSectionShowsOnlyWithTheCapabilityBitAndNeverUnderRpcs3()
    {
        Assert.True(CombosPanel.ShowsProfiles(ConsoleSession));
        Assert.False(CombosPanel.ShowsProfiles(SessionInfo.Empty with { QwarkVersion = 56 }));
        Assert.False(CombosPanel.ShowsProfiles(ConsoleSession with { Flags = SessionFlags.Emulator | SessionFlags.NoCodePatches }));
    }

    /// <summary>
    /// The switch is the console's, so it is a row of the Settings page's Console tab (it was the
    /// Combos panel's until the page had tabs, and the Qwark tab's until the second round), there
    /// only while a console with users is connected, and on no other tab.
    /// </summary>
    [Fact]
    public void TheSwitchIsOnTheConsoleTabOfTheSettingsPage()
    {
        var memory = new InputRateMemory();
        var connected = new SettingsContext(ConsoleSession, true, memory);

        Assert.Contains(SettingRow.PerUserSettings, SettingsLayout.Rows(SettingsTab.Console, connected));
        foreach (var tab in SettingsNav.Tabs.Where(t => t != SettingsTab.Console))
        {
            Assert.DoesNotContain(SettingRow.PerUserSettings, SettingsLayout.Rows(tab, connected));
        }

        Assert.DoesNotContain(SettingRow.PerUserSettings,
            SettingsLayout.Rows(SettingsTab.Console, connected with { Connected = false }));
        var rpcs3 = ConsoleSession with { Flags = SessionFlags.Emulator | SessionFlags.NoCodePatches };
        Assert.DoesNotContain(SettingRow.PerUserSettings,
            SettingsLayout.Rows(SettingsTab.Console, connected with { Session = rpcs3 }));
    }

    /// <summary>
    /// Whose settings the Console tab's Qwark table holds is its heading's: "Qwark (User 1)" while
    /// per-user settings are on and someone is signed in, a name if the console sends one, and plain
    /// "Qwark" with the switch off, with nobody signed in yet, or before PROFILE_INFO has said who.
    /// </summary>
    [Fact]
    public void TheQwarkTablesHeadingNamesTheSignedInUserOnlyWhilePerUserSettingsAreOn()
    {
        var active = With(ProfileFlags.On | ProfileFlags.Active);
        Assert.Equal("Qwark (User 1)", QwarkSettings.QwarkHeading(active, User1));
        Assert.Equal("Qwark (User 2)", QwarkSettings.QwarkHeading(active, User1 with { UserId = 2 }));

        // A name comes only with named profiles, a later revision.
        Assert.Equal("Qwark (Alex)", QwarkSettings.QwarkHeading(active, User1 with { Name = "Alex" }));

        Assert.Equal("Qwark", QwarkSettings.QwarkHeading(ConsoleSession, User1));
        Assert.Equal("Qwark", QwarkSettings.QwarkHeading(With(ProfileFlags.On), User1));
        Assert.Equal("Qwark", QwarkSettings.QwarkHeading(active, ProfileInfo.None));

        Assert.Equal("User 1's", CombosPanel.PossessiveName(User1));
        Assert.Equal("this user", CombosPanel.UserName(ProfileInfo.None));
    }

    [Fact]
    public void TheOverrideButtonShowsOnlyWithAnActiveProfileAndOnPlainRows()
    {
        var plain = new ComboEntry(ComboAction.Die, Shared);
        var overridden = new ComboEntry(ComboAction.Die, Own, ComboEntryFlags.Overridden);
        var active = With(ProfileFlags.On | ProfileFlags.Active);

        Assert.False(CombosPanel.ShowsOverride(ConsoleSession));
        Assert.False(CombosPanel.ShowsOverride(With(ProfileFlags.On)));
        Assert.True(CombosPanel.ShowsOverride(active));
        Assert.False(CombosPanel.ShowsOverride(SessionInfo.Empty with
        {
            Flags = SessionFlags.Emulator,
            ProfileFlags = ProfileFlags.Supported | ProfileFlags.On | ProfileFlags.Active,
        }));

        Assert.True(CombosPanel.ShowsOverrideButton(active, plain));
        Assert.False(CombosPanel.ShowsOverrideButton(active, overridden));
        Assert.False(CombosPanel.ShowsOverrideButton(With(ProfileFlags.On), plain));
    }

    /// <summary>
    /// Build 57 on hardware: the line saying the user has overridden combos showed whenever a
    /// user's profile was active, with every row green. It shows only while a drawn row is orange.
    /// </summary>
    [Fact]
    public void TheOverriddenCombosLineShowsOnlyWithAnOverride()
    {
        var active = With(ProfileFlags.On | ProfileFlags.Active);
        var plain = new[]
        {
            new ComboEntry(ComboAction.Die, Shared),
            new ComboEntry(ComboAction.SavePosition, 0),
        };
        var oneOverridden = new[]
        {
            new ComboEntry(ComboAction.Die, Own, ComboEntryFlags.Overridden),
            new ComboEntry(ComboAction.SavePosition, 0),
        };

        Assert.False(CombosPanel.ShowsOverrideHint(active, plain));
        Assert.False(CombosPanel.ShowsOverrideHint(active, Array.Empty<ComboEntry>()));
        Assert.True(CombosPanel.ShowsOverrideHint(active, oneOverridden));

        // No active user, no line, whatever COMBO_LIST says.
        Assert.False(CombosPanel.ShowsOverrideHint(With(ProfileFlags.On), oneOverridden));
        Assert.False(CombosPanel.ShowsOverrideHint(ConsoleSession, oneOverridden));

        // An override on a row the table does not draw (the console menu's, without a console menu) is no orange row.
        var noMenu = active with { Flags2 = active.Flags2 | SessionFlags2.NoMenu };
        var menuOnly = new[] { new ComboEntry(ComboAction.OpenConsoleMenu, Own, ComboEntryFlags.Overridden) };
        Assert.False(CombosPanel.ShowsOverrideHint(noMenu with { QwarkVersion = CombosPanel.ConsoleMenuBuild }, menuOnly));
        Assert.True(CombosPanel.ShowsOverrideHint(active with { QwarkVersion = CombosPanel.ConsoleMenuBuild }, menuOnly));
    }

    /// <summary>
    /// The combos table's buttons cell holds Capture, Clear and Override side by side, and an
    /// overridden row's Override and Clear Override line up with them: the first button is one width
    /// on every row and Clear Override ends where a plain row's Override does. The stand-in font is
    /// 7 pixels a character, the default font's width, with the client's style (padding 6, spacing 8).
    /// </summary>
    [Fact]
    public void ComboButtonsFitTheirCell()
    {
        static float Measure(string text) => text.Length * 7f;

        var wide = CombosPanel.ButtonWidthsFor(Measure, framePadding: 6f, spacing: 8f, withOverride: true);
        float capture = Measure("Capture") + 12f;
        float clear = Measure("Clear") + 12f;
        float overrideButton = Measure("Override") + 12f;

        // The first button is as wide as the widest of Capture, Cancel and Override.
        Assert.Equal(overrideButton, wide.First);
        Assert.True(wide.First >= capture);
        Assert.Equal(clear + 8f + overrideButton, wide.Rest);
        Assert.True(wide.Rest >= Measure("Clear Override") + 12f);
        Assert.Equal(wide.First + 8f + clear + 8f + overrideButton, wide.Column);

        // With no Override anywhere the cell is the first button and Clear, and Combo has the rest.
        var narrow = CombosPanel.ButtonWidthsFor(Measure, 6f, 8f, withOverride: false);
        Assert.Equal(wide.First, narrow.First);
        Assert.Equal(wide.First + 8f + clear, narrow.Column);
    }

    /// <summary>
    /// Clear on an overridden row removes only the override; the row is then a plain one, so a
    /// second Clear clears the shared combo. Capture follows the row the same way.
    /// </summary>
    [Fact]
    public void ClearTwiceOnAnOverriddenRowClearsTheOverrideThenTheCombo()
    {
        var active = With(ProfileFlags.On | ProfileFlags.Active);
        var overridden = new ComboEntry(ComboAction.Die, Own, ComboEntryFlags.Overridden);
        Assert.Equal(CombosPanel.ClearRequest.RemoveOverride, CombosPanel.ClearFor(active, overridden));
        Assert.True(CombosPanel.CaptureIsOverride(active, overridden));

        // What COMBO_LIST says after the first Clear: the shared combo, unflagged.
        var afterFirst = new ComboEntry(ComboAction.Die, Shared);
        Assert.Equal(CombosPanel.ClearRequest.ClearShared, CombosPanel.ClearFor(active, afterFirst));
        Assert.False(CombosPanel.CaptureIsOverride(active, afterFirst));
    }

    /// <summary>
    /// A module without per-user settings (a release of build 56, PROTOCOL.md 3.1.2) never flags a
    /// row; if one did, the row is a shared one all the same: drawn plain, cleared with COMBO_SET,
    /// captured with COMBO_SET, and given no Override button.
    /// </summary>
    [Fact]
    public void WithoutPerUserSettingsNoRowIsAnOverride()
    {
        var release = SessionInfo.Empty with { QwarkVersion = 56 };
        var flagged = new ComboEntry(ComboAction.Die, Own, ComboEntryFlags.Overridden);

        Assert.False(CombosPanel.IsOverridden(release, flagged));
        Assert.Equal(CombosPanel.ClearRequest.ClearShared, CombosPanel.ClearFor(release, flagged));
        Assert.False(CombosPanel.CaptureIsOverride(release, flagged));
        Assert.False(CombosPanel.ShowsOverride(release));
        Assert.False(CombosPanel.ShowsOverrideButton(release, new ComboEntry(ComboAction.Die, Shared)));
    }

    /// <summary>The Default button is gone; the console menu's row names its default in the tooltip instead.</summary>
    [Fact]
    public void TheConsoleMenuRowNamesItsDefaultCombo()
    {
        string tip = CombosPanel.Tooltip(ComboAction.OpenConsoleMenu)!;
        Assert.Contains($"Default: {PadButtons.Describe(ComboDefaults.OpenConsoleMenu)}.", tip);
    }

    // ------------------------------------------------------------------ AppState

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

    private static Task PumpForAsync(AppState state, int milliseconds) => PumpAsync(state, () => false, milliseconds);

    private static int Count(FakeQwarkServer server, Opcode opcode) => server.RequestLog().Count(op => op == opcode);

    private static FakeQwarkServer StartServer(bool profiles = true)
    {
        var server = new FakeQwarkServer { ProfilesSupported = profiles };
        server.Start();
        return server;
    }

    [Fact]
    public async Task TheClientReadsTheProfileOnConnectOnlyWhenTheConsoleHasThem()
    {
        using var server = StartServer();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Profile.UserId == 1));
        Assert.Equal(string.Empty, state.Profile.Name);

        using var old = StartServer(profiles: false);
        using var other = await ConnectedStateAsync(old);
        await PumpForAsync(other, 600);
        Assert.Equal(0, Count(old, Opcode.ProfileInfo));
        Assert.Equal(ProfileInfo.None, other.Profile);
    }

    /// <summary>
    /// A release module of build 56 leaves the per-user settings out (PROTOCOL.md 3.1.2): no bits,
    /// and the ops UNSUPPORTED. The client shows nothing of them and sends none of the three ops,
    /// even when the Combos panel's switch or the copy-or-defaults answer is reached for anyway.
    /// </summary>
    [Fact]
    public async Task AReleaseModuleWithoutPerUserSettingsIsNeverAskedAnything()
    {
        using var server = StartServer();
        server.ReleaseModule = true;
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Session.QwarkVersion != 0));
        await PumpForAsync(state, 600);
        Assert.False(CombosPanel.ShowsProfiles(state.Session));
        Assert.False(CombosPanel.ShowsOverride(state.Session));
        Assert.Equal(ProfileInfo.None, state.Profile);

        state.TurnProfilesOn();
        state.TurnProfilesOff();
        state.AnswerProfileQuestion(ProfileSetup.Copy);
        await PumpForAsync(state, 300);

        Assert.Null(state.ProfileQuestion);
        Assert.Equal(0, Count(server, Opcode.ProfileInfo));
        Assert.Equal(0, Count(server, Opcode.ProfileEnable));
        Assert.Equal(0, Count(server, Opcode.ComboOverride));
        Assert.True(state.Combos.All(c => !CombosPanel.IsOverridden(state.Session, c)));
    }

    [Fact]
    public async Task UnderRpcs3TheClientNeverAsks()
    {
        using var server = StartServer();
        server.Emulator = true;
        server.NoCodePatches = true;
        using var state = await ConnectedStateAsync(server);

        await PumpForAsync(state, 600);
        Assert.Equal(0, Count(server, Opcode.ProfileInfo));
    }

    [Fact]
    public async Task ASequenceMoveReadsTheProfileAndTheListsThatFollowIt()
    {
        using var server = StartServer();
        using var state = await ConnectedStateAsync(server);
        Assert.True(await PumpAsync(state, () => state.Profile.Supported));
        await PumpForAsync(state, 300);

        server.ClearRequestLog();
        server.ProfilesEnabled = true;

        Assert.True(await PumpAsync(state, () => state.Profile.On));
        Assert.True(await PumpAsync(state, () =>
            Count(server, Opcode.ComboList) > 0 && Count(server, Opcode.ModList) > 0 && Count(server, Opcode.PosList) > 0
            && Count(server, Opcode.ManipList) > 0));
    }

    /// <summary>
    /// The question is asked only when the box is ticked: a console whose switch is already on,
    /// and a user who signs in later with no profile, get no prompt at all.
    /// </summary>
    [Fact]
    public async Task AUserWithNoProfileIsNeverAskedUnlessTheBoxIsTicked()
    {
        using var server = StartServer();
        server.ProfilesEnabled = true;
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Profile.Active));
        await PumpForAsync(state, 400);
        Assert.Null(state.ProfileQuestion);

        ToXmb(server);
        server.SignInAtXmb(2);
        Assert.True(await PumpAsync(state, () => state.Profile.UserId == 2));
        await PumpForAsync(state, 400);
        Assert.Null(state.ProfileQuestion);
        Assert.Equal(0, Count(server, Opcode.ProfileEnable));
    }

    /// <summary>A COMBO_LIST read after an override carries the flag the Combos panel draws orange from.</summary>
    [Fact]
    public async Task TheCombosTheClientHoldsCarryTheOverride()
    {
        using var server = StartServer();
        using var state = await ConnectedStateAsync(server);
        await state.Client.ProfileEnableAsync(true, ProfileSetup.Fresh);
        await state.Client.ComboOverrideAsync(ComboAction.Die, Own);

        state.RefreshCombos();
        Assert.True(await PumpAsync(state, () => state.Combos.Any(c => c.Action == ComboAction.Die && c.Overridden)));
    }

    [Fact]
    public async Task TickingTheBoxAsksForAUserWithNoProfileAndJustTurnsOnForAKnownOne()
    {
        using var server = StartServer();
        using var state = await ConnectedStateAsync(server);
        Assert.True(await PumpAsync(state, () => state.Profile.Supported));

        state.TurnProfilesOn();
        Assert.True(await PumpAsync(state, () => state.ProfileQuestion is not null));
        Assert.Equal(1u, state.ProfileQuestion!.UserId);
        Assert.Equal("User 1", state.ProfileQuestion.Who);
        Assert.False(server.ProfilesEnabled);

        // Cancel leaves the switch off.
        state.AnswerProfileQuestion(null);
        await PumpForAsync(state, 300);
        Assert.False(server.ProfilesEnabled);

        state.TurnProfilesOn();
        Assert.True(await PumpAsync(state, () => state.ProfileQuestion is not null));
        state.AnswerProfileQuestion(ProfileSetup.Copy);
        Assert.True(await PumpAsync(state, () => server.ProfilesEnabled && server.HasProfile(1)));
        Assert.Equal(new byte[] { 1, 1 }, server.LastPayload(Opcode.ProfileEnable));

        state.TurnProfilesOff();
        Assert.True(await PumpAsync(state, () => !server.ProfilesEnabled));

        // Set up before: on again with no question.
        state.TurnProfilesOn();
        Assert.True(await PumpAsync(state, () => server.ProfilesEnabled));
        Assert.Null(state.ProfileQuestion);
        Assert.Equal(new byte[] { 1, 0 }, server.LastPayload(Opcode.ProfileEnable));
    }
}
