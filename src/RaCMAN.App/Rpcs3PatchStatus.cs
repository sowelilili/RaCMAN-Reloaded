using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>Where the savefile helper patch stands for the game running under RPCS3.</summary>
public enum SaveFilePatchState
{
    /// <summary>Nothing to say: not an RPCS3 session, or a qwark that writes the helper itself.</summary>
    Hidden,

    /// <summary>Waiting for a connection, a game, qwark's words or the look at RPCS3's folder.</summary>
    Waiting,

    /// <summary>qwark finds its helper in the game: saving and loading work.</summary>
    Active,

    /// <summary>The patch is in RPCS3 and switched on; the game has to start again to take it.</summary>
    RestartGame,

    /// <summary>Not installed, out of date or switched off: the button is offered.</summary>
    Install,

    /// <summary>Cannot be installed from here, and <see cref="SaveFilePatchStatus.Message"/> says why.</summary>
    Unavailable,
}

/// <summary>The line the Connection panel shows, and whether it offers the button.</summary>
public sealed record SaveFilePatchStatus(SaveFilePatchState State, string Message, bool FolderProblem = false)
{
    public static readonly SaveFilePatchStatus Hidden = new(SaveFilePatchState.Hidden, string.Empty);

    public bool CanInstall => State == SaveFilePatchState.Install;
}

public enum SaveFilePatchReplyKind
{
    NotAsked,
    Pending,
    Ok,

    /// <summary>UNSUPPORTED: the game has no helper, or it is switched off.</summary>
    NoHelper,

    /// <summary>UNKNOWN_OP: a qwark from before SAVEFILE_PATCH.</summary>
    TooOld,

    /// <summary>Anything else, with the reason.</summary>
    Failed,
}

/// <summary>What qwark said to SAVEFILE_PATCH for the running game.</summary>
public sealed record SaveFilePatchReply(SaveFilePatchReplyKind Kind, PatchReply? Patch = null, string Problem = "")
{
    public static readonly SaveFilePatchReply NotAsked = new(SaveFilePatchReplyKind.NotAsked);

    public static readonly SaveFilePatchReply Pending = new(SaveFilePatchReplyKind.Pending);

    public static SaveFilePatchReply Ok(PatchReply patch) => new(SaveFilePatchReplyKind.Ok, patch);
}

