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

/// <summary>
/// What a planet combo offers. qwark numbers a planet list by the game's own planet ids and fills
/// the gaps with parenthesised notes, so the client draws the real planets and remembers which id
/// each of them was.
/// </summary>
public class PlanetChoiceTests
{
    // UYA's list, whose id 0 is a placeholder because RAC3Form's combo box was one-based.
    private static string[] Uya() => new[] { "(none)", "Veldin", "Florana", "Starship Phoenix" };

    // Deadlocked's, which has filler in the middle as well as at the front.
    private static string[] Deadlocked() => new[]
    {
        "(unused)", "Dread Zone", "Catacrom", "(infinite loop)", "Sarathos",
    };

    [Theory]
    [InlineData("(none)", true)]
    [InlineData("(unused)", true)]
    [InlineData("(infinite loop)", true)]
    [InlineData("  (none)  ", true)]
    [InlineData("Veldin", false)]
    [InlineData("Aranos 2", false)]
    [InlineData("Obani Gemini (part 2)", false)]
    [InlineData("(", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void APlaceholderIsANameThatIsNothingButANoteInBrackets(string? name, bool placeholder)
    {
        Assert.Equal(placeholder, PlanetChoices.IsPlaceholder(name));
    }

    [Fact]
    public void ThePlaceholdersAreLeftOutAndTheIdsAroundThemAreNot()
    {
        var choices = PlanetChoices.For(Uya());

        Assert.Equal(new[] { "Veldin", "Florana", "Starship Phoenix" }, choices.Labels);
        Assert.Equal(new[] { 1, 2, 3 }, choices.Indices);
        Assert.Equal(3, choices.Count);

        // Picking the first entry is still planet 1, which is what PLANET_LOAD carries.
        Assert.Equal(1, choices.PlanetAt(0));
        Assert.Equal(3, choices.PlanetAt(2));
    }

    [Fact]
    public void FillerInTheMiddleOfAListGoesTheSameWay()
    {
        var choices = PlanetChoices.For(Deadlocked());

        Assert.Equal(new[] { "Dread Zone", "Catacrom", "Sarathos" }, choices.Labels);
        Assert.Equal(new[] { 1, 2, 4 }, choices.Indices);
        Assert.Equal(4, choices.PlanetAt(2));
    }

    [Fact]
    public void APlanetKnowsWhereItSitsInTheComboAndAHiddenOneDoesNot()
    {
        var choices = PlanetChoices.For(Deadlocked());

        Assert.Equal(0, choices.PositionOf(1));
        Assert.Equal(2, choices.PositionOf(4));

        // Ids 0 and 3 are the filler, and a selection left on one has nowhere to sit.
        Assert.Equal(-1, choices.PositionOf(0));
        Assert.Equal(-1, choices.PositionOf(3));
        Assert.Equal(-1, choices.PositionOf(99));
    }

    [Fact]
    public void AGameWithNoPlanetListHasNothingToOfferAndDoesNotThrow()
    {
        var choices = PlanetChoices.For(Array.Empty<string>());

        Assert.Empty(choices.Labels);
        Assert.Equal(0, choices.Count);
        Assert.Equal(0, choices.PlanetAt(0));
        Assert.Equal(-1, choices.PositionOf(0));
    }

    /// <summary>
    /// An empty combo would leave no way to pick a planet at all, so a list that is somehow nothing
    /// but placeholders is offered whole rather than hidden.
    /// </summary>
    [Fact]
    public void AListOfNothingButPlaceholdersIsStillAList()
    {
        var choices = PlanetChoices.For(new[] { "(none)", "(unused)" });

        Assert.Equal(new[] { "(none)", "(unused)" }, choices.Labels);
        Assert.Equal(new[] { 0, 1 }, choices.Indices);
    }

    [Fact]
    public void RaC1AndRaC2KeepEveryPlanetTheyHave()
    {
        var rac1 = PlanetChoices.For(new[] { "Veldin", "Novalis", "Aridia" });
        Assert.Equal(new[] { 0, 1, 2 }, rac1.Indices);
        Assert.Equal(3, rac1.Count);
    }
}

/// <summary>
/// What the quick block at the top of the Game page claims for itself, and what the sections below
/// it are left with. Nothing may be drawn twice: the block sends the console's own DIE opcode and
/// draws the two flagged savefile ACTIONs, so a game that describes any of those loses those rows
/// from its sections, and a section left with nothing at all stops appearing.
/// <para>
/// The game and title here are ones no layout entry is keyed by, so the sorting is qwark's own
/// DESCRIBE groups and nothing the shipped gamelayout.json says.
/// </para>
/// </summary>
public class GameQuickBlockTests
{
    private const string UnknownTitle = "ZZZZ99999";

    /// <summary>Group 0 is Cheats, 1 is Player and 2 is Savefile, as qwark names them.</summary>
    private static readonly string[] Groups = { "Cheats", "Player", "Savefile" };

    private static Feature Action(byte id, byte group, string label, FeatureFlags flags = FeatureFlags.None) =>
        new(id, FeatureKind.Action, group, 0, flags, 0xFF, 0, 0, label);

    private static Feature Toggle(byte id, byte group, string label) =>
        new(id, FeatureKind.Toggle, group, 0, FeatureFlags.None, 0xFF, 0, 0, label);

    private static DescribeResult Describe(params Feature[] features) =>
        new(GameId.None, Groups, Array.Empty<string>(), features);

    private static IReadOnlyDictionary<string, List<Feature>> Sections(DescribeResult describe) =>
        GamePanel.SectionsFor(UnknownTitle, GameId.None, describe);

    [Fact]
    public void TheSavefileActionsAndADescribedDieAreTheQuickBlocks()
    {
        var describe = Describe(
            Action(2, 1, "Die"),
            Action(6, 2, "Set aside file", FeatureFlags.SaveAside),
            Action(7, 2, "Load set-aside file", FeatureFlags.LoadAside),
            Toggle(0, 0, "Fast loads"));

        var claimed = GamePanel.QuickClaims(describe);

        Assert.Equal(new byte[] { 2, 6, 7 }, claimed.OrderBy(id => id));
    }

    [Fact]
    public void AGameWithNoHelperAndNoDieActionLeavesTheBlockNothingToClaim()
    {
        var describe = Describe(Toggle(0, 0, "Fast loads"), Action(1, 1, "Refill health"));

        Assert.Empty(GamePanel.QuickClaims(describe));

        // And the sections are exactly what the groups said, none of them emptied.
        var sections = Sections(describe);
        Assert.Equal(new[] { "Fast loads" }, sections["Cheats"].Select(f => f.Label));
        Assert.Equal(new[] { "Refill health" }, sections["Player"].Select(f => f.Label));
    }

    [Theory]
    [InlineData("Die", true)]
    [InlineData("die", true)]
    [InlineData("  Die  ", true)]
    [InlineData("Die instantly", false)]
    [InlineData("Suicide", false)]
    public void OnlyAnActionCalledDieIsTheOneTheBlockAlreadyHas(string label, bool claimed)
    {
        Assert.Equal(claimed, GamePanel.IsDieAction(Action(2, 1, label)));

        // A toggle of that name is a cheat, not the console's DIE, so it keeps its row.
        Assert.False(GamePanel.IsDieAction(Toggle(2, 1, label)));
    }

    [Fact]
    public void ASectionLeftWithNothingButTheBlocksRowsStopsAppearing()
    {
        // The Savefile group of a game whose helper is the whole of it.
        var describe = Describe(
            Toggle(0, 0, "Fast loads"),
            Action(6, 2, "Set aside file", FeatureFlags.SaveAside),
            Action(7, 2, "Load set-aside file", FeatureFlags.LoadAside));

        var sections = Sections(describe);

        Assert.False(sections.ContainsKey("Savefile"));
        Assert.Equal(new[] { "Fast loads" }, sections["Cheats"].Select(f => f.Label));
    }

    [Fact]
    public void ASectionWithSomethingElseInItKeepsThatAndLosesOnlyTheBlocksRows()
    {
        var describe = Describe(
            Action(6, 2, "Set aside file", FeatureFlags.SaveAside),
            Action(7, 2, "Load set-aside file", FeatureFlags.LoadAside),
            Action(8, 2, "Refresh trophy state"),
            Action(2, 1, "Die"),
            Toggle(3, 1, "Invincible"));

        var sections = Sections(describe);

        Assert.Equal(new[] { "Refresh trophy state" }, sections["Savefile"].Select(f => f.Label));
        Assert.Equal(new[] { "Invincible" }, sections["Player"].Select(f => f.Label));
    }

    /// <summary>
    /// A VALUE still defaults to the top table and the block claims none of them, so the "Values"
    /// header has what it always had.
    /// </summary>
    [Fact]
    public void TheValueTableIsUntouchedByTheBlock()
    {
        var bolts = new Feature(3, FeatureKind.Value, 1, 0, FeatureFlags.None, 0, 0, 99999, "Bolts");
        var describe = Describe(bolts, Action(2, 1, "Die"));

        var sections = Sections(describe);

        Assert.Equal(new[] { "Bolts" }, sections[GameLayout.ValuesSection].Select(f => f.Label));
        Assert.False(sections.ContainsKey("Player"));
    }
}

/// <summary>
/// The slot dropdown the quick block draws beside the save and load buttons: the console's own
/// slots, and what it says before the console has listed any.
/// </summary>
public class SlotPickerTests
{
    private static PositionList Slots(params bool[] filled)
    {
        var slots = new PositionSlot[filled.Length];
        for (int i = 0; i < filled.Length; i++) slots[i] = new PositionSlot((byte)i, filled[i], 1f, 2f, 3f);
        return new PositionList(2, slots);
    }

    [Fact]
    public void EverySlotTheConsoleListedIsAnEntryAndTheFullOnesSaySo()
    {
        Assert.Equal(
            new[] { "Slot 0 (saved)", "Slot 1", "Slot 2 (saved)" },
            PositionsPanel.SlotLabels(Slots(true, false, true), selected: 1));
    }

    [Fact]
    public void SlotsThatHaveNotBeenReadYetStillNameTheSelectedOne()
    {
        // Outside INGAME there is no POS_LIST to draw, and a blank box would say less than this.
        Assert.Equal(new[] { "Slot 3" }, PositionsPanel.SlotLabels(PositionList.Empty, selected: 3));
    }
}

/// <summary>Which boxes the Positions panel offers beside "Load planet", and for which games.</summary>
public class PlanetResetOptionTests
{
    [Fact]
    public void RaC1HasSpecialBoltsAndNoLevelFlags()
    {
        // The console is what says so, and for RaC1 it answers LEVELFLAGS_GET UNSUPPORTED.
        var boxes = PlanetResetOptions.For(GameId.Rac1, levelFlagsUnsupported: true);

        Assert.False(boxes.LevelFlags);
        Assert.True(boxes.SpecialBolts);
    }

    [Fact]
    public void RaC2AndUyaHaveBoth()
    {
        foreach (var game in new[] { GameId.Rac2, GameId.Rac3 })
        {
            var boxes = PlanetResetOptions.For(game, levelFlagsUnsupported: false);
            Assert.True(boxes.LevelFlags);
            Assert.True(boxes.SpecialBolts);
        }
    }

    [Fact]
    public void DeadlockedHasNeither()
    {
        var boxes = PlanetResetOptions.For(GameId.Rac4, levelFlagsUnsupported: true);

        Assert.False(boxes.LevelFlags);
        Assert.False(boxes.SpecialBolts);
    }

    [Fact]
    public void AConsoleThatRefusesLevelFlagsTakesTheBoxAwayWhateverTheGame()
    {
        var boxes = PlanetResetOptions.For(GameId.Rac2, levelFlagsUnsupported: true);

        Assert.False(boxes.LevelFlags);
        Assert.True(boxes.SpecialBolts);
    }

    [Fact]
    public void NoGameMeansNoBoxes()
    {
        var boxes = PlanetResetOptions.For(GameId.None, levelFlagsUnsupported: false);

        Assert.False(boxes.LevelFlags);
        Assert.False(boxes.SpecialBolts);
    }
}

/// <summary>
/// The two decimals the Positions panel draws, and the width that stops a row shuffling sideways
/// while the player moves.
/// </summary>
public class PositionFormatTests
{
    [Fact]
    public void ACoordinateIsTwoDecimalsInAFixedWidth()
    {
        Assert.Equal("     1.23", PositionsPanel.Coordinate(1.2345f));
        Assert.Equal(" -1234.57", PositionsPanel.Coordinate(-1234.567f));
        Assert.Equal("     0.00", PositionsPanel.Coordinate(0f));
    }

    [Fact]
    public void AnEmptySlotIsTheSameWidthAsAFullOne()
    {
        Assert.Equal(PositionsPanel.CoordinateWidth, PositionsPanel.Coordinate(null).Length);
        Assert.EndsWith("-", PositionsPanel.Coordinate(null));
    }

    [Fact]
    public void EveryCoordinateAPlanetHoldsIsTheSameLength()
    {
        foreach (float value in new[] { 0f, 1f, -1f, 9.999f, -99.995f, 1234.5f, -1234.56f })
        {
            Assert.Equal(PositionsPanel.CoordinateWidth, PositionsPanel.Coordinate(value).Length);
        }
    }

    [Fact]
    public void ANumberTooBigForTheWidthIsShownWholeRatherThanCut()
    {
        // Padding never truncates: an impossible coordinate is still readable, it just pushes.
        Assert.Equal("1234567.00", PositionsPanel.Coordinate(1234567f));
    }
}

/// <summary>
/// The mods table's name column: two lines at most, so a long name is readable without the row
/// turning into a paragraph. The measure stands in for ImGui.CalcTextSize at one unit per
/// character, which makes the widths below character counts.
/// </summary>
public class ModNameWrapTests
{
    private static float Measure(string text) => text.Length;

    private static int Lines(string text) => text.Split('\n').Length;

    [Fact]
    public void ANameThatFitsIsLeftAlone()
    {
        Assert.Equal("Flight", ModsPanel.WrapName("Flight", 20f, Measure));
        Assert.Equal("Flight", ModsPanel.WrapName("  Flight  ", 20f, Measure));
    }

    [Fact]
    public void ALongerNameTakesASecondLineAtTheSpace()
    {
        Assert.Equal("Incremental\nRNG", ModsPanel.WrapName("Incremental RNG", 12f, Measure));
    }

    [Fact]
    public void TheSecondLineIsTheLastOneAndEndsInAnEllipsis()
    {
        string wrapped = ModsPanel.WrapName("Aaaa Bbbb Cccc Dddd Eeee", 10f, Measure);

        Assert.Equal(2, Lines(wrapped));
        Assert.Equal("Aaaa Bbbb\nCccc Dd...", wrapped);
    }

    [Fact]
    public void NoNameEverTakesAThirdLine()
    {
        foreach (string name in new[]
                 {
                     "One two three four five six seven eight nine ten eleven twelve",
                     "Supercalifragilisticexpialidocious and then some more of it",
                     "a b c d e f g h i j k l m n o p q r s t u v w x y z",
                 })
        {
            foreach (float width in new[] { 6f, 10f, 25f, 40f })
            {
                Assert.True(Lines(ModsPanel.WrapName(name, width, Measure)) <= 2);
            }
        }
    }

    [Fact]
    public void AWordWithNoSpaceInItIsBrokenWhereverItRunsOut()
    {
        Assert.Equal("Supercalif\nragilistic", ModsPanel.WrapName("Supercalifragilistic", 10f, Measure));
    }

    [Fact]
    public void ANameWithNothingInItDrawsNothing()
    {
        Assert.Equal(string.Empty, ModsPanel.WrapName(null, 10f, Measure));
        Assert.Equal(string.Empty, ModsPanel.WrapName("   ", 10f, Measure));
    }

    /// <summary>
    /// A column with no room in it happens on the frame a window is dragged shut; the name is
    /// handed back whole rather than measured into nothing.
    /// </summary>
    [Fact]
    public void AColumnWithNoWidthIsNotWorthWrappingInto()
    {
        Assert.Equal("Incremental RNG", ModsPanel.WrapName("Incremental RNG", 0f, Measure));
        Assert.Equal("Incremental RNG", ModsPanel.WrapName("Incremental RNG", -5f, Measure));
    }
}

/// <summary>What the Memory panel's patch table calls each row.</summary>
public class PatchNameTests
{
    private static PatchEntry Patch(PatchKind kind, string name) => new(0x1B0000, 2, kind, name);

    [Fact]
    public void AModsPatchIsNamedAfterTheMod()
    {
        Assert.Equal("Flight", MemoryPanel.PatchName(Patch(PatchKind.Mod, "Flight")));

        // The name arrives in a fixed 32-byte field, so what is left of it is trimmed.
        Assert.Equal("Flight", MemoryPanel.PatchName(Patch(PatchKind.Mod, "  Flight  ")));
    }

    [Fact]
    public void AFeaturesPatchIsNamedAfterTheToggle()
    {
        Assert.Equal("Fast loads", MemoryPanel.PatchName(Patch(PatchKind.Feature, "Fast loads")));
    }

    [Fact]
    public void ARowWithNoNameSaysWhatKindOfThingItIs()
    {
        Assert.Equal("(a mod)", MemoryPanel.PatchName(Patch(PatchKind.Mod, string.Empty)));
        Assert.Equal("(a feature)", MemoryPanel.PatchName(Patch(PatchKind.Feature, "   ")));
        Assert.Equal("(this client)", MemoryPanel.PatchName(Patch(PatchKind.Client, string.Empty)));
    }
}

/// <summary>
/// How wide the Unlocks table's columns are. The game says how many value columns there are, so
/// the widths cannot be written down: the name keeps half the table however many of them arrive.
/// </summary>
public class UnlockColumnTests
{
    [Fact]
    public void TheNameKeepsHalfTheTableAndTheValuesShareTheRest()
    {
        // UYA: Owned, Level, XP and Ammo, which used to leave the names nothing.
        var weights = UnlocksPanel.ColumnWeights(4);

        Assert.Equal(5, weights.Length);
        Assert.Equal(0.5f, weights[0]);
        Assert.Equal(0.125f, weights[1]);
        Assert.Equal(0.125f, weights[4]);
    }

    [Fact]
    public void OneValueColumnTakesTheOtherHalfOnItsOwn()
    {
        Assert.Equal(new[] { 0.5f, 0.5f }, UnlocksPanel.ColumnWeights(1));
    }

    /// <summary>A category whose entries declare nothing but their names is one column wide.</summary>
    [Fact]
    public void WithNoValueColumnsTheNameTakesTheWholeTable()
    {
        Assert.Equal(new[] { 1f }, UnlocksPanel.ColumnWeights(0));
        Assert.Equal(new[] { 1f }, UnlocksPanel.ColumnWeights(-1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    public void TheWeightsAlwaysAddUpToTheWholeTable(int columns)
    {
        Assert.Equal(1f, UnlocksPanel.ColumnWeights(columns).Sum(), 5);
    }
}
