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
