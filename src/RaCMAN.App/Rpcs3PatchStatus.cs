using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>
/// qwark's own patches for a game under RPCS3. Both are the game's rather than the user's, both go
/// into the same patch file as RPCS3 patches, and the Connection panel installs them together.
/// </summary>
public enum QwarkPatchPart
{
    /// <summary>The savefile helper (build 47): saving and loading through the client.</summary>
    SaveFileHelper,

    /// <summary>The code switches (build 50): the features that patch game code, toggled through flag bytes.</summary>
    CodeSwitches,
}

/// <summary>What each of qwark's patches is called, where it is filed, and how to say it.</summary>
public static class QwarkPatchParts
{
    public static readonly IReadOnlyList<QwarkPatchPart> All = new[] { QwarkPatchPart.SaveFileHelper, QwarkPatchPart.CodeSwitches };

    /// <summary>The description the part is filed under, in the patch file and in RPCS3's Patch Manager.</summary>
    public static string Description(this QwarkPatchPart part) =>
        part == QwarkPatchPart.SaveFileHelper ? Rpcs3Patches.Description : Rpcs3Patches.SwitchesDescription;

    /// <summary>What the Connection panel's line about the part begins with.</summary>
    public static string Title(this QwarkPatchPart part) =>
        part == QwarkPatchPart.SaveFileHelper ? "Savefile helper" : "Code switches";

    /// <summary>The part in the middle of a sentence.</summary>
    public static string Noun(this QwarkPatchPart part) =>
        part == QwarkPatchPart.SaveFileHelper ? "the savefile helper" : "the code switches";

    /// <summary>What the part is for, in the confirmation's list.</summary>
    public static string Purpose(this QwarkPatchPart part) =>
        part == QwarkPatchPart.SaveFileHelper
            ? "saving and loading save files from RaCMAN"
            : "the cheats that patch game code, such as fast loads and infinite ammo";

    /// <summary>"qwark's savefile helper", "qwark's code switches", or "qwark's savefile helper and code switches".</summary>
    public static string Names(IReadOnlyCollection<QwarkPatchPart> parts)
    {
        bool helper = parts.Contains(QwarkPatchPart.SaveFileHelper);
        bool switches = parts.Contains(QwarkPatchPart.CodeSwitches);
        return helper && switches ? "qwark's savefile helper and code switches"
            : switches ? "qwark's code switches"
            : "qwark's savefile helper";
    }
}

/// <summary>Where one of qwark's patches stands for the game running under RPCS3.</summary>
public enum QwarkPatchState
{
    /// <summary>Nothing to say: not an RPCS3 session, or a qwark that patches the game itself.</summary>
    Hidden,

    /// <summary>Waiting for a connection, a game, qwark's words or the look at RPCS3's folder.</summary>
    Waiting,

    /// <summary>qwark finds the patch in the game: what it is for works.</summary>
    Active,

    /// <summary>The patch is in RPCS3 and switched on; the game has to start again to take it.</summary>
    RestartGame,

    /// <summary>Not installed, out of date or switched off: the install is offered.</summary>
    Install,

    /// <summary>Cannot be installed from here, and <see cref="QwarkPatchStatus.Message"/> says why.</summary>
    Unavailable,

    /// <summary>qwark has no such patch for this game, so the Connection panel leaves it out.</summary>
    Absent,
}

/// <summary>What the Connection panel says about one of qwark's patches, and whether it can be installed.</summary>
public sealed record QwarkPatchStatus(QwarkPatchState State, string Message, bool FolderProblem = false)
{
    public static readonly QwarkPatchStatus Hidden = new(QwarkPatchState.Hidden, string.Empty);

    public bool CanInstall => State == QwarkPatchState.Install;
}

/// <summary>One line of the Connection panel's status, coloured by its state.</summary>
public sealed record QwarkPatchLine(QwarkPatchState State, string Text);

