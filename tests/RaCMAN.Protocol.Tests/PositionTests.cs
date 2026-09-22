using System.Buffers.Binary;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// POS_EDIT and POS_STORE, revision 1.13: the two ops that let a client change a slot without
/// standing where the position is. The bytes they put on the wire, and what the console answers
/// for a slot that does not exist, a slot with nothing in it and a blob of the wrong length.
/// </summary>
public class PositionTests
{
    private const byte Planet = 2;

    private static async Task<(FakeQwarkServer Server, QwarkClient Client)> ConnectAsync()
    {
        var server = new FakeQwarkServer();
        server.Start();

        var client = new QwarkClient { AutoReconnect = false };
        await client.ConnectAsync("127.0.0.1", server.Port);
        return (server, client);
    }

    /// <summary>An opcode is part of the contract, so the numbers are checked rather than assumed.</summary>
    [Fact]
    public void TheTwoOpsSitAtTheEndOfThePositionBlock()
    {
        Assert.Equal(0x004A, (int)Opcode.PosEdit);
        Assert.Equal(0x004B, (int)Opcode.PosStore);
    }

    [Fact]
    public async Task PosEditPutsTheSlotThreePadBytesAndThreeBigEndianFloatsOnTheWire()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await client.PosEditAsync(1, 1.5f, -2.25f, 3f);

