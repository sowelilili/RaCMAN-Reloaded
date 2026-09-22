using RaCMAN.App;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The saved positions the old client kept in its config.txt, read back out of it. Every game it
/// ran wrote them under the planet's own name — <c>&lt;PlanetName&gt;SavedPos&lt;slot&gt;</c> — as
/// the hex of the bytes at the player coordinates, which is byte for byte the blob qwark keeps for
/// a slot today; Deadlocked's is that snapshot with its two camera floats, which rac4.cs wrote as
/// two keys of their own.
/// </summary>
public class LegacyPositionTests
{
    /// <summary>One value as ReadMemoryStr wrote it: lower-case hex, two characters a byte.</summary>
    private static string Hex(byte first, int length) =>
        string.Concat(Enumerable.Range(0, length).Select(i => ((byte)(first + i)).ToString("x2")));

    private static LegacyConfig Config(params string[] lines) =>
        LegacyConfig.Parse(string.Join("\n", lines) + "\n");

    private static PositionList Slots(byte planet, params bool[] filled)
    {
        var slots = new PositionSlot[filled.Length];
        for (int i = 0; i < filled.Length; i++) slots[i] = new PositionSlot((byte)i, filled[i], 0f, 0f, 0f);
        return new PositionList(planet, slots);
    }

    /// <summary>
    /// The name tables are the key the old file was written under, so a wrong index would look for
    /// a planet nobody saved on. Spot-checked against the four planetsList arrays.
    /// </summary>
    [Fact]
    public void ThePlanetNamesAreIndexedTheWayTheGamesNumberTheirPlanets()
    {
        Assert.Equal("Veldin", LegacyLibraryImport.LegacyPlanets(GameId.Rac1)[0]);
        Assert.Equal("Aranos", LegacyLibraryImport.LegacyPlanets(GameId.Rac2)[0]);
        Assert.Equal("Florana", LegacyLibraryImport.LegacyPlanets(GameId.Rac3)[1]);
        Assert.Equal("Sarathos", LegacyLibraryImport.LegacyPlanets(GameId.Rac4)[4]);
        Assert.Equal("GhostStation", LegacyLibraryImport.LegacyPlanets(GameId.Rac4)[14]);
        Assert.Empty(LegacyLibraryImport.LegacyPlanets(GameId.None));
    }

    /// <summary>
    /// Deadlocked's blob is the three keys end to end, in the order qwark reads them: 0x20 of
    /// position and rotation, then left-right, then up-down.
    /// </summary>
    [Fact]
    public void DeadlockedsBlobIsTheSnapshotWithTheTwoCameraFloatsAfterIt()
    {
        var config = Config(
            "SarathosSavedPos0 = " + Hex(0x10, 0x20),
            "SarathosSavedCamLR0 = " + Hex(0x80, 4),
            "SarathosSavedCamUD0 = " + Hex(0x90, 4));

        var position = Assert.Single(LegacyLibraryImport.Positions(config, GameId.Rac4));

        Assert.Equal(4, position.Planet);
        Assert.Equal(0, position.Slot);
        Assert.Equal("Sarathos", position.PlanetName);
        Assert.Equal(LegacyLibraryImport.Rac4BlobLength, position.Blob.Length);
        Assert.Equal(0x10, position.Blob[0]);
        Assert.Equal(0x80, position.Blob[LegacyLibraryImport.Rac4MainLength]);
        Assert.Equal(0x90, position.Blob[LegacyLibraryImport.Rac4MainLength + 4]);
    }

    /// <summary>
    /// A file from before rac4.cs saved the camera has neither key. qwark stores the camera and
    /// never restores it, so the position still comes over with those eight bytes at zero.
    /// </summary>
    [Fact]
    public void ADeadlockedPositionWithNoCameraKeysStillComesOverWithZerosThere()
    {
        var config = Config("KronosSavedPos2 = " + Hex(1, 0x20));

        var position = Assert.Single(LegacyLibraryImport.Positions(config, GameId.Rac4));

        Assert.Equal(5, position.Planet);
        Assert.Equal(2, position.Slot);
        Assert.Equal(LegacyLibraryImport.Rac4BlobLength, position.Blob.Length);
        Assert.Equal(new byte[8], position.Blob[LegacyLibraryImport.Rac4MainLength..]);
    }

    /// <summary>The other three wrote one key of thirty bytes, which is their whole blob.</summary>
    [Fact]
    public void TheOtherThreeGamesAreTheThirtyBytesIGameSavePositionWrote()
    {
        var config = Config(
            "OozlaSavedPos1 = " + Hex(0x40, LegacyLibraryImport.PlainBlobLength),
            "FloranaSavedPos0 = " + Hex(0x50, LegacyLibraryImport.PlainBlobLength),
            "NovalisSavedPos3 = " + Hex(0x60, LegacyLibraryImport.PlainBlobLength));

        var rac2 = Assert.Single(LegacyLibraryImport.Positions(config, GameId.Rac2));
        Assert.Equal(1, rac2.Planet);
        Assert.Equal(1, rac2.Slot);
        Assert.Equal(LegacyLibraryImport.PlainBlobLength, rac2.Blob.Length);
        Assert.Equal(0x40, rac2.Blob[0]);

        Assert.Equal(1, Assert.Single(LegacyLibraryImport.Positions(config, GameId.Rac3)).Planet);
        Assert.Equal(3, Assert.Single(LegacyLibraryImport.Positions(config, GameId.Rac1)).Slot);
    }

