using RaCMAN.App;
using RaCMAN.App.Panels;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The pieces of panel behaviour that are not ImGui: the watchlist files behind the Memory
/// panel's saved-list dropdown, and the path the Mods panel hands to the ZIP install.
/// </summary>
public class PanelDataTests : IDisposable
{
    private const string TitleId = "NPEA00386";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "racman-watchlists-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static List<SavedWatch> OneWatch(uint address = 0x300000) =>
        new() { new SavedWatch { Address = address, Size = 4, Name = "bolts", Format = "dec" } };

    // ---------------------------------------------------------------- watchlist file names

    [Fact]
    public void TheTitlesOwnFileIsTheDefaultListAndHasNoName()
    {
        Assert.True(WatchlistStore.TryGetName(TitleId, $"{TitleId}.json", out var name));
        Assert.Null(name);
    }

    [Fact]
    public void ANamedFileGivesBackTheNameBetweenTheTitleAndTheExtension()
    {
        Assert.True(WatchlistStore.TryGetName(TitleId, $"{TitleId}.speedrun.json", out var name));
        Assert.Equal("speedrun", name);

        // A name may hold dots of its own; only the first separator belongs to the title.
        Assert.True(WatchlistStore.TryGetName(TitleId, $"{TitleId}.rac2.boss.json", out var dotted));
        Assert.Equal("rac2.boss", dotted);
    }

    [Fact]
    public void AFileBelongingToAnotherTitleIsNotOneOfThisTitlesLists()
    {
        // ListFor matches on a prefix, so these can turn up in its listing.
        Assert.False(WatchlistStore.TryGetName(TitleId, $"{TitleId}X.json", out _));
        Assert.False(WatchlistStore.TryGetName(TitleId, "NPEA00385.json", out _));
        Assert.False(WatchlistStore.TryGetName(TitleId, $"{TitleId}.json.bak", out _));
        Assert.False(WatchlistStore.TryGetName(TitleId, $"{TitleId}..json", out _));
        Assert.False(WatchlistStore.TryGetName(TitleId, string.Empty, out _));
        Assert.False(WatchlistStore.TryGetName(string.Empty, $"{TitleId}.json", out _));
    }

    [Fact]
    public void AFullPathIsAcceptedAsWellAsABareFileName()
    {
        Assert.True(WatchlistStore.TryGetName(TitleId, Path.Combine(@"C:\watchlists", $"{TitleId}.speedrun.json"), out var name));
        Assert.Equal("speedrun", name);
    }

    [Fact]
    public void EverySavedListComesBackFromTheListingWithItsName()
    {
        var store = new WatchlistStore(_folder);
        store.Save(TitleId, OneWatch());
        store.Save(TitleId, OneWatch(0x300010), "speedrun");
        store.Save("NPEA00385", OneWatch(0x300020));

        var names = new List<string?>();
        foreach (var file in store.ListFor(TitleId))
        {
            if (WatchlistStore.TryGetName(TitleId, file, out var name)) names.Add(name);
        }

        Assert.Equal(2, names.Count);
        Assert.Contains(null, names);
        Assert.Contains("speedrun", names);
    }

    // ---------------------------------------------------------------- delete

    [Fact]
    public void DeleteRemovesOneListAndLeavesTheOthers()
    {
        var store = new WatchlistStore(_folder);
        store.Save(TitleId, OneWatch());
        store.Save(TitleId, OneWatch(0x300010), "speedrun");

        Assert.True(store.Delete(TitleId, "speedrun"));

        Assert.False(File.Exists(store.FileFor(TitleId, "speedrun")));
        Assert.True(File.Exists(store.FileFor(TitleId)));
        Assert.Single(store.ListFor(TitleId));

        Assert.True(store.Delete(TitleId));
        Assert.Empty(store.ListFor(TitleId));
    }

    [Fact]
    public void DeletingAListThatIsNotThereSaysSoRatherThanThrowing()
    {
        var store = new WatchlistStore(_folder);

        Assert.False(store.Delete(TitleId));
        Assert.False(store.Delete(TitleId, "never-saved"));
    }

