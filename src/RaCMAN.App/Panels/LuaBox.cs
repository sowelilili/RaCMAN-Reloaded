using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The Lua box (revision 1.27): the start of the Debug section for a game whose LUA_STATUS
/// answers, which today is ToD. Two preset buttons, a one-line source box with Run, the last
/// LUA_EXEC's status and text, and a status line from LUA_STATUS, read about once a second while
/// the box is on screen. Every button sends one LUA_EXEC and shows what came back.
/// <para>
/// On a console the first LUA_EXEC of a boot writes qwark's helper into the game, so nothing here
/// ever sends one by itself: only a press does. LUA_STATUS is only a status, and is safe to poll.
/// </para>
/// </summary>
public static class LuaBox
{
    /// <summary>The first preset: half speed, and the answer is "Lua 5.0.2".</summary>
    public const string HalfSpeedSource = "time_set_scale(0.5) return _VERSION";

    /// <summary>The second preset: back to normal speed.</summary>
    public const string NormalSpeedSource = "time_set_scale(1) return _VERSION";

    /// <summary>How often the status line is read while the box is drawn.</summary>
    public const float PollSeconds = 1f;

    private static string _source = string.Empty;
    private static bool _running;
    private static string? _lastLine;
    private static LineKind _lastKind;
    private static string _lastOutput = string.Empty;
    private static float _sincePoll = PollSeconds;

    /// <summary>How a line is coloured.</summary>
    public enum LineKind
    {
        Ok,
        Warning,
        Error,
        Hint,
    }

    /// <summary>The last run's line, for the summary of a headless run; empty before one.</summary>
    public static string LastSummary => _lastLine ?? string.Empty;

    /// <summary>Forgets the last answer: a different game, or everything read again.</summary>
    public static void Reset()
    {
        _running = false;
        _lastLine = null;
        _lastOutput = string.Empty;
        _sincePoll = PollSeconds;
    }

    public static void Draw(AppState state)
    {
        Ui.Heading("Lua");

        _sincePoll += ImGui.GetIO().DeltaTime;
        if (_sincePoll >= PollSeconds)
        {
            _sincePoll = 0;
            state.RefreshLuaStatus();
        }

        foreach (var (kind, text) in StatusLines(state.Lua, state.LuaStatusRefusal, state.CodePatchesUnsupported))
        {
            DrawLine(kind, text);
        }

        ImGui.Spacing();
        ImGui.BeginDisabled(_running || !state.Connected);
        if (ImGui.Button("Lua test (half speed)")) Send(state, HalfSpeedSource);
        if (ImGui.IsItemHovered()) Ui.SetTooltip(HalfSpeedSource);

        ImGui.SameLine();
        if (ImGui.Button("Normal speed")) Send(state, NormalSpeedSource);
        if (ImGui.IsItemHovered()) Ui.SetTooltip(NormalSpeedSource);

        ImGui.SetNextItemWidth(Math.Max(200, ImGui.GetContentRegionAvail().X - 80));
        bool enter = Ui.SubmitTextWithHint("##lua-source", "Lua source, e.g. return _VERSION", ref _source,
            LuaExecRequest.MaxSourceBytes);
        ImGui.SameLine();
        bool run = ImGui.Button("Run");
        if ((run || enter) && _source.Trim().Length > 0) Send(state, _source);
        ImGui.EndDisabled();

        if (_running)
        {
            Ui.Hint("Waiting for the game to run it...");
        }
        else if (_lastLine is { } line)
        {
            DrawLine(_lastKind, line);
            if (_lastOutput.Length > 0) Ui.Paragraph(_lastOutput);
        }
    }

    private static void DrawLine(LineKind kind, string text)
    {
        switch (kind)
        {
            case LineKind.Ok: Ui.Success(text); break;
            case LineKind.Warning: Ui.Warning(text); break;
            case LineKind.Error: Ui.Error(text); break;
            default: Ui.Hint(text); break;
        }
    }

    /// <summary>One LUA_EXEC, off the render thread; the answer comes back through the queue.</summary>
    private static void Send(AppState state, string source)
    {
        _running = true;
        var client = state.Client;
        _ = Task.Run(async () =>
        {
            LuaExecResult? result = null;
            Status? refusal = null;
            string? failure = null;
            try
            {
                result = await client.LuaExecAsync(source).ConfigureAwait(false);
            }
            catch (QwarkStatusException ex)
            {
                refusal = ex.Status;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }

            state.Post(() =>
            {
                _running = false;
                var (kind, line, output) = result is not null ? Describe(result)
                    : refusal is { } status ? Refused(status, state.CodePatchesUnsupported)
                    : (LineKind.Error, $"LUA_EXEC failed: {failure}", string.Empty);
                _lastKind = kind;
                _lastLine = line;
                _lastOutput = output;

                // What the chunk did to the helper, and any error it made the game report.
                _sincePoll = PollSeconds;
            });
        });
    }