/// <summary>
/// Both of qwark's patches, as the Connection panel reports them. A reason that holds for both —
/// no connection, no game, no RPCS3 folder — is said once; otherwise each part has a line of its
/// own that begins with its name. A part the game does not have is left out, so a game with only
/// one of them reports that one alone.
/// </summary>
public sealed record QwarkPatchesStatus(QwarkPatchStatus Helper, QwarkPatchStatus Switches)
{
    public static readonly QwarkPatchesStatus Hidden = new(QwarkPatchStatus.Hidden, QwarkPatchStatus.Hidden);

    public QwarkPatchStatus For(QwarkPatchPart part) => part == QwarkPatchPart.SaveFileHelper ? Helper : Switches;

    /// <summary>The parts there is something to say about: neither hidden nor absent from the game.</summary>
    public IReadOnlyList<QwarkPatchPart> Shown =>
        QwarkPatchParts.All.Where(part => For(part).State is not (QwarkPatchState.Hidden or QwarkPatchState.Absent)).ToList();

    /// <summary>The parts the install would write: each one not installed, out of date or switched off.</summary>
    public IReadOnlyList<QwarkPatchPart> Installable => Shown.Where(part => For(part).CanInstall).ToList();

    public bool CanInstall => Installable.Count > 0;

    /// <summary>Whether the RPCS3 folder setting is the fix for what a line says.</summary>
    public bool FolderProblem => Shown.Any(part => For(part).FolderProblem);

    /// <summary>What the panel draws, one line each.</summary>
    public IReadOnlyList<QwarkPatchLine> Lines
    {
        get
        {
            if (Helper.State == QwarkPatchState.Hidden && Switches.State == QwarkPatchState.Hidden) return Array.Empty<QwarkPatchLine>();

            var shown = Shown;
            if (shown.Count == 0)
            {
                return new[] { new QwarkPatchLine(QwarkPatchState.Unavailable, "qwark has no patches for this game, so there is nothing to install.") };
            }

            if (shown.Count == 2 && Helper == Switches) return new[] { new QwarkPatchLine(Helper.State, Helper.Message) };

            return shown.Select(part => new QwarkPatchLine(For(part).State, $"{part.Title()}: {For(part).Message}")).ToList();
        }
    }

    /// <summary>The lines as one, for a toast, a test or the summary of a headless run.</summary>
    public string Message => string.Join(" ", Lines.Select(line => line.Text));

    /// <summary>
    /// The one state that sums both up, by what the user has to do next: install, restart the game,
    /// wait, read why not, or nothing at all.
    /// </summary>
    public QwarkPatchState State
    {
        get
        {
            if (Lines.Count == 0) return QwarkPatchState.Hidden;

            var states = Shown.Select(part => For(part).State).ToList();
            if (states.Count == 0) return QwarkPatchState.Unavailable;

            foreach (var state in new[] { QwarkPatchState.Install, QwarkPatchState.RestartGame, QwarkPatchState.Waiting, QwarkPatchState.Unavailable })
            {
                if (states.Contains(state)) return state;
            }

            return QwarkPatchState.Active;
        }
    }
}

public enum QwarkPatchReplyKind
{
    NotAsked,
    Pending,
    Ok,

    /// <summary>UNSUPPORTED: the game has no such patch, or it is switched off.</summary>
    NotSupported,

    /// <summary>UNKNOWN_OP: a qwark from before the op (SAVEFILE_PATCH in build 47, SWITCH_PATCH in build 50).</summary>
    TooOld,

    /// <summary>Anything else, with the reason.</summary>
    Failed,
}

/// <summary>What qwark said to SAVEFILE_PATCH or SWITCH_PATCH for the running game.</summary>
public sealed record QwarkPatchReply(QwarkPatchReplyKind Kind, PatchReply? Patch = null, string Problem = "")
{
    public static readonly QwarkPatchReply NotAsked = new(QwarkPatchReplyKind.NotAsked);

    public static readonly QwarkPatchReply Pending = new(QwarkPatchReplyKind.Pending);

    public static QwarkPatchReply Ok(PatchReply patch) => new(QwarkPatchReplyKind.Ok, patch);
}