    [Fact]
    public void ADeletedListIsGoneFromTheLoadAsWell()
    {
        var store = new WatchlistStore(_folder);
        store.Save(TitleId, OneWatch(), "speedrun");
        Assert.Single(store.Load(TitleId, "speedrun"));

        store.Delete(TitleId, "speedrun");

        Assert.Empty(store.Load(TitleId, "speedrun"));
    }

    // ---------------------------------------------------------------- ZIP path

    [Fact]
    public void AnEmptyPathBoxNormalisesToNothingAtAll()
    {
        Assert.Equal(string.Empty, ModsPanel.NormalizeZipPath(string.Empty));
        Assert.Equal(string.Empty, ModsPanel.NormalizeZipPath("   "));
        Assert.Equal(string.Empty, ModsPanel.NormalizeZipPath("\"\""));
        Assert.Equal(string.Empty, ModsPanel.NormalizeZipPath(null));
    }

    [Fact]
    public void TheQuotesExplorersCopyAsPathAddsAreNotPartOfThePath()
    {
        Assert.Equal(@"C:\downloads\mod.zip", ModsPanel.NormalizeZipPath("\"C:\\downloads\\mod.zip\""));
        Assert.Equal(@"C:\downloads\mod.zip", ModsPanel.NormalizeZipPath("  \"C:\\downloads\\mod.zip\"  "));
        Assert.Equal(@"C:\downloads\mod.zip", ModsPanel.NormalizeZipPath(@"C:\downloads\mod.zip"));
    }

    [Fact]
    public void AnEmptyPathIsWhyTheInstallHasToBeGuarded()
    {
        var library = new ModLibrary(_folder);

        // Not IOException, not InvalidDataException: the install used to let this one through and
        // it went all the way out of the render loop.
        Assert.ThrowsAny<ArgumentException>(() => library.OpenZip(string.Empty, TitleId));
        Assert.Equal(string.Empty, ModsPanel.NormalizeZipPath(string.Empty));
    }

    // ---------------------------------------------------------------- the mods table

    private static LocalMod Mod(string name, string author, bool shipped = false) => new()
    {
        Directory = Path.Combine("mods", name),
        DirName = name.ToLowerInvariant(),
        Name = name,
        Author = author,
        Shipped = shipped,
    };

    [Fact]
    public void TheAuthorLeftTheTableForTheNamesTooltip()
    {
        Assert.Equal("Incremental RNG\nby Someone\nIn your mods folder",
            ModsPanel.TooltipFor(Mod("Incremental RNG", "Someone")));
    }

    [Fact]
    public void AModWithNoAuthorIsJustItsNameAndItsLibraryOnHover()
    {
        Assert.Equal("Incremental RNG\nIn your mods folder", ModsPanel.TooltipFor(Mod("Incremental RNG", string.Empty)));
        Assert.Equal("Incremental RNG\nIn your mods folder", ModsPanel.TooltipFor(Mod("Incremental RNG", "   ")));
    }

    /// <summary>
    /// Which library a mod came out of is worth a line, because a shipped one is replaced whole the
    /// next time the client updates itself and is therefore not somewhere to keep an edit.
    /// </summary>
    [Fact]
    public void TheTooltipSaysWhichLibraryTheModCameFrom()
    {
        Assert.EndsWith("\nShips with RaCMAN Reloaded", ModsPanel.TooltipFor(Mod("Flight", "Someone", shipped: true)));
    }

    // ---------------------------------------------------------------- file dialog

    [Fact]
    public void TheBrowseButtonHasADialogToOpenOnThisMachine()
    {
        // Windows always has comdlg32; a Linux CI box may have neither zenity nor kdialog, and
        // then the panel keeps the paste-a-path box as the way in.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) Assert.True(FileDialog.IsSupported);

        // Asking twice must not change the answer: the panel asks once a frame.
        Assert.Equal(FileDialog.IsSupported, FileDialog.IsSupported);
    }
}

