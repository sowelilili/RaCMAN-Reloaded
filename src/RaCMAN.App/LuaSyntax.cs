namespace RaCMAN.App;

/// <summary>
/// What the Lua console colours, and how (2026-10-31): a line of Lua 5.0 cut into words, each with
/// its kind. No parser, only what a reader's eye wants told apart: the reserved words, the standard
/// globals, numbers, strings and comments. Long brackets (<c>[[ ... ]]</c>, <c>--[[ ... ]]</c> and
/// their <c>[==[</c> forms) run over lines, so each line starts in the state the one before it
/// left, which <see cref="Tokens"/> takes and hands back.
/// </summary>
public static class LuaSyntax
{
    /// <summary>What a run of a line is coloured as.</summary>
    public enum Kind
    {
        Code,
        Keyword,
        Builtin,
        Number,
        String,
        Comment,
    }

    /// <summary>
    /// Where a line starts: in plain code, or inside a long comment or long string opened on a line
    /// before it, with the number of '=' its bracket had (Lua 5.0 itself has only <c>[[</c>; the
    /// level form is 5.1's, and colouring it the same costs nothing).
    /// </summary>
    public readonly record struct State(Kind Inside, int Level)
    {
        public static readonly State Plain = new(Kind.Code, 0);

        public bool InLong => Inside is Kind.Comment or Kind.String;
    }

    /// <summary>Lua 5.0's reserved words.</summary>
    public static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in", "local",
        "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
    };

    /// <summary>The standard library's globals the game's Lua 5.0.2 has, and <c>_VERSION</c>.</summary>
    public static readonly IReadOnlySet<string> Builtins = new HashSet<string>(StringComparer.Ordinal)
    {
        "_G", "_VERSION", "assert", "collectgarbage", "dofile", "error", "gcinfo", "getfenv", "getmetatable",
        "ipairs", "loadfile", "loadstring", "next", "pairs", "pcall", "print", "rawequal", "rawget", "rawset",
        "require", "setfenv", "setmetatable", "tonumber", "tostring", "type", "unpack", "xpcall",
        "coroutine", "debug", "io", "math", "os", "string", "table",
    };

    /// <summary>
    /// The runs of one line (no line break in it), each with its kind, in order and covering every
    /// character that is not white space between words. <paramref name="state"/> is where the line
    /// starts and comes back as where the next one does.
    /// </summary>
    public static List<(int Start, int Length, Kind Kind)> Tokens(string line, ref State state)
    {
        var runs = new List<(int, int, Kind)>();
        int i = 0;
        int n = line.Length;

        // The rest of a long comment or string from the line before.
        if (state.InLong)
        {
            int close = LongClose(line, 0, state.Level);
            if (close < 0)
            {
                if (n > 0) runs.Add((0, n, state.Inside));
                return runs;
            }

            runs.Add((0, close, state.Inside));
            i = close;
            state = State.Plain;
        }

        while (i < n)
        {
            char c = line[i];

            if (c is ' ' or '\t')
            {
                i++;
                continue;
            }

            // Comments: "--" to the end of the line, or a long bracket right after it.
            if (c == '-' && i + 1 < n && line[i + 1] == '-')
            {
                int level = LongOpen(line, i + 2);
                if (level >= 0)
                {
                    int bodyStart = i + 2 + level + 2;
                    int close = LongClose(line, bodyStart, level);
                    if (close < 0)
                    {
                        runs.Add((i, n - i, Kind.Comment));
                        state = new State(Kind.Comment, level);
                        return runs;
                    }

                    runs.Add((i, close - i, Kind.Comment));
                    i = close;
                    continue;
                }

                runs.Add((i, n - i, Kind.Comment));
                return runs;
            }

            // Long strings.
            if (c == '[')
            {
                int level = LongOpen(line, i);
                if (level >= 0)
                {
                    int close = LongClose(line, i + level + 2, level);
                    if (close < 0)
                    {
                        runs.Add((i, n - i, Kind.String));
                        state = new State(Kind.String, level);
                        return runs;
                    }

                    runs.Add((i, close - i, Kind.String));
                    i = close;
                    continue;
                }
            }

            // Quoted strings, with their escapes; one left open ends with the line, as Lua says.
            if (c is '"' or '\'')
            {
                int j = i + 1;
                while (j < n && line[j] != c)
                {
                    j += line[j] == '\\' ? 2 : 1;
                }

                j = Math.Min(n, j + 1);
                runs.Add((i, j - i, Kind.String));
                i = j;
                continue;
            }

            // Numbers: digits, a decimal point, an exponent, and 0x hex (which the game's 5.0.2 reads
            // through strtod).
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(line[i + 1])))
            {
                int j = i + 1;
                while (j < n)
                {
                    char d = line[j];
                    if (char.IsAsciiLetterOrDigit(d) || d == '.' || d == '_') j++;
                    else if ((d is '+' or '-') && (line[j - 1] is 'e' or 'E') && !IsHex(line, i)) j++;
                    else break;
                }

                runs.Add((i, j - i, Kind.Number));
                i = j;
                continue;
            }

            // Names.
            if (char.IsAsciiLetter(c) || c == '_')
            {
                int j = i + 1;
                while (j < n && (char.IsAsciiLetterOrDigit(line[j]) || line[j] == '_')) j++;
                string word = line[i..j];

                // A field (t.print, obj:type) is the table's, not the standard global.
                bool field = i > 0 && line[i - 1] is '.' or ':' && !(i > 1 && line[i - 2] == '.');
                var kind = Keywords.Contains(word) ? Kind.Keyword
                    : !field && Builtins.Contains(word) ? Kind.Builtin
                    : Kind.Code;
                runs.Add((i, j - i, kind));
                i = j;
                continue;
            }

            // Anything else, operators and punctuation, one run up to the next thing that starts a word.
            int k = i + 1;
            while (k < n && !StartsSomething(line, k)) k++;
            runs.Add((i, k - i, Kind.Code));
            i = k;
        }

        return runs;
    }

    /// <summary>The state each line of a whole text starts in, from the first line's <see cref="State.Plain"/>.</summary>
    public static State[] LineStates(IReadOnlyList<string> lines)
    {
        var states = new State[lines.Count];
        var state = State.Plain;
        for (int i = 0; i < lines.Count; i++)
        {
            states[i] = state;
            Tokens(lines[i], ref state);
        }

        return states;
    }

    private static bool IsHex(string line, int start) =>
        start + 1 < line.Length && line[start] == '0' && line[start + 1] is 'x' or 'X';

    private static bool StartsSomething(string line, int at)
    {
        char c = line[at];
        return c is ' ' or '\t' or '"' or '\'' or '[' or '_'
               || char.IsAsciiLetterOrDigit(c)
               || (c == '-' && at + 1 < line.Length && line[at + 1] == '-');
    }

    /// <summary>The level of a long bracket opening at <paramref name="at"/> ("[[", "[=[", ...), or -1 for none.</summary>
    private static int LongOpen(string line, int at)
    {
        if (at >= line.Length || line[at] != '[') return -1;
        int j = at + 1;
        while (j < line.Length && line[j] == '=') j++;
        return j < line.Length && line[j] == '[' ? j - at - 1 : -1;
    }

    /// <summary>The index just past the closing bracket of the given level from <paramref name="from"/> on, or -1.</summary>
    private static int LongClose(string line, int from, int level)
    {
        string close = "]" + new string('=', level) + "]";
        int at = line.IndexOf(close, Math.Min(from, line.Length), StringComparison.Ordinal);
        return at < 0 ? -1 : at + close.Length;
    }
}
