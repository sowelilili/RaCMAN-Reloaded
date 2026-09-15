using System.Buffers.Binary;
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
        for (int panel = 0; panel < PanelNav.Count; panel++)
        {
            if (panel == PanelNav.Mods || panel == PanelNav.SaveFiles) continue;
            Assert.Null(PanelNav.DisabledReason(panel, true));
        }
    }

    /// <summary>
    /// The order the side nav draws, which is also the order --panel counts in. The constants are
    /// what the window and the tests index by, so they have to name the right rows.
    /// </summary>
    [Fact]
    public void TheNavIsInTheOrderTheNamesAreListedIn()
    {
        Assert.Equal(
            new[]
            {
                "Connection", "Game", "Unlocks", "Positions", "Combos", "Mods",
                "Save files", "Autosplitter", "Input display", "Level flags", "Memory", "Settings",
            },
            PanelNav.Names);

        Assert.Equal("Connection", PanelNav.Names[PanelNav.Connection]);
        Assert.Equal("Game", PanelNav.Names[PanelNav.Game]);
        Assert.Equal("Unlocks", PanelNav.Names[PanelNav.Unlocks]);
        Assert.Equal("Positions", PanelNav.Names[PanelNav.Positions]);
        Assert.Equal("Combos", PanelNav.Names[PanelNav.Combos]);
        Assert.Equal("Mods", PanelNav.Names[PanelNav.Mods]);
        Assert.Equal("Save files", PanelNav.Names[PanelNav.SaveFiles]);
        Assert.Equal("Autosplitter", PanelNav.Names[PanelNav.Autosplitter]);
        Assert.Equal("Input display", PanelNav.Names[PanelNav.InputDisplay]);
        Assert.Equal("Level flags", PanelNav.Names[PanelNav.LevelFlags]);
        Assert.Equal("Memory", PanelNav.Names[PanelNav.Memory]);
        Assert.Equal("Settings", PanelNav.Names[PanelNav.Settings]);

        // The numbers --panel counts, spelled out once so a regroup cannot move one quietly.
        Assert.Equal(0, PanelNav.Connection);
        Assert.Equal(1, PanelNav.Game);
        Assert.Equal(2, PanelNav.Unlocks);
        Assert.Equal(3, PanelNav.Positions);
        Assert.Equal(4, PanelNav.Combos);
        Assert.Equal(5, PanelNav.Mods);
        Assert.Equal(6, PanelNav.SaveFiles);
        Assert.Equal(7, PanelNav.Autosplitter);
        Assert.Equal(8, PanelNav.InputDisplay);
        Assert.Equal(9, PanelNav.LevelFlags);
        Assert.Equal(10, PanelNav.Memory);
        Assert.Equal(11, PanelNav.Settings);
        Assert.Equal(12, PanelNav.Count);
    }

    [Fact]
    public void ASeparatorOpensEveryGroupButTheFirst()
    {
        // Connection alone, then the game and its unlocks, the two a run fills in as it goes, the
        // two libraries, the two that drive something else on this PC, the two raw-state panels,
        // and the settings on their own.
        Assert.False(PanelNav.StartsGroup(PanelNav.Connection));
        Assert.True(PanelNav.StartsGroup(PanelNav.Game));
        Assert.True(PanelNav.StartsGroup(PanelNav.Positions));
        Assert.True(PanelNav.StartsGroup(PanelNav.Mods));
        Assert.True(PanelNav.StartsGroup(PanelNav.Autosplitter));
        Assert.True(PanelNav.StartsGroup(PanelNav.LevelFlags));
        Assert.True(PanelNav.StartsGroup(PanelNav.Settings));

        foreach (int inside in new[]
                 {
                     PanelNav.Unlocks, PanelNav.Combos, PanelNav.SaveFiles,
                     PanelNav.InputDisplay, PanelNav.Memory,
                 })
        {
            Assert.False(PanelNav.StartsGroup(inside));
        }
    }

    [Fact]
    public void TheHelpNumbersEveryPanelInNavOrder()
    {
        string help = string.Join(" ", PanelNav.HelpLines());

        for (int panel = 0; panel < PanelNav.Count; panel++)
        {
            Assert.Contains($"{panel} {PanelNav.Names[panel].ToLowerInvariant()}", help);
        }

        // The lines are joined with commas, so only the last one ends without one.
        var lines = PanelNav.HelpLines();
        for (int i = 0; i < lines.Count - 1; i++) Assert.EndsWith(",", lines[i]);
        Assert.DoesNotContain(",", lines[^1][^1].ToString());
    }
}

