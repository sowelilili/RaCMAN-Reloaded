using System.Buffers.Binary;
using System.Text;

using RaCMAN.Protocol.Testing;

namespace RaCMAN.Protocol.Tests;

public class ParsingTests
{
    /// <summary>
    /// A SessionInfo assembled by hand at the offsets section 3 of PROTOCOL.md gives, so the
    /// fixture is independent of SpanWriter.
    /// </summary>
    private static byte[] HandBuiltSessionInfo()
    {
        var b = new byte[SessionInfo.Size];
        b[0] = 1;                                                     // protocol_version
        b[1] = 42;                                                    // qwark_version
        b[2] = (byte)SessionState.Ingame;                             // state
        b[3] = (byte)GameId.Rac3;                                     // game
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 7);        // generation
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 123456);   // tick
        Encoding.ASCII.GetBytes("NPEA00387").CopyTo(b, 12);           // title_id[12]
        b[24] = 0x01;                                                 // flags: PREVIOUS_PENDING
        b[25] = 3;                                                    // selected_slot
        b[26] = 5;                                                    // selected_planet
        b[27] = 0x03;                                                 // planet_flags
        b[28] = 9;                                                    // current_planet
        // b[29..31] pad
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(32), 1.5f);    // pos[0]
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(36), -2.25f);  // pos[1]
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(40), 300f);    // pos[2]
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(44), 0x8041);  // pad_mask: l2 + cross + left
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(48), 0.25f);   // analog rx
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(52), -0.5f);   // analog ry
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(56), 0.75f);   // analog lx
        BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(60), -1.0f);   // analog ly
        // readout[16] at 64..127 since revision 1.1
        for (int i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(64 + i * 4), (uint)(1000 + i));
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(128), 0x0000000000000005ul);  // toggle_state
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(136), 0x0000000000000004ul);  // toggle_auto
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(144), 0x0000000000000002ul);  // freeze_active
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(152), 0x00000003);            // mod_loaded
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(156), 0x00000001);            // mod_auto
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(160), 0x00000002);            // mod_previous
        return b;
    }

    [Fact]
    public void SessionInfoParsesEveryFieldAtTheDocumentedOffset()
    {
        var info = SessionInfo.Parse(HandBuiltSessionInfo());

        Assert.Equal(1, info.ProtocolVersion);
        Assert.Equal(42, info.QwarkVersion);
        Assert.Equal(SessionState.Ingame, info.State);
        Assert.Equal(GameId.Rac3, info.Game);
        Assert.Equal(7u, info.Generation);
        Assert.Equal(123456u, info.Tick);
        Assert.Equal("NPEA00387", info.TitleId);
        Assert.True(info.PreviousPending);
        Assert.Equal(3, info.SelectedSlot);
        Assert.Equal(5, info.SelectedPlanet);
        Assert.Equal(PlanetFlags.ResetLevelFlags | PlanetFlags.ResetSpecialBolts, info.PlanetFlags);
        Assert.Equal(9, info.CurrentPlanet);
        Assert.Equal(1.5f, info.PosX);
        Assert.Equal(-2.25f, info.PosY);
        Assert.Equal(300f, info.PosZ);
        Assert.Equal(0x8041u, info.PadMask);
        Assert.Equal(new[] { 0.25f, -0.5f, 0.75f, -1.0f }, info.Analog);
        Assert.Equal(Enumerable.Range(1000, 16).Select(i => (uint)i).ToArray(), info.Readout);
        Assert.Equal(1015u, info.ReadoutAt(15));
        Assert.Null(info.ReadoutAt(16));
        Assert.Equal(5ul, info.ToggleState);
        Assert.Equal(4ul, info.ToggleAuto);
        Assert.Equal(2ul, info.FreezeActive);
        Assert.Equal(3u, info.ModLoaded);
        Assert.Equal(1u, info.ModAuto);
        Assert.Equal(2u, info.ModPrevious);
    }

    [Fact]
    public void SessionInfoRoundTripsThroughItsOwnWriter()
    {
        var original = HandBuiltSessionInfo();
        var again = SessionInfo.Parse(original).ToBytes();
        Assert.Equal(original, again);
    }

    [Fact]
    public void SessionInfoIsExactly164Bytes()
    {
        Assert.Equal(164, SessionInfo.Size);
        Assert.Equal(16, SessionInfo.ReadoutCount);
        Assert.Equal(164, SessionInfo.Empty.ToBytes().Length);
        Assert.Throws<ProtocolException>(() => SessionInfo.Parse(new byte[163]));
    }

    [Fact]
    public void TelemetryPacketParsesMagicSessionAndWatches()
    {
        var session = HandBuiltSessionInfo();
        var packet = new byte[4 + 164 + 1 + 2 * 12];
        Encoding.ASCII.GetBytes("QWRK").CopyTo(packet, 0);
        session.CopyTo(packet, 4);
        packet[168] = 2;                                                 // nwatch

        packet[169] = 3;                                                 // id
        packet[170] = 1;                                                 // size
        packet[171] = 1;                                                 // valid
        packet[172] = 0;                                                 // pad
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(173), 0xAB); // right-aligned 1-byte value

        packet[181] = 4;
        packet[182] = 4;
        packet[183] = 0;                                                 // not valid this tick
        packet[184] = 0;
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(185), 0);

        var parsed = TelemetryPacket.Parse(packet);

        Assert.Equal(GameId.Rac3, parsed.Session.Game);
        Assert.Equal(2, parsed.Watches.Length);
        Assert.Equal(new WatchValue(3, 1, true, 0xAB), parsed.Watches[0]);
        Assert.Equal(new WatchValue(4, 4, false, 0), parsed.Watches[1]);
        Assert.Equal(packet, parsed.ToBytes());
    }

    [Fact]
    public void TelemetryPacketWithoutMagicIsRejected()
    {
        var packet = new byte[4 + 164 + 1];
        Encoding.ASCII.GetBytes("NOPE").CopyTo(packet, 0);
        Assert.Throws<ProtocolException>(() => TelemetryPacket.Parse(packet));
    }

    [Fact]
    public void FullTelemetryPacketIs937Bytes()
    {
        // Section 4: "Maximum 64 watches, so the packet never exceeds 937 bytes."
        var watches = Enumerable.Range(0, 64).Select(i => new WatchValue((byte)i, 8, true, (ulong)i)).ToArray();
        Assert.Equal(937, new TelemetryPacket(SessionInfo.Empty, watches).ToBytes().Length);
    }

    [Fact]
    public void FeatureParsesFromItsFieldList()
    {
        var entry = new byte[Feature.Size];
        entry[0] = 12;                                               // id
        entry[1] = (byte)FeatureKind.Value;                          // kind
        entry[2] = 1;                                                // group
        entry[3] = 0;                                                // aux: 0 for anything but ENUM
        entry[4] = (byte)(FeatureFlags.Auto | FeatureFlags.WritesCode);
        entry[5] = 2;                                                // readout: mirrors readout 2
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(8), 1);   // min
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(12), 99); // max
        Encoding.ASCII.GetBytes("Bolts").CopyTo(entry, 16);          // label[32]

        var feature = Feature.Parse(entry);

        Assert.Equal(12, feature.Id);
        Assert.Equal(FeatureKind.Value, feature.Kind);
        Assert.Equal(1, feature.Group);
        Assert.Equal(0, feature.OptionCount);
        Assert.Equal((byte?)2, feature.MirrorReadout);
        Assert.True(feature.Auto);
        Assert.True(feature.WritesCode);
        Assert.Equal(1u, feature.Min);
        Assert.Equal(99u, feature.Max);
        Assert.Equal("Bolts", feature.Label);
        Assert.Equal(entry, feature.ToBytes());
    }

    [Fact]
    public void FeatureIs48BytesAndAuxIsTheEnumOptionCount()
    {
        Assert.Equal(48, Feature.Size);
        Assert.Throws<ProtocolException>(() => Feature.Parse(new byte[44]));

        var choice = new Feature(1, FeatureKind.Enum, 0, 3, FeatureFlags.None, 7, 0, 2, "Ghost mode");
        Assert.Equal(3, choice.OptionCount);
        Assert.Equal((byte?)7, choice.MirrorReadout);

        // TOGGLE and ACTION never mirror a readout, and 0xFF means "none" for the kinds that can.
        var toggle = new Feature(2, FeatureKind.Toggle, 0, 0, FeatureFlags.None, 0, 0, 0, "Ghost");
        Assert.Null(toggle.MirrorReadout);
        Assert.Equal(0, toggle.OptionCount);

        var unmirrored = new Feature(3, FeatureKind.Color, 0, 0, FeatureFlags.None, 0xFF, 0, 0, "Colour");
        Assert.Null(unmirrored.MirrorReadout);
    }

    [Fact]
    public void DescribeParsesGroupsReadoutsAndFeatures()
    {
        var describe = new DescribeResult(
            GameId.Rac2,
            new[] { "Cheats", "Setups" },
            new[] { "Bolts" },
            new[]
            {
                new Feature(0, FeatureKind.Toggle, 0, 0, FeatureFlags.None, 0xFF, 0, 0, "Infinite ammo"),
                new Feature(1, FeatureKind.Enum, 1, 3, FeatureFlags.None, 0, 0, 2, "Category"),
            });

        var parsed = DescribeResult.Parse(FakeQwarkServer.EncodeDescribe(describe));

        Assert.Equal(GameId.Rac2, parsed.Game);
        Assert.Equal(new[] { "Cheats", "Setups" }, parsed.Groups);
        Assert.Equal(new[] { "Bolts" }, parsed.Readouts);
        Assert.Equal(2, parsed.Features.Length);
        Assert.Equal("Infinite ammo", parsed.Features[0].Label);
        Assert.Equal(FeatureKind.Enum, parsed.Features[1].Kind);
        Assert.Equal(3, parsed.Features[1].OptionCount);
        Assert.Equal((byte?)0, parsed.Features[1].MirrorReadout);
        Assert.Equal("Setups", parsed.GroupName(parsed.Features[1].Group));
    }

    [Fact]
    public void DescribeRejectsThe44ByteFeatureStride()
    {
        // Revision 1.1 fixed Feature at 48 bytes; the old 44-byte stride is no longer accepted.
        var payload = new List<byte> { (byte)GameId.Rac1, 0, 0, 2 };
        payload.AddRange(new byte[2 * 44]);

        Assert.Throws<ProtocolException>(() => DescribeResult.Parse(payload.ToArray()));
    }

    [Fact]
    public void ModEntryParsesAt120Bytes()
    {
        var entry = new byte[ModEntry.Size];
        entry[0] = 4;                                                       // index
        entry[1] = (byte)(ModFlags.Loaded | ModFlags.NeedsLua);             // flags
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(4), 0xDEADBEEF); // hash
        Encoding.ASCII.GetBytes("dl-cs").CopyTo(entry, 8);                  // dirname[32]
        Encoding.ASCII.GetBytes("Deadlocked crash patches").CopyTo(entry, 40); // name[32]
        Encoding.ASCII.GetBytes("1.2.3").CopyTo(entry, 72);                 // version[16]
        Encoding.ASCII.GetBytes("someone").CopyTo(entry, 88);               // author[32]

        var mod = ModEntry.Parse(entry);

        Assert.Equal(4, mod.Index);
        Assert.True(mod.Loaded);
        Assert.True(mod.NeedsLua);
        Assert.False(mod.Auto);
        Assert.Equal(0xDEADBEEFu, mod.Hash);
        Assert.Equal("dl-cs", mod.DirName);
        Assert.Equal("Deadlocked crash patches", mod.Name);
        Assert.Equal("1.2.3", mod.Version);
        Assert.Equal("someone", mod.Author);
        Assert.Equal(entry, mod.ToBytes());
    }

    [Fact]
    public void WatchFreezeAndPatchListsParse()
    {
        var watches = new byte[] { 2, 0, 4, 0, 0, 0x00, 0x30, 0x00, 0x00, 1, 1, 0, 0, 0x00, 0x30, 0x00, 0x08 };
        var parsedWatches = WatchEntry.ParseList(watches);
        Assert.Equal(new WatchEntry(0, 4, 0x300000), parsedWatches[0]);
        Assert.Equal(new WatchEntry(1, 1, 0x300008), parsedWatches[1]);

        var freezes = new byte[1 + 16];
        freezes[0] = 1;
        freezes[1] = 7;    // id
        freezes[2] = 2;    // size
        BinaryPrimitives.WriteUInt32BigEndian(freezes.AsSpan(5), 0x310000);
        BinaryPrimitives.WriteUInt64BigEndian(freezes.AsSpan(9), 10);
        var parsedFreezes = FreezeEntry.ParseList(freezes);
        Assert.Equal(new FreezeEntry(7, 2, 0x310000, 10), parsedFreezes[0]);

        var patches = new byte[1 + 40];
        patches[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(patches.AsSpan(1), 0x1B0000);
        BinaryPrimitives.WriteUInt16BigEndian(patches.AsSpan(5), 3);
        patches[7] = (byte)PatchKind.Mod;
        Encoding.ASCII.GetBytes("crash-patch").CopyTo(patches, 9);
        var parsedPatches = PatchEntry.ParseList(patches);
        Assert.Equal(new PatchEntry(0x1B0000, 3, PatchKind.Mod, "crash-patch"), parsedPatches[0]);
    }

    [Fact]
    public void PositionListParses()
    {
        var payload = new byte[2 + 2 * 16];
        payload[0] = 4;  // planet
        payload[1] = 2;  // nslots
        payload[2] = 1;  // filled
        BinaryPrimitives.WriteSingleBigEndian(payload.AsSpan(6), 1f);
        BinaryPrimitives.WriteSingleBigEndian(payload.AsSpan(10), 2f);
        BinaryPrimitives.WriteSingleBigEndian(payload.AsSpan(14), 3f);

        var list = PositionList.Parse(payload);
        Assert.Equal(4, list.Planet);
        Assert.Equal(2, list.Slots.Length);
        Assert.Equal(new PositionSlot(0, true, 1f, 2f, 3f), list.Slots[0]);
        Assert.False(list.Slots[1].Filled);
    }

    [Fact]
    public void UnlockListParsesAt44Bytes()
    {
        var payload = new byte[1 + 24 + 1 + Unlock.Size];
        payload[0] = 1;
        Encoding.ASCII.GetBytes("Weapons").CopyTo(payload, 1);
        payload[25] = 1;
        payload[26] = 3;   // id
        payload[27] = 0;   // category
        payload[28] = 0b1011;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(30), 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(34), 0);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(38), 5);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(42), 99);
        Encoding.ASCII.GetBytes("Blaster").CopyTo(payload, 46);

        var list = UnlockList.Parse(payload);
        Assert.Equal(new[] { "Weapons" }, list.Categories);
        var unlock = Assert.Single(list.Unlocks);
        Assert.Equal(3, unlock.Id);
        Assert.Equal("Blaster", unlock.Name);
        Assert.True(unlock.HasField(0));
        Assert.False(unlock.HasField(2));
        Assert.Equal(new uint[] { 1, 0, 5, 99 }, unlock.Values);
    }

    [Fact]
    public void PreviousSessionParses()
    {
        var payload = new byte[8 + 4 + 1 + 16 + 1 + 8];
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(0), 0b101);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8), 0b10);
        payload[12] = 1;
        payload[13] = 4;  // freeze size
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(17), 0x320000);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(21), 42);
        payload[29] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(30), 0x1C0000);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(34), 2);

        var previous = PreviousSession.Parse(payload);
        Assert.Equal(0b101ul, previous.Toggles);
        Assert.Equal(0b10u, previous.Mods);
        Assert.Equal(new PreviousFreeze(4, 0x320000, 42), Assert.Single(previous.Freezes));
        Assert.Equal(new PreviousPatch(0x1C0000, 2), Assert.Single(previous.Patches));
        Assert.False(previous.IsEmpty);
    }

    [Fact]
    public void ComboAndDirListsParse()
    {
        var combos = new byte[] { 1, (byte)ComboAction.LoadPosition, 0, 0, 0, 0, 0, 0, 0x0B };
        var parsedCombos = ComboEntry.ParseList(combos);
        Assert.Equal(new ComboEntry(ComboAction.LoadPosition, 0x0B), Assert.Single(parsedCombos));

        var dir = new byte[] { 0, 2, 1, 3, 0, 0, 0, 0, (byte)'m', (byte)'o', (byte)'d', 0, 8, 0, 0, 0x04, 0x00, (byte)'p', (byte)'a', (byte)'t', (byte)'c', (byte)'h', (byte)'.', (byte)'t', (byte)'x' };
        var parsedDir = DirEntry.ParseList(dir);
        Assert.Equal(2, parsedDir.Length);
        Assert.True(parsedDir[0].IsDirectory);
        Assert.Equal("mod", parsedDir[0].Name);
        Assert.Equal(1024u, parsedDir[1].Size);
        Assert.Equal("patch.tx", parsedDir[1].Name);
    }

    [Fact]
    public void MobyTableParses()
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0), 0x95F6B0);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), 0x95F6B4);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(8), 0x100);

        var info = MobyTableInfo.Parse(payload);
        Assert.Equal(0x95F6B0u, info.TablePointerAddress);
        Assert.Equal(0x95F6B4u, info.TableEndPointerAddress);
        Assert.Equal(0x100, info.Stride);
    }

    [Fact]
    public void PadMaskDecodesToTheOgButtonNames()
    {
        Assert.Equal("None", PadButtons.Describe(0));
        Assert.Equal("L2 + Cross + Left", PadButtons.Describe(0x8041));
        Assert.Equal(new[] { "L1", "R1" }, PadButtons.DecodeNames(0x0C).ToArray());
    }

    [Theory]
    [InlineData("123456789", 0xCBF43926u)]
    [InlineData("", 0x00000000u)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0x414FA339u)]
    public void Crc32MatchesKnownVectors(string input, uint expected)
    {
        Assert.Equal(expected, Crc32.Compute(Encoding.ASCII.GetBytes(input)));
    }

    [Fact]
    public void Crc32SumTextIsEightLowercaseHexDigits()
    {
        Assert.Equal("cbf43926", Crc32.ToSumText(0xCBF43926));
        Assert.Equal("0000000f", Crc32.ToSumText(15));
        Assert.True(Crc32.TryParseSum("cbf43926", out uint parsed));
        Assert.Equal(0xCBF43926u, parsed);
    }
}
