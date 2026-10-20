using System.Text;
using RaCMAN.App;
using Velopack.Logging;
using Velopack.Sources;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The update banner's release notes: which releases' notes GitHub's list gives, how the Markdown
/// written on a release page is read, and that the list is the one Velopack fetches anyway. Nothing
/// here reaches the network: GitHub's answers are strings, and Velopack is handed a downloader that
/// refuses anything it was not told about.
/// </summary>
public class ReleaseNotesTests
{
    private const string RepositoryUrl = "https://github.com/sowelilili/RaCMAN-Reloaded";

    private static string Release(string tag, string? body, bool draft = false, bool prerelease = false)
    {
        string bodyJson = body is null ? "null" : System.Text.Json.JsonSerializer.Serialize(body);
        return $$"""
            {"tag_name":"{{tag}}","name":"RaCMAN Reloaded {{tag}}","body":{{bodyJson}},"draft":{{(draft ? "true" : "false")}},
             "prerelease":{{(prerelease ? "true" : "false")}},"published_at":"2026-10-01T00:00:00Z","assets":[]}
            """;
    }

    private static string List(params string[] releases) => "[" + string.Join(",", releases) + "]";

    // ---------------------------------------------------------------- which releases

    [Fact]
    public void EveryReleaseSinceThisOneUpToTheOfferNewestFirst()
    {
        string json = List(
            Release("v1.2.1", "one-two-one"),
            Release("v1.4.0", "too new"),
            Release("v1.3.0", "one-three"),
            Release("v1.2.0", "the one running"),
            Release("v1.1.0", "older still"));

        var notes = ReleaseNotes.FromGithubReleases(json, "1.2.0", "1.3.0");

        Assert.Equal(new[] { "1.3.0", "1.2.1" }, notes.Select(n => n.Version));
        Assert.Equal("one-three", notes[0].Markdown);
    }

    [Fact]
    public void DraftsPrereleasesAndEmptyPagesAreLeftOut()
    {
        string json = List(
            Release("v1.3.0", "  \r\n "),
            Release("v1.2.3", null),
            Release("v1.2.2", "draft", draft: true),
            Release("v1.2.1-beta.1", "beta", prerelease: true),
            Release("v1.2.1", "kept"));

        var notes = ReleaseNotes.FromGithubReleases(json, "1.2.0", "1.3.0");

        Assert.Equal("1.2.1", Assert.Single(notes).Version);
    }

    [Fact]
    public void NotesThatCannotBeReadAreNoNotes()
    {
        Assert.Empty(ReleaseNotes.FromGithubReleases(null, "1.0.0", "2.0.0"));
        Assert.Empty(ReleaseNotes.FromGithubReleases("", "1.0.0", "2.0.0"));
        Assert.Empty(ReleaseNotes.FromGithubReleases("{\"message\":\"API rate limit exceeded\"}", "1.0.0", "2.0.0"));
        Assert.Empty(ReleaseNotes.FromGithubReleases("[{\"tag_name\": 5, \"body\": []}, 7, \"x\"]", "1.0.0", "2.0.0"));
        Assert.Empty(ReleaseNotes.FromGithubReleases("[{\"tag_name\":\"v1.1.0\",\"bo", "1.0.0", "2.0.0"));
    }

    [Fact]
    public void ATagIsReadAsAVersion()
    {
        Assert.Equal("1.2.0", ReleaseNotes.VersionOf("v1.2.0"));
        Assert.Equal("1.2.0", ReleaseNotes.VersionOf("1.2.0"));
        Assert.Equal("1.2.0-beta.1", ReleaseNotes.VersionOf("v1.2.0-beta.1"));
    }

    // ---------------------------------------------------------------- after the update

    private static UpdateNotesRecord Record(string version) => new()
    {
        Version = version,
        Releases = { new ReleaseNote { Version = version, Markdown = "- fixed" } },
    };

    [Fact]
    public void TheVersionTheNotesDescribeShowsThemOnceAndLetsThemGo()
    {
        var (show, keep) = ReleaseNotes.AfterUpdate(Record("1.3.0"), "1.3.0");

        Assert.Equal("1.3.0", Assert.Single(show).Version);
        Assert.False(keep);
    }