            var payload = server.LastPayload(Opcode.PosEdit);
            Assert.NotNull(payload);
            Assert.Equal(16, payload!.Length);
            Assert.Equal(1, payload[0]);
            Assert.Equal(new byte[] { 0, 0, 0 }, payload[1..4]);
            Assert.Equal(1.5f, BinaryPrimitives.ReadSingleBigEndian(payload.AsSpan(4, 4)));
            Assert.Equal(-2.25f, BinaryPrimitives.ReadSingleBigEndian(payload.AsSpan(8, 4)));
            Assert.Equal(3f, BinaryPrimitives.ReadSingleBigEndian(payload.AsSpan(12, 4)));
        }
    }

    [Fact]
    public async Task PosStorePutsTheSlotTheLengthTwoPadBytesAndTheBlobOnTheWire()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var blob = new byte[server.PositionBlobLength];
            for (int i = 0; i < blob.Length; i++) blob[i] = (byte)(0xA0 + i);

            await client.PosStoreAsync(5, blob);

            var payload = server.LastPayload(Opcode.PosStore);
            Assert.NotNull(payload);
            Assert.Equal(4 + blob.Length, payload!.Length);
            Assert.Equal(5, payload[0]);
            Assert.Equal(blob.Length, payload[1]);
            Assert.Equal(new byte[] { 0, 0 }, payload[2..4]);
            Assert.Equal(blob, payload[4..]);
        }
    }

    /// <summary>
    /// The blob goes in whole and the slot is filled from then on, coordinates and all: POS_LIST
    /// reads the same three floats back out of the front of it.
    /// </summary>
    [Fact]
    public async Task PosStoreFillsAnEmptySlotWithTheBlobItWasGiven()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            Assert.False((await client.PosListAsync()).Slots[4].Filled);

            var blob = Blob(server, 11f, 22f, 33f);
            await client.PosStoreAsync(4, blob);

            var slot = (await client.PosListAsync()).Slots[4];
            Assert.True(slot.Filled);
            Assert.Equal(11f, slot.X);
            Assert.Equal(22f, slot.Y);
            Assert.Equal(33f, slot.Z);
            Assert.Equal(blob, server.PositionBlob(Planet, 4));
        }
    }

    /// <summary>
    /// The length is the running game's, so a blob of any other length is another game's position
    /// and is refused without a byte of it being written.
    /// </summary>
    [Fact]
    public async Task PosStoreRefusesABlobThatIsNotTheRunningGamesLength()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var wrong = new byte[server.PositionBlobLength + 2];
            var error = await Assert.ThrowsAsync<QwarkStatusException>(() => client.PosStoreAsync(4, wrong));

            Assert.Equal(Status.BadArg, error.Status);
            Assert.Null(server.PositionBlob(Planet, 4));
        }
    }

    [Fact]
    public async Task PosStoreRefusesASlotAPlanetDoesNotHave()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var error = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.PosStoreAsync(FakeQwarkServer.PositionSlotCount, Blob(server, 1f, 2f, 3f)));

            Assert.Equal(Status.BadArg, error.Status);
        }
    }

    /// <summary>
    /// The coordinates move and nothing else in the blob does: the rotation and the camera behind
    /// them are the game's, and a client that never read them must not write over them.
    /// </summary>
    [Fact]
    public async Task PosEditMovesTheCoordinatesAndLeavesTheRestOfTheBlobAlone()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            var blob = Blob(server, 1f, 2f, 3f);
            for (int i = 12; i < blob.Length; i++) blob[i] = (byte)i;
            await client.PosStoreAsync(6, blob);

            await client.PosEditAsync(6, -1.5f, 0f, 4096.5f);

            var slot = (await client.PosListAsync()).Slots[6];
            Assert.Equal(-1.5f, slot.X);
            Assert.Equal(0f, slot.Y);
            Assert.Equal(4096.5f, slot.Z);

            var stored = server.PositionBlob(Planet, 6);
            Assert.NotNull(stored);
            Assert.Equal(blob[12..], stored![12..]);
        }
    }

    [Fact]
    public async Task PosEditRefusesAnEmptySlotAndASlotAPlanetDoesNotHave()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            // Slots 2 upwards hold nothing until something is stored in them.
            var empty = await Assert.ThrowsAsync<QwarkStatusException>(() => client.PosEditAsync(3, 1f, 2f, 3f));
            Assert.Equal(Status.NotFound, empty.Status);

            var past = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.PosEditAsync(FakeQwarkServer.PositionSlotCount, 1f, 2f, 3f));
            Assert.Equal(Status.BadArg, past.Status);
        }
    }

    /// <summary>
    /// Both read and write the running process, so both answer NOT_INGAME outside it, exactly as
    /// POS_LIST does.
    /// </summary>
    [Fact]
    public async Task BothNeedIngame()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.Session = server.Session with { State = SessionState.Xmb };

            var edit = await Assert.ThrowsAsync<QwarkStatusException>(() => client.PosEditAsync(0, 1f, 2f, 3f));
            Assert.Equal(Status.NotIngame, edit.Status);

            var store = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.PosStoreAsync(4, new byte[server.PositionBlobLength]));
            Assert.Equal(Status.NotIngame, store.Status);
        }
    }

    /// <summary>A title the module has no game for has no slots, so both are game ops and refused.</summary>
    [Fact]
    public async Task BothAreGameOpsOnATitleTheModuleHasNoGameFor()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            server.UnknownGame = true;

            var edit = await Assert.ThrowsAsync<QwarkStatusException>(() => client.PosEditAsync(0, 1f, 2f, 3f));
            Assert.Equal(Status.Unsupported, edit.Status);

            var store = await Assert.ThrowsAsync<QwarkStatusException>(
                () => client.PosStoreAsync(0, new byte[server.PositionBlobLength]));
            Assert.Equal(Status.Unsupported, store.Status);
        }
    }

    /// <summary>A blob longer than a slot can hold never reaches the wire.</summary>
    [Fact]
    public async Task AnImpossibleBlobIsRefusedBeforeItIsSent()
    {
        var (server, client) = await ConnectAsync();
        using (server)
        using (client)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => client.PosStoreAsync(0, Array.Empty<byte>()));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => client.PosStoreAsync(0, new byte[QwarkClient.MaxPositionBlob + 1]));

            Assert.DoesNotContain(Opcode.PosStore, server.RequestLog());
        }
    }

    private static byte[] Blob(FakeQwarkServer server, float x, float y, float z)
    {
        var blob = new byte[server.PositionBlobLength];
        BinaryPrimitives.WriteSingleBigEndian(blob.AsSpan(0, 4), x);
        BinaryPrimitives.WriteSingleBigEndian(blob.AsSpan(4, 4), y);
        BinaryPrimitives.WriteSingleBigEndian(blob.AsSpan(8, 4), z);
        return blob;
    }
}
