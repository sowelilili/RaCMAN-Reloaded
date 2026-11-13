using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using ImGuiNET;
using RaCMAN.App;
using RaCMAN.App.Panels;

namespace RaCMAN.Protocol.Tests;

/// <summary>
/// The toast box is a fixed width (<see cref="Toast.Width"/>, half the smallest window) and wraps
/// every toast inside it, so a toast that says too much grows tall instead of running off the
/// screen. A toast is a brief: this holds every one of them to <see cref="Toast.MaxLines"/> lines of
/// that box, measured with ImGui's own CalcTextSize in the font the client draws with.
/// <para>
/// The texts are found two ways. <see cref="ToastSource"/> reads the source for every string written
/// straight into a toast (the argument of AddToast, a request's success message, a step's progress
/// line, the message of a send step that fails), with a representative value in each hole. The
/// helpers that build a toast out of parts are called below with representative values instead.
/// </para>
/// </summary>
public class ToastLengthTests
{
    [Fact]
    public void EveryToastWrittenInTheSourceFitsInThreeLines()
    {
        var sites = ToastSource.Scan();

        // A scan that found nothing has stopped reading the source, not proved anything.
        Assert.True(sites.Count > 150, $"Only {sites.Count} toast texts were found in the source");

        var tooLong = sites
            .Where(site => ToastMeasure.Lines(site.Text) > Toast.MaxLines)
            .Select(site => $"{site.Where}: {ToastMeasure.Lines(site.Text)} lines: {site.Text}")
            .ToList();

        Assert.True(tooLong.Count == 0, "Toasts longer than three lines:\n" + string.Join("\n", tooLong));
    }

    [Fact]
    public void EveryToastAHelperBuildsFitsInThreeLines()
    {
        var tooLong = HelperToasts()
            .Where(text => ToastMeasure.Lines(text) > Toast.MaxLines)
            .Select(text => $"{ToastMeasure.Lines(text)} lines: {text}")
            .ToList();

        Assert.True(tooLong.Count == 0, "Toasts longer than three lines:\n" + string.Join("\n", tooLong));
    }

    [Fact]
    public void TheMeasureCountsWrappedLines()
    {
        // The guard is only as good as its ruler: one word, a line's worth, and a long paragraph.
        Assert.Equal(1, ToastMeasure.Lines("Connected"));
        Assert.Equal(3, ToastMeasure.Lines(string.Join(" ", Enumerable.Repeat("word", 30))));
        Assert.True(ToastMeasure.Lines(string.Join(" ", Enumerable.Repeat("word", 60))) > Toast.MaxLines);
    }