/// <summary>
/// What the session says, as far as qwark's patches are concerned: SAVEFILE_INFO's
/// <c>installed</c> for the helper, and flags.CODE_SWITCHES for the switches.
/// </summary>
public sealed record QwarkPatchSession(
    bool Connected,
    bool CodePatchesUnsupported,
    bool Ingame,
    bool KnownGame,
    string TitleId,
    byte QwarkBuild,
    SaveFileInfo Info,
    bool CodeSwitches = false)
{
    /// <summary>Whether qwark finds the part in the game this session.</summary>
    public bool IsActive(QwarkPatchPart part) => part == QwarkPatchPart.SaveFileHelper ? Info.Installed : CodeSwitches;
}

/// <summary>
/// What RPCS3's folder says about the running game's patches: where the folder is, the executable's
/// hash, what the title's patch file holds and which of its entries for that executable
/// patch_config.yml switches on. <see cref="Enabled"/> is the savefile helper's switch and
/// <see cref="EnabledEntries"/> every one of them, by description. Each step is only taken when the
/// one before it worked, and each one that did not says why.
/// </summary>
public sealed record Rpcs3PatchDisk(
    string TitleId,
    Rpcs3FolderLookup Folder,
    ExecutableHashLookup? Hash,
    PatchFileState? File,
    bool Enabled,
    string Problem,
    IReadOnlyList<string>? EnabledEntries = null)
{
    /// <summary>Whether patch_config.yml switches on the running executable's entry filed under <paramref name="description"/>.</summary>
    public bool IsEnabled(string description) =>
        EnabledEntries is not null
            ? EnabledEntries.Contains(description, StringComparer.Ordinal)
            : Enabled && description == Rpcs3Patches.Description;

    /// <summary>The running executable's mod entries that patch_config.yml switches on: what RPCS3 applies at the next boot besides qwark's.</summary>
    public IReadOnlyList<PatchFileEntry> EnabledMods()
    {
        if (Hash?.Hash is not { } hash || File is not { Kind: PatchFileKind.Ours } file) return Array.Empty<PatchFileEntry>();
        return file.EntriesFor(hash).Where(entry => entry.ModDir is not null && IsEnabled(entry.Description)).ToList();
    }

    public static Rpcs3PatchDisk Inspect(string? overrideFolder, Rpcs3Environment environment, string titleId)
    {
        var located = Rpcs3Patches.Locate(overrideFolder, environment);
        if (located.Folder is not { } folder) return new Rpcs3PatchDisk(titleId, located, null, null, false, string.Empty);

        var hash = Rpcs3Patches.FindExecutableHash(folder, titleId);
        if (hash.Hash is not { } executable) return new Rpcs3PatchDisk(titleId, located, hash, null, false, string.Empty);

        var file = Rpcs3Patches.ReadPatchFile(folder.PatchFile(titleId));

        // patch_config.yml is read whatever the patch file holds: one RPCS3 cannot read is worth
        // saying before the button is pressed rather than after.
        string config = folder.PatchConfigFile;
        string? text;
        try
        {
            text = System.IO.File.Exists(config) ? System.IO.File.ReadAllText(config) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Rpcs3PatchDisk(titleId, located, hash, file, false, $"{config} could not be read: {ex.Message}");
        }

        try
        {
            // Also the refusal a write would meet, so the panels say it first.
            var mine = file.Kind == PatchFileKind.Ours ? file.EntriesFor(executable).ToList() : new List<PatchFileEntry>();
            var on = Rpcs3Patches.EnabledAmong(text, mine.Select(entry => entry.Key(titleId)), config);
            var enabled = mine.Where(entry => on.Contains(entry.Key(titleId))).Select(entry => entry.Description).ToArray();

            return new Rpcs3PatchDisk(titleId, located, hash, file,
                enabled.Contains(Rpcs3Patches.Description, StringComparer.Ordinal), string.Empty, enabled);
        }
        catch (Rpcs3PatchException ex)
        {
            return new Rpcs3PatchDisk(titleId, located, hash, file, false, ex.Message);
        }
    }

    /// <summary>A look that did not finish, for an error nothing above saw coming.</summary>
    public static Rpcs3PatchDisk Failed(string titleId, string problem) =>
        new(titleId, new Rpcs3FolderLookup(null, problem), null, null, false, string.Empty);
}