/// <summary>The session state as the client spells it: upper case, the way the panels do.</summary>
public class SessionStateNameTests
{
    [Fact]
    public void EveryStateIsSpeltInUpperCase()
    {
        Assert.Equal("INGAME", SessionState.Ingame.DisplayName());
        Assert.Equal("XMB", SessionState.Xmb.DisplayName());
        Assert.Equal("BOOTING", SessionState.Booting.DisplayName());
        Assert.Equal("QUITTING", SessionState.Quitting.DisplayName());
    }

    [Fact]
    public void AStateThisClientDoesNotKnowStillReadsAsAState()
    {
        // A newer module could name a fifth state; the status line still shows it the same way.
        Assert.Equal("4", ((SessionState)4).DisplayName());
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
        "(unused)", "DreadZone", "Catacrom", "(infinite loop)", "Sarathos",
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

        Assert.Equal(new[] { "DreadZone", "Catacrom", "Sarathos" }, choices.Labels);
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

/// <summary>
/// The Value box in the Memory panel's watches table: what it takes back in each of the formats it
/// shows, and the bytes that go on the wire. The panel owns the box; this is the arithmetic behind
/// it, which is the part that can be tested without a window.
/// </summary>
public class WatchValueCodecTests
{
    private static ulong Parse(string text, byte size, string format)
    {
        Assert.True(WatchValueCodec.TryParse(text, size, format, out ulong value), $"'{text}' should parse as {format}/{size}");
        return value;
    }

    private static void Rejects(string text, byte size, string format)
    {
        Assert.False(WatchValueCodec.TryParse(text, size, format, out ulong value), $"'{text}' should not parse as {format}/{size}");
        Assert.Equal(0ul, value);
    }

    // ---------------------------------------------------------------- hex

    [Fact]
    public void HexIsReadWithOrWithoutThePrefixTheBoxShows()
    {
        Assert.Equal(0xDEADBEEFul, Parse("0xDEADBEEF", 4, "hex"));
        Assert.Equal(0xDEADBEEFul, Parse("deadbeef", 4, "hex"));
        Assert.Equal(0xFFul, Parse("0xFF", 1, "hex"));
        Assert.Equal(ulong.MaxValue, Parse("FFFFFFFFFFFFFFFF", 8, "hex"));
    }

    [Fact]
    public void HexTooWideForTheWatchIsRefusedRatherThanTruncated()
    {
        Rejects("0x100", 1, "hex");
        Rejects("0x10000", 2, "hex");
        Rejects("0x100000000", 4, "hex");
    }

    [Fact]
    public void TextThatIsNotHexIsRefused()
    {
        Rejects("zz", 4, "hex");
        Rejects("0x", 4, "hex");
        Rejects("", 4, "hex");
        Rejects("   ", 4, "hex");
        Rejects("-1", 4, "hex");
    }

    // ---------------------------------------------------------------- dec

    [Fact]
    public void DecimalIsUnsignedAndFitsTheWatch()
    {
        Assert.Equal(255ul, Parse("255", 1, "dec"));
        Assert.Equal(65535ul, Parse("65535", 2, "dec"));
        Assert.Equal(ulong.MaxValue, Parse("18446744073709551615", 8, "dec"));

        Rejects("256", 1, "dec");
        Rejects("-1", 1, "dec");
        Rejects("4.5", 4, "dec");
        Rejects("bolts", 4, "dec");
    }

    /// <summary>Every other address and value box on the panel takes a hex prefix, so this one does too.</summary>
    [Fact]
    public void DecimalStillTakesAHexPrefix()
    {
        Assert.Equal(0x20ul, Parse("0x20", 4, "dec"));
        Rejects("0x100", 1, "dec");
    }

    // ---------------------------------------------------------------- signed

    [Fact]
    public void ASignedNumberIsStoredAsTheTwosComplementBitsOfItsSize()
    {
        Assert.Equal(0xFFul, Parse("-1", 1, "signed"));
        Assert.Equal(0xFFFEul, Parse("-2", 2, "signed"));
        Assert.Equal(0xFFFFFFFFul, Parse("-1", 4, "signed"));
        Assert.Equal(ulong.MaxValue, Parse("-1", 8, "signed"));
        Assert.Equal(7ul, Parse("7", 1, "signed"));
    }

    [Fact]
    public void ASignedNumberOutsideTheWatchesRangeIsRefused()
    {
        Assert.Equal(0x80ul, Parse("-128", 1, "signed"));
        Assert.Equal(0x7Ful, Parse("127", 1, "signed"));
        Rejects("128", 1, "signed");
        Rejects("-129", 1, "signed");
        Rejects("32768", 2, "signed");
        Rejects("2147483648", 4, "signed");

        // The widest watch is the whole of long, and one step past it is still refused.
        Assert.Equal(0x8000000000000000ul, Parse("-9223372036854775808", 8, "signed"));
        Rejects("9223372036854775808", 8, "signed");
    }

    // ---------------------------------------------------------------- float

    [Fact]
    public void AFloatIsTheIeeeBitsOfTheSizeItFits()
    {
        Assert.Equal((ulong)(uint)BitConverter.SingleToInt32Bits(1.5f), Parse("1.5", 4, "float"));
        Assert.Equal((ulong)BitConverter.DoubleToInt64Bits(-0.25), Parse("-0.25", 8, "float"));
        Assert.Equal(0ul, Parse("0", 4, "float"));
    }

    /// <summary>A byte and a halfword have no float in them, and FormatValue shows them as decimal.</summary>
    [Fact]
    public void AFloatOnASizeWithNoFloatInItIsReadAsDecimal()
    {
        Assert.Equal(12ul, Parse("12", 1, "float"));
        Assert.Equal(1000ul, Parse("1000", 2, "float"));
        Rejects("1.5", 1, "float");
        Rejects("300", 1, "float");
    }

    [Fact]
    public void TextThatIsNotANumberIsRefusedInEveryFormat()
    {
        foreach (var format in Ui.ValueFormats)
        {
            Rejects("nope", 4, format);
            Rejects("", 4, format);
        }
    }

    // ---------------------------------------------------------------- round trip and bytes

    /// <summary>What the cell shows is what the cell takes: every format, every size.</summary>
    [Fact]
    public void WhatFormatValueWritesIsWhatTryParseReadsBack()
    {
        foreach (byte size in new byte[] { 1, 2, 4, 8 })
        {
            ulong value = 0x0123456789ABCDEFul & WatchValueCodec.MaxFor(size);
            foreach (var format in new[] { "dec", "hex", "signed" })
            {
                Assert.Equal(value, Parse(Ui.FormatValue(value, size, format), size, format));
            }

            // The float format is decimal on the sizes with no float in them, on both sides of the
            // trip, so those go round as well.
            if (size is 1 or 2) Assert.Equal(42ul, Parse(Ui.FormatValue(42, size, "float"), size, "float"));
        }

        // A float is shown to four decimals, so it only comes back bit for bit for a number that
        // survives the rounding. That is the whole of what the box promises.
        ulong single = (uint)BitConverter.SingleToInt32Bits(2.5f);
        Assert.Equal(single, Parse(Ui.FormatValue(single, 4, "float"), 4, "float"));

        ulong wide = (ulong)BitConverter.DoubleToInt64Bits(-2.5);
        Assert.Equal(wide, Parse(Ui.FormatValue(wide, 8, "float"), 8, "float"));
    }

    [Fact]
    public void TheBytesAreBigEndianAndAsWideAsTheWatch()
    {
        Assert.Equal(new byte[] { 0xAB }, WatchValueCodec.Encode(0xAB, 1));
        Assert.Equal(new byte[] { 0x12, 0x34 }, WatchValueCodec.Encode(0x1234, 2));
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, WatchValueCodec.Encode(0xDEADBEEF, 4));
        Assert.Equal(new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF },
            WatchValueCodec.Encode(0x0123456789ABCDEF, 8));
    }

