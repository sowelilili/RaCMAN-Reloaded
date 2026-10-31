using System.Numerics;
using System.Text;
using ImGuiNET;
using RaCMAN.Protocol;

namespace RaCMAN.App.Panels;

/// <summary>
/// The Lua console (revision 1.27, a console since 2026-10-31): the start of the Debug section for
/// a game whose LUA_STATUS answers, which today is ToD. A status line from LUA_STATUS, read about
/// once a second while the box is on screen; a multi-line editor with Lua's colours
/// (<see cref="LuaEditor"/>), in a box the user can make taller; Run, or Ctrl+Enter, which sends the
/// whole script as one LUA_EXEC and shows what came back under it; and the scripts' files, saved
/// by name into the data folder's <c>lua</c> folder and opened from there or from anywhere.
/// <para>
/// On a console the first LUA_EXEC of a boot writes qwark's helper into the game, so nothing here
/// ever sends one by itself: only Run does. LUA_STATUS is only a status, and is safe to poll.
/// A script past LUA_EXEC's 8192 bytes (PROTOCOL.md 5.15, <c>LUAMB_CHUNK_CAP</c>) is never sent:
/// the box says how long it is and Run waits until it fits.
/// </para>
/// </summary>
public static class LuaBox
{
    /// <summary>What a new editor holds: a line about the console, and a chunk that answers the game's Lua version.</summary>
    public const string StarterSource = "-- Runs in the game on your console. Ctrl+Enter or Run sends it.\nreturn _VERSION\n";

    /// <summary>How often the status line is read while the box is drawn.</summary>
    public const float PollSeconds = 1f;

    /// <summary>The editor's height before the user drags it, in lines of text.</summary>
    private const int StartLines = 14;

    private static LuaEditor? _editor;
    private static bool _running;
    private static string? _lastLine;
    private static LineKind _lastKind;
    private static string _lastOutput = string.Empty;
    private static float _sincePoll = PollSeconds;

    // The files.
    private static string _name = string.Empty;
    private static string _savedText = StarterSource;
    private static string _openPath = string.Empty;
    private static string[] _scripts = Array.Empty<string>();
    private static bool _listed;
    private static bool _dialogOpen;
    private static bool _overwriteArmed;
    private static (string Path, string Name)? _pendingOpen;

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

    /// <summary>
    /// Forgets the last answer: a different game, or everything read again. The script in the
    /// editor is the user's and stays.
    /// </summary>
    public static void Reset()
    {
        _running = false;
        _lastLine = null;
        _lastOutput = string.Empty;
        _sincePoll = PollSeconds;
        _listed = false;
        _overwriteArmed = false;
        _pendingOpen = null;
    }

    private static LuaEditor Editor
    {
        get
        {
            if (_editor is null)
            {
                _editor = new LuaEditor();
                _editor.SetText(StarterSource);
            }

            return _editor;
        }
    }

    // ---------------------------------------------------------------- what Run sends

    /// <summary>
    /// What Run does with a script: the source to send, or why nothing is sent. A script with
    /// nothing but white space in it has nothing to run, and one longer than LUA_EXEC carries
    /// (<see cref="LuaExecRequest.MaxSourceBytes"/> bytes of UTF-8) is refused here, with its
    /// length, rather than by the console.
    /// </summary>
    public static (string? Source, string? Problem) Prepare(string text)
    {
        text ??= string.Empty;
        if (text.Trim().Length == 0) return (null, "There is no Lua to run.");

        int bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > LuaExecRequest.MaxSourceBytes)
        {
            return (null, $"This script is {bytes} bytes. Your console runs at most {LuaExecRequest.MaxSourceBytes} bytes "
                          + "at a time: shorten it, or run it in parts.");
        }