/// <summary>
/// qwark's own patches for the RPCS3 target, the savefile helper and the code switches: asks qwark
/// for their words, looks at RPCS3's folder, decides what the Connection panel says about each, and
/// does the install the user confirmed, both parts in one write. No ImGui: the panel calls
/// <see cref="Update"/> once a frame and draws what comes back.
/// <para>
/// Nothing here runs unless the panel is drawn with the RPCS3 target, and nothing is ever written
/// without <see cref="Install"/>, which only the confirmation dialog calls. The file work happens
/// off the render thread and comes back through <see cref="AppState.Post"/>.
/// </para>
/// </summary>
public sealed class Rpcs3PatchController
{
    /// <summary>How long a refusal that only means "not now" (BUSY, NOT_INGAME) waits before the words are asked for again.</summary>
    private const long RetryMs = 1000;

    /// <summary>The words of one part: what was asked for, for which game, and when to ask again.</summary>
    private sealed class Ask
    {
        public string Key = string.Empty;
        public long RetryAtMs;
        public QwarkPatchReply Reply = QwarkPatchReply.NotAsked;
    }

    private readonly AppState _state;
    private readonly Ask _helper = new();
    private readonly Ask _switches = new();

    private string _diskKey = string.Empty;
    private int _diskSequence;
    private int _bump;

    public Rpcs3PatchController(AppState state, Rpcs3Environment environment)
    {
        _state = state;
        Environment = environment;
    }

    /// <summary>The machine RPCS3 is looked for on. Tests set the folder in the settings instead, which wins.</summary>
    public Rpcs3Environment Environment { get; set; }

    /// <summary>What qwark said to SAVEFILE_PATCH for the running game.</summary>
    public QwarkPatchReply HelperReply => _helper.Reply;

    /// <summary>What qwark said to SWITCH_PATCH for the running game.</summary>
    public QwarkPatchReply SwitchesReply => _switches.Reply;

    public QwarkPatchReply ReplyFor(QwarkPatchPart part) => AskFor(part).Reply;

    private Ask AskFor(QwarkPatchPart part) => part == QwarkPatchPart.SaveFileHelper ? _helper : _switches;

    public Rpcs3PatchDisk? Disk { get; private set; }

    /// <summary>True from the confirmation until the files are written or the install has failed.</summary>
    public bool Installing { get; private set; }

    /// <summary>What the last <see cref="Update"/> decided, for the summary line of a headless run.</summary>
    public QwarkPatchesStatus LastStatus { get; private set; } = QwarkPatchesStatus.Hidden;

    /// <summary>The folder the last look found, which is where an install writes.</summary>
    public Rpcs3Folder? Folder => Disk is { } disk && disk.TitleId == _state.Session.TitleId ? disk.Folder.Folder : null;

    /// <summary>
    /// Once a frame, from the panel: asks for whatever the answer is still missing, then decides.
    /// Each part's words are asked for once per game and qwark build while qwark does not find that
    /// part in the game, and the folder is looked at again when the game boots again, when the
    /// folder setting changes, and on <see cref="Recheck"/>.
    /// </summary>
    public QwarkPatchesStatus Update()
    {
        if (!_state.Settings.Rpcs3Target) return LastStatus = QwarkPatchesStatus.Hidden;

        var session = _state.Session;
        var facts = new QwarkPatchSession(
            _state.Connected,
            _state.CodePatchesUnsupported,
            _state.Ingame,
            !_state.UnknownGame,
            session.TitleId,
            _state.Hello?.QwarkVersion ?? session.QwarkVersion,
            _state.SaveFile,
            session.CodeSwitches);

        bool wanted = facts.Connected && facts.CodePatchesUnsupported && facts.Ingame && facts.KnownGame && !_state.ConsoleBusy;
        if (wanted)
        {
            bool any = false;
            foreach (var part in QwarkPatchParts.All)
            {
                if (facts.IsActive(part)) continue;

                EnsureReply(part, session);
                any = true;
            }

            if (any) EnsureDisk(session);
        }

        return LastStatus = Decide(facts, _helper.Reply, _switches.Reply, Disk);
    }