    /// <summary>The line and text for an answered LUA_EXEC: its status by name, then the first result or the message.</summary>
    public static (LineKind Kind, string Line, string Output) Describe(LuaExecResult result)
    {
        int code = (int)result.Status;
        string number = code is >= 0 and < 0x100 ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"0x{code:X}";
        string cut = result.Truncated ? " (cut short)" : string.Empty;

        if (result.Ok)
        {
            string value = result.OutType switch
            {
                LuaType.None => "no result",
                LuaType.Boolean => (result.OutFlags & LuaOutFlags.True) != 0 ? "boolean: true" : "boolean: false",
                _ => $"{result.OutType.DisplayName()}{cut}",
            };
            return (LineKind.Ok, $"Last run: OK ({number}), {value}.", result.OutType == LuaType.Boolean ? string.Empty : result.Output);
        }

        var kind = result.Status is LuaExecStatus.Timeout or LuaExecStatus.NotRunning or LuaExecStatus.Cancelled
            ? LineKind.Warning
            : LineKind.Error;
        string why = result.Status switch
        {
            LuaExecStatus.Timeout => " The game did not answer in time; the chunk was withdrawn and will not run.",
            LuaExecStatus.NotRunning => " No gameplay frame ran (paused, a menu or a load); the chunk was withdrawn. Unpause and try again.",
            LuaExecStatus.Hung => " The game hung inside the chunk.",
            LuaExecStatus.NoState => " The game has no lua_State right now.",
            _ => string.Empty,
        };
        return (kind, $"Last run: {result.Status.DisplayName()} ({number}).{why}{cut}", result.Output);
    }

    /// <summary>The line for a LUA_EXEC the console refused, with what to do about it.</summary>
    public static (LineKind Kind, string Line, string Output) Refused(Status status, bool rpcs3)
    {
        string why = status switch
        {
            Status.Busy => " Another request is still out, or qwark is still looking for the helper. Try again.",
            Status.NotFound => " The game has no lua_State: it is not in a level (the main menu or a load).",
            Status.NotIngame => " The game is not running.",
            Status.Unsupported => rpcs3
                ? " The Lua helper is not in the game. Install its RPCS3 patch on the Connection panel, then restart the game."
                : " This game has no Lua helper.",
            _ => string.Empty,
        };
        var kind = status is Status.Busy or Status.NotFound or Status.NotIngame ? LineKind.Warning : LineKind.Error;
        return (kind, $"Last run: LUA_EXEC answered {status.WireName()}.{why}", string.Empty);
    }

    /// <summary>The status line from LUA_STATUS: the helper, the heartbeat, and the game's last script error.</summary>
    public static IReadOnlyList<(LineKind Kind, string Text)> StatusLines(LuaStatusInfo? info, Status? refusal, bool rpcs3)
    {
        var lines = new List<(LineKind, string)>();
        if (refusal is { } status)
        {
            lines.Add((LineKind.Warning, $"LUA_STATUS answered {status.WireName()}."));
        }

        if (info is null)
        {
            if (refusal is null) lines.Add((LineKind.Hint, "Asking qwark about the Lua helper..."));
            return lines;
        }

        switch (info.Installed)
        {
            case LuaInstalled.Yes:
                string lua = info.State == 0 ? "no lua_State (not in a level)" : $"lua_State 0x{info.State:X8}";
                string phase = info.Phase switch
                {
                    LuaPhase.Waiting => ", waiting for an answer",
                    LuaPhase.Withdrawing => ", withdrawing a request",
                    LuaPhase.Hung => ", the game hung inside a chunk",
                    _ => string.Empty,
                };
                lines.Add((info.Phase == LuaPhase.Hung ? LineKind.Error : LineKind.Ok,
                    $"Helper installed. Heartbeat {info.Heartbeat}, {lua}{phase}."));
                break;

            case LuaInstalled.Checking:
                lines.Add((LineKind.Hint, "qwark is still checking whether the Lua helper is in the game..."));
                break;

            default:
                lines.Add(rpcs3
                    ? (LineKind.Warning, "Helper not in the game. Under RPCS3 it is an RPCS3 patch: install it on the "
                                         + "Connection panel, then restart the game.")
                    : (LineKind.Hint, "Helper not installed yet. The first Run puts it in the game."));
                break;
        }

        if (info.ErrorSeq > 0)
        {
            string script = info.ErrorScript.Length > 0 ? info.ErrorScript : "a script";
            lines.Add((LineKind.Hint, $"Game script errors: {info.ErrorSeq}. Last, in {script}: {info.Error}"));
        }

        return lines;
    }
}
