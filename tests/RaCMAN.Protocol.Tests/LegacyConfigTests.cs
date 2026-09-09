using RaCMAN.App;
using Xunit;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The old RaCMAN's config.txt, as ConfigureCombos.GetCombos and AttachPS3Form read it: "key = value"
/// lines, decimal combo masks, a zero or missing combo meaning the old built-in default.
/// </summary>
public class LegacyConfigTests
{
    private const string Sample = """
        ip = 192.168.1.50
        tos = yes
        savePosCombo = 11
        loadPosCombo = 0
        dieCombo = 0x5
        loadPlanetCombo = 1536
        runScriptCombo = 255
        autoApplyMods_NPEA00423 = crash-patch, il-ghost,,crash-patch
        autoApplyMods_NPEA00386 = fast-boot
        SavedColor1No0 = -65536
        """;

    [Fact]
    public void ReadsTheIpAndKeepsTheLastValueOfARepeatedKey()
    {
        var config = LegacyConfig.Parse(Sample + "\nip = 10.0.0.9\n");
        Assert.Equal("10.0.0.9", config.Ip);
    }

    [Fact]
    public void CombosUseTheFileValueOrTheOldDefault()
    {
        var config = LegacyConfig.Parse(Sample);
        var combos = config.Combos.ToDictionary(c => c.Action);

        Assert.True(config.HasComboKeys);
        Assert.Equal(11u, combos[ComboAction.SavePosition].Mask);
        Assert.False(combos[ComboAction.SavePosition].WasDefault);

        // loadPosCombo = 0 meant "use the default", exactly as GetCombos did.
        Assert.Equal(0x7u, combos[ComboAction.LoadPosition].Mask);
        Assert.True(combos[ComboAction.LoadPosition].WasDefault);

        // A hand-edited hex value is accepted too.
        Assert.Equal(0x5u, combos[ComboAction.Die].Mask);
        Assert.Equal(0x600u, combos[ComboAction.LoadPlanet].Mask);

        // Missing entirely: the old default.
        Assert.Equal(0x100u, combos[ComboAction.LoadSetAsideFile].Mask);
        Assert.True(combos[ComboAction.LoadSetAsideFile].WasDefault);
    }

    [Fact]
    public void ModAutoListsAreKeyedByTitleTrimmedAndDeduplicated()
    {
        var config = LegacyConfig.Parse(Sample);
        var mods = config.ModAutoByTitle;

        Assert.Equal(2, mods.Count);
        Assert.Equal(new[] { "crash-patch", "il-ghost" }, mods["NPEA00423"]);
        Assert.Equal(new[] { "fast-boot" }, mods["npea00386"]);   // title lookup is case-insensitive
    }

    [Fact]
    public void AnEmptyOrForeignFileHasNothingToImport()
    {
        Assert.False(LegacyConfig.Parse(string.Empty).HasAnything);
        Assert.False(LegacyConfig.Parse("tos = yes\nSavedColor1No0 = 5\n").HasAnything);

        var onlyIp = LegacyConfig.Parse("ip = 192.168.0.2");
        Assert.True(onlyIp.HasAnything);
        Assert.False(onlyIp.HasComboKeys);
        Assert.Equal(5, onlyIp.Combos.Count);   // the defaults are still offered
    }

    [Fact]
    public void AColourSlotNeedsAllThreeSwatchesAndKeepsThePickersOrder()
    {
        // Slot 0 is complete; slot 1 is missing its third swatch, as a file saved by an older
        // build of the old client can be. SavedColor1 is FRONT, SavedColor2 the MID colour the
        // tint word keeps, SavedColor3 the BACK one.
        var config = LegacyConfig.Parse("""
            SavedColor1No0 = -14647041
            SavedColor2No0 = -16744193
            SavedColor3No0 = -16752433
            SavedColor1No1 = -65536
            SavedColor2No1 = -65536
            """);

        var slot = Assert.Single(config.ColourSlots);
        Assert.Equal(0, slot.Slot);

        // Color.ToArgb() is signed, so an opaque colour is a negative decimal: -14647041 is
        // 0xFF2080FF, the picker's own default front colour.
        Assert.Equal(0x2080FFu, slot.Front);
        Assert.Equal(0x0060CFu, slot.Back);
        Assert.Equal(0x0080FFu, slot.Tint);

        Assert.True(config.HasAnything);
    }

    [Fact]
    public void EverySavedSlotIsOfferedLowestFirst()
    {
        var config = LegacyConfig.Parse("""
            SavedColor1No2 = 255
            SavedColor2No2 = 255
            SavedColor3No2 = 255
            SavedColor1No0 = 0x40FF0000
            SavedColor2No0 = 16711680
            SavedColor3No0 = notanumber
            SavedColor1No10 = 1
            SavedColor2No10 = 2
            SavedColor3No10 = 3
            """);

        // Slot 0 has an unparseable swatch, so it is not offered at all.
        Assert.Equal(new[] { 2, 10 }, config.ColourSlots.Select(s => s.Slot));
        Assert.Equal(0x0000FFu, config.ColourSlots[0].Front);
        Assert.Equal(3u, config.ColourSlots[1].Back);
        Assert.Equal(2u, config.ColourSlots[1].Tint);
    }

    [Fact]
    public void AHalfWrittenSlotIsNotSomethingToImport()
    {
        // The shared sample has one swatch of slot 0 and nothing else of it.
        Assert.Empty(LegacyConfig.Parse("SavedColor1No0 = -65536").ColourSlots);
        Assert.Empty(LegacyConfig.Parse(Sample).ColourSlots);
    }

    [Theory]
    [InlineData("-65536", true, 0xFF0000u)]     // Color.Red.ToArgb()
    [InlineData("-16777216", true, 0x000000u)]  // opaque black
    [InlineData("0", true, 0u)]
    [InlineData("0x8000FF80", true, 0x00FF80u)]
    [InlineData("", false, 0u)]
    [InlineData("red", false, 0u)]
    public void SavedColoursAreSignedArgbIntsWithTheAlphaDropped(string text, bool ok, uint expected)
    {
        Assert.Equal(ok, LegacyConfig.TryParseArgb(text, out uint rgb));
        Assert.Equal(expected, rgb);
    }

    [Theory]
    [InlineData("11", 11u, true)]
    [InlineData("0x600", 0x600u, true)]
    [InlineData("-1", 0u, false)]
    [InlineData("abc", 0u, false)]
    public void MasksParseAsDecimalOrHex(string text, uint expected, bool ok)
    {
        Assert.Equal(ok, LegacyConfig.TryParseMask(text, out uint mask));
        Assert.Equal(expected, mask);
    }
}