    /// <summary>"Check again": the words, the folder and SAVEFILE_INFO are all read afresh.</summary>
    public void Recheck()
    {
        _bump++;
        _helper.RetryAtMs = 0;
        _switches.RetryAtMs = 0;
        _state.RefreshSaveFileInfo();
    }

    private void EnsureReply(QwarkPatchPart part, SessionInfo session)
    {
        var ask = AskFor(part);
        string key = $"{session.TitleId}|{session.Game}|{session.QwarkVersion}|{_bump}";
        if (key != ask.Key)
        {
            ask.Key = key;
            ask.Reply = QwarkPatchReply.NotAsked;
        }

        if (ask.Reply.Kind != QwarkPatchReplyKind.NotAsked || System.Environment.TickCount64 < ask.RetryAtMs) return;

        ask.Reply = QwarkPatchReply.Pending;
        var client = _state.Client;
        _ = Task.Run(async () =>
        {
            var (reply, retry) = await AskAsync(client, part).ConfigureAwait(false);
            _state.Post(() =>
            {
                if (ask.Key != key) return;

                ask.Reply = reply;
                if (retry) ask.RetryAtMs = System.Environment.TickCount64 + RetryMs;
            });
        });
    }

    private static Task<PatchReply> Fetch(QwarkClient client, QwarkPatchPart part) =>
        part == QwarkPatchPart.SaveFileHelper ? client.SaveFilePatchAsync() : client.SwitchPatchAsync();

    /// <summary>SAVEFILE_PATCH or SWITCH_PATCH, with every answer turned into something the panel can say.</summary>
    private static async Task<(QwarkPatchReply Reply, bool Retry)> AskAsync(QwarkClient client, QwarkPatchPart part)
    {
        try
        {
            return (QwarkPatchReply.Ok(await Fetch(client, part).ConfigureAwait(false)), false);
        }
        catch (QwarkStatusException ex) when (ex.Status == Status.Unsupported)
        {
            return (new QwarkPatchReply(QwarkPatchReplyKind.NotSupported), false);
        }
        catch (QwarkStatusException ex) when (ex.Status == Status.UnknownOp)
        {
            return (new QwarkPatchReply(QwarkPatchReplyKind.TooOld), false);
        }
        catch (QwarkStatusException ex) when (ex.Status is Status.NotIngame or Status.Busy)
        {
            // The game is starting or ending under the question; the next frame asks again.
            return (QwarkPatchReply.NotAsked, true);
        }
        catch (QwarkStatusException ex)
        {
            return (new QwarkPatchReply(QwarkPatchReplyKind.Failed,
                Problem: $"qwark refused to send {part.Noun()} ({ex.Status})."), false);
        }
        catch (Exception ex)
        {
            return (new QwarkPatchReply(QwarkPatchReplyKind.Failed,
                Problem: $"Asking qwark for {part.Noun()} failed: {ex.Message}"), false);
        }
    }

    private void EnsureDisk(SessionInfo session)
    {
        string title = session.TitleId;
        string folderSetting = _state.Settings.Rpcs3Folder;
        string key = $"{title}|{session.Generation}|{folderSetting}|{_bump}";
        if (key == _diskKey) return;

        _diskKey = key;
        int sequence = ++_diskSequence;
        var environment = Environment;
        _ = Task.Run(() =>
        {
            Rpcs3PatchDisk disk;
            try
            {
                disk = Rpcs3PatchDisk.Inspect(folderSetting, environment, title);
            }
            catch (Exception ex)
            {
                disk = Rpcs3PatchDisk.Failed(title, $"Checking RPCS3's folder failed: {ex.Message}");
            }

            _state.Post(() =>
            {
                if (sequence == _diskSequence) Disk = disk;
            });
        });
    }