    /// <summary>Every toast a helper builds, with representative values for its parts.</summary>
    internal static IEnumerable<string> HelperToasts()
    {
        // Status codes, for a button and for the debug view, in and out of a game.
        foreach (var status in Enum.GetValues<Status>())
        {
            foreach (var state in Enum.GetValues<SessionState>())
            {
                foreach (bool debug in new[] { false, true })
                {
                    foreach (var opcode in new[] { Opcode.ConfigSave, Opcode.FeatureTrigger, Opcode.MemWrite })
                    {
                        if (StatusToast.For(status, opcode, state, quiet: false, debug) is { } text) yield return text;
                    }
                }
            }
        }

        // Manip events, for a long group name.
        const string group = "Mega Corp Games Store Glitch";
        foreach (var kind in Enum.GetValues<ManipEventKind>())
        {
            if (ManipEvents.NoticeFor(kind, group) is { Toast: not null } notice) yield return notice.Text;
        }

        yield return ManipEvents.Applied(group);
        yield return ManipEvents.NotApplied(group);

        // A game module that is not to be had, every way, with and without an answer.
        foreach (var state in Enum.GetValues<GameModuleState>())
        {
            foreach (uint error in new[] { 0u, 0x80010002u })
            {
                var info = new GameModuleInfo(state, GameId.Rac2, 1, GameModuleFlags.Modules, 900, 900, 1024, 128, 3000, 0, error, 0, 0);
                yield return AppState.GameModuleToast(info, "NPEA00385");
            }
        }

        yield return AppState.GameModuleToast(null, "NPEA00385");
        yield return AppState.UiPakToast;

        // A boot copy a webMAN load could not replace, at its wordiest step.
        yield return AppState.BootCopyFailedToast("uploading it: FTP timed out after 30 s sending the file");
        yield return AppState.BootCopyFailedToast("reading boot_plugins.txt: FTP RETR refused: 425 Can't open data connection");
        yield return UiPak.MissingNote;
        yield return UiPak.SendFailedNote + " (The process cannot access the file in use)";

        // Every reason a save, a load or a set-aside is refused or stops, in each toast that says it.
        foreach (var error in Enum.GetValues<SaveFileError>())
        {
            foreach (var request in Enum.GetValues<SaveFileRequest>())
            {
                foreach (var reason in Enum.GetValues<SaveFileReason>())
                {
                    string why = error.Describe(request, reason);
                    yield return SaveFilesPanel.FailureToast(true, new InvalidOperationException(why));
                    yield return GamePanel.AsideRefusedToast("Set aside a save", why);
                }
            }
        }

        // A premade save copied, renamed on the way, or already there.
        var entry = new SaveFileEntry("Placeholder Name.sav", false, true, 0, 1, 0, 64);
        yield return SaveFilesPanel.CopyToast(entry, new PremadeCopy("Placeholder Name", "Placeholder Name.sav", false));
        yield return SaveFilesPanel.CopyToast(entry, new PremadeCopy("Placeholder Name", "Placeholder Name (2).sav", false));
        yield return SaveFilesPanel.CopyToast(entry, new PremadeCopy("Placeholder Name", "Placeholder Name (2).sav", true));
        yield return SaveFilesPanel.DescribeRenames(new[]
        {
            new SaveFileRename("misc", "veldin", "veldin.sav", Dropped: false),
            new SaveFileRename("misc", "veldin", "veldin.sav", Dropped: true),
        });

        yield return "Mod One, Mod Two were not enabled: " + Rpcs3ModsController.ChangedWhileReading("Placeholder Name");
        yield return LevelFlagsPanel.CheckGroupToast("Placeholder Name");
        yield return LevelFlagsPanel.CheckAllToast(12);

        // The boot install, every way it can go.
        foreach (var outcome in Enum.GetValues<BootInstallOutcome>())
        {
            foreach (bool pak in new[] { false, true })
            {
                foreach (int modules in new[] { 0, 1, 5 })
                {
                    yield return ConnectionPanel.BootInstallMessage(new BootInstallResult(outcome, 8, pak, modules));
                }
            }
        }

        // An uninstall: done, nothing to do, and stopped part way with a rescan that failed.
        var mod = new ModEntry(0, ModFlags.None, 0x12345678, "placeholder", "Placeholder Name", "1.0", "someone");
        var plan = new UninstallPlan("NPEA00385", new ModRow(null, mod, "placeholder", true), false,
            Array.Empty<UninstallStep>(), Array.Empty<string>(), null);
        var done = new[] { "unloaded", "auto flag off", "deleted from your console", "deleted from your mods folder" };
        yield return ModUninstall.Describe(plan, new UninstallOutcome(done, null, null));
        yield return ModUninstall.Describe(plan, new UninstallOutcome(Array.Empty<string>(), null, "Busy"));
        yield return ModUninstall.Describe(plan, new UninstallOutcome(done[..2],
            $"{ModUninstall.StepName(UninstallStepKind.DeleteConsoleFolder)} failed: your console answered Busy", "Busy"));

        yield return new DataFolderMigration.Result(true, new[] { "colours", "watchlists", "savefiles" })
            .Describe();
    }
}

/// <summary>ImGui's own measure of a toast, in the font and at the width the client draws it with.</summary>
internal static class ToastMeasure
{
    private static readonly object Gate = new();

