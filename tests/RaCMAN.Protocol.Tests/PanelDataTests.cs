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
