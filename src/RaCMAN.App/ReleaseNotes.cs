using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Velopack.Sources;

namespace RaCMAN.App;

/// <summary>One release's notes: its version, and the Markdown written on its GitHub release page.</summary>
public sealed class ReleaseNote
{
    private string _markdown = string.Empty;
    private IReadOnlyList<NoteBlock>? _blocks;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("notes")]
    public string Markdown
    {
        get => _markdown;
        set
        {
            _markdown = value ?? string.Empty;
            _blocks = null;
        }
    }

    /// <summary>The notes parsed for drawing, once rather than on every frame.</summary>
    [JsonIgnore]
    public IReadOnlyList<NoteBlock> Blocks => _blocks ??= ReleaseMarkdown.Parse(_markdown);
}

/// <summary>
/// The notes of a downloaded update, kept in the settings until the version they describe has
/// started and shown them once. Written when the download finishes rather than on Restart to update,
/// because Velopack also applies a downloaded update by itself on the next start.
/// </summary>
public sealed class UpdateNotesRecord
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("releases")]
    public List<ReleaseNote> Releases { get; set; } = new();
}

/// <summary>
/// The release notes out of GitHub's list of releases. That list is the first thing Velopack's
/// GithubSource asks GitHub for, to find the update feed, and every release in it carries the text
/// written on its release page, so the notes cost no request of their own: see
/// <see cref="ReleaseListCapture"/>.
/// </summary>
public static class ReleaseNotes
{
    /// <summary>
    /// The notes of every release newer than <paramref name="current"/> and no newer than
    /// <paramref name="target"/>, newest first: one entry when the user is a version behind, several
    /// when they skipped some. Drafts, prereleases (which the updater never offers) and releases
    /// with nothing written on them are left out. Never throws: notes that cannot be read are no
    /// notes, and the update goes ahead without them.
    /// </summary>
    public static IReadOnlyList<ReleaseNote> FromGithubReleases(string? json, string current, string target)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ReleaseNote>();

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<ReleaseNote>();

            var notes = new List<ReleaseNote>();
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object) continue;
                if (Flag(release, "draft") || Flag(release, "prerelease")) continue;

                string? tag = Text(release, "tag_name");
                string? body = Text(release, "body");
                if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(body)) continue;

                string version = VersionOf(tag);
                if (!AppVersion.IsNewer(version, current) || AppVersion.IsNewer(version, target)) continue;
                if (notes.Exists(n => AppVersion.Compare(n.Version, version) == 0)) continue;

                notes.Add(new ReleaseNote { Version = version, Markdown = body.Trim() });
            }

            notes.Sort((a, b) => AppVersion.Compare(b.Version, a.Version));
            return notes;
        }
        catch (JsonException)
        {
            return Array.Empty<ReleaseNote>();
        }
    }

    /// <summary>A tag as a version: "v1.2.0" is 1.2.0.</summary>
    public static string VersionOf(string tag)
    {
        string text = AppVersion.Clean(tag);
        return text.StartsWith('v') || text.StartsWith('V') ? text[1..] : text;
    }

    /// <summary>
    /// What the first start after an update does with the notes kept for it: shows them when this
    /// is the version they describe, keeps them when that version has not been applied yet (it
    /// was downloaded, and Velopack applies it on a later start), and drops them otherwise.
    /// </summary>
    public static (IReadOnlyList<ReleaseNote> Show, bool Keep) AfterUpdate(UpdateNotesRecord? stored, string current)
    {
        if (stored is null) return (Array.Empty<ReleaseNote>(), false);

        int order = AppVersion.Compare(stored.Version, current);
        if (order > 0) return (Array.Empty<ReleaseNote>(), true);
        if (order < 0) return (Array.Empty<ReleaseNote>(), false);

        var show = (stored.Releases ?? new List<ReleaseNote>())
            .Where(n => n is not null && !string.IsNullOrWhiteSpace(n.Markdown))
            .ToList();
        return (show, false);
    }

    private static bool Flag(JsonElement release, string name) =>
        release.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement release, string name) =>
        release.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// Velopack's downloader, passed through untouched, keeping one response on the way: GitHub's list
/// of releases, which GithubSource fetches on every check to find the update feed. The notes are
/// read out of it afterwards, so there is no second request and no second path to the network;
/// <see cref="UpdateService.Allowed"/> gates this exactly as it gates the check.
/// </summary>
public sealed class ReleaseListCapture : IFileDownloader
{
    private readonly IFileDownloader _inner;
    private string? _last;

    public ReleaseListCapture(IFileDownloader inner) => _inner = inner;

    /// <summary>The last releases list seen since <see cref="Clear"/>, or null.</summary>
    public string? Last => Volatile.Read(ref _last);

    public void Clear() => Volatile.Write(ref _last, null);