    /// <summary>How many lines <paramref name="text"/> takes in the toast box.</summary>
    public static int Lines(string text)
    {
        lock (Gate)
        {
            var previous = ImGui.GetCurrentContext();
            var context = ImGui.CreateContext();
            try
            {
                ImGui.SetCurrentContext(context);
                var io = ImGui.GetIO();

                // The font the client uses: ImGuiController adds the default font and nothing else.
                io.Fonts.AddFontDefault();
                io.Fonts.GetTexDataAsRGBA32(out IntPtr _, out int _, out int _, out int _);
                io.DisplaySize = new Vector2(WindowGeometry.DefaultWidth, WindowGeometry.DefaultHeight);
                io.DeltaTime = 1f / 60f;

                ImGui.NewFrame();
                var size = ImGui.CalcTextSize(text, Toast.WrapWidth);
                float line = ImGui.GetTextLineHeight();
                ImGui.EndFrame();

                return Math.Max(1, (int)MathF.Round(size.Y / line));
            }
            finally
            {
                ImGui.DestroyContext(context);
                if (previous != IntPtr.Zero) ImGui.SetCurrentContext(previous);
            }
        }
    }
}

/// <summary>
/// Reads the client's source for the strings that become toasts: the argument of every AddToast,
/// the success message of every Run, the line of every progress Report or connect step, the
/// message of every send step's or save's exception, an (ok, message) answer, an RPCS3 mod's
/// refusal and a hash lookup's problem, and what is put in the problem and message variables that
/// are toasted as they are (each with the words its toast puts around it). A hole in an interpolated string gets a representative value for what it
/// holds (a file name, a count, an error message); strings joined with + are one toast, and the
/// branches of a ?: are a toast each. A toast passed in a variable has no string at its call site
/// and is left to the helper tests.
/// </summary>
internal static class ToastSource
{
    public readonly record struct Site(string Where, string Text);

    /// <summary>The calls whose string arguments are toasts.</summary>
    private static readonly Regex Call = new(
        @"(?<![\w.])(?:\w+\??\.)*(?<name>AddToast|_toast|Run|Report|say|Ps3ConnectOutcome|GameModuleSendException|UiPakSendException|SprxUpdateException|NotAnsweredException|ExecutableHashLookup)\s*\(",
        RegexOptions.Compiled);

    /// <summary>The variables whose value is toasted as it is, or inside the words given.</summary>
    private static readonly Regex Assignment = new(
        @"(?<![\w.])(?<name>Problem|message|failure)\s*=(?![=>])",
        RegexOptions.Compiled);

    /// <summary>A tuple of a success flag and the sentence that says how it went.</summary>
    private static readonly Regex Outcome = new(
        @"\((?=\s*(?:true|false)\s*,\s*[$@]*"")",
        RegexOptions.Compiled);

    /// <summary>An RPCS3 mod refusal: no patch, the refusal, and the reason a toast carries.</summary>
    private static readonly Regex Refusal = new(
        @"\((?=\s*null\s*,\s*ModPatchRefusal\.)",
        RegexOptions.Compiled);

    /// <summary>The words a toast puts around a variable's value: an uninstall's failure, the worst case.</summary>
    private static (string Before, string After) Around(string name) => name switch
    {
        "failure" => ("Placeholder Name not uninstalled: ", ". Done first: unloaded, auto flag off."),
        "NotAnsweredException" => ("Save failed: ", string.Empty),
        "SprxUpdateException" => ("qwark.sprx update failed while ", ": The process cannot access the file in use"),
        "ExecutableHashLookup" or "ModPatchRefusal" => ("Mod One, Mod Two were not enabled: ", string.Empty),
        _ => (string.Empty, string.Empty),
    };

