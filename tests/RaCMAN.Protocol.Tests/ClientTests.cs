using System.Diagnostics;
using System.Text;

using RaCMAN.App;
using RaCMAN.App.Panels;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

public class ClientTests
{
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

    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync(bool autoReconnect = false)
    {
        var server = new FakeQwarkServer();
        server.Start();

        var client = new QwarkClient { AutoReconnect = autoReconnect };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    [Fact]
    public async Task ConnectSendsHelloAndSubscribe()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            Assert.True(client.IsConnected);
            Assert.Equal(1, server.HelloCount);
            Assert.Equal(1, server.SubscribeCount);
            Assert.NotNull(client.LatestSession);
            Assert.Equal("NPEA00385", client.LatestSession!.TitleId);
            Assert.True(client.TelemetryPort > 0);
        }
    }

    [Fact]
    public async Task TelemetryArrivesOverUdp()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            int received = 0;
            client.TelemetryReceived += _ => Interlocked.Increment(ref received);

            Assert.True(await WaitFor(() => client.LatestTelemetry is not null));
            Assert.True(await WaitFor(() => Volatile.Read(ref received) >= 2));
            Assert.Equal(GameId.Rac1, client.LatestTelemetry!.Session.Game);
        }
    }

    [Fact]
    public async Task GetStateReturnsTheTelemetryPacketOverTcp()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var packet = await client.GetStateAsync();
            Assert.Equal("NPEA00385", packet.Session.TitleId);
            Assert.Equal(SessionState.Ingame, packet.Session.State);
        }
    }

    [Fact]
    public async Task DescribeReturnsTheFeatureTable()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var describe = await client.DescribeAsync();
            Assert.Equal(GameId.Rac1, describe.Game);
            Assert.Equal(new[] { "Cheats", "Movement" }, describe.Groups);
            Assert.Equal(11, describe.Features.Length);
            Assert.Equal("Infinite ammo", describe.Features[0].Label);
            Assert.Equal(FeatureKind.Color, describe.Features[5].Kind);

            // Revision 1.2: the two flagged savefile ACTIONs at the end of the table.
            Assert.True(describe.Features[6].SavesAside);
            Assert.True(describe.Features[7].LoadsAside);

            // Revision 1.3: the live toggle, whose state the console reads out of the game.
            Assert.True(describe.Features[8].IsLive);

            // qwark has nothing to apply on boot for it, so the auto op is refused.
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.FeatureSetAutoAsync(describe.Features[8].Id, true));
            Assert.Equal(Status.Unsupported, refused.Status);

            // The toggle that patches instructions, which a platform without code patches refuses.
            Assert.True(describe.Features[9].WritesCode);

            // Revision 1.7: the signed VALUE, and the width the row carries in its `bits` byte.
            var signed = describe.Features[10];
            Assert.True(signed.IsSigned);
            Assert.Equal(16, signed.FieldBits);
            Assert.Equal(0u, signed.Min);
            Assert.Equal(0u, signed.Max);
        }
    }

    /// <summary>
    /// Revision 1.7, end to end: the console reports the raw field, the client reads it as the
    /// number the game means, and what it sends back is the low bits of that field again.
    /// </summary>
    [Fact]
    public async Task SignedValueRoundTripsThroughDescribeAndFeatureSet()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var describe = await client.DescribeAsync();
            var qe = Assert.Single(Array.FindAll(describe.Features, f => f.IsSigned));
            Assert.Equal("QE offset", qe.Label);
            Assert.Equal(16, qe.FieldBits);
            Assert.Equal((byte?)4, qe.MirrorReadout);
            const byte mirror = 4;

            // The console holds 0xFFFF in the halfword, which is the -1 the old dialog offered.
            var state = await client.GetStateAsync();
            Assert.Equal(0xFFFFu, state.Session.ReadoutAt(mirror));
            Assert.Equal(-1L, qe.SignExtend(state.Session.ReadoutAt(mirror)!.Value));

            await client.FeatureSetAsync(qe.Id, qe.Encode(-42));

            state = await client.GetStateAsync();
            Assert.Equal(0xFFD6u, state.Session.ReadoutAt(mirror));
            Assert.Equal(-42L, qe.SignExtend(state.Session.ReadoutAt(mirror)!.Value));

            // A value past the end of the field is clamped on the way out, never wrapped.
            await client.FeatureSetAsync(qe.Id, qe.Encode(-70000));

            state = await client.GetStateAsync();
            Assert.Equal(0x8000u, state.Session.ReadoutAt(mirror));
            Assert.Equal(-32768L, qe.SignExtend(state.Session.ReadoutAt(mirror)!.Value));
        }
    }

    [Fact]
    public async Task FeatureOptionsReturnsTheEnumChoices()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var options = await client.FeatureOptionsAsync(4);
            Assert.Equal(new[] { "Off", "Ghost", "Ghost and no clip" }, options);

            var missing = await Assert.ThrowsAsync<QwarkStatusException>(() => client.FeatureOptionsAsync(9));
            Assert.Equal(Status.Unsupported, missing.Status);
        }
    }

    [Fact]
    public async Task FeatureSetAutoUpdatesTheAutoMask()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.FeatureSetAutoAsync(3, true);
            var state = await client.GetStateAsync();
            Assert.Equal(0b1000ul, state.Session.ToggleAuto);
        }
    }

    [Fact]
    public async Task TelemetryTickAdvances()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            Assert.True(await WaitFor(() => client.LatestTelemetry is not null));
            uint first = client.LatestTelemetry!.Session.Tick;
            Assert.True(await WaitFor(() => client.LatestTelemetry!.Session.Tick > first));
        }
    }

    [Fact]
    public async Task FeatureSetUpdatesTheToggleMask()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.FeatureSetAsync(1, 1);
            var state = await client.GetStateAsync();
            Assert.Equal(0b10ul, state.Session.ToggleState);

            await client.FeatureSetAsync(1, 0);
            state = await client.GetStateAsync();
            Assert.Equal(0ul, state.Session.ToggleState);
        }
    }

    [Fact]
    public async Task WatchesAppearInTheListAndInTelemetry()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            byte id = await client.WatchAddAsync(server.MemoryBase + 4, 4);
            Assert.Equal(0, id);

            // The same address and size returns the existing id.
            Assert.Equal(id, await client.WatchAddAsync(server.MemoryBase + 4, 4));

            var list = await client.WatchListAsync();
            Assert.Equal(new WatchEntry(0, 4, server.MemoryBase + 4), Assert.Single(list));

            Assert.True(await WaitFor(() => client.LatestTelemetry?.Watches.Length == 1));
            var watch = client.LatestTelemetry!.Watches[0];
            Assert.True(watch.Valid);
            Assert.Equal(0x04050607ul, watch.Value);

            await client.WatchRemoveAsync(id);
            Assert.Empty(await client.WatchListAsync());
        }
    }

    [Fact]
    public async Task MemoryReadAndWriteHitTheFakeProcess()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var read = await client.MemReadAsync(server.MemoryBase, 8);
            Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }, read);

            await client.MemWriteAsync(server.MemoryBase + 2, new byte[] { 0xDE, 0xAD });
            read = await client.MemReadAsync(server.MemoryBase, 4);
            Assert.Equal(new byte[] { 0, 1, 0xDE, 0xAD }, read);
        }
    }

    [Fact]
    public async Task ListOpcodesRoundTrip()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var positions = await client.PosListAsync();
            Assert.Equal(8, positions.Slots.Length);
            Assert.True(positions.Slots[0].Filled);
            Assert.False(positions.Slots[7].Filled);

            var planets = await client.PlanetListAsync();
            Assert.Equal(new[] { "Veldin", "Novalis", "Aridia", "Kerwan" }, planets);

            var mods = await client.ModListAsync();
            Assert.Equal(2, mods.Length);
            Assert.Equal("crash-patch", mods[0].DirName);
            Assert.True(mods[1].NeedsLua);

            await client.ComboSetAsync(ComboAction.SavePosition, 0x0B);
            var combos = await client.ComboListAsync();
            Assert.Equal(new ComboEntry(ComboAction.SavePosition, 0x0B), Assert.Single(combos));
        }
    }

    /// <summary>
    /// COMBO_SUSPEND, revision 1.8. The fake server takes `u8 suspend` and nothing else, so a
    /// request encoded any other way comes back BAD_ARG rather than being recorded.
    /// </summary>
    [Fact]
    public async Task ComboSuspendRoundTrips()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            Assert.Equal(0x0082, (int)Opcode.ComboSuspend);
            Assert.Null(server.CombosSuspended);

            await client.ComboSuspendAsync(true);
            Assert.True(server.CombosSuspended);

            await client.ComboSuspendAsync(false);
            Assert.False(server.CombosSuspended);
        }
    }

    [Fact]
    public async Task UnlockListAndSetRoundTrip()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            const byte owned = UnlockList.PrimarySlot;
            const byte xp = 2;
            const byte ammo = 3;

            var list = await client.UnlockListAsync();
            Assert.Equal(new[] { "Weapons", "Gadgets" }, list.Categories);
            Assert.Equal(5, list.Unlocks.Length);

            // The console says what its four value slots mean; the panel draws them from this.
            Assert.Equal(4, list.Fields.Length);
            Assert.Equal(new UnlockField("Owned", UnlockFieldKind.Flag, 0), list.FieldAt(owned));
            Assert.Equal(new UnlockField("Level", UnlockFieldKind.Number, 8), list.FieldAt(1));
            Assert.Equal("XP", list.FieldAt(xp).Name);
            Assert.Equal(UnlockFieldKind.Number, list.FieldAt(ammo).Kind);

            var ryno = list.Unlocks.Single(u => u.Name == "RYNO");
            Assert.Equal(0u, ryno.Values[owned]);
            Assert.True(ryno.HasField(ammo));
            Assert.False(ryno.HasField(xp));

            await client.UnlockSetAsync(ryno.Id, owned, 1);
            await client.UnlockSetAsync(ryno.Id, ammo, 50);

            var again = (await client.UnlockListAsync()).Unlocks.Single(u => u.Id == ryno.Id);
            Assert.Equal(1u, again.Values[owned]);
            Assert.Equal(50u, again.Values[ammo]);

            // A field the entry does not carry is refused rather than silently written.
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.UnlockSetAsync(ryno.Id, xp, 3));
            Assert.Equal(Status.BadArg, refused.Status);
        }
    }

    [Fact]
    public async Task LevelFlagsGetSetAndResetRoundTrip()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var flags = await client.LevelFlagsGetAsync(2);
            Assert.Equal(64, flags.Length);
            Assert.Equal(server.LevelFlags[2], flags);

            await client.LevelFlagsSetAsync(2, offset: 5, value: 0xA7);
            flags = await client.LevelFlagsGetAsync(2);
            Assert.Equal(0xA7, flags[5]);

            var outOfRange = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.LevelFlagsSetAsync(2, offset: 64, value: 1));
            Assert.Equal(Status.BadArg, outOfRange.Status);

            await client.LevelFlagsResetAsync(2);
            Assert.Equal(1, server.LevelFlagsResetCount);
            Assert.All(await client.LevelFlagsGetAsync(2), b => Assert.Equal(0, b));

            var missing = await Assert.ThrowsAsync<QwarkStatusException>(() => client.LevelFlagsGetAsync(200));
            Assert.Equal(Status.Unsupported, missing.Status);
        }
    }

    [Fact]
    public async Task MobyTablePointsAtRowsTheClientCanRead()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var info = await client.MobyTableAsync();
            Assert.Equal(FakeQwarkServer.MobyStride, info.Stride);

            uint start = BitConverter.ToUInt32(
                (await client.MemReadAsync(info.TablePointerAddress, 4)).Reverse().ToArray(), 0);
            uint end = BitConverter.ToUInt32(
                (await client.MemReadAsync(info.TableEndPointerAddress, 4)).Reverse().ToArray(), 0);

            Assert.Equal(server.MemoryBase + FakeQwarkServer.MobyTableOffset, start);
            Assert.Equal(FakeQwarkServer.MobyCount, (int)((end - start) / info.Stride));

            var rows = await client.MemReadAsync(start, end - start);
            Assert.Equal(FakeQwarkServer.MobyCount * FakeQwarkServer.MobyStride, rows.Length);
        }
    }

    [Fact]
    public async Task MobyTableReaderWalksTheTableAndDecodesTheRac1Layout()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var layout = MobyLayouts.Load(Path.Combine(AppContext.BaseDirectory, "data", "moby"))[GameId.Rac1];
            var snapshot = await MobyTableReader.ReadAsync(client, layout);

            Assert.Equal(FakeQwarkServer.MobyCount, snapshot.TotalRows);
            Assert.Equal(FakeQwarkServer.MobyCount, snapshot.Rows.Length);
            Assert.False(snapshot.Capped);
            Assert.Equal(server.MemoryBase + FakeQwarkServer.MobyTableOffset, snapshot.Start);

            // The fake console lays down x = 100 + i, oClass = 5000 + 7i, UID = 1 + i, state = i % 5.
            for (int i = 0; i < snapshot.Rows.Length; i++)
            {
                var row = snapshot.Rows[i];
                Assert.Equal(i, row.Index);
                Assert.Equal(snapshot.Start + (uint)(i * FakeQwarkServer.MobyStride), row.Address);
                Assert.Equal(100f + i, row.X);
                Assert.Equal(200f + i, row.Y);
                Assert.Equal(300f + i, row.Z);
                Assert.Equal(5000 + i * 7, row.OClass);
                Assert.Equal(1 + i, row.Uid);
                Assert.Equal(i % 5, row.State);
            }
        }
    }

    [Fact]
    public async Task MobyTableReaderCapsTheRowCountAndSaysSo()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var snapshot = await MobyTableReader.ReadAsync(client, layout: null, rowCap: 3);

            Assert.Equal(FakeQwarkServer.MobyCount, snapshot.TotalRows);
            Assert.Equal(3, snapshot.Rows.Length);
            Assert.True(snapshot.Capped);
            Assert.Contains("capped", snapshot.Describe());

            // With no layout file only the position, which is at 0x10 in all four games, is read.
            Assert.Equal(100f, snapshot.Rows[0].X);
            Assert.Null(snapshot.Rows[0].OClass);
            Assert.Null(snapshot.Rows[0].Uid);
        }
    }

    [Fact]
    public async Task FeatureSetMovesTheReadoutItMirrors()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            // Feature 3 is the VALUE that mirrors readout 0; feature 4 is the ENUM on readout 2.
            await client.FeatureSetAsync(3, 4242);
            await client.FeatureSetAsync(4, 2);

            var state = await client.GetStateAsync();
            Assert.Equal(4242u, state.Session.Readout[0]);
            Assert.Equal(2u, state.Session.Readout[2]);

            // A mirrored feature must not also flip a toggle bit.
            Assert.Equal(0ul, state.Session.ToggleState);
        }
    }

    [Fact]
    public async Task UnknownOpcodeSurfacesAsAStatusException()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var error = await Assert.ThrowsAsync<QwarkStatusException>(() => client.DieAsync());
            Assert.Equal(Status.UnknownOp, error.Status);
            Assert.Equal(Opcode.Die, error.Opcode);

            // The connection stays open after an unknown opcode.
            Assert.True(client.IsConnected);
            await client.HeartbeatAsync();
        }
    }

    [Fact]
    public async Task RequestsArePipelinedAndMatchedBySeq()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var tasks = new List<Task>();
            for (int i = 0; i < 16; i++)
            {
                uint address = server.MemoryBase + (uint)(i * 4);
                tasks.Add(Task.Run(async () =>
                {
                    var bytes = await client.MemReadAsync(address, 4);
                    Assert.Equal((byte)(address - server.MemoryBase), bytes[0]);
                }));
            }

            await Task.WhenAll(tasks);
        }
    }

    [Fact]
    public async Task HeartbeatKeepsAnIdleConnectionAlive()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            Assert.True(await WaitFor(() => server.HeartbeatCount >= 1, 6000));
        }
    }

    /// <summary>
    /// Telemetry going missing in the XMB is packets going missing, the same as in a game: once UDP
    /// has been gone for the fallback wait the client asks for the snapshot over TCP, ten times a
    /// second, which also keeps the console counting it as a subscriber, and goes back to UDP as
    /// soon as packets arrive again. A build that read this silence as a game starting asked for
    /// nothing for twenty seconds, the console stopped sending to it, and the client polled over
    /// TCP for the rest of the connection.
    /// </summary>
    [Fact]
    public async Task TelemetryLostInTheXmbFallsBackAndRecovers()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.Session = server.Session with { State = SessionState.Xmb };
            Assert.True(await WaitFor(() => client.LatestSession?.State == SessionState.Xmb));

            server.TelemetryMuted = true;
            Assert.True(await WaitFor(() => client.TelemetryViaTcp, 4000));

            // Ten a second once it has fallen back, not one per fallback wait.
            int polls = server.GetStateCount;
            await Task.Delay(1000);
            Assert.True(server.GetStateCount - polls >= 7, $"{server.GetStateCount - polls} polls in a second");

            server.TelemetryMuted = false;
            Assert.True(await WaitFor(() => !client.TelemetryViaTcp, 3000));
        }
    }

    /// <summary>
    /// A console on WiFi loses packets in bursts. A gap well under the fallback wait asks for
    /// nothing over TCP and never shows the fallback, which at 400 ms it did, as a flash.
    /// </summary>
    [Fact]
    public async Task ABriefTelemetryGapDoesNotFallBack()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            Assert.True(await WaitFor(() => client.LatestTelemetry is not null));

            int polls = server.GetStateCount;
            bool sawTcp = false;

            server.TelemetryMuted = true;
            var gap = Stopwatch.StartNew();
            while (gap.ElapsedMilliseconds < 800)
            {
                sawTcp |= client.TelemetryViaTcp;
                await Task.Delay(20);
            }
            server.TelemetryMuted = false;

            Assert.True(await WaitFor(() => client.TelemetryAgeMs is >= 0 and < 200));
            sawTcp |= client.TelemetryViaTcp;

            Assert.False(sawTcp);
            Assert.Equal(polls, server.GetStateCount);
        }
    }

    [Fact]
    public async Task DroppedConnectionFailsPendingRequestsAndRaisesDisconnected()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            Exception? seen = null;
            bool fired = false;
            client.Disconnected += ex => { seen = ex; fired = true; };

            server.DropClients();

            Assert.True(await WaitFor(() => fired));
            Assert.False(client.IsConnected);
            await Assert.ThrowsAnyAsync<Exception>(() => client.HeartbeatAsync());
        }
    }

    [Fact]
    public async Task AutoReconnectReestablishesTheSession()
    {
        var (server, client) = await ConnectAsync(autoReconnect: true);
        using (server)
        using (client)
        {
            int established = 1;
            client.SessionEstablished += _ => Interlocked.Increment(ref established);

            server.DropClients();
            Assert.True(await WaitFor(() => !client.IsConnected));

            Assert.True(await WaitFor(() => client.IsConnected, 15000));
            Assert.True(await WaitFor(() => Volatile.Read(ref established) >= 2));
            Assert.Equal(2, server.HelloCount);
            Assert.Equal(2, server.SubscribeCount);

            // Telemetry resumes on the new subscription.
            Assert.True(await WaitFor(() => client.LatestTelemetry is not null));
        }
    }

    [Fact]
    public async Task ReconnectBacksOffWhileTheServerIsGone()
    {
        var server = new FakeQwarkServer();
        server.Start();
        using var client = new QwarkClient { AutoReconnect = true };
        await client.ConnectAsync("127.0.0.1", server.Port);

        server.Dispose();
        Assert.True(await WaitFor(() => !client.IsConnected));
        Assert.True(await WaitFor(() => client.ReconnectAttempt >= 2, 10000));
        Assert.True(client.WantsConnection);
        Assert.True(client.ReconnectRemaining > TimeSpan.Zero);

        await client.DisconnectAsync();
        Assert.False(client.WantsConnection);
    }

    [Fact]
    public async Task TheHookRunsBeforeEveryReconnectAttemptAndAFailingOneChangesNothing()
    {
        using var server = new FakeQwarkServer();
        server.Start();

        using var client = new QwarkClient { AutoReconnect = true };
        int hooks = 0;

        // The client knows nothing about what the hook does, so one that throws must leave the
        // attempt it precedes exactly as it would have been: this is where the app asks webMAN
        // whether the module is still loaded, and a console that is away refuses that too.
        client.BeforeReconnect = (_, _) =>
        {
            Interlocked.Increment(ref hooks);
            throw new IOException("webMAN did not answer");
        };

        await client.ConnectAsync("127.0.0.1", server.Port);

        // The console drops the link but is still there. The hook runs, throws, and the attempt
        // behind it lands anyway: a hook that failed says nothing about whether qwark is up.
        server.DropClients();
        Assert.True(await WaitFor(() => !client.IsConnected));
        Assert.True(await WaitFor(() => client.IsConnected, 15000));
        Assert.True(Volatile.Read(ref hooks) >= 1);
        Assert.Equal(2, server.HelloCount);

        // Now the console goes away for good, and every attempt the loop makes is preceded by one
        // hook of its own rather than by the first one only.
        int before = Volatile.Read(ref hooks);
        server.StopListening();
        server.DropClients();

        Assert.True(await WaitFor(() => !client.IsConnected));
        Assert.True(await WaitFor(() => Volatile.Read(ref hooks) >= before + 3, 20000));
        Assert.True(client.ReconnectAttempt >= 3);

        await client.DisconnectAsync();
    }

    [Fact]
    public async Task ModLibraryUploadsAModAndWritesQwarkSum()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var root = Path.Combine(Path.GetTempPath(), "racman-reloaded-tests", Guid.NewGuid().ToString("N"));
            var modDir = Path.Combine(root, "NPEA00385", "test-mod");
            Directory.CreateDirectory(modDir);
            File.WriteAllText(Path.Combine(modDir, "patch.txt"),
                "#- name: Test mod\n#- version: 1.0.0\n#- author: nobody\n0x1B0000: 0x60000000\n0x1C0000: cave.bin\n");
            File.WriteAllBytes(Path.Combine(modDir, "cave.bin"), new byte[] { 1, 2, 3, 4 });

            try
            {
                var library = new ModLibrary(root);
                var mod = Assert.Single(library.Scan("NPEA00385"));
                Assert.Equal("Test mod", mod.Name);
                Assert.Equal("1.0.0", mod.Version);
                Assert.Equal("nobody", mod.Author);
                Assert.Equal(new[] { "cave.bin" }, mod.BinFiles);
                Assert.False(mod.NeedsLua);

                bool uploaded = await library.EnsureUploadedAsync(client, "NPEA00385", mod, consoleHash: 0);
                Assert.True(uploaded);

                Assert.Contains("/dev_hdd0/qwark/mods/NPEA00385/test-mod", server.Directories);
                Assert.True(server.Files.ContainsKey("/dev_hdd0/qwark/mods/NPEA00385/test-mod/patch.txt"));
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, server.Files["/dev_hdd0/qwark/mods/NPEA00385/test-mod/cave.bin"]);

                var sum = Encoding.ASCII.GetString(server.Files["/dev_hdd0/qwark/mods/NPEA00385/test-mod/qwark.sum"]);
                Assert.Equal(8, sum.Length);
                Assert.Equal(ModLibrary.ComputeHash(mod), Convert.ToUInt32(sum, 16));
                Assert.Equal(1, server.ModRescanCount);

                // A matching console hash skips the upload.
                Assert.False(await library.EnsureUploadedAsync(client, "NPEA00385", mod, mod.Hash));
                Assert.Equal(1, server.ModRescanCount);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RequestOnADeadClientThrowsRatherThanHanging()
    {
        using var client = new QwarkClient { AutoReconnect = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.HeartbeatAsync());
    }

    // ---------------------------------------------------------------- session-change hygiene
    //
    // "Side panels never outlive the game" (DESIGN.md section 4). AppState.Tick is the whole of
    // that rule and touches no ImGui, so it can be driven here with a hand-rolled frame loop.

    private static async Task<AppState> ConnectedStateAsync(FakeQwarkServer server)
    {
        var state = new AppState(new Settings { AutoReconnect = false });
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

    [Fact]
    public async Task LeavingIngameDropsEverythingReadOutOfGameMemory()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));
        state.RefreshUnlocks();
        Assert.True(await PumpAsync(state, () => state.Unlocks.Unlocks.Length > 0));

        server.Session = server.Session with { State = SessionState.Quitting };

        Assert.True(await PumpAsync(state, () => state.Session.State == SessionState.Quitting));
        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length == 0));
        Assert.Empty(state.Unlocks.Unlocks);
        Assert.False(state.Ingame);

        // The descriptors describe the game, not the process, so they stay for the panels to grey out.
        Assert.NotEmpty(state.Describe.Features);
    }

    [Fact]
    public async Task ComingBackIntoTheGameRefetchesTheLists()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));

        server.Session = server.Session with { State = SessionState.Quitting };
        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length == 0));

        server.Session = server.Session with { State = SessionState.Ingame };
        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));
    }

    [Fact]
    public async Task ASameGameRebootClearsTheStaleViewsAndRereads()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));
        state.RefreshUnlocks();
        Assert.True(await PumpAsync(state, () => state.Unlocks.Unlocks.Length > 0));

        // A new generation with the same title is the reboot signal, section 4.1.
        server.Session = server.Session with { Generation = server.Session.Generation + 1 };

        Assert.True(await PumpAsync(state, () => state.Unlocks.Unlocks.Length == 0));
        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));
    }

    [Fact]
    public async Task ADifferentGameDropsEveryListAndRereadsThem()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Describe.Game == GameId.Rac1));

        server.Describe = server.Describe with
        {
            Game = GameId.Rac2,
            Features = new[] { new Feature(0, FeatureKind.Toggle, 0, 0, FeatureFlags.None, 0xFF, 0, 0, "Only one") },
        };

        server.Planets = new[] { "Oozla", "Maktar" };
        server.Session = server.Session with { Game = GameId.Rac2, TitleId = "NPEA00386", Generation = 9 };

        Assert.True(await PumpAsync(state, () => state.Describe.Game == GameId.Rac2));
        Assert.Single(state.Describe.Features);
        Assert.True(await PumpAsync(state, () => state.Planets.Length == 2));
    }

    [Fact]
    public async Task LosingTheConnectionKeepsWhatTheConsoleDescribed()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0));
        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));

        server.DropClients();

        Assert.True(await PumpAsync(state, () => !state.Connected));
        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length == 0));
        Assert.Empty(state.Session.TitleId);
        Assert.Null(state.Hello);

        // The console still holds all of it and this client will be back, so the panels keep the
        // layout they had rather than emptying every time the link hiccups.
        Assert.NotEmpty(state.Describe.Features);
        Assert.NotEmpty(state.Planets);
        Assert.Equal(GameId.Rac1, state.DescribedGame);
        Assert.Equal("NPEA00385", state.DescribedTitle);
    }

    /// <summary>
    /// The user's Deadlocked session: quit, XMB, and the same game booted again. What the console
    /// described is the panels' whole layout, and it is the same game's, so nothing is thrown away
    /// and nothing is described a second time.
    /// </summary>
    [Fact]
    public async Task AQuitAndARebootReuseTheDescriptionInsteadOfReadingItAgain()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0));
        Assert.True(await PumpAsync(state, () => state.AutosplitEvents.Length > 0));
        int described = server.DescribeCount;
        int features = state.Describe.Features.Length;

        // QUITTING, then the XMB with no game at all: the session change every panel follows.
        server.Session = server.Session with { State = SessionState.Quitting };
        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length == 0));

        server.Session = server.Session with
        {
            State = SessionState.Xmb,
            Game = GameId.None,
            TitleId = string.Empty,
        };

        Assert.True(await PumpAsync(state, () => state.Session.State == SessionState.Xmb));
        Assert.False(state.Ingame);

        // Everything described is still here, and still says which game it belongs to.
        Assert.Equal(features, state.Describe.Features.Length);
        Assert.NotEmpty(state.AutosplitEvents);
        Assert.NotEmpty(state.Planets);
        Assert.Equal(GameId.Rac1, state.DescribedGame);
        Assert.Equal(GameId.Rac1, state.Autosplitter.Game);
        Assert.NotEmpty(state.Autosplitter.Descriptors);
        Assert.Equal(described, server.DescribeCount);

        // The same game boots again, one generation on.
        server.Session = server.Session with
        {
            State = SessionState.Ingame,
            Game = GameId.Rac1,
            TitleId = "NPEA00385",
            Generation = server.Session.Generation + 1,
        };

        Assert.True(await PumpAsync(state, () => state.Positions.Slots.Length > 0));
        Assert.Equal(features, state.Describe.Features.Length);
        Assert.Equal(described, server.DescribeCount);

        // And none of it was said out loud. The reads that land mid-transition are nobody's doing,
        // so a NOT_INGAME or an UNSUPPORTED from one of them is not a toast, and the reboot itself
        // is debug-only now: the console handles it and the user has a game to play.
        Assert.DoesNotContain(state.Toasts, toast => toast.Kind == ToastKind.Error);
        Assert.DoesNotContain(state.Toasts, toast => toast.Text.Contains("rebooted", StringComparison.Ordinal));
    }

    /// <summary>The user asked, so this one does read all of it back.</summary>
    [Fact]
    public async Task ReReadEverythingDescribesTheGameAgain()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0));
        int described = server.DescribeCount;

        state.ForceRefresh();
        Assert.True(await PumpAsync(state, () => server.DescribeCount > described));
        Assert.True(await PumpAsync(state, () => state.Describe.Features.Length > 0));
    }

    [Fact]
    public async Task HelloCarriesTheProtocolAndBuildVersions()
    {
        using var server = new FakeQwarkServer();
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Hello is not null));
        Assert.Equal(QwarkClient.ClientProtocolVersion, state.Hello!.ProtocolVersion);
        Assert.Equal(12, state.Hello.QwarkVersion);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(31, true)]     // the build before the one this client ships with
    [InlineData(32, false)]    // exactly the expected build: Deadlocked weapon levels shown as the game shows them
    [InlineData(33, false)]    // a console ahead of the client is not the client's problem
    public void IsStaleBuildOnlyFlagsOlderModules(byte reported, bool stale)
    {
        Assert.Equal(32, QwarkClient.ExpectedQwarkBuild);
        Assert.Equal(stale, QwarkClient.IsStaleBuild(reported));
    }

    // ------------------------------------------------------------------ the combo capture hold
    //
    // The Combos panel keeps the ImGui out of the sequence: Capture, Cancel and the session reset
    // are three calls that only move a static and send one request, so the whole of the hold can
    // be driven here with the same frame loop.

    private const uint CaptureMask = (uint)PadButton.Cross | (uint)PadButton.Square;

    /// <summary>Runs frames with the pad at <paramref name="mask"/> until the panel has seen it.</summary>
    private static async Task PressAsync(AppState state, FakeQwarkServer server, uint mask)
    {
        server.Session = server.Session with { PadMask = mask };
        Assert.True(await PumpAsync(state, () => state.Session.PadMask == mask));
        CombosPanel.Update(state);
    }

    [Fact]
    public async Task CapturingHoldsTheCombosOffAndCommitCommitsAndResumes()
    {
        using var server = new FakeQwarkServer();
        // The pad has to sit still to be captured; the fake console walks it through the buttons.
        server.AnimateInput = false;
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Hello is not null));
        Assert.Null(server.CombosSuspended);

        CombosPanel.BeginCapture(state, ComboAction.Die);
        Assert.True(await PumpAsync(state, () => server.CombosSuspended == true));

        // The press, then the release that commits it: the same run of masks telemetry delivers.
        await PressAsync(state, server, CaptureMask);
        Assert.False(server.Combos.ContainsKey(ComboAction.Die));

        await PressAsync(state, server, 0);

        Assert.True(await PumpAsync(state, () => server.Combos.ContainsKey(ComboAction.Die)));
        Assert.Equal(CaptureMask, server.Combos[ComboAction.Die]);
        Assert.True(await PumpAsync(state, () => server.CombosSuspended == false));
    }

    [Fact]
    public async Task CancellingACaptureHandsTheCombosBackAndStoresNothing()
    {
        using var server = new FakeQwarkServer();
        server.AnimateInput = false;
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Hello is not null));

        CombosPanel.BeginCapture(state, ComboAction.LoadPlanet);
        Assert.True(await PumpAsync(state, () => server.CombosSuspended == true));

        await PressAsync(state, server, CaptureMask);

        CombosPanel.EndCapture(state);
        Assert.True(await PumpAsync(state, () => server.CombosSuspended == false));

        // And the buttons that were down when Cancel was pressed store nothing on the way up.
        await PressAsync(state, server, 0);
        Assert.Empty(server.Combos);
    }

    [Fact]
    public async Task AGameChangeDropsTheCaptureAndTheHoldWithIt()
    {
        using var server = new FakeQwarkServer();
        server.AnimateInput = false;
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Hello is not null));

        CombosPanel.BeginCapture(state, ComboAction.SavePosition);
        Assert.True(await PumpAsync(state, () => server.CombosSuspended == true));

        server.Session = server.Session with { Game = GameId.Rac2, TitleId = "NPEA00386" };

        Assert.True(await PumpAsync(state, () => server.CombosSuspended == false));
        Assert.Empty(server.Combos);
    }

    // ------------------------------------------------------------------ RPCS3 (the session flags)

    [Fact]
    public async Task TheEmulatorAndCodePatchFlagsReachTheSession()
    {
        using var server = new FakeQwarkServer();
        server.Emulator = true;
        server.NoCodePatches = true;
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Hello is not null));
        Assert.True(state.Hello!.IsEmulator);
        Assert.True(state.Hello.CodePatchesUnsupported);

        // And through telemetry, which is what the panels actually read.
        Assert.True(await PumpAsync(state, () => state.Telemetry is not null));
        Assert.True(state.IsEmulator);
        Assert.True(state.CodePatchesUnsupported);
    }

    [Fact]
    public async Task WithoutTheFlagsNothingIsGatedAndPreviousPendingStillWorks()
    {
        using var server = new FakeQwarkServer();
        server.Session = server.Session with { Flags = SessionFlags.PreviousPending };
        server.Start();
        using var state = await ConnectedStateAsync(server);

        Assert.True(await PumpAsync(state, () => state.Telemetry is not null));
        Assert.True(state.Session.PreviousPending);
        Assert.False(state.IsEmulator);
        Assert.False(state.CodePatchesUnsupported);
    }

    [Fact]
    public async Task CodePatchOpsAreRefusedWhenTheConsoleSaysItCannotPatchCode()
    {
        var server = new FakeQwarkServer();
        server.NoCodePatches = true;
        server.Start();

        var client = new QwarkClient { AutoReconnect = false };
        using (server)
        using (client)
        {
            await client.ConnectAsync("127.0.0.1", server.Port);
            var describe = await client.DescribeAsync();
            var patcher = Array.Find(describe.Features, f => f.WritesCode)!;

            var setting = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.FeatureSetAsync(patcher.Id, 1));
            Assert.Equal(Status.Unsupported, setting.Status);

            var loading = await Assert.ThrowsAsync<QwarkStatusException>(() => client.ModLoadAsync("crash-patch"));
            Assert.Equal(Status.Unsupported, loading.Status);

            var patching = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.PatchApplyAsync(new[] { new PatchWord(0x300100, 0x60000000) }));
            Assert.Equal(Status.Unsupported, patching.Status);

            // A cheat that only writes data is untouched: the flag is about instructions.
            await client.FeatureSetAsync(0, 1);
            Assert.True((server.Session.ToggleState & 1ul) != 0);
        }
    }

    [Fact]
    public async Task TheSameOpsWorkWithTheFlagOff()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            Assert.False(server.NoCodePatches);

            var describe = await client.DescribeAsync();
            var patcher = Array.Find(describe.Features, f => f.WritesCode)!;

            await client.FeatureSetAsync(patcher.Id, 1);
            Assert.True((server.Session.ToggleState & (1ul << patcher.Id)) != 0);
        }
    }
}
