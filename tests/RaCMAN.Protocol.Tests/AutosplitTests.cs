using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The autosplit wire contract of revision 1.4: the 16-byte event, the 28-byte descriptor, the
/// AUTOSPLIT_EVENTS reply, and the receiver that has to tell the 20-byte push datagram from a
/// telemetry packet on the same socket, drop the two duplicate copies of it, and fill a gap over
/// TCP before delivering the event that exposed it.
/// </summary>
public class AutosplitTests
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

    private static async Task<(FakeQwarkServer Server, QwarkClient Client, List<AutosplitEvent> Seen)> ConnectAsync(
        Action<FakeQwarkServer>? before = null,
        bool safetyPoll = false)
    {
        var server = new FakeQwarkServer();
        before?.Invoke(server);
        server.Start();

        var seen = new List<AutosplitEvent>();
        var client = new QwarkClient { AutoReconnect = false, AutosplitSafetyPoll = safetyPoll };
        client.AutosplitEventReceived += ev =>
        {
            lock (seen) seen.Add(ev);
        };

        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client, seen);
    }

    private static AutosplitEvent[] Snapshot(List<AutosplitEvent> seen)
    {
        lock (seen) return seen.ToArray();
    }

    // ---------------------------------------------------------------- parsing

    [Fact]
    public void EventIsSixteenBigEndianBytesAndRoundTrips()
    {
        var ev = new AutosplitEvent(0x01020304, 0x0A0B0C0D, AutosplitKind.Split, 1, 3);
        var bytes = ev.ToBytes();

        Assert.Equal(AutosplitEvent.Size, bytes.Length);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, bytes[..4]);
        Assert.Equal(new byte[] { 0x0A, 0x0B, 0x0C, 0x0D }, bytes[4..8]);
        Assert.Equal((byte)AutosplitKind.Split, bytes[8]);
        Assert.Equal(1, bytes[9]);
        Assert.Equal(new byte[] { 0, 0 }, bytes[10..12]);
        Assert.Equal(new byte[] { 0, 0, 0, 3 }, bytes[12..16]);

        Assert.Equal(ev, AutosplitEvent.Parse(bytes));
        Assert.True(ev.IsPlanetEntered);
    }

    [Fact]
    public void EventDescIsTwentyEightBytesAndRoundTripsItsLabelAndFlags()
    {
        var desc = new AutosplitEventDesc(1, AutosplitKind.Split,
            AutosplitEventFlags.EnabledByDefault | AutosplitEventFlags.PlanetRoute, "Planet entered");
        var bytes = desc.ToBytes();

        Assert.Equal(AutosplitEventDesc.Size, bytes.Length);
        Assert.Equal(0, bytes[3]);
        Assert.Equal(0, bytes[^1]);   // the label is NUL-padded to its 24 bytes

        var parsed = AutosplitEventDesc.Parse(bytes);
        Assert.Equal(desc, parsed);
        Assert.True(parsed.EnabledByDefault);
        Assert.True(parsed.PlanetRoute);

        var plain = AutosplitEventDesc.Parse(
            new AutosplitEventDesc(2, AutosplitKind.Split, AutosplitEventFlags.None, "Boss defeated").ToBytes());
        Assert.False(plain.EnabledByDefault);
        Assert.False(plain.PlanetRoute);
    }

    [Fact]
    public void TheDescribeListRoundTripsThroughItsCountPrefix()
    {
        var descriptors = new[]
        {
            new AutosplitEventDesc(1, AutosplitKind.Split,
                AutosplitEventFlags.EnabledByDefault | AutosplitEventFlags.PlanetRoute, "Planet entered"),
            new AutosplitEventDesc(2, AutosplitKind.Split, AutosplitEventFlags.EnabledByDefault, "Protopet defeated"),
        };

        var payload = AutosplitEventDesc.EncodeList(descriptors);
        Assert.Equal(1 + 2 * AutosplitEventDesc.Size, payload.Length);
        Assert.Equal(descriptors, AutosplitEventDesc.ParseList(payload));
        Assert.Empty(AutosplitEventDesc.ParseList(new byte[] { 0 }));
    }

    [Fact]
    public void TheEventsReplyCarriesTheLatestSequenceNumberAndTheEvents()
    {
        var reply = new AutosplitEventsReply(9, new[]
        {
            new AutosplitEvent(8, 100, AutosplitKind.Split, 1, 2),
            new AutosplitEvent(9, 140, AutosplitKind.Split, 1, 3),
        });

        var bytes = reply.ToBytes();
        Assert.Equal(5 + 2 * AutosplitEvent.Size, bytes.Length);

        var parsed = AutosplitEventsReply.Parse(bytes);
        Assert.Equal(9u, parsed.LatestSeq);
        Assert.Equal(reply.Events, parsed.Events);
        Assert.Equal(9u, parsed.HighestSeq);

        // Nothing yet: latest_seq is 0 and the list is empty, which is what a fresh module reports.
        var empty = AutosplitEventsReply.Parse(new AutosplitEventsReply(0, Array.Empty<AutosplitEvent>()).ToBytes());
        Assert.Equal(0u, empty.LatestSeq);
        Assert.Empty(empty.Events);
        Assert.Equal(0u, empty.HighestSeq);
    }

    [Fact]
    public void TheDatagramIsRecognisedByItsMagicAndLengthAndNoTelemetryPacketIs()
    {
        var ev = new AutosplitEvent(5, 600, AutosplitKind.Reset, 0, 0);
        var datagram = AutosplitDatagram.Build(ev);

        Assert.Equal(AutosplitDatagram.Size, datagram.Length);
        Assert.Equal((byte)'Q', datagram[0]);
        Assert.Equal((byte)'E', datagram[1]);
        Assert.Equal(AutosplitDatagram.Version, datagram[2]);
        Assert.Equal(0, datagram[3]);

        Assert.True(AutosplitDatagram.TryParse(datagram, out var parsed));
        Assert.Equal(ev, parsed);

        // The telemetry packet shares the socket: same first byte, different everything else.
        var telemetry = new TelemetryPacket(SessionInfo.Empty, Array.Empty<WatchValue>()).ToBytes();
        Assert.False(AutosplitDatagram.Matches(telemetry));
        Assert.False(AutosplitDatagram.TryParse(telemetry, out _));

        // Right length, wrong magic, and a version this build does not know.
        Assert.False(AutosplitDatagram.Matches(new byte[AutosplitDatagram.Size]));
        datagram[2] = 2;
        Assert.True(AutosplitDatagram.Matches(datagram));
        Assert.False(AutosplitDatagram.TryParse(datagram, out _));
    }

    // ---------------------------------------------------------------- the two ops

    [Fact]
    public async Task DescribeReturnsTheRunningGamesRowsAndUnsupportedWithoutAWatcher()
    {
        var (server, client, _) = await ConnectAsync();
        using (server)
        using (client)
        {
            var rows = await client.AutosplitDescribeAsync();
            Assert.Equal(3, rows.Length);
            Assert.Equal("Planet entered", rows[0].Label);
            Assert.True(rows[0].PlanetRoute);
            Assert.True(rows[0].EnabledByDefault);
            Assert.False(rows[2].EnabledByDefault);

            server.AutosplitDescriptors.Clear();
            var refused = await Assert.ThrowsAsync<QwarkStatusException>(() => client.AutosplitDescribeAsync());
            Assert.Equal(Status.Unsupported, refused.Status);
        }
    }

    [Fact]
    public async Task EventsSinceASequenceNumberOnlyCarriesTheNewerOnes()
    {
        var (server, client, _) = await ConnectAsync(s => s.AutosplitPush = false);
        using (server)
        using (client)
        {
            server.EmitAutosplitEvent(AutosplitKind.Start);
            server.EmitAutosplitEvent(AutosplitKind.Split, 1, 2);
            server.EmitAutosplitEvent(AutosplitKind.Split, 1, 3);

            var all = await client.AutosplitEventsAsync(0);
            Assert.Equal(3u, all.LatestSeq);
            Assert.Equal(new uint[] { 1, 2, 3 }, all.Events.Select(e => e.Seq));
            Assert.Equal(AutosplitKind.Start, all.Events[0].Kind);
            Assert.Equal(3u, all.Events[2].Arg);

            var newer = await client.AutosplitEventsAsync(2);
            Assert.Equal(3u, newer.LatestSeq);
            Assert.Equal(new uint[] { 3 }, newer.Events.Select(e => e.Seq));

            var none = await client.AutosplitEventsAsync(3);
            Assert.Empty(none.Events);
            Assert.Equal(3u, none.LatestSeq);
        }
    }

    // ---------------------------------------------------------------- the receiver

    [Fact]
    public async Task APushedEventIsRaisedOnceThoughItArrivesThreeTimes()
    {
        var (server, client, seen) = await ConnectAsync();
        using (server)
        using (client)
        {
            var emitted = server.EmitAutosplitEvent(AutosplitKind.Split, 1, 3);

            Assert.True(await WaitFor(() => Snapshot(seen).Length == 1));

            // All three copies have gone out by now; the dedupe is what keeps the count at one.
            await Task.Delay(300);
            var events = Snapshot(seen);
            Assert.Single(events);
            Assert.Equal(emitted, events[0]);
            Assert.Equal(emitted.Seq, client.LastAutosplitSeq);
        }
    }

    [Fact]
    public async Task AGapInThePushIsFilledFromTheRingAndDeliveredInOrder()
    {
        var (server, client, seen) = await ConnectAsync(s => s.AutosplitPush = false);
        using (server)
        using (client)
        {
            // Two events whose datagrams never arrived, then one that did.
            server.EmitAutosplitEvent(AutosplitKind.Start);
            server.EmitAutosplitEvent(AutosplitKind.Split, 1, 2);
            server.AutosplitPush = true;
            server.EmitAutosplitEvent(AutosplitKind.Split, 1, 3);

            Assert.True(await WaitFor(() => Snapshot(seen).Length == 3));

            await Task.Delay(200);
            var events = Snapshot(seen);
            Assert.Equal(new uint[] { 1, 2, 3 }, events.Select(e => e.Seq));
            Assert.Equal(AutosplitKind.Start, events[0].Kind);
            Assert.Equal(3u, events[2].Arg);
        }
    }

    [Fact]
    public async Task WhatTheRingAlreadyHeldOnConnectIsRecordedButNeverRaised()
    {
        var (server, client, seen) = await ConnectAsync(s =>
        {
            s.EmitAutosplitEvent(AutosplitKind.Start);
            s.EmitAutosplitEvent(AutosplitKind.Split, 1, 2);
            s.EmitAutosplitEvent(AutosplitKind.Split, 1, 3);
        });

        using (server)
        using (client)
        {
            // The pushes for those three go out as soon as there is a subscriber; none may be acted on.
            await Task.Delay(300);
            Assert.Empty(Snapshot(seen));
            Assert.Equal(3u, client.LastAutosplitSeq);

            // The next one is a new event and does arrive.
            server.EmitAutosplitEvent(AutosplitKind.Split, 2, 0);
            Assert.True(await WaitFor(() => Snapshot(seen).Length == 1));
            Assert.Equal(4u, Snapshot(seen)[0].Seq);
        }
    }

    [Fact]
    public async Task TheSafetyPollDeliversAnEventWhosePushWasLost()
    {
        var (server, client, seen) = await ConnectAsync(s => s.AutosplitPush = false, safetyPoll: true);
        using (server)
        using (client)
        {
            server.EmitAutosplitEvent(AutosplitKind.Split, 1, 3);

            // No datagram at all: only the 1 Hz poll can find this one.
            Assert.True(await WaitFor(() => Snapshot(seen).Length == 1, 4000));
            Assert.Equal(1u, Snapshot(seen)[0].Seq);
        }
    }

    [Fact]
    public async Task ATelemetryPacketOnTheSameSocketIsStillParsedAsTelemetry()
    {
        var (server, client, seen) = await ConnectAsync();
        using (server)
        using (client)
        {
            Assert.True(await WaitFor(() => client.LatestTelemetry is not null));
            Assert.Empty(Snapshot(seen));

            // A datagram whose length matches nothing is dropped by both parsers.
            using var udp = new UdpClient();
            var junk = new byte[AutosplitDatagram.Size];
            junk[0] = (byte)'Q';
            junk[1] = (byte)'W';
            await udp.SendAsync(junk, junk.Length, new IPEndPoint(IPAddress.Loopback, client.TelemetryPort));

            await Task.Delay(200);
            Assert.Empty(Snapshot(seen));
            Assert.NotNull(client.LatestTelemetry);
        }
    }

    [Fact]
    public async Task AModuleFromBeforeRevisionOnePointFourTurnsTheWholeThingOff()
    {
        // UNKNOWN_OP on the prime is how an older SPRX answers; the client stops asking rather
        // than polling a console that will never know the op.
        var (server, client, seen) = await ConnectAsync(s => s.AutosplitSupported = false, safetyPoll: true);
        using (server)
        using (client)
        {
            Assert.False(client.AutosplitAvailable);

            server.AutosplitPush = false;
            server.EmitAutosplitEvent(AutosplitKind.Split, 1, 3);
            await Task.Delay(1500);
            Assert.Empty(Snapshot(seen));

            var unknown = await Assert.ThrowsAsync<QwarkStatusException>(() => client.AutosplitEventsAsync(0));
            Assert.Equal(Status.UnknownOp, unknown.Status);
        }
    }
}