    public static List<Site> Scan(string? root = null)
    {
        var sites = new List<Site>();
        string src = root ?? SourceFolder();

        foreach (var path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(src, path).Replace('\\', '/');
            if (relative.Contains("/bin/") || relative.Contains("/obj/") || relative.Contains("/Testing/")) continue;

            string code = File.ReadAllText(path);
            var mask = CodeMask(code);

            foreach (Match match in Call.Matches(code))
            {
                if (!mask[match.Index]) continue;

                string name = match.Groups["name"].Value;
                string prefix = match.Value;

                // Task.Run is not a request. A save or load reports its steps on the panel's own
                // status line rather than in toasts.
                if (name == "Run" && prefix.StartsWith("Task.", StringComparison.Ordinal)) continue;
                if (name == "Report" && relative.EndsWith("/SaveFiles.cs", StringComparison.Ordinal)) continue;
                if (name.EndsWith("Exception", StringComparison.Ordinal) && !IsNew(code, match.Index)) continue;

                int open = match.Index + match.Length - 1;
                var arguments = Arguments(code, open);
                if (arguments.Count == 0) continue;

                IEnumerable<string> toastArguments = name switch
                {
                    // The first argument is the text; the second is its kind.
                    "AddToast" or "_toast" or "Report" or "NotAnsweredException" => arguments.Take(1),

                    // A connect step's words come after the step, an outcome's after the step too.
                    "say" => arguments.Skip(1).Take(1),
                    "Ps3ConnectOutcome" => arguments.Skip(2).Take(1),

                    // A hash lookup's problem, after the hash it did not find.
                    "ExecutableHashLookup" => arguments.Skip(1).Take(1),

                    // A request's success message: every argument after the work that is neither a
                    // lambda nor the quiet switch.
                    "Run" => arguments.Skip(1).Where(a => !a.Contains("=>") && !a.TrimStart().StartsWith("quiet", StringComparison.Ordinal)),

                    // The message after the step.
                    _ => arguments.Skip(1).Take(1),
                };

                int line = 1 + code.AsSpan(0, match.Index).Count('\n');
                var (before, after) = Around(name);
                foreach (string argument in toastArguments)
                {
                    foreach (string text in Texts(argument))
                    {
                        sites.Add(new Site($"{relative}:{line}", before + text + after));
                    }
                }
            }

            // (ok, message): what opening a folder or a link and the firewall helper answer, and a
            // toast as it stands.
            foreach (Match match in Outcome.Matches(code))
            {
                if (!mask[match.Index]) continue;

                int line = 1 + code.AsSpan(0, match.Index).Count('\n');
                foreach (string argument in Arguments(code, match.Index).Skip(1).Take(1))
                {
                    foreach (string text in Texts(argument))
                    {
                        sites.Add(new Site($"{relative}:{line}", text));
                    }
                }
            }

            foreach (Match match in Refusal.Matches(code))
            {
                if (!mask[match.Index]) continue;

                int line = 1 + code.AsSpan(0, match.Index).Count('\n');
                var (before, after) = Around("ModPatchRefusal");
                foreach (string argument in Arguments(code, match.Index).Skip(2).Take(1))
                {
                    foreach (string text in Texts(argument))
                    {
                        sites.Add(new Site($"{relative}:{line}", before + text + after));
                    }
                }
            }

            foreach (Match match in Assignment.Matches(code))
            {
                if (!mask[match.Index]) continue;

                // A declaration (string message = ...) is a local of some other purpose, such as a
                // confirmation's question; only an assignment to the toasted variable counts.
                int back = match.Index - 1;
                while (back >= 0 && code[back] is ' ' or '\t') back--;
                if (back >= 0 && (char.IsLetterOrDigit(code[back]) || code[back] is '?' or '_' or '>')) continue;

                string expression = UntilSemicolon(code, match.Index + match.Length);
                int line = 1 + code.AsSpan(0, match.Index).Count('\n');
                var (before, after) = Around(match.Groups["name"].Value);
                foreach (string text in Texts(expression))
                {
                    sites.Add(new Site($"{relative}:{line}", before + text + after));
                }
            }
        }

        return sites;
    }

    /// <summary>The expression from <paramref name="start"/> to the semicolon that ends its statement.</summary>
    private static string UntilSemicolon(string code, int start)
    {
        int depth = 0;
        int i = start;
        while (i < code.Length)
        {
            if (LiteralAt(code, i) is { } literal)
            {
                i = literal.End;
                continue;
            }

            char c = code[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            if (depth < 0 || (c == ';' && depth == 0)) break;
            i++;
        }

        return code[start..Math.Min(i, code.Length)];
    }

    private static bool IsNew(string code, int index)
    {
        int i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(code[i])) i--;
        return i >= 2 && code.Substring(i - 2, 3) == "new";
    }