    /// <summary>The high bits of a value too wide for the watch are dropped, not spilled into the next byte.</summary>
    [Fact]
    public void OnlyTheLowBytesOfTheValueAreWritten()
    {
        Assert.Equal(new byte[] { 0xEF }, WatchValueCodec.Encode(0xDEADBEEF, 1));
        Assert.Equal(new byte[] { 0xBE, 0xEF }, WatchValueCodec.Encode(0xDEADBEEF, 2));
    }

    [Fact]
    public void ASizeThatIsNotAWatchSizeIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WatchValueCodec.Encode(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WatchValueCodec.Encode(0, 9));

        Rejects("1", 0, "dec");
        Rejects("1", 9, "dec");
    }

    [Fact]
    public void TheWidestValueAWatchHoldsIsItsSizeInBits()
    {
        Assert.Equal(0xFFul, WatchValueCodec.MaxFor(1));
        Assert.Equal(0xFFFFul, WatchValueCodec.MaxFor(2));
        Assert.Equal(0xFFFFFFFFul, WatchValueCodec.MaxFor(4));
        Assert.Equal(ulong.MaxValue, WatchValueCodec.MaxFor(8));
    }

    // ---------------------------------------------------------------- the viewer's byte cells

    [Theory]
    [InlineData("FF", 0xFF)]
    [InlineData("00", 0x00)]
    [InlineData("0a", 0x0A)]
    [InlineData(" 7f ", 0x7F)]
    public void ACellOfTwoHexDigitsIsAByte(string text, int value)
    {
        Assert.True(WatchValueCodec.TryParseByte(text, out byte parsed));
        Assert.Equal((byte)value, parsed);
    }

    /// <summary>A pair that is not finished is not a value: the cell drops it rather than writing.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("F")]
    [InlineData("FFF")]
    [InlineData("GG")]
    [InlineData("0xFF")]
    [InlineData("-1")]
    public void HalfATypedByteIsRefused(string text)
    {
        Assert.False(WatchValueCodec.TryParseByte(text, out byte parsed));
        Assert.Equal(0, parsed);
    }
}