    /// <summary>
    /// True for GitHub's list-releases call, <c>/repos/{owner}/{repo}/releases</c>, whatever its query:
    /// not for a release's assets, whose addresses end in a file name.
    /// </summary>
    public static bool IsReleaseList(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && Regex.IsMatch(uri.AbsolutePath, "^(/api/v3)?/repos/[^/]+/[^/]+/releases/?$", RegexOptions.IgnoreCase);

    public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        string text = await _inner.DownloadString(url, headers, timeout).ConfigureAwait(false);
        if (IsReleaseList(url)) Volatile.Write(ref _last, text);
        return text;
    }

    public Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default) =>
        _inner.DownloadFile(url, targetFile, progress, headers, timeout, cancelToken);

    public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
        _inner.DownloadBytes(url, headers, timeout);
}

public enum NoteBlockKind
{
    Paragraph,
    Heading,
    Bullet,
    Quote,
    Code,
    Rule,
    Blank,
}

/// <summary>A run of text that is drawn one way: plain, bold, or a link to <see cref="Link"/>.</summary>
public sealed record NoteSpan(string Text, bool Bold = false, string? Link = null);

/// <summary>
/// One line of notes. <see cref="Level"/> is a heading's level, or how deep a bullet or an indented
/// line sits; <see cref="Marker"/> is a numbered item's number ("2."), empty for a bullet.
/// </summary>
public sealed record NoteBlock(NoteBlockKind Kind, IReadOnlyList<NoteSpan> Spans, int Level = 0, string Marker = "");

/// <summary>
/// The Markdown release notes use, and no more: headings, bullets and numbered items, bold, links
/// (written, angle-bracketed or bare), code, quotes and rules. Italics, strike-through and code
/// lose their marks and keep their text; anything else is shown as written. Every line is a line of
/// its own, as on GitHub's releases page.
/// </summary>
public static partial class ReleaseMarkdown
{
    public static IReadOnlyList<NoteBlock> Parse(string? markdown)
    {
        var blocks = new List<NoteBlock>();
        if (string.IsNullOrEmpty(markdown)) return blocks;

        bool fence = false;
        bool comment = false;

        foreach (string raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Replace("\t", "    ").TrimEnd();
            string trimmed = line.TrimStart();

            if (comment)
            {
                if (trimmed.Contains("-->", StringComparison.Ordinal)) comment = false;
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = !fence;
                continue;
            }

            if (fence)
            {
                blocks.Add(new NoteBlock(NoteBlockKind.Code, new[] { new NoteSpan(line) }));
                continue;
            }

            // A template's hidden hint, which GitHub does not show either.
            if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                comment = trimmed.IndexOf("-->", 4, StringComparison.Ordinal) < 0;
                continue;
            }

            if (trimmed.Length == 0)
            {
                if (blocks.Count > 0 && blocks[^1].Kind != NoteBlockKind.Blank)
                {
                    blocks.Add(new NoteBlock(NoteBlockKind.Blank, Array.Empty<NoteSpan>()));
                }

                continue;
            }

            int depth = Math.Min((line.Length - trimmed.Length) / 2, 6);

            if (RuleLine().IsMatch(trimmed))
            {
                blocks.Add(new NoteBlock(NoteBlockKind.Rule, Array.Empty<NoteSpan>()));
                continue;
            }

            var heading = HeadingLine().Match(trimmed);
            if (heading.Success)
            {
                string title = heading.Groups[2].Value.TrimEnd('#').TrimEnd();
                blocks.Add(new NoteBlock(NoteBlockKind.Heading, Inline(title), heading.Groups[1].Length));
                continue;
            }

            var bullet = BulletLine().Match(trimmed);
            if (bullet.Success)
            {
                blocks.Add(new NoteBlock(NoteBlockKind.Bullet, Inline(bullet.Groups[1].Value), depth));
                continue;
            }

            var numbered = NumberedLine().Match(trimmed);
            if (numbered.Success)
            {
                blocks.Add(new NoteBlock(NoteBlockKind.Bullet, Inline(numbered.Groups[2].Value), depth,
                    numbered.Groups[1].Value + "."));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                blocks.Add(new NoteBlock(NoteBlockKind.Quote, Inline(trimmed.TrimStart('>', ' '))));
                continue;
            }

            blocks.Add(new NoteBlock(NoteBlockKind.Paragraph, Inline(trimmed), depth));
        }