    /// <summary>
    /// The old file keyed on the planet's name alone, so RaC1's Orxon and Deadlocked's shared one
    /// line of it. The length is what tells them apart: thirty bytes is not a Deadlocked position.
    /// </summary>
    [Fact]
    public void AValueOfAnotherGamesLengthIsNotThisGamesPosition()
    {
        var config = Config("OrxonSavedPos0 = " + Hex(1, LegacyLibraryImport.PlainBlobLength));

        Assert.Single(LegacyLibraryImport.Positions(config, GameId.Rac1));
        Assert.Empty(LegacyLibraryImport.Positions(config, GameId.Rac4));
    }

    [Fact]
    public void AValueThatIsNotHexAtAllIsLeftWhereItIs()
    {
        var config = Config(
            "VeldinSavedPos0 = hello",
            "NovalisSavedPos0 = " + Hex(1, LegacyLibraryImport.PlainBlobLength - 1));

        Assert.Empty(LegacyLibraryImport.Positions(config, GameId.Rac1));
    }

    /// <summary>
    /// Deadlocked numbers three of its planets INFLOOP. The key is the name, so the same line would
    /// otherwise be counted once per index that carries it.
    /// </summary>
    [Fact]
    public void APlanetNameAGameRepeatsIsCountedOnce()
    {
        var config = Config("INFLOOPSavedPos0 = " + Hex(1, 0x20));

        var position = Assert.Single(LegacyLibraryImport.Positions(config, GameId.Rac4));
        Assert.Equal(3, position.Planet);
    }

    [Fact]
    public void AFileWithNoPositionsInItSaysSo()
    {
        Assert.False(Config("ip = 192.168.1.5", "tos = yes").HasSavedPositions);
        Assert.True(Config("SarathosSavedPos0 = " + Hex(1, 0x20)).HasSavedPositions);

        // Not part of HasAnything: a file full of positions is not an instruction to set the old
        // combo defaults on the console.
        Assert.False(Config("SarathosSavedPos0 = " + Hex(1, 0x20)).HasAnything);
    }

    // ---------------------------------------------------------------- the plan

    /// <summary>
    /// POS_STORE writes the current planet's slots, so the plan is that planet's alone: a slot the
    /// console already filled is never written over, and the old client's ninth and tenth slots
    /// have nowhere to go.
    /// </summary>
    [Fact]
    public void ThePlanIsTheLoadedPlanetsEmptySlotsAndNothingElse()
    {
        var config = Config(
            "SarathosSavedPos0 = " + Hex(0x10, 0x20),   // the console already holds one here
            "SarathosSavedPos2 = " + Hex(0x20, 0x20),
            "SarathosSavedPos3 = " + Hex(0x30, 0x20),
            "SarathosSavedPos9 = " + Hex(0x40, 0x20),   // past the eight a planet has now
            "KronosSavedPos0 = " + Hex(0x50, 0x20));    // another planet, another day

        var plan = LegacyLibraryImport.PlanPositions(config, GameId.Rac4,
            Slots(4, true, false, false, false, false, false, false, false));

        Assert.Equal(4, plan.Planet);
        Assert.Equal("Sarathos", plan.PlanetName);
        Assert.Equal(new byte[] { 2, 3 }, plan.Store.Select(p => p.Slot));
        Assert.Equal(1, plan.Occupied);
        Assert.Equal(1, plan.PastTheEnd);
        Assert.Equal(4, plan.Found);
        Assert.True(plan.Anything);

        Assert.Equal(
            "Sarathos: 2 to import, 1 whose slot is already filled, 1 past the 8 slots a planet has now",
            plan.Describe());
    }

    /// <summary>
    /// Without a POS_LIST there is no planet to write into and no way to know which slots are
    /// taken, so nothing is planned rather than something being written over.
    /// </summary>
    [Fact]
    public void NothingIsPlannedWithoutTheConsolesOwnSlotList()
    {
        var config = Config("SarathosSavedPos0 = " + Hex(1, 0x20));

        var plan = LegacyLibraryImport.PlanPositions(config, GameId.Rac4, PositionList.Empty);

        Assert.False(plan.Anything);
        Assert.Equal(0, plan.Found);
        Assert.Equal("no planet to import into", plan.Describe());
    }

    [Fact]
    public void APlanetTheOldClientNeverNamedPlansNothing()
    {
        var config = Config("SarathosSavedPos0 = " + Hex(1, 0x20));

        // Past the end of Deadlocked's own list of sixteen.
        Assert.False(LegacyLibraryImport.PlanPositions(config, GameId.Rac4, Slots(40, false)).Anything);

        // And a planet with nothing saved on it says so rather than saying nothing.
        Assert.Equal(
            "DreadZone: no saved positions in the file",
            LegacyLibraryImport.PlanPositions(config, GameId.Rac4, Slots(1, false)).Describe());
    }

    [Fact]
    public void HexIsTakenOnlyAtTheLengthAskedFor()
    {
        Assert.True(LegacyLibraryImport.TryHex("00ff10", 3, out var bytes));
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x10 }, bytes);

        Assert.False(LegacyLibraryImport.TryHex("00ff", 3, out _));
        Assert.False(LegacyLibraryImport.TryHex("00ff1020", 3, out _));
        Assert.False(LegacyLibraryImport.TryHex("00ffzz", 3, out _));
        Assert.False(LegacyLibraryImport.TryHex(null, 3, out _));
    }
}