    [Fact]
    public void NotesForAnUpdateNotAppliedYetWaitForIt()
    {
        var (show, keep) = ReleaseNotes.AfterUpdate(Record("1.3.0"), "1.2.0");

        Assert.Empty(show);
        Assert.True(keep);
    }

    [Fact]
    public void NotesForAnOlderVersionAreDroppedUnseen()
    {
        var (show, keep) = ReleaseNotes.AfterUpdate(Record("1.3.0"), "1.4.0");

        Assert.Empty(show);
        Assert.False(keep);
        Assert.Empty(ReleaseNotes.AfterUpdate(null, "1.4.0").Show);
    }

    // ---------------------------------------------------------------- the list Velopack fetches

    [Fact]
    public void OnlyGithubsReleasesListIsKept()
    {
        Assert.True(ReleaseListCapture.IsReleaseList(
            "https://api.github.com/repos/sowelilili/RaCMAN-Reloaded/releases?per_page=10&page=1"));
        Assert.True(ReleaseListCapture.IsReleaseList("https://github.example.com/api/v3/repos/a/b/releases"));

        Assert.False(ReleaseListCapture.IsReleaseList(
            "https://github.com/sowelilili/RaCMAN-Reloaded/releases/download/v1.3.0/releases.win.json"));
        Assert.False(ReleaseListCapture.IsReleaseList("https://api.github.com/repos/a/b/releases/assets/12"));
        Assert.False(ReleaseListCapture.IsReleaseList("not a url"));
        Assert.False(ReleaseListCapture.IsReleaseList(null));
    }

    /// <summary>
    /// A downloader that answers the two requests GithubSource makes for a check and refuses
    /// everything else, so the test both stays offline and proves the notes add no request.
    /// </summary>
    private sealed class FakeGithub : IFileDownloader
    {
        public readonly List<string> Requests = new();
        public string ReleaseList = "[]";

        public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Requests.Add(url);
            if (url.Contains("/releases?", StringComparison.Ordinal)) return Task.FromResult(ReleaseList);
            throw new InvalidOperationException($"unexpected request for {url}");
        }

        public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            Requests.Add(url);
            if (url.EndsWith("releases.win.json", StringComparison.Ordinal))
            {
                return Task.FromResult(Encoding.UTF8.GetBytes(
                    """{"Assets":[{"PackageId":"RaCMANReloaded","Version":"1.3.0","Type":"Full","FileName":"RaCMANReloaded-1.3.0-full.nupkg","SHA1":"00","SHA256":"00","Size":1}]}"""));
            }