/// <summary>What the session says, as far as the savefile helper patch is concerned.</summary>
public sealed record SaveFilePatchSession(
    bool Connected,
    bool CodePatchesUnsupported,
    bool Ingame,
    bool KnownGame,
    string TitleId,
    byte QwarkBuild,
    SaveFileInfo Info);

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
/// The savefile helper patch for the RPCS3 target: asks qwark for the helper's words, looks at
/// RPCS3's folder, decides what the Connection panel says, and does the install the user confirmed.
/// No ImGui: the panel calls <see cref="Update"/> once a frame and draws what comes back.
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

    private readonly AppState _state;

    private string _replyKey = string.Empty;
    private long _retryAtMs;
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

    public SaveFilePatchReply Reply { get; private set; } = SaveFilePatchReply.NotAsked;

    public Rpcs3PatchDisk? Disk { get; private set; }

    /// <summary>True from the confirmation until the files are written or the install has failed.</summary>
    public bool Installing { get; private set; }

    /// <summary>What the last <see cref="Update"/> decided, for the summary line of a headless run.</summary>
    public SaveFilePatchStatus LastStatus { get; private set; } = SaveFilePatchStatus.Hidden;

    /// <summary>The folder the last look found, which is where an install writes.</summary>
    public Rpcs3Folder? Folder => Disk is { } disk && disk.TitleId == _state.Session.TitleId ? disk.Folder.Folder : null;

    /// <summary>
    /// Once a frame, from the panel: asks for whatever the answer is still missing, then decides.
    /// The words are asked for once per game and qwark build, and the folder is looked at again when
    /// the game boots again, when the folder setting changes, and on <see cref="Recheck"/>.
    /// </summary>
    public SaveFilePatchStatus Update()
    {
        if (!_state.Settings.Rpcs3Target) return LastStatus = SaveFilePatchStatus.Hidden;

        var session = _state.Session;
        var facts = new SaveFilePatchSession(
            _state.Connected,
            _state.CodePatchesUnsupported,
            _state.Ingame,
            !_state.UnknownGame,
            session.TitleId,
            _state.Hello?.QwarkVersion ?? session.QwarkVersion,
            _state.SaveFile);

        bool wanted = facts.Connected && facts.CodePatchesUnsupported && facts.Ingame && facts.KnownGame
                      && !facts.Info.Installed && !_state.ConsoleBusy;
        if (wanted)
        {
            EnsureReply(session);
            EnsureDisk(session);
        }

        return LastStatus = Decide(facts, Reply, Disk);
    }

    /// <summary>"Check again": the words, the folder and SAVEFILE_INFO are all read afresh.</summary>
    public void Recheck()
    {
        _bump++;
        _retryAtMs = 0;
        _state.RefreshSaveFileInfo();
    }

    private void EnsureReply(SessionInfo session)
    {
        string key = $"{session.TitleId}|{session.Game}|{session.QwarkVersion}|{_bump}";
        if (key != _replyKey)
        {
            _replyKey = key;
            Reply = SaveFilePatchReply.NotAsked;
        }

        if (Reply.Kind != SaveFilePatchReplyKind.NotAsked || System.Environment.TickCount64 < _retryAtMs) return;

        Reply = SaveFilePatchReply.Pending;
        var client = _state.Client;
        _ = Task.Run(async () =>
        {
            var (reply, retry) = await AskAsync(client).ConfigureAwait(false);
            _state.Post(() =>
            {
                if (_replyKey != key) return;

                Reply = reply;
                if (retry) _retryAtMs = System.Environment.TickCount64 + RetryMs;
            });
        });
    }

    /// <summary>SAVEFILE_PATCH, with every answer turned into something the panel can say.</summary>
    private static async Task<(SaveFilePatchReply Reply, bool Retry)> AskAsync(QwarkClient client)
    {
        try
        {
            return (SaveFilePatchReply.Ok(await client.SaveFilePatchAsync().ConfigureAwait(false)), false);
        }
        catch (QwarkStatusException ex) when (ex.Status == Status.Unsupported)
        {
            return (new SaveFilePatchReply(SaveFilePatchReplyKind.NoHelper), false);
        }
        catch (QwarkStatusException ex) when (ex.Status == Status.UnknownOp)
        {
            return (new SaveFilePatchReply(SaveFilePatchReplyKind.TooOld), false);
        }
        catch (QwarkStatusException ex) when (ex.Status is Status.NotIngame or Status.Busy)
        {
            // The game is starting or ending under the question; the next frame asks again.
            return (SaveFilePatchReply.NotAsked, true);
        }
        catch (QwarkStatusException ex)
        {
            return (new SaveFilePatchReply(SaveFilePatchReplyKind.Failed,
                Problem: $"qwark refused to send the savefile helper ({ex.Status})."), false);
        }
        catch (Exception ex)
        {
            return (new SaveFilePatchReply(SaveFilePatchReplyKind.Failed,
                Problem: $"Asking qwark for the savefile helper failed: {ex.Message}"), false);
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
    /// Writes the patch into <paramref name="folder"/>, which is the folder the confirmation named.
    /// The words and the hash are fetched again first, so what is written is what is running now.
    /// The outcome is a toast either way, and the status is looked at again afterwards.
    /// </summary>
    public void Install(Rpcs3Folder folder)
    {
        if (Installing) return;

        var session = _state.Session;
        string title = session.TitleId;
        string game = session.GameName;
        byte build = _state.Hello?.QwarkVersion ?? session.QwarkVersion;
        var client = _state.Client;
        var writer = _state.Rpcs3Writer;

        Installing = true;
        _ = Task.Run(async () =>
        {
            string? error = null;
            try
            {
                var patch = await client.SaveFilePatchAsync().ConfigureAwait(false);
                var hash = Rpcs3Patches.FindExecutableHash(folder, title);
                string executable = hash.Hash ?? throw new Rpcs3PatchException(hash.Problem);

                // Planned under the writer's lock, from the files as they are then, so a mod the
                // Mods panel is writing at the same moment is kept rather than written away.
                writer.Commit(() => Rpcs3Patches.Plan(folder, title, game, executable, patch, build));
            }
            catch (QwarkStatusException ex)
            {
                error = ex.Status switch
                {
                    Status.Unsupported => "this game has no savefile helper",
                    Status.NotIngame => "the game is not running",
                    Status.UnknownOp => "this qwark-rpcs3 cannot supply the helper",
                    _ => $"qwark answered {ex.Status}",
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
                    _state.AddToast($"Savefile helper patch written for {game}. Restart the game in RPCS3 to load it.",
                        ToastKind.Success);

                    // The files have changed and the words have not: only the folder is looked at
                    // again, and what it said before the install is not shown in the meantime.
                    Disk = null;
                    _diskKey = string.Empty;
                }
                else
                {
                    _state.AddToast($"The savefile helper patch was not installed: {error}", ToastKind.Error);
                    Recheck();
                }
            });
        });
    }

    /// <summary>
    /// The whole decision, from the facts alone. The order is the order of what has to be true
    /// first: a connection, a game, qwark's words, RPCS3's folder, the hash, the files.
    /// </summary>
    public static SaveFilePatchStatus Decide(SaveFilePatchSession session, SaveFilePatchReply reply, Rpcs3PatchDisk? disk)
    {
        if (!session.Connected)
        {
            return new(SaveFilePatchState.Waiting, "Connect to see whether the savefile helper patch is installed.");
        }

        // qwark writes the helper into the game itself here; there is nothing for RPCS3 to do.
        if (!session.CodePatchesUnsupported) return SaveFilePatchStatus.Hidden;

        if (!session.Ingame)
        {
            return new(SaveFilePatchState.Waiting, "Start the game in RPCS3 to see whether the savefile helper patch is installed.");
        }

        if (!session.KnownGame)
        {
            return new(SaveFilePatchState.Unavailable, "qwark does not know this game, so it has no savefile helper for it.");
        }

        if (session.Info.Installed)
        {
            return new(SaveFilePatchState.Active, "Active: the savefile helper is in the game, so saving and loading work.");
        }

        switch (reply.Kind)
        {
            case SaveFilePatchReplyKind.NotAsked:
            case SaveFilePatchReplyKind.Pending:
                return new(SaveFilePatchState.Waiting, "Asking qwark for the savefile helper...");

            case SaveFilePatchReplyKind.NoHelper:
                return new(SaveFilePatchState.Unavailable, "This game has no savefile helper, so there is nothing to install.");

            case SaveFilePatchReplyKind.TooOld:
                return new(SaveFilePatchState.Unavailable,
                    $"qwark-rpcs3 build {session.QwarkBuild} cannot supply the savefile helper patch. "
                    + $"It needs build {QwarkClient.ExpectedQwarkBuild}.");

            case SaveFilePatchReplyKind.Failed:
                return new(SaveFilePatchState.Unavailable, reply.Problem);
        }

        var patch = reply.Patch!;

        if (disk is null || !string.Equals(disk.TitleId, session.TitleId, StringComparison.Ordinal))
        {
            return new(SaveFilePatchState.Waiting, "Looking at RPCS3's folder...");
        }

        if (disk.Folder.Folder is null) return new(SaveFilePatchState.Unavailable, disk.Folder.Problem, FolderProblem: true);
        if (disk.Hash is not { Hash: { } hash }) return new(SaveFilePatchState.Unavailable, disk.Hash?.Problem ?? string.Empty);

        var file = disk.File!;
        if (file.Kind is PatchFileKind.Foreign or PatchFileKind.Broken) return new(SaveFilePatchState.Unavailable, file.Problem);
        if (disk.Problem.Length > 0) return new(SaveFilePatchState.Unavailable, disk.Problem);

        var entry = file.EntryFor(hash);
        if (entry is null)
        {
            return new(SaveFilePatchState.Install,
                "Not installed. Under RPCS3, saving and loading need the savefile helper as an RPCS3 patch.");
        }

        if (!entry.Holds(patch))
        {
            return new(SaveFilePatchState.Install,
                "Out of date: the patch in RPCS3 is not the helper this qwark build supplies. Install the new one.");
        }

        if (!disk.Enabled)
        {
            return new(SaveFilePatchState.Install,
                "The patch is in RPCS3's patches folder, but RPCS3's patch settings switch it off. Install it again to switch it on.");
        }

        if (disk.Hash.PatchApplied)
        {
            return new(SaveFilePatchState.Unavailable,
                "RPCS3 applied the patch when this game started, but qwark does not find the helper in the game. "
                + "Press Check again. If this stays, restart the game in RPCS3.");
        }

        return new(SaveFilePatchState.RestartGame,
            "Installed. Restart the game in RPCS3 to apply changes.");
    }
}