/// <summary>
/// The colour preset files behind the Game panel's "Presets" row: one file per game, names unique
/// however they are capitalised, and nothing on disk that can stop the row from drawing.
/// </summary>
public class ColourPresetStoreTests : IDisposable
{
    private const string Front = ColourPresetStore.ChargebootFront;
    private const string Back = ColourPresetStore.ChargebootBack;
    private const string Tint = ColourPresetStore.ChargebootTint;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "racman-colours-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static KeyValuePair<string, uint>[] Chargeboots(uint front, uint back, uint tint) => new[]
    {
        new KeyValuePair<string, uint>(Front, front),
        new KeyValuePair<string, uint>(Back, back),
        new KeyValuePair<string, uint>(Tint, tint),
    };

    [Fact]
    public void APresetComesBackWithTheColoursItWasSavedWith()
    {
        var store = new ColourPresetStore(_folder);
        store.Save(GameId.Rac2, "neon", Chargeboots(0x2080FF, 0x0060CF, 0x00FFFF));

        var preset = Assert.Single(store.List(GameId.Rac2));
        Assert.Equal("neon", preset.Name);

        Assert.True(preset.TryGetColour(Front, out uint front));
        Assert.True(preset.TryGetColour(Back, out uint back));
        Assert.True(preset.TryGetColour(Tint, out uint tint));
        Assert.Equal(0x2080FFu, front);
        Assert.Equal(0x0060CFu, back);
        Assert.Equal(0x00FFFFu, tint);

        // Hex on disk, so the file is worth opening in an editor.
        Assert.Contains("2080FF", File.ReadAllText(store.FileFor(GameId.Rac2)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PresetsAreKeyedByGameSoOneGamesFileIsNotAnothers()
    {
        var store = new ColourPresetStore(_folder);
        store.Save(GameId.Rac2, "neon", Chargeboots(1, 2, 3));

        Assert.Equal("rac2.json", Path.GetFileName(store.FileFor(GameId.Rac2)));
        Assert.Empty(store.List(GameId.Rac3));
        Assert.Single(store.List(GameId.Rac2));
    }

    [Fact]
    public void SavingAgainUnderTheSameNameReplacesItWhateverTheCase()
    {
        var store = new ColourPresetStore(_folder);
        store.Save(GameId.Rac3, "Neon", Chargeboots(0x111111, 0x222222, 0x333333));
        store.Save(GameId.Rac3, "  neon  ", Chargeboots(0xAABBCC, 0, 0));

        var preset = Assert.Single(store.List(GameId.Rac3));
        Assert.Equal("neon", preset.Name);
        Assert.True(preset.TryGetColour(Front, out uint front));
        Assert.Equal(0xAABBCCu, front);

        // The lookup is case-insensitive too, so a hand-typed label still matches.
        Assert.True(preset.TryGetColour("chargeboots primary front", out uint again));
        Assert.Equal(0xAABBCCu, again);
    }

    [Fact]
    public void ASecondNameIsASecondPresetAndTheListIsAlphabetical()
    {
        var store = new ColourPresetStore(_folder);
        store.Save(GameId.Rac2, "zebra", Chargeboots(1, 2, 3));
        store.Save(GameId.Rac2, "apple", Chargeboots(4, 5, 6));

        Assert.Equal(new[] { "apple", "zebra" }, store.List(GameId.Rac2).Select(p => p.Name));
        Assert.NotNull(store.Load(GameId.Rac2, "APPLE"));
        Assert.Null(store.Load(GameId.Rac2, "pear"));
    }

    [Fact]
    public void DeleteDropsOnePresetAndSaysSoWhenThereWasNoneToDrop()
    {
        var store = new ColourPresetStore(_folder);
        store.Save(GameId.Rac2, "keep", Chargeboots(1, 2, 3));
        store.Save(GameId.Rac2, "drop", Chargeboots(4, 5, 6));

        Assert.True(store.Delete(GameId.Rac2, "DROP"));
        Assert.Equal(new[] { "keep" }, store.List(GameId.Rac2).Select(p => p.Name));

        Assert.False(store.Delete(GameId.Rac2, "drop"));
        Assert.False(store.Delete(GameId.Rac4, "keep"));
    }

    [Fact]
    public void AMissingFolderOrFileIsSimplyNoPresets()
    {
        var store = new ColourPresetStore(Path.Combine(_folder, "not-there"));

        Assert.Empty(store.List(GameId.Rac1, out string? problem));
        Assert.Null(problem);
        Assert.Null(store.Load(GameId.Rac1, "neon"));
    }

    [Fact]
    public void ABrokenFileReadsAsEmptyAndSaysWhyInsteadOfThrowing()
    {
        Directory.CreateDirectory(_folder);
        var store = new ColourPresetStore(_folder);
        File.WriteAllText(store.FileFor(GameId.Rac2), "{ this is not the list it should be");

        Assert.Empty(store.List(GameId.Rac2, out string? problem));
        Assert.NotNull(problem);

        // And the row can still save over it.
        store.Save(GameId.Rac2, "neon", Chargeboots(1, 2, 3));
        Assert.Single(store.List(GameId.Rac2));
    }

    [Fact]
    public void APresetNeedsAName()
    {
        var store = new ColourPresetStore(_folder);
        Assert.ThrowsAny<ArgumentException>(() => store.Save(GameId.Rac2, "   ", Chargeboots(1, 2, 3)));
    }

    [Theory]
    [InlineData("2080FF", true, 0x2080FFu)]
    [InlineData("#2080ff", true, 0x2080FFu)]
    [InlineData("0x2080FF", true, 0x2080FFu)]
    [InlineData("F", true, 0xFu)]
    [InlineData("", false, 0u)]
    [InlineData("nothex", false, 0u)]
    [InlineData("2080FF00", false, 0u)]
    public void ColoursOnDiskAreSixHexDigits(string text, bool ok, uint expected)
    {
        Assert.Equal(ok, ColourPresetStore.TryParseColour(text, out uint rgb));
        Assert.Equal(expected, rgb);
        if (ok) Assert.Equal(6, ColourPresetStore.FormatColour(rgb).Length);
    }
}

/// <summary>
/// The Combos panel's capture, fed the way telemetry feeds it: one pad mask per frame at 30 Hz,
/// which is slow enough that two buttons pressed together are rarely released together.
/// </summary>
public class ComboCaptureTests
{
    private const uint Cross = (uint)PadButton.Cross;
    private const uint Square = (uint)PadButton.Square;
    private const uint L1 = (uint)PadButton.L1;

    /// <summary>Feeds a run of frames and gives back what the capture committed, or null.</summary>
    private static uint? Press(ComboCapture capture, params uint[] frames)
    {
        uint? committed = null;
        foreach (uint frame in frames)
        {
            uint? answer = capture.Feed(frame);
            if (answer is not null) committed = answer;
        }

        return committed;
    }

    [Fact]
    public void TheFullestMaskOfThePressIsWhatIsStored()
    {
        // Cross first, Square added, then Cross let go of a frame before Square: the old capture
        // kept the last non-zero mask and stored Square on its own.
        var capture = new ComboCapture();
        Assert.Equal(Cross | Square, Press(capture, Cross, Cross | Square, Square, 0));
    }

    [Fact]
    public void OneButtonIsStillOneButton()
    {
        var capture = new ComboCapture();
        Assert.Equal(Cross, Press(capture, Cross, 0));
    }

    [Fact]
    public void NothingIsCommittedUntilThePadIsEmpty()
    {
        var capture = new ComboCapture();

        Assert.Null(capture.Feed(Cross));
        Assert.Null(capture.Feed(Cross | Square));
        Assert.Null(capture.Feed(Square));

        // And the panel can say what it has so far while the buttons are still down.
        Assert.Equal(Cross | Square, capture.Captured);

        Assert.Equal(Cross | Square, capture.Feed(0));
    }

    [Fact]
    public void ThePadSittingAtZeroCommitsNothingAtAll()
    {
        var capture = new ComboCapture();
        Assert.Null(capture.Feed(0));
        Assert.Null(capture.Feed(0));
        Assert.Equal(0u, capture.Captured);
    }

    [Fact]
    public void ASecondCaptureStartsFromNothing()
    {
        var capture = new ComboCapture();
        Assert.Equal(Cross | Square, Press(capture, Cross, Cross | Square, 0));
        Assert.Equal(0u, capture.Captured);

        Assert.Equal(L1, Press(capture, L1, 0));
    }

    [Fact]
    public void AnEquallyFullMaskLaterInThePressWins()
    {
        // Sliding off Cross onto Square without a frame in between: two masks of one button each,
        // and the one the user ended on is the one they meant.
        var capture = new ComboCapture();
        Assert.Equal(Square, Press(capture, Cross, Square, 0));
    }

    [Fact]
    public void CancellingThrowsAwayWhatWasHeld()
    {
        var capture = new ComboCapture();
        Assert.Null(capture.Feed(Cross | Square));

        capture.Reset();

        Assert.Equal(0u, capture.Captured);
        Assert.Null(capture.Feed(0));
    }
}

/// <summary>The side nav's rule for the panels an RPCS3 session cannot drive.</summary>
public class PanelNavTests
{
    [Fact]
    public void ModsAndSaveFilesAreGreyedOutWhenTheConsoleRefusesCodePatches()
    {
        Assert.Equal(Ui.ModsAreCodePatches, PanelNav.DisabledReason(PanelNav.Mods, true));

        // The savefile helper is a mod, so the panel that drives it goes the same way.
        Assert.Equal(Ui.NoCodePatches, PanelNav.DisabledReason(PanelNav.SaveFiles, true));
    }

    [Fact]
    public void OnAConsoleThatPatchesCodeNothingIsGreyedOut()
    {
        Assert.Null(PanelNav.DisabledReason(PanelNav.Mods, false));
        Assert.Null(PanelNav.DisabledReason(PanelNav.SaveFiles, false));
    }

    [Fact]
    public void EveryOtherPanelIsLeftAlone()
    {
        for (int panel = 0; panel < 12; panel++)
        {
            if (panel == PanelNav.Mods || panel == PanelNav.SaveFiles) continue;
            Assert.Null(PanelNav.DisabledReason(panel, true));
        }
    }
}

/// <summary>
/// How big the input display draws the pad in the panel: the skin's own size, or as much of it as
/// the room allows. There is no scale to set any more, so this rule is the whole of it.
/// </summary>
public class InputDisplayFitTests
{
    // About the size of a real skin sheet.
    private const float Width = 800f;
    private const float Height = 730f;

    [Fact]
    public void APanelWithRoomToSpareDrawsTheSkinAtItsOwnSize()
    {
        Assert.Equal(1f, InputDisplayPanel.FitScale(Width, Height, 1200f, 1000f));
    }

    [Fact]
    public void TheTighterOfTheTwoAxesDecidesTheScale()
    {
        // Half the width it wants, all the height: the pad is drawn at half size.
        Assert.Equal(0.5f, InputDisplayPanel.FitScale(Width, Height, 400f, 1000f), 4);

        Assert.Equal(0.5f, InputDisplayPanel.FitScale(Width, Height, 1200f, 365f), 4);
    }

    [Fact]
    public void ASizeThatMakesNoSenseDrawsAtOneToOneRatherThanVanishing()
    {
        Assert.Equal(1f, InputDisplayPanel.FitScale(0f, Height, 1200f, 1000f));
        Assert.Equal(1f, InputDisplayPanel.FitScale(Width, 0f, 1200f, 1000f));

        // A panel scrolled down to nothing, or one narrower than its own scrollbar.
        Assert.Equal(1f, InputDisplayPanel.FitScale(Width, Height, -10f, 1000f));
        Assert.Equal(1f, InputDisplayPanel.FitScale(Width, Height, 1200f, 0f));
    }
}