/// <summary>
/// The Memory panel's hex dump, now that its bytes are boxes: what a committed cell does to this
/// side's copy of memory, which is what keeps the grid showing the byte that was sent rather than
/// the byte the last read brought back.
/// </summary>
public class ViewerByteEditTests
{
    [Fact]
    public void ACommittedCellChangesTheDumpItCameFrom()
    {
        var dump = new byte[] { 0x11, 0x22, 0x33 };

        Assert.True(MemoryPanel.TryApplyByteEdit(dump, 1, "AB", out byte value));
        Assert.Equal(0xAB, value);
        Assert.Equal(new byte[] { 0x11, 0xAB, 0x33 }, dump);
    }

    [Fact]
    public void ACellThatDidNotChangeAnythingWritesNothing()
    {
        var dump = new byte[] { 0x11, 0x22 };

        Assert.False(MemoryPanel.TryApplyByteEdit(dump, 0, "11", out _));
        Assert.Equal(new byte[] { 0x11, 0x22 }, dump);
    }

    [Theory]
    [InlineData(0, "F")]
    [InlineData(0, "")]
    [InlineData(0, "zz")]
    [InlineData(-1, "AB")]
    [InlineData(2, "AB")]
    public void NothingIsWrittenForATextOrAPlaceTheDumpCannotTake(int index, string text)
    {
        var dump = new byte[] { 0x11, 0x22 };

        Assert.False(MemoryPanel.TryApplyByteEdit(dump, index, text, out _));
        Assert.Equal(new byte[] { 0x11, 0x22 }, dump);
    }
}

/// <summary>
/// The moby inspector's fields: what each type shows in its box, and what the box takes back. The
/// window owns the boxes, this owns the bytes.
/// </summary>
public class MobyFieldCodecTests
{
    private static MobyField Field(string type, int offset = 0, int rawLength = 0) =>
        new() { Name = "field", Type = type, Offset = offset, RawLength = rawLength };