        return (text, null);
    }

    /// <summary>
    /// One run, as Run makes it: the script checked (<see cref="Prepare"/>), sent as one LUA_EXEC,
    /// and the answer, a refusal or a failure turned into the line and text the box shows. A
    /// script that is not sent comes back as an error line without a request going out.
    /// </summary>
    public static async Task<(LineKind Kind, string Line, string Output)> RunAsync(QwarkClient client, string text, bool rpcs3)
    {
        var (source, problem) = Prepare(text);
        if (source is null) return (LineKind.Error, problem!, string.Empty);

        try
        {
            var result = await client.LuaExecAsync(source).ConfigureAwait(false);
            return Describe(result);
        }
        catch (QwarkStatusException ex)
        {
            return Refused(ex.Status, rpcs3);
        }
        catch (Exception ex)
        {
            return (LineKind.Error, $"LUA_EXEC failed: {ex.Message}", string.Empty);
        }
    }

    // ---------------------------------------------------------------- drawing

    public static void Draw(AppState state)
    {
        Ui.Heading("Lua console");

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
        DrawFiles(state);
        ImGui.Spacing();

        // The editor in a box of its own whose bottom edge drags it taller or shorter.
        var editor = Editor;
        float start = ImGui.GetTextLineHeight() * StartLines + ImGui.GetStyle().FramePadding.Y * 2;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.BeginChild("##lua-editor-box", new Vector2(-1, start), ImGuiChildFlags.Borders | ImGuiChildFlags.ResizeY,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PopStyleVar();
        if (editor.Draw(ImGui.GetContentRegionAvail()))
        {
            _overwriteArmed = false;
        }

        bool ctrlEnter = editor.RunRequested;
        ImGui.EndChild();

        var (source, problem) = Prepare(editor.Text);
        int bytes = Encoding.UTF8.GetByteCount(editor.Text);

        ImGui.BeginDisabled(_running || !state.Connected || source is null);
        bool run = ImGui.Button("Run");
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            Ui.SetTooltip(!state.Connected ? "Connect to your console first"
                : problem ?? "Runs the whole script in the game (Ctrl+Enter in the editor does the same)");
        }

        ImGui.SameLine();
        var counter = bytes > LuaExecRequest.MaxSourceBytes ? Ui.Red : Ui.Grey;
        Ui.Text(counter, $"{bytes} / {LuaExecRequest.MaxSourceBytes} bytes    line {editor.CursorLine}, column {editor.CursorColumn}");

        if ((run || ctrlEnter) && !_running && state.Connected)
        {
            if (source is not null) Send(state, source);
            else if (ctrlEnter) Show(LineKind.Error, problem!, string.Empty);
        }
        else if (ctrlEnter && !state.Connected)
        {
            Show(LineKind.Warning, "Connect to your console first.", string.Empty);
        }

        if (bytes > LuaExecRequest.MaxSourceBytes) Ui.Error(problem!);

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

    private static void Show(LineKind kind, string line, string output)
    {
        _lastKind = kind;
        _lastLine = line;
        _lastOutput = output;
    }

    /// <summary>One LUA_EXEC, off the render thread; the answer comes back through the queue.</summary>
    private static void Send(AppState state, string source)
    {
        _running = true;
        var client = state.Client;
        bool rpcs3 = state.CodePatchesUnsupported;
        _ = Task.Run(async () =>
        {
            var (kind, line, output) = await RunAsync(client, source, rpcs3).ConfigureAwait(false);
            state.Post(() =>
            {
                _running = false;
                Show(kind, line, output);

                // What the chunk did to the helper, and any error it made the game report.
                _sincePoll = PollSeconds;
            });
        });
    }

    // ---------------------------------------------------------------- the files

    /// <summary>True while the editor holds something that is not what was last saved or opened.</summary>
    public static bool Modified => _editor is not null && !string.Equals(_editor.Text, _savedText, StringComparison.Ordinal);

    private static void DrawFiles(AppState state)
    {
        var store = state.LuaScripts;
        if (!_listed) Relist(state);

        // Save: a name, into the lua folder.
        ImGui.SetNextItemWidth(180);
        bool enter = Ui.SubmitTextWithHint("##lua-name", "Script name", ref _name, 64);
        if (ImGui.IsItemEdited()) _overwriteArmed = false;
        ImGui.SameLine();
        string cleaned = LuaScriptStore.CleanName(_name);
        if (_overwriteArmed)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
            if (ImGui.Button($"Replace {cleaned}.lua")) Save(state, overwrite: true);
            ImGui.PopStyleColor();
            ImGui.SameLine();
            if (ImGui.Button("Cancel##lua-save")) _overwriteArmed = false;
        }
        else
        {
            ImGui.BeginDisabled(cleaned.Length == 0);
            if (ImGui.Button("Save") || (enter && cleaned.Length > 0)) Save(state, overwrite: false);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                Ui.SetTooltip(cleaned.Length == 0 ? "Type a name for the script first" : $"Saves the editor to {store.FileFor(cleaned)}");
            }
        }

        if (Modified)
        {
            ImGui.SameLine();
            Ui.Text(Ui.Grey, "(not saved)");
        }

        // Open: a saved script, or any .lua file.
        string preview = _scripts.Length == 0 ? "(none saved)" : "Open a saved script";
        ImGui.SetNextItemWidth(180);
        ImGui.BeginDisabled(_scripts.Length == 0);
        if (ImGui.BeginCombo("##lua-saved", preview))
        {
            foreach (var script in _scripts)
            {
                if (ImGui.Selectable(script)) AskOpen(state, Path.Combine(store.Folder, script + LuaScriptStore.Extension), script);
            }

            ImGui.EndCombo();
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) Ui.SetTooltip("The scripts in your Lua scripts folder");

        ImGui.SameLine();
        ImGui.BeginDisabled(!FileDialog.IsSupported || _dialogOpen);
        if (ImGui.Button("Open file...")) Browse(state);
        ImGui.EndDisabled();
        ImGui.SameLine();
        Ui.OpenFolderButton(state, store.Folder);

        // Where there is no dialog to show, a path box does its job.
        if (!FileDialog.IsSupported)
        {
            ImGui.SetNextItemWidth(-90);
            bool open = Ui.SubmitTextWithHint("##lua-path", "Path of a .lua file", ref _openPath, 512);
            ImGui.SameLine();
            if (ImGui.Button("Open##lua-path") || open)
            {
                string path = _openPath.Trim().Trim('"').Trim();
                if (path.Length > 0) AskOpen(state, path, Path.GetFileNameWithoutExtension(path));
            }
        }

        if (_pendingOpen is { } pending)
        {
            Ui.Warning($"The editor has changes that are not saved. Open {Path.GetFileName(pending.Path)} anyway?");
            if (ImGui.Button("Open, and lose the changes")) Open(state, pending.Path, pending.Name);
            ImGui.SameLine();
            if (ImGui.Button("Keep editing")) _pendingOpen = null;
        }
    }

    private static void Relist(AppState state)
    {
        _listed = true;
        try
        {
            _scripts = state.LuaScripts.List().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _scripts = Array.Empty<string>();
            state.AddToast($"Lua scripts: {ex.Message}", ToastKind.Error);
        }
    }

    private static void Save(AppState state, bool overwrite)
    {
        var store = state.LuaScripts;
        string cleaned = LuaScriptStore.CleanName(_name);
        if (cleaned.Length == 0) return;

        try
        {
            string path = store.FileFor(cleaned);
            if (!overwrite && File.Exists(path) && !string.Equals(_savedName, cleaned, StringComparison.OrdinalIgnoreCase))
            {
                _overwriteArmed = true;
                return;
            }

            string text = Editor.Text;
            store.Save(cleaned, text);
            _name = cleaned;
            _savedName = cleaned;
            _savedText = text;
            _overwriteArmed = false;
            state.AddToast($"Lua script saved to {path}", ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _overwriteArmed = false;
            state.AddToast($"Lua script save failed: {ex.Message}", ToastKind.Error);
        }

        Relist(state);
    }

    /// <summary>The name the editor's text was last saved or opened under, for the overwrite question.</summary>
    private static string? _savedName;

    private static void AskOpen(AppState state, string path, string name)
    {
        if (Modified)
        {
            _pendingOpen = (path, name);
            return;
        }

        Open(state, path, name);
    }

    private static void Open(AppState state, string path, string name)
    {
        _pendingOpen = null;
        try
        {
            string text = LuaScriptStore.Read(path);
            Editor.SetText(text);
            _savedText = Editor.Text;
            _name = LuaScriptStore.CleanName(name);

            // A file from elsewhere is saved into the lua folder under its name, so a script in the
            // folder already called that is a replacement worth asking about.
            bool inFolder = string.Equals(Path.GetFullPath(Path.GetDirectoryName(path) ?? string.Empty),
                Path.GetFullPath(state.LuaScripts.Folder), StringComparison.OrdinalIgnoreCase);
            _savedName = inFolder ? _name : null;
            _overwriteArmed = false;
            int bytes = Encoding.UTF8.GetByteCount(text);
            state.AddToast(bytes > LuaExecRequest.MaxSourceBytes
                ? $"Opened {Path.GetFileName(path)}: {bytes} bytes, more than your console runs at a time"
                : $"Opened {Path.GetFileName(path)}", bytes > LuaExecRequest.MaxSourceBytes ? ToastKind.Error : ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            state.AddToast($"Could not open {path}: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>
    /// The platform's own open dialog, off the render thread (it is modal); the path comes back
    /// through <see cref="AppState.Post"/>, as the Mods panel's ZIP install does it.
    /// </summary>
    private static void Browse(AppState state)
    {
        _dialogOpen = true;
        _ = Task.Run(async () =>
        {
            try
            {
                string? picked = await FileDialog.OpenAsync("Open a Lua script", "Lua scripts", "lua").ConfigureAwait(false);
                state.Post(() =>
                {
                    _dialogOpen = false;
                    if (picked is not null) AskOpen(state, picked, Path.GetFileNameWithoutExtension(picked));
                });
            }
            catch (Exception ex)
            {
                state.Post(() =>
                {
                    _dialogOpen = false;
                    state.AddToast($"File dialog failed: {ex.Message}", ToastKind.Error);
                });
            }
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