    private static string SourceFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src");
            if (Directory.Exists(Path.Combine(candidate, "RaCMAN.App"))) return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("src/RaCMAN.App is not above the test's own folder");
    }

    // ------------------------------------------------------------------ lexing

    /// <summary>True for every character that is code, false inside comments and string literals.</summary>
    private static bool[] CodeMask(string code)
    {
        var mask = new bool[code.Length + 1];
        int i = 0;
        while (i < code.Length)
        {
            if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                while (i < code.Length && code[i] != '\n') i++;
                continue;
            }

            if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? code.Length : end + 2;
                continue;
            }

            if (LiteralAt(code, i) is { } literal)
            {
                i = literal.End;
                continue;
            }

            mask[i] = true;
            i++;
        }

        return mask;
    }

    private sealed record Literal(int Start, int End, string Text, bool HasHoles);

    /// <summary>The string or char literal that starts at <paramref name="i"/>, if one does.</summary>
    private static Literal? LiteralAt(string code, int i)
    {
        int start = i;
        bool interpolated = false, verbatim = false;
        while (i < code.Length && (code[i] == '$' || code[i] == '@'))
        {
            if (code[i] == '$') interpolated = true;
            else verbatim = true;
            i++;
        }

        if (i >= code.Length) return null;

        if (code[i] == '\'' && i == start)
        {
            int j = i + 1;
            while (j < code.Length && code[j] != '\'')
            {
                if (code[j] == '\\') j++;
                j++;
            }

            return new Literal(start, j + 1, string.Empty, false);
        }

        if (code[i] != '"') return null;
        if (i > start && start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_')) return null;

        // Raw strings: """ ... """, never used for a toast, skipped whole.
        if (i + 2 < code.Length && code[i + 1] == '"' && code[i + 2] == '"')
        {
            int end = code.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
            return new Literal(start, end < 0 ? code.Length : end + 3, string.Empty, false);
        }

        var text = new StringBuilder();
        bool holes = false;
        int k = i + 1;
        while (k < code.Length)
        {
            char c = code[k];
            if (verbatim && c == '"')
            {
                if (k + 1 < code.Length && code[k + 1] == '"')
                {
                    text.Append('"');
                    k += 2;
                    continue;
                }

                break;
            }

            if (!verbatim && c == '"') break;

            if (!verbatim && c == '\\')
            {
                char next = k + 1 < code.Length ? code[k + 1] : ' ';
                text.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
                k += 2;
                continue;
            }

            if (interpolated && c == '{')
            {
                if (k + 1 < code.Length && code[k + 1] == '{')
                {
                    text.Append('{');
                    k += 2;
                    continue;
                }

                int close = HoleEnd(code, k);
                text.Append(Representative(code.Substring(k + 1, close - k - 1)));
                holes = true;
                k = close + 1;
                continue;
            }

            if (interpolated && c == '}' && k + 1 < code.Length && code[k + 1] == '}')
            {
                text.Append('}');
                k += 2;
                continue;
            }

            text.Append(c);
            k++;
        }

        return new Literal(start, k + 1, text.ToString(), holes);
    }

    /// <summary>The index of the brace that closes the hole opening at <paramref name="open"/>.</summary>
    private static int HoleEnd(string code, int open)
    {
        int depth = 0;
        int i = open;
        while (i < code.Length)
        {
            if (i > open && LiteralAt(code, i) is { } literal)
            {
                i = literal.End;
                continue;
            }

            if (code[i] == '{') depth++;
            if (code[i] == '}' && --depth == 0) return i;
            i++;
        }

        return code.Length - 1;
    }

    /// <summary>The top-level arguments of the call whose parenthesis opens at <paramref name="open"/>.</summary>
    private static List<string> Arguments(string code, int open)
    {
        var arguments = new List<string>();
        int depth = 0;
        int start = open + 1;
        int i = open;
        while (i < code.Length)
        {
            if (LiteralAt(code, i) is { } literal)
            {
                i = literal.End;
                continue;
            }

            if (code[i] == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                while (i < code.Length && code[i] != '\n') i++;
                continue;
            }

            char c = code[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    string last = code.Substring(start, i - start);
                    if (last.Trim().Length > 0) arguments.Add(last);
                    return arguments;
                }
            }
            else if (c == ',' && depth == 1)
            {
                arguments.Add(code.Substring(start, i - start));
                start = i + 1;
            }

            i++;
        }

        return arguments;
    }

    /// <summary>
    /// The toasts one argument can produce: runs of literals joined by + are one text (a value
    /// between them counts as a representative value), anything else between two literals starts
    /// another text.
    /// </summary>
    private static IEnumerable<string> Texts(string argument)
    {
        var texts = new List<string>();
        StringBuilder? current = null;
        int afterLast = -1;
        int i = 0;
        while (i < argument.Length)
        {
            if (LiteralAt(argument, i) is { } literal && literal.End > literal.Start + 1 && IsStringStart(argument, i))
            {
                if (current is not null)
                {
                    string between = argument.Substring(afterLast, i - afterLast).Trim();
                    if (between.StartsWith('+'))
                    {
                        string middle = between.Trim('+', ' ', '\r', '\n', '\t');
                        if (middle.Length > 0) current.Append(Representative(middle));
                    }
                    else
                    {
                        texts.Add(current.ToString());
                        current = null;
                    }
                }

                current ??= new StringBuilder();
                current.Append(literal.Text);
                afterLast = literal.End;
                i = literal.End;
                continue;
            }

            i++;
        }

        if (current is not null)
        {
            // A value added after the last literal ("... failed: " + error) is part of the toast.
            string rest = argument[afterLast..].Trim();
            if (rest.StartsWith('+')) current.Append(Representative(rest.TrimStart('+').Trim()));
            texts.Add(current.ToString());
        }

        // A literal on its own that is a lookup key or a separator, not a sentence, says nothing.
        return texts.Where(text => text.Trim().Length > 0);
    }

    private static bool IsStringStart(string text, int i)
    {
        while (i < text.Length && (text[i] == '$' || text[i] == '@')) i++;
        return i < text.Length && text[i] == '"';
    }

    // ------------------------------------------------------------------ representative values

    /// <summary>A plausible value for what a hole, or a value joined in with +, holds.</summary>
    private static string Representative(string expression)
    {
        string hole = expression.Trim();

        // A format: 0x{address:X8} is that many digits, anything else ({seconds:0.#}) a short number.
        int colon = TopLevelColon(hole);
        if (colon > 0)
        {
            string format = hole[(colon + 1)..];
            hole = hole[..colon];
            var digits = Regex.Match(format, @"^[XxDd](\d+)$");
            return digits.Success ? new string('8', int.Parse(digits.Groups[1].Value)) : "30";
        }

        // A choice between words, "was" or "were": the longest of them.
        var words = new List<string>();
        for (int i = 0; i < hole.Length; i++)
        {
            if (LiteralAt(hole, i) is { } literal && IsStringStart(hole, i))
            {
                words.Add(literal.Text);
                i = literal.End - 1;
            }
        }

        if (hole.Contains('?') && words.Count > 0) return words.OrderByDescending(w => w.Length).First();

        string lower = hole.ToLowerInvariant();
        if (lower.Contains("message") || Regex.IsMatch(lower, @"^(error|problem|why|reason|failed|detail|diskproblem)$"))
        {
            return "The process cannot access the file in use"; // 41: an OS error, the usual size
        }

        if (lower.Contains("sprxname")) return "qwark.sprx";
        if (lower.Contains("uipak.filename")) return "ui.pak";
        if (lower.Contains("filename") || lower.EndsWith("name(game)")) return "rac2.sprx";
        if (lower.Contains("displayname()")) return "Deadlocked";
        if (lower.Contains("path") || lower.Contains("folder") || lower.Contains("filefor") || lower.Contains("dir")
            || lower is "staged" or "target" or "temporary" or "remotefolder")
        {
            return "/dev_hdd0/qwark/mods/racman";
        }

        if (lower.Contains("count") || lower.Contains("length") || lower.Contains("slot") || lower.Contains("planet")
            || lower.Contains("generation") || lower.Contains("version") || lower.Contains("updatestaged")
            || lower.Contains("size") || lower.Contains("total") || lower.Contains("bytes"))
        {
            return "1234";
        }

        if (lower.Contains("names") || lower.Contains("join")) return "Mod One, Mod Two";
        if (lower.Contains("game")) return "Deadlocked";

        // A name: a save, a mod, a preset, a category.
        return "Placeholder Name";
    }

    private static int TopLevelColon(string hole)
    {
        int depth = 0;
        for (int i = 0; i < hole.Length; i++)
        {
            if (LiteralAt(hole, i) is { } literal && IsStringStart(hole, i))
            {
                i = literal.End - 1;
                continue;
            }

            char c = hole[i];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == '?' && depth == 0) return -1;
            else if (c == ':' && depth == 0) return i;
        }

        return -1;
    }
}