            throw new InvalidOperationException($"unexpected request for {url}");
        }

        public Task DownloadFile(string url, string targetFile, Action<int> progress,
            IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) =>
            throw new InvalidOperationException($"unexpected download of {url}");
    }

    [Fact]
    public async Task TheNotesComeFromTheListVelopackFetchesForItsCheckAndCostNoRequest()
    {
        var github = new FakeGithub
        {
            ReleaseList = """
                [{"tag_name":"v1.3.0","name":"RaCMAN Reloaded 1.3.0","body":"## Fixes\r\n- **Bold** thing","draft":false,
                  "prerelease":false,"published_at":"2026-10-01T00:00:00Z",
                  "assets":[{"name":"releases.win.json","url":"https://api.github.com/repos/x/y/releases/assets/1",
                             "browser_download_url":"https://github.com/sowelilili/RaCMAN-Reloaded/releases/download/v1.3.0/releases.win.json",
                             "content_type":"application/json"}]}]
                """,
        };

        var capture = new ReleaseListCapture(github);
        var source = new GithubSource(RepositoryUrl, null, false, capture);

        var feed = await source.GetReleaseFeed(new NullVelopackLogger(), "RaCMANReloaded", "win", null, null);

        Assert.Equal("1.3.0", Assert.Single(feed.Assets).Version.ToString());
        Assert.Equal(2, github.Requests.Count);
        Assert.Equal(github.ReleaseList, capture.Last);

        var notes = ReleaseNotes.FromGithubReleases(capture.Last, "1.2.0", "1.3.0");
        Assert.Equal("## Fixes\r\n- **Bold** thing", Assert.Single(notes).Markdown);

        capture.Clear();
        Assert.Null(capture.Last);
    }

    // ---------------------------------------------------------------- the Markdown

    [Fact]
    public void HeadingsBulletsAndNumbersAreBlocks()
    {
        var blocks = ReleaseMarkdown.Parse("# Title\r\n### Fixes ###\r\n- one\r\n  * nested\r\n+ two\r\n3. three\r\nplain");

        Assert.Equal(
            new[] { NoteBlockKind.Heading, NoteBlockKind.Heading, NoteBlockKind.Bullet, NoteBlockKind.Bullet,
                    NoteBlockKind.Bullet, NoteBlockKind.Bullet, NoteBlockKind.Paragraph },
            blocks.Select(b => b.Kind));

        Assert.Equal(1, blocks[0].Level);
        Assert.Equal(3, blocks[1].Level);
        Assert.Equal("Fixes", Plain(blocks[1]));
        Assert.Equal(0, blocks[2].Level);
        Assert.Equal(1, blocks[3].Level);
        Assert.Equal("nested", Plain(blocks[3]));
        Assert.Equal("", blocks[4].Marker);
        Assert.Equal("3.", blocks[5].Marker);
        Assert.Equal("three", Plain(blocks[5]));
    }

    [Fact]
    public void BlankLinesCollapseAndDoNotEndTheNotes()
    {
        var blocks = ReleaseMarkdown.Parse("\n\none\n\n\n\ntwo\n\n\n");

        Assert.Equal(new[] { NoteBlockKind.Paragraph, NoteBlockKind.Blank, NoteBlockKind.Paragraph }, blocks.Select(b => b.Kind));
    }

    [Fact]
    public void RulesQuotesCodeAndHiddenCommentsAreReadAsSuch()
    {
        var blocks = ReleaseMarkdown.Parse(
            "<!-- a hint\nover two lines -->\n<!-- one line -->\n---\n> quoted **text**\n```\n**not bold** # nor a heading\n```\n* * *");

        Assert.Equal(new[] { NoteBlockKind.Rule, NoteBlockKind.Quote, NoteBlockKind.Code, NoteBlockKind.Rule },
            blocks.Select(b => b.Kind));
        Assert.Equal("quoted text", Plain(blocks[1]));
        Assert.Equal("**not bold** # nor a heading", Plain(blocks[2]));
    }

    [Fact]
    public void BoldIsASpanAndItsMarksGo()
    {
        var spans = ReleaseMarkdown.Inline("a **bold** and __also__ b");

        Assert.Equal(new[] { "a ", "bold", " and ", "also", " b" }, spans.Select(s => s.Text));
        Assert.Equal(new[] { false, true, false, true, false }, spans.Select(s => s.Bold));
    }

    [Fact]
    public void MarksThatAreNotMarkupStayAsWritten()
    {
        Assert.Equal("snake_case and my__dunder__name stay", PlainInline("snake_case and my__dunder__name stay"));
        Assert.Equal("a ** b", PlainInline("a ** b"));
        Assert.Equal("5 * 3 and *star", PlainInline("5 * 3 and *star"));
        Assert.Equal("Any% (co-op) 100%", PlainInline("Any% (co-op) 100%"));
        Assert.Equal("*literal*", PlainInline(@"\*literal\*"));
    }

    [Fact]
    public void ItalicsCodeAndStrikeThroughKeepTheirTextOnly()
    {
        Assert.Equal("an italic word, code, and gone", PlainInline("an *italic* word, `code`, and ~~gone~~"));
    }

    [Fact]
    public void LinksKeepTheirTextAndTheirAddress()
    {
        var spans = ReleaseMarkdown.Inline("See [the **page**](https://example.com/a_(b) \"title\") or <https://example.com/x>.");

        var links = spans.Where(s => s.Link is not null).ToList();
        Assert.Equal(2, links.Count);
        Assert.Equal("the page", links[0].Text);
        Assert.Equal("https://example.com/a_(b)", links[0].Link);
        Assert.Equal("https://example.com/x", links[1].Text);
        Assert.Equal("https://example.com/x", links[1].Link);
        Assert.Equal(".", spans[^1].Text);
    }

    [Fact]
    public void ABareAddressIsALinkWithoutTheFullStopAfterIt()
    {
        var spans = ReleaseMarkdown.Inline("Changelog: https://github.com/o/r/compare/v1.0.0...v1.1.0. (see https://x.org/y)");

        var links = spans.Where(s => s.Link is not null).Select(s => s.Link).ToList();
        Assert.Equal(new[] { "https://github.com/o/r/compare/v1.0.0...v1.1.0", "https://x.org/y" }, links);
        Assert.Equal(". (see ", spans[2].Text);
        Assert.Equal(")", spans[^1].Text);
    }

    [Fact]
    public void AnImageIsItsDescriptionLinkedToThePicture()
    {
        var span = Assert.Single(ReleaseMarkdown.Inline("![the new panel](https://example.com/p.png)"));

        Assert.Equal("the new panel", span.Text);
        Assert.Equal("https://example.com/p.png", span.Link);
    }

    [Fact]
    public void GithubsGeneratedNotesRead()
    {
        const string generated =
            "## What's Changed\r\n"
            + "* Manips: picking a group applies nothing by @sowelilili in https://github.com/sowelilili/RaCMAN-Reloaded/pull/12\r\n"
            + "\r\n"
            + "**Full Changelog**: https://github.com/sowelilili/RaCMAN-Reloaded/compare/v1.0.0...v1.1.0";

        var blocks = ReleaseMarkdown.Parse(generated);

        Assert.Equal(new[] { NoteBlockKind.Heading, NoteBlockKind.Bullet, NoteBlockKind.Blank, NoteBlockKind.Paragraph },
            blocks.Select(b => b.Kind));
        Assert.Equal("What's Changed", Plain(blocks[0]));
        Assert.Equal("https://github.com/sowelilili/RaCMAN-Reloaded/pull/12", blocks[1].Spans[^1].Link);
        Assert.True(blocks[3].Spans[0].Bold);
        Assert.Equal("Full Changelog", blocks[3].Spans[0].Text);
        Assert.Equal("https://github.com/sowelilili/RaCMAN-Reloaded/compare/v1.0.0...v1.1.0", blocks[3].Spans[^1].Link);
    }

    [Fact]
    public void TheParsedNotesAreKeptUntilTheTextChanges()
    {
        var note = new ReleaseNote { Version = "1.3.0", Markdown = "- one" };
        var first = note.Blocks;

        Assert.Same(first, note.Blocks);

        note.Markdown = "# two";
        Assert.Equal(NoteBlockKind.Heading, Assert.Single(note.Blocks).Kind);
    }

    // ---------------------------------------------------------------- the links it opens

    [Fact]
    public void OnlyWebAddressesAreOpened()
    {
        Assert.Equal("https://github.com/sowelilili/RaCMAN-Reloaded/releases",
            FileExplorer.WebAddress(" https://github.com/sowelilili/RaCMAN-Reloaded/releases "));
        Assert.NotNull(FileExplorer.WebAddress("http://example.com"));

        Assert.Null(FileExplorer.WebAddress("file:///C:/Windows/System32/calc.exe"));
        Assert.Null(FileExplorer.WebAddress(@"C:\Windows"));
        Assert.Null(FileExplorer.WebAddress("javascript:alert(1)"));
        Assert.Null(FileExplorer.WebAddress("www.example.com"));
        Assert.Null(FileExplorer.WebAddress(null));
    }

    private static string Plain(NoteBlock block) => string.Concat(block.Spans.Select(s => s.Text));

    private static string PlainInline(string text) => string.Concat(ReleaseMarkdown.Inline(text).Select(s => s.Text));
}