    /// <summary>A row with one of everything in it, big-endian, the way the console stores it.</summary>
    private static byte[] Row()
    {
        var row = new byte[64];
        row[0] = 0xFF;                                              // u8 255, i8 -1
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(2), 0xFFFE);
        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(4), 0x0012ABCD);
        BinaryPrimitives.WriteInt32BigEndian(row.AsSpan(8), -2);
        BinaryPrimitives.WriteSingleBigEndian(row.AsSpan(12), 1.5f);
        BinaryPrimitives.WriteUInt64BigEndian(row.AsSpan(16), 0x0123456789ABCDEF);
        BinaryPrimitives.WriteSingleBigEndian(row.AsSpan(24), 1.5f);
        BinaryPrimitives.WriteSingleBigEndian(row.AsSpan(28), -2.5f);
        BinaryPrimitives.WriteSingleBigEndian(row.AsSpan(32), 300f);
        BinaryPrimitives.WriteSingleBigEndian(row.AsSpan(36), 0f);
        row[40] = 0xDE;
        row[41] = 0xAD;
        row[42] = 0xBE;
        row[43] = 0xEF;
        return row;
    }

    [Theory]
    [InlineData("u8", 0, "255")]
    [InlineData("i8", 0, "-1")]
    [InlineData("u16", 2, "65534")]
    [InlineData("i16", 2, "-2")]
    [InlineData("u32", 4, "1223629")]
    [InlineData("ptr", 4, "0x0012ABCD")]
    [InlineData("i32", 8, "-2")]
    [InlineData("f32", 12, "1.5")]
    [InlineData("u64", 16, "0x0123456789ABCDEF")]
    [InlineData("vec4f", 24, "1.5, -2.5, 300, 0")]
    [InlineData("vec3f", 24, "1.5, -2.5, 300")]
    public void EachTypeIsShownInTheFormatItsBoxTakesBack(string type, int offset, string shown)
    {
        var field = Field(type, offset);
        Assert.Equal(shown, MobyFieldCodec.Format(field, Row()));

        // What it shows is what it takes: the same bytes come back out of the box's own text.
        Assert.True(MobyFieldCodec.TryEncode(field, shown, out var bytes));
        Assert.Equal(Row().AsSpan(offset, field.Length).ToArray(), bytes);
    }

    [Fact]
    public void ARawBlockIsHexAndIsNeverWritten()
    {
        var field = Field("bytes", 40, 4);

        Assert.Equal("DE AD BE EF", MobyFieldCodec.Format(field, Row()));
        Assert.False(MobyFieldCodec.IsEditable(field.Type));
        Assert.False(MobyFieldCodec.TryEncode(field, "DE AD BE EF", out _));
    }

    /// <summary>A row too short for the field is a dash on screen rather than an exception.</summary>
    [Fact]
    public void AFieldPastTheEndOfTheRowShowsNothing()
    {
        Assert.Equal(string.Empty, MobyFieldCodec.Format(Field("u32", 62), Row()));
        Assert.Equal(string.Empty, MobyFieldCodec.Format(Field("vec4f", 60), Row()));
        Assert.False(MobyFieldCodec.TryReadScalar(Field("u32", 62), Row(), out _));
    }

    [Theory]
    [InlineData("u8", 1)]
    [InlineData("i16", 2)]
    [InlineData("u32", 4)]
    [InlineData("ptr", 4)]
    [InlineData("f32", 4)]
    [InlineData("u64", 0)]
    [InlineData("vec4f", 0)]
    [InlineData("bytes", 0)]
    public void OnlyAFieldAWatchFitsOffersOne(string type, int size)
    {
        Assert.Equal((byte)size, MobyFieldCodec.WatchSize(Field(type, rawLength: 8)));
    }

    [Fact]
    public void APointerFieldSaysWhatItPointsAt()
    {
        Assert.True(MobyFieldCodec.TryReadPointer(Field("ptr", 4), Row(), out uint target));
        Assert.Equal(0x0012ABCDu, target);

        // Only a pointer: nothing else in a row is an address as far as this client knows.
        Assert.False(MobyFieldCodec.TryReadPointer(Field("u32", 4), Row(), out _));
    }

    [Fact]
    public void AVectorTakesItsFloatsSeparatedByCommasOrSpaces()
    {
        var field = Field("vec4f", 24);

        Assert.True(MobyFieldCodec.TryEncode(field, "1 2 3 4", out var spaced));
        Assert.True(MobyFieldCodec.TryEncode(field, "1, 2, 3, 4", out var commas));
        Assert.Equal(commas, spaced);
        Assert.Equal(1f, BinaryPrimitives.ReadSingleBigEndian(spaced));
        Assert.Equal(4f, BinaryPrimitives.ReadSingleBigEndian(spaced.AsSpan(12)));

        // Three floats are not a vec4f, and neither is a word.
        Assert.False(MobyFieldCodec.TryEncode(field, "1, 2, 3", out _));
        Assert.False(MobyFieldCodec.TryEncode(field, "1, 2, 3, 4, 5", out _));
        Assert.False(MobyFieldCodec.TryEncode(field, "x, y, z, w", out _));
    }

    [Fact]
    public void TextThatIsNotTheTypeIsRefusedAndNothingIsWritten()
    {
        Assert.False(MobyFieldCodec.TryEncode(Field("u8"), "256", out _));
        Assert.False(MobyFieldCodec.TryEncode(Field("i8"), "-129", out _));
        Assert.False(MobyFieldCodec.TryEncode(Field("ptr"), "not-an-address", out _));
        Assert.False(MobyFieldCodec.TryEncode(Field("f32"), "1.2.3", out _));
        Assert.False(MobyFieldCodec.TryEncode(Field("u32"), "   ", out _));
    }

    /// <summary>
    /// The format a field is read and written in is the one the watch it becomes gets, so a watch
    /// made out of a field shows what the field showed.
    /// </summary>
    [Theory]
    [InlineData("u8", "dec")]
    [InlineData("u32", "dec")]
    [InlineData("i16", "signed")]
    [InlineData("f32", "float")]
    [InlineData("ptr", "hex")]
    [InlineData("u64", "hex")]
    public void EachTypeCarriesItsFormatToTheWatchesTable(string type, string format)
    {
        Assert.Equal(format, MobyFieldCodec.FormatFor(type));
        Assert.Contains(format, Ui.ValueFormats);
    }
}