        while (blocks.Count > 0 && blocks[^1].Kind == NoteBlockKind.Blank) blocks.RemoveAt(blocks.Count - 1);
        return blocks;
    }

    /// <summary>One line's text as spans: bold, links, and plain text between them.</summary>
    public static IReadOnlyList<NoteSpan> Inline(string text)
    {
        var spans = new List<NoteSpan>();
        var run = new StringBuilder();
        bool bold = false;
        string boldMark = string.Empty;
        bool italic = false;

        void Flush()
        {
            if (run.Length == 0) return;
            spans.Add(new NoteSpan(run.ToString(), bold));
            run.Clear();
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '\\' && (char.IsPunctuation(next) || char.IsSymbol(next)))
            {
                run.Append(next);
                i += 2;
                continue;
            }

            // Bold: ** anywhere, __ only at the start of a word, so a name like my__name stays as written.
            if ((c == '*' || c == '_') && next == c)
            {
                string mark = new(c, 2);
                if (bold && mark == boldMark)
                {
                    Flush();
                    bold = false;
                    i += 2;
                    continue;
                }

                bool opens = !bold && i + 2 < text.Length && !char.IsWhiteSpace(text[i + 2])
                             && text.IndexOf(mark, i + 2, StringComparison.Ordinal) > i + 2
                             && (c == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1]));
                if (opens)
                {
                    Flush();
                    bold = true;
                    boldMark = mark;
                    i += 2;
                    continue;
                }
            }

            // Italics have no font to be drawn in; the marks go and the words stay.
            if (c == '*' && (italic || next != '\0' && !char.IsWhiteSpace(next) && text.IndexOf('*', i + 1) > i + 1))
            {
                italic = !italic;
                i++;
                continue;
            }

            if (c == '~' && next == '~')
            {
                i += 2;
                continue;
            }

            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    run.Append(text, i + 1, end - i - 1);
                    i = end + 1;
                    continue;
                }
            }

            // ![alt](address) and [text](address): the text, linked.
            int start = c == '!' && next == '[' ? i + 1 : i;
            if (text[start] == '[' && TryLink(text, start, out string label, out string address, out int after))
            {
                Flush();
                string shown = Plain(label);
                spans.Add(new NoteSpan(shown.Length > 0 ? shown : address, bold, address));
                i = after;
                continue;
            }

            // <https://...>
            if (c == '<')
            {
                int end = text.IndexOf('>', i + 1);
                if (end > i && IsWebAddress(text.AsSpan(i + 1, end - i - 1)))
                {
                    Flush();
                    string angled = text.Substring(i + 1, end - i - 1);
                    spans.Add(new NoteSpan(angled, bold, angled));
                    i = end + 1;
                    continue;
                }
            }

            // A bare address, as GitHub's generated "Full Changelog" line has.
            if ((c == 'h' || c == 'H') && (i == 0 || !char.IsLetterOrDigit(text[i - 1])) && StartsWithWebAddress(text, i))
            {
                int end = i;
                while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '<') end++;
                while (end > i && ".,;:!?'\"*".Contains(text[end - 1])) end--;
                if (end > i && text[end - 1] == ')' && !text.AsSpan(i, end - i).Contains('(')) end--;

                Flush();
                string bare = text[i..end];
                spans.Add(new NoteSpan(bare, bold, bare));
                i = end;
                continue;
            }

            run.Append(c);
            i++;
        }

        Flush();
        return spans;
    }

    /// <summary>The text of some Markdown with its marks taken out, for a link's label.</summary>
    public static string Plain(string text) => string.Concat(Inline(text).Select(s => s.Text));

    private static bool TryLink(string text, int open, out string label, out string address, out int after)
    {
        label = address = string.Empty;
        after = open;

        int depth = 0;
        int close = -1;
        for (int j = open; j < text.Length; j++)
        {
            if (text[j] == '[') depth++;
            else if (text[j] == ']' && --depth == 0)
            {
                close = j;
                break;
            }
        }

        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return false;

        int parens = 0;
        int end = -1;
        for (int j = close + 1; j < text.Length; j++)
        {
            if (text[j] == '(') parens++;
            else if (text[j] == ')' && --parens == 0)
            {
                end = j;
                break;
            }
        }

        if (end < 0) return false;

        // A title after the address ("title") is not shown.
        string target = text.Substring(close + 2, end - close - 2).Trim();
        int space = target.IndexOf(' ');
        if (space > 0) target = target[..space];
        target = target.Trim('<', '>');
        if (target.Length == 0) return false;

        label = text.Substring(open + 1, close - open - 1);
        address = target;
        after = end + 1;
        return true;
    }

    private static bool StartsWithWebAddress(string text, int at) =>
        text.AsSpan(at).StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || text.AsSpan(at).StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static bool IsWebAddress(ReadOnlySpan<char> text) =>
        (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        && !text.ContainsAny(' ', '\t');

    [GeneratedRegex(@"^(\*\s*){3,}$|^(-\s*){3,}$|^(_\s*){3,}$")]
    private static partial Regex RuleLine();

    [GeneratedRegex(@"^(#{1,6})(?:\s+(.*))?$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^[-*+]\s+(.*)$")]
    private static partial Regex BulletLine();

    [GeneratedRegex(@"^(\d{1,9})[.)]\s+(.*)$")]
    private static partial Regex NumberedLine();
}