    /// <summary>
    /// Writes <paramref name="parts"/> into <paramref name="folder"/>, which is the folder the
    /// confirmation named, in one write, and switches them on. The words and the hash are fetched
    /// again first, so what is written is what is running now; a part qwark will not hand over
    /// stops the whole install, and nothing is written. The outcome is a toast either way, and the
    /// status is looked at again afterwards.
    /// </summary>
    public void Install(Rpcs3Folder folder, IReadOnlyCollection<QwarkPatchPart> parts)
    {
        if (Installing || parts.Count == 0) return;

        var session = _state.Session;
        string title = session.TitleId;
        string game = session.GameName;
        byte build = _state.Hello?.QwarkVersion ?? session.QwarkVersion;
        var client = _state.Client;
        var writer = _state.Rpcs3Writer;
        var wanted = QwarkPatchParts.All.Where(parts.Contains).ToList();
        string names = QwarkPatchParts.Names(wanted);

        Installing = true;
        _ = Task.Run(async () =>
        {
            string? error = null;
            var asking = wanted[0];
            try
            {
                PatchReply? helper = null;
                PatchReply? switches = null;
                foreach (var part in wanted)
                {
                    asking = part;
                    var words = await Fetch(client, part).ConfigureAwait(false);
                    if (part == QwarkPatchPart.SaveFileHelper) helper = words;
                    else switches = words;
                }

                var hash = Rpcs3Patches.FindExecutableHash(folder, title);
                string executable = hash.Hash ?? throw new Rpcs3PatchException(hash.Problem);

                // Planned under the writer's lock, from the files as they are then, so a mod the
                // Mods panel is writing at the same moment is kept rather than written away.
                writer.Commit(() => Rpcs3Patches.PlanQwark(folder, title, game, executable, helper, switches, build));
            }
            catch (QwarkStatusException ex)
            {
                error = ex.Status switch
                {
                    Status.Unsupported => asking == QwarkPatchPart.SaveFileHelper
                        ? "this game has no savefile helper"
                        : "this game has no code switches",
                    Status.NotIngame => "the game is not running",
                    Status.UnknownOp => $"this qwark-rpcs3 cannot supply {asking.Noun()}",
                    _ => $"qwark answered {ex.Status} to {ex.Opcode}",
                };
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            _state.Post(() =>
            {
                Installing = false;
                if (error is null)
                {
                    _state.AddToast($"Wrote {names} for {game}. Restart the game in RPCS3 to load "
                                    + $"{(wanted.Count == 1 ? "it" : "them")}.", ToastKind.Success);

                    // The files have changed and the words have not: only the folder is looked at
                    // again, and what it said before the install is not shown in the meantime.
                    Disk = null;
                    _diskKey = string.Empty;
                }
                else
                {
                    _state.AddToast($"{names} {(wanted.Count == 1 ? "was" : "were")} not installed: {error}", ToastKind.Error);
                    Recheck();
                }
            });
        });
    }

    /// <summary>Both parts, from the facts alone: <see cref="Decide(QwarkPatchPart, QwarkPatchSession, QwarkPatchReply, Rpcs3PatchDisk?)"/> for each.</summary>
    public static QwarkPatchesStatus Decide(QwarkPatchSession session, QwarkPatchReply helper, QwarkPatchReply switches, Rpcs3PatchDisk? disk) =>
        new(Decide(QwarkPatchPart.SaveFileHelper, session, helper, disk),
            Decide(QwarkPatchPart.CodeSwitches, session, switches, disk));

    /// <summary>
    /// The whole decision for one part, from the facts alone. The order is the order of what has
    /// to be true first: a connection, a game, qwark's words, RPCS3's folder, the hash, the files.
    /// Everything up to the words and after them up to the files is the same for both parts and is
    /// said in the same words, so the panel can say it once.
    /// </summary>
    public static QwarkPatchStatus Decide(QwarkPatchPart part, QwarkPatchSession session, QwarkPatchReply reply, Rpcs3PatchDisk? disk)
    {
        bool helper = part == QwarkPatchPart.SaveFileHelper;

        if (!session.Connected)
        {
            return new(QwarkPatchState.Waiting, "Connect to see whether qwark's patches are installed.");
        }

        // qwark patches the game itself here; there is nothing for RPCS3 to do.
        if (!session.CodePatchesUnsupported) return QwarkPatchStatus.Hidden;

        if (!session.Ingame)
        {
            return new(QwarkPatchState.Waiting, "Start the game in RPCS3 to see whether qwark's patches are installed.");
        }

        if (!session.KnownGame)
        {
            return new(QwarkPatchState.Unavailable, "qwark does not know this game, so it has no patches for it.");
        }

        if (session.IsActive(part))
        {
            return new(QwarkPatchState.Active,
                helper ? "Active. Saving and loading work." : "Active. The cheats that patch game code work.");
        }

        switch (reply.Kind)
        {
            case QwarkPatchReplyKind.NotAsked:
            case QwarkPatchReplyKind.Pending:
                return new(QwarkPatchState.Waiting, "Asking qwark for its patches...");

            case QwarkPatchReplyKind.NotSupported:
                return new(QwarkPatchState.Absent, helper ? "This game has no savefile helper." : "This game has no code switches.");

            case QwarkPatchReplyKind.TooOld:
                return new(QwarkPatchState.Unavailable,
                    $"qwark-rpcs3 build {session.QwarkBuild} cannot supply {part.Noun()}. "
                    + $"It needs build {QwarkClient.ExpectedQwarkBuild}.");

            case QwarkPatchReplyKind.Failed:
                return new(QwarkPatchState.Unavailable, reply.Problem);
        }

        var patch = reply.Patch!;

        if (disk is null || !string.Equals(disk.TitleId, session.TitleId, StringComparison.Ordinal))
        {
            return new(QwarkPatchState.Waiting, "Looking at RPCS3's folder...");
        }

        if (disk.Folder.Folder is null) return new(QwarkPatchState.Unavailable, disk.Folder.Problem, FolderProblem: true);
        if (disk.Hash is not { Hash: { } hash }) return new(QwarkPatchState.Unavailable, disk.Hash?.Problem ?? string.Empty);

        var file = disk.File!;
        if (file.Kind is PatchFileKind.Foreign or PatchFileKind.Broken) return new(QwarkPatchState.Unavailable, file.Problem);
        if (disk.Problem.Length > 0) return new(QwarkPatchState.Unavailable, disk.Problem);

        // A mod RPCS3 applies over the same words would leave neither whole, so the install would
        // refuse; the line says so first. DL Crash Patches writes exactly the words Deadlocked's
        // code switches do.
        var clashes = Rpcs3Patches.ModsOver(PatchRanges.Of(patch), disk.EnabledMods());
        if (clashes.Count > 0)
        {
            string names = Rpcs3Patches.JoinNames(clashes.Select(clash => clash.Name).ToList());
            bool one = clashes.Count == 1;
            return new(QwarkPatchState.Unavailable,
                $"{names} {(one ? "is" : "are")} enabled on the Mods panel and {(one ? "writes" : "write")} to the same "
                + $"addresses as {part.Noun()}. Disable {(one ? "it" : "them")} there first, then install.");
        }

        string description = part.Description();
        var entry = file.EntryFor(hash, description);
        if (entry is null)
        {
            return new(QwarkPatchState.Install,
                helper
                    ? "Not installed. Under RPCS3, saving and loading need it."
                    : "Not installed. Under RPCS3, the cheats that patch game code need it.");
        }

        if (!entry.Holds(patch))
        {
            return new(QwarkPatchState.Install,
                "Out of date: the patch in RPCS3 is not the one this qwark build supplies. Install the new one.");
        }

        if (!disk.IsEnabled(description))
        {
            return new(QwarkPatchState.Install,
                "The patch is in RPCS3's patches folder, but RPCS3's patch settings switch it off. Install it again to switch it on.");
        }

        if (disk.Hash.AppliedAtBoot(description))
        {
            return new(QwarkPatchState.Unavailable,
                $"RPCS3 applied the patch when this game started, but qwark does not find {part.Noun()} in the game. "
                + "Press Check again. If this stays, restart the game in RPCS3.");
        }

        return new(QwarkPatchState.RestartGame, "Installed. Restart the game in RPCS3 to apply changes.");
    }
}