/// <summary>
/// The lines the moby inspector draws: the struct list with its vectors taken apart, and how much
/// room the four columns holding them need. The window measures the text and draws it; which
/// strings there are to measure is all decided here, so all of it is tested here.
/// </summary>
public class MobyInspectorRowTests
{
    private static MobyField Field(string name, string type, int offset, int rawLength = 0) =>
        new() { Name = name, Type = type, Offset = offset, RawLength = rawLength };

    /// <summary>A font that is one unit per character, which is all the arithmetic needs of one.</summary>
    private static float Characters(string text) => text.Length;

    /// <summary>
    /// A vector is four numbers the game writes one at a time, so it is four lines: each named for
    /// its component, at its own offset, and a watch like any other four-byte field.
    /// </summary>
    [Theory]
    [InlineData("vec4f", 4)]
    [InlineData("vec3f", 3)]
    public void AVectorIsOneRowPerComponent(string type, int count)
    {
        var rows = MobyInspectorRows.Build(new[] { Field("position", type, 0x10) });
        var names = new[] { "position.x", "position.y", "position.z", "position.w" };

        Assert.Equal(count, rows.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(names[i], rows[i].Name);
            Assert.Equal(0x10 + (i * 4), rows[i].Field.Offset);
            Assert.Equal($"0x{0x10 + (i * 4):X2}", rows[i].Offset);
            Assert.Equal("f32", rows[i].Field.Type);
            Assert.Equal("f32", rows[i].Type);
            Assert.Equal((byte)4, MobyFieldCodec.WatchSize(rows[i].Field));
        }
    }

    [Fact]
    public void AComponentShowsAndWritesItsOwnFloat()
    {
        var row = new byte[32];
        BinaryPrimitives.WriteSingleBigEndian(row.AsSpan(0x10), 1.5f);
        BinaryPrimitives.WriteSingleBigEndian(row.AsSpan(0x14), -2.5f);
        BinaryPrimitives.WriteSingleBigEndian(row.AsSpan(0x18), 300f);
        BinaryPrimitives.WriteSingleBigEndian(row.AsSpan(0x1C), 0f);

        var rows = MobyInspectorRows.Build(new[] { Field("position", "vec4f", 0x10) });
        MobyInspectorRows.Refresh(rows, row);

        Assert.Equal(new[] { "1.5", "-2.5", "300", "0" }, rows.Select(line => line.Value));

        // Its box writes those four bytes and nothing either side of them.
        Assert.True(MobyFieldCodec.TryEncode(rows[1].Field, "9.5", out var bytes));
        Assert.Equal(4, bytes.Length);
        Assert.Equal(9.5f, BinaryPrimitives.ReadSingleBigEndian(bytes));
    }

    /// <summary>Everything that is not a vector is one line, and is the struct's own entry.</summary>
    [Fact]
    public void EverythingElseIsOneLineAndTheFieldItself()
    {
        var fields = new[] { Field("state", "i8", 0x20), Field("bSphere", "bytes", 0, 16), Field("wide", "u64", 0x30) };
        var rows = MobyInspectorRows.Build(fields);

        Assert.Equal(3, rows.Count);
        Assert.Same(fields[0], rows[0].Field);
        Assert.Equal("0x20", rows[0].Offset);
        Assert.Equal("i8", rows[0].Type);
        Assert.Equal((byte)1, MobyFieldCodec.WatchSize(rows[0].Field));

        // The raw block is where its length is said, and neither it nor an eight-byte field is a
        // watch: that is still 1, 2 or 4 bytes.
        Assert.Equal("bytes[16]", rows[1].Type);
        Assert.Equal((byte)0, MobyFieldCodec.WatchSize(rows[1].Field));
        Assert.Equal("u64", rows[2].Type);
        Assert.Equal((byte)0, MobyFieldCodec.WatchSize(rows[2].Field));
    }

    /// <summary>Split or not, the lines are still the whole row: in order, and with no byte lost.</summary>
    [Fact]
    public void TheLinesStillCoverTheRowTheyCameFrom()
    {
        var fields = new[]
        {
            Field("bSphere", "bytes", 0, 16),
            Field("position", "vec4f", 16),
            Field("state", "i8", 32),
            Field("group", "u8", 33),
            Field("mClass", "i8", 34),
            Field("alpha", "i8", 35),
            Field("pClass", "ptr", 36),
        };

        int next = 0;
        var rows = MobyInspectorRows.Build(fields);

        Assert.Equal(fields.Length + 3, rows.Count);
        foreach (var row in rows)
        {
            Assert.Equal(next, row.Field.Offset);
            next += row.Field.Length;
        }

        Assert.Equal(40, next);
    }

    [Fact]
    public void EachColumnIsAsWideAsTheLongestThingInIt()
    {
        var rows = MobyInspectorRows.Build(new[]
        {
            Field("position", "vec4f", 0x10),
            Field("updateDistance", "u8", 0x30),
        });

        var widths = MobyInspectorRows.Measure(rows, Characters, room: 1000f);

        Assert.Equal(14f, widths.Field);                                    // updateDistance
        Assert.Equal(6f, widths.Offset);                                    // the header, over 0x10
        Assert.Equal(4f, widths.Type);                                      // the header, over f32
        Assert.Equal(10f, widths.Value);                                    // nothing read yet
        Assert.Equal(34f, widths.Total);

        // A value longer than all of that takes the column with it.
        rows[4].Value = "12345678901234";
        Assert.Equal(14f, MobyInspectorRows.Measure(rows, Characters, room: 1000f).Value);
    }

    /// <summary>
    /// A raw block is 191 characters of hex and no screen is that wide. Value is the column that
    /// gives way, down to a floor that can still be typed into, because its box scrolls along where
    /// the other three would simply be cut off.
    /// </summary>
    [Fact]
    public void TheValueColumnIsTheOneThatGivesWay()
    {
        var rows = MobyInspectorRows.Build(new[] { Field("bSphere", "bytes", 0, 16) });
        rows[0].Value = new string('A', 200);

        var widths = MobyInspectorRows.Measure(rows, Characters, room: 60f);
        Assert.Equal(7f, widths.Field);                                     // bSphere
        Assert.Equal(9f, widths.Type);                                      // bytes[16]
        Assert.Equal(38f, widths.Value);
        Assert.Equal(60f, widths.Total);

        // And never below what a value can be typed into, however little room there is.
        var tight = MobyInspectorRows.Measure(rows, Characters, room: 1f);
        Assert.Equal((float)MobyInspectorRows.ValueSample.Length, tight.Value);
    }

    /// <summary>A table with nothing in it is still as wide as the four headers.</summary>
    [Fact]
    public void TheHeadersAreAFloorUnderEveryColumn()
    {
        var widths = MobyInspectorRows.Measure(Array.Empty<MobyInspectorRow>(), Characters, room: 1000f);

        Assert.Equal((float)MobyInspectorRows.FieldHeader.Length, widths.Field);
        Assert.Equal((float)MobyInspectorRows.OffsetHeader.Length, widths.Offset);
        Assert.Equal((float)MobyInspectorRows.TypeHeader.Length, widths.Type);
        Assert.Equal((float)MobyInspectorRows.ValueSample.Length, widths.Value);
    }
}
