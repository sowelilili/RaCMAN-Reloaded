using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>Where one mod stands under RPCS3, as the Status column says it.</summary>
public enum ModRpcs3State
{
    /// <summary>RPCS3's folder has not been looked at yet.</summary>
    Waiting,

    /// <summary>qwark-rpcs3 finds the mod's words and caves in game memory this session.</summary>
    Loaded,

    /// <summary>qwark-rpcs3 has not looked for it yet this session.</summary>
    Checking,

    /// <summary>
    /// Enabled, and not in the game yet, or written this session: RPCS3 applies it at the next boot
    /// of the game.
    /// </summary>
    EnabledRestartNeeded,

    /// <summary>Enabled from an older copy than the one in the library.</summary>
    UpdateAvailable,

    /// <summary>RPCS3 applied it at this boot and qwark-rpcs3 does not find it: something wrote over it.</summary>
    NotInGameMemory,

    /// <summary>Not switched on in RPCS3, and not in the game.</summary>
    Disabled,

    /// <summary>
    /// Switched off in RPCS3, and qwark-rpcs3 still finds it in game memory: RPCS3 takes it out
    /// when the game is restarted.
    /// </summary>
    DisabledRestartNeeded,

    /// <summary>A Lua automation, which qwark cannot run.</summary>
    NeedsLua,

    /// <summary>qwark-rpcs3 cannot parse it, or a .bin it names is missing.</summary>
    ParseError,

    /// <summary>More than qwark-rpcs3 can hand over in one reply.</summary>
    TooLarge,

    /// <summary>No patch words and no caves: nothing for RPCS3 to apply, and nothing qwark-rpcs3 ever looks for.</summary>
    NothingToPatch,

    /// <summary>Enabled, but qwark-rpcs3 has no copy, so it cannot look for it in the game.</summary>
    NotUploaded,

    /// <summary>RPCS3's folder cannot be read, so whether the mod is enabled is not known.</summary>
    Unknown,
}

/// <summary>How the Status column colours a state, the way the console column does.</summary>
public enum ModRpcs3Tone
{
    Quiet,
    Good,
    Pending,
    Bad,
}

/// <summary>What MOD_PATCH said about a mod that makes it impossible to enable, remembered for the session.</summary>
public enum ModPatchRefusal
{
    None,
    NeedsLua,
    ParseError,
    TooLarge,
    NothingToPatch,
}

/// <summary>
/// One row's Status and Enabled cells: the state and its words, the tooltip, whether the checkbox
/// is ticked, and whether it can be clicked and why not.
/// </summary>
public sealed record ModRpcs3Status(
    ModRpcs3State State,
    string Text,
    ModRpcs3Tone Tone,
    string Tooltip,
    bool Enabled,
    bool CanToggle,
    string ToggleReason)
{
    public bool CanUpdate => State == ModRpcs3State.UpdateAvailable;
}

/// <summary>
/// Everything one row's status is decided from. <see cref="Answering"/> is qwark-rpcs3 answering
/// MOD_PATCH, which it does at the XMB and in a game and not while one is starting or ending.
/// </summary>
public sealed record ModRpcs3Facts(
    LocalMod Mod,
    ModEntry? Console,
    Rpcs3PatchDisk? Disk,
    ModPatchRefusal Refusal = ModPatchRefusal.None,
    bool RewrittenThisBoot = false,
    bool Connected = true,
    bool Answering = true);

/// <summary>What the confirmation dialog asks, and what saying yes does.</summary>
public enum ModsAction
{
    Enable,
    Disable,
    Update,
}

/// <summary>
/// A question for the user before anything they did not click directly is changed: a dependency
/// enabled with the mod that needs it, a dependant disabled with the mod it needs, an update.
/// </summary>
public sealed record ModsConfirmation(ModsAction Action, string Title, string Message, string ConfirmLabel, IReadOnlyList<LocalMod> Mods);

/// <summary>
/// The <c>#- depends</c> lines, walked the way the console's MOD_LOAD walks them: by mod name,
/// depth first, each dependency before the mod that names it and in the order the line lists them.
/// </summary>
public static class ModDependencies
{
    /// <summary>The names on a mod's <c>#- depends</c> line: comma separated, trimmed, empty ones skipped.</summary>
    public static IReadOnlyList<string> NamesIn(LocalMod mod) =>
        mod.Variables.TryGetValue("depends", out var line)
            ? line.Split(',').Select(name => name.Trim()).Where(name => name.Length > 0).ToList()
            : Array.Empty<string>();

    /// <summary>
    /// Everything enabling <paramref name="target"/> needs, dependencies first and the target last,
    /// each once. A name no mod in <paramref name="library"/> has, or a chain that comes back to
    /// itself, is a refusal with the reason; qwark answers MOD_LOAD NOT_FOUND and BAD_ARG for those.
    /// </summary>
    public static (IReadOnlyList<LocalMod> Order, string Problem) Resolve(LocalMod target, IReadOnlyList<LocalMod> library)
    {
        var order = new List<LocalMod>();
        var walking = new List<LocalMod>();
        string problem = Walk(target, library, order, walking);
        return problem.Length == 0 ? (order, string.Empty) : (Array.Empty<LocalMod>(), problem);
    }

    private static string Walk(LocalMod mod, IReadOnlyList<LocalMod> library, List<LocalMod> order, List<LocalMod> walking)
    {
        if (order.Contains(mod)) return string.Empty;
        if (walking.Contains(mod))
        {
            var loop = walking.SkipWhile(m => m != mod).Select(m => m.Name).Append(mod.Name);
            return $"{string.Join(" needs ", loop)}: the mods need each other, so none of them can be enabled.";
        }

        walking.Add(mod);
        foreach (var name in NamesIn(mod))
        {
            var dependency = Find(name, library);
            if (dependency is null)
            {
                walking.RemoveAt(walking.Count - 1);
                return $"{mod.Name} needs {name}, which is not in the mod library for this game.";
            }

            string problem = Walk(dependency, library, order, walking);
            if (problem.Length > 0) return problem;
        }

        walking.RemoveAt(walking.Count - 1);
        order.Add(mod);
        return string.Empty;
    }

    /// <summary>The mod a depends line names: the one whose name is exactly that, as qwark compares them.</summary>
    public static LocalMod? Find(string name, IReadOnlyList<LocalMod> library) =>
        library.FirstOrDefault(mod => string.Equals(mod.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Every mod <paramref name="isEnabled"/> says is enabled that needs <paramref name="target"/>,
    /// directly or through another enabled mod: what has to be switched off with it.
    /// </summary>
    public static IReadOnlyList<LocalMod> Dependants(LocalMod target, IReadOnlyList<LocalMod> library, Func<LocalMod, bool> isEnabled)
    {
        var found = new List<LocalMod>();
        var queue = new Queue<LocalMod>();
        queue.Enqueue(target);

        while (queue.Count > 0)
        {
            var needed = queue.Dequeue();
            foreach (var mod in library)
            {
                if (mod == target || found.Contains(mod) || !isEnabled(mod)) continue;
                if (!NamesIn(mod).Contains(needed.Name, StringComparer.Ordinal)) continue;

                found.Add(mod);
                queue.Enqueue(mod);
            }
        }

        return found;
    }

    /// <summary>"X needs Y. Enable both?", and the same with more names in it.</summary>
    public static string EnableQuestion(LocalMod target, IReadOnlyList<LocalMod> needed) =>
        $"{target.Name} needs {Rpcs3Patches.JoinNames(needed.Select(m => m.Name).ToList())}. "
        + (needed.Count == 1 ? "Enable both?" : $"Enable all {needed.Count + 1}?");

    /// <summary>"Y needs X. Disable both?", and the same with more names in it.</summary>
    public static string DisableQuestion(LocalMod target, IReadOnlyList<LocalMod> dependants) =>
        $"{Rpcs3Patches.JoinNames(dependants.Select(m => m.Name).ToList())} "
        + (dependants.Count == 1 ? "needs" : "need")
        + $" {target.Name}. "
        + (dependants.Count == 1 ? "Disable both?" : $"Disable all {dependants.Count + 1}?");
}

/// <summary>
/// The Mods panel under RPCS3. Every mod is words and caves, which qwark-rpcs3 cannot write into a
/// game RPCS3 has recompiled, so a mod goes in the way the savefile helper does: as an entry in
/// RPCS3's per-title patch file, switched on in patch_config.yml, applied when the game boots.
/// qwark-rpcs3 hands out the words (MOD_PATCH) and says through MOD_LIST whether it finds them in
/// the game; this decides what each row says, and does the writes the user asked for.
/// <para>
/// No ImGui: the panel calls <see cref="Update"/> once a frame, draws <see cref="StatusFor"/>, and
/// draws <see cref="Pending"/> as a dialog when there is one. Nothing is written without the user
/// ticking a box or saying yes to a question; the file work happens off the render thread and
/// comes back through <see cref="AppState.Post"/>.
/// </para>
/// </summary>
public sealed class Rpcs3ModsController
{
    /// <summary>How often MOD_LIST is read again while qwark-rpcs3 is still looking for mods in the game.</summary>
    private const long CheckingPollMs = 1000;

    private readonly AppState _state;
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModPatchRefusal> _refusals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Title, uint Generation)> _rewritten = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModRpcs3State> _lastStates = new(StringComparer.OrdinalIgnoreCase);

    private string _diskKey = string.Empty;
    private int _diskSequence;
    private int _bump;
    private long _nextPollMs;

    public Rpcs3ModsController(AppState state, Rpcs3Environment environment)
    {
        _state = state;
        Environment = environment;
    }

    /// <summary>The machine RPCS3 is looked for on. Tests set the folder in the settings instead, which wins.</summary>
    public Rpcs3Environment Environment { get; set; }

    /// <summary>The last look at RPCS3's folder for the title on screen, or null while it is being taken.</summary>
    public Rpcs3PatchDisk? Disk { get; private set; }

    /// <summary>The question the panel is to ask, or null.</summary>
    public ModsConfirmation? Pending { get; private set; }

    /// <summary>True while any write is under way.</summary>
    public bool Writing => _busy.Count > 0;

    /// <summary>What each row said when it was last drawn, by folder name, for the summary line of a headless run.</summary>
    public IReadOnlyDictionary<string, ModRpcs3State> LastStates => _lastStates;

    /// <summary>
    /// The title whose mods are on screen: the running one, or the one that just quit, since what
    /// RPCS3's folder holds for it does not go away with the game.
    /// </summary>
    private string Title => _state.CurrentTitle;

    /// <summary>
    /// Once a frame, from the panel: looks at RPCS3's folder again when the game boots again, when
    /// the folder setting changes and after every write, and reads MOD_LIST again while qwark-rpcs3
    /// is still looking for mods in the game.
    /// </summary>
    public void Update()
    {
        string title = Title;
        if (Rpcs3Patches.IsTitleId(title)) EnsureDisk(title);

        if (_state.Connected && !_state.ConsoleBusy && _state.ConsoleMods.Any(mod => mod.Checking)
            && System.Environment.TickCount64 >= _nextPollMs)
        {
            _nextPollMs = System.Environment.TickCount64 + CheckingPollMs;
            _state.RefreshConsoleMods(quiet: true);
        }
    }

    /// <summary>"Check again": RPCS3's folder and MOD_LIST are read afresh.</summary>
    public void Recheck()
    {
        _bump++;
        if (_state.Connected) _state.RefreshConsoleMods(quiet: false);
    }

    private void EnsureDisk(string title)
    {
        string folderSetting = _state.Settings.Rpcs3Folder;
        string key = $"{title}|{_state.Session.Generation}|{folderSetting}|{_bump}";
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

    /// <summary>Why RPCS3's folder cannot say which mods are enabled, or empty when it can (or has not been looked at yet).</summary>
    public string DiskProblem => DiskProblemOf(Disk);

    /// <summary>Whether the Connection panel's folder setting is the fix for <see cref="DiskProblem"/>.</summary>
    public bool FolderProblem => Disk is { Folder.Folder: null };

    private static string DiskProblemOf(Rpcs3PatchDisk? disk)
    {
        if (disk is null) return string.Empty;
        if (disk.Folder.Folder is null) return disk.Folder.Problem;
        if (disk.Hash is not { Hash: not null }) return disk.Hash?.Problem ?? string.Empty;
        if (disk.File is { Kind: PatchFileKind.Foreign or PatchFileKind.Broken } file) return file.Problem;
        return disk.Problem;
    }

    // ---------------------------------------------------------------- the rows

    /// <summary>What one row says, from what is known about it now.</summary>
    public ModRpcs3Status StatusFor(LocalMod mod)
    {
        var console = _state.ConsoleMods.FirstOrDefault(m => string.Equals(m.DirName, mod.DirName, StringComparison.OrdinalIgnoreCase));
        var facts = new ModRpcs3Facts(
            mod,
            console,
            Disk is { } disk && disk.TitleId == Title ? disk : null,
            _refusals.TryGetValue(RefusalKey(mod), out var refusal) ? refusal : ModPatchRefusal.None,
            RewrittenThisBoot(mod),
            _state.Connected,
            !_state.ConsoleBusy);

        var status = Decide(facts);
        _lastStates[mod.DirName] = status.State;
        return status;
    }

    /// <summary>True while a write for this mod is under way; its checkbox waits for it.</summary>
    public bool IsBusy(LocalMod mod) => _busy.Contains(mod.DirName);

    private static string RefusalKey(LocalMod mod) => $"{mod.DirName}|{mod.Hash:x8}";

    private bool RewrittenThisBoot(LocalMod mod)
    {
        if (Disk?.Hash?.Hash is not { } hash) return false;
        return _rewritten.TryGetValue($"{hash}|{mod.DirName}", out var at)
               && at.Title == Title
               && at.Generation == _state.Session.Generation;
    }

    /// <summary>
    /// The whole decision for one row, from the facts alone. What makes a mod impossible to enable
    /// comes first, then whether it is enabled at all: a disabled one that qwark-rpcs3 still finds
    /// in the game waits for a restart to leave it. Then, for an enabled one, what qwark-rpcs3 says
    /// about the game: a rewrite waiting for a restart, a newer copy in the library, still looking,
    /// found, applied and yet not found, or simply not there until the game boots again.
    /// </summary>
    public static ModRpcs3Status Decide(ModRpcs3Facts facts)
    {
        var mod = facts.Mod;
        var console = facts.Console;
        var disk = facts.Disk;

        if (disk is null)
        {
            return new(ModRpcs3State.Waiting, "...", ModRpcs3Tone.Quiet, "Looking at RPCS3's folder...",
                false, false, "Looking at RPCS3's folder...");
        }

        string problem = DiskProblemOf(disk);
        if (problem.Length > 0)
        {
            // Whether it is enabled cannot be read, but whether it is in the game is qwark's to say.
            return console is { Loaded: true, Checking: false }
                ? new(ModRpcs3State.Loaded, "Loaded", ModRpcs3Tone.Good, "qwark-rpcs3 finds this mod in game memory.\n" + problem,
                    false, false, problem)
                : new(ModRpcs3State.Unknown, "Unknown", ModRpcs3Tone.Quiet, problem, false, false, problem);
        }

        string hash = disk.Hash!.Hash!;
        var entry = disk.File?.Kind == PatchFileKind.Ours ? disk.File.ModEntryFor(hash, mod.DirName) : null;
        bool enabled = entry is not null && disk.IsEnabled(entry.Description);

        // Enabling takes qwark-rpcs3: it uploads the mod, parses it and hands out its words.
        // Switching one off only takes RPCS3's folder.
        string enableBlocked = !facts.Connected ? "Connect to qwark-rpcs3 first."
            : !facts.Answering ? "Wait for the game to finish starting or stopping in RPCS3."
            : string.Empty;

        var blocker = Blocker(facts);
        if (blocker is { } blocked)
        {
            return blocked with
            {
                Enabled = enabled,
                CanToggle = enabled,
                ToggleReason = enabled ? string.Empty : blocked.Tooltip,
            };
        }

        if (!enabled)
        {
            // The box says off, the game says on: RPCS3 only ever changes the patches in the game
            // when it boots it, so this one is in memory until the game is restarted.
            if (console is { Loaded: true, Checking: false })
            {
                return new(ModRpcs3State.DisabledRestartNeeded, "Disabled - restart needed", ModRpcs3Tone.Pending,
                    "Switched off in RPCS3, but still in the game: RPCS3 applies patch changes when the game boots, "
                    + "so it stays in the game until the game is restarted in RPCS3.",
                    false, enableBlocked.Length == 0, enableBlocked);
            }

            return new(ModRpcs3State.Disabled, "Disabled", ModRpcs3Tone.Quiet,
                "Not switched on in RPCS3. Tick Enabled to add it as an RPCS3 patch.",
                false, enableBlocked.Length == 0, enableBlocked);
        }

        if (facts.RewrittenThisBoot)
        {
            return Enabled(ModRpcs3State.EnabledRestartNeeded, "Enabled - restart needed", ModRpcs3Tone.Pending,
                "Its RPCS3 patch was written this session. RPCS3 applies patches when the game boots, "
                + "so restart the game in RPCS3 to load it.");
        }

        uint? recorded = Rpcs3Patches.LibraryHashIn(entry!.Notes);
        if (recorded != mod.Hash)
        {
            return Enabled(ModRpcs3State.UpdateAvailable, "Update available", ModRpcs3Tone.Pending,
                "The copy of this mod in the library is not the one its RPCS3 patch was made from. "
                + "Update rewrites the patch from the library's copy.");
        }

        if (console is null)
        {
            return Enabled(ModRpcs3State.NotUploaded, "Not uploaded", ModRpcs3Tone.Quiet,
                "qwark-rpcs3 has no copy of this mod, so it cannot look for it in the game. "
                + "Untick it and tick it again to upload it.");
        }

        if (console.Checking)
        {
            return Enabled(ModRpcs3State.Checking, "Checking...", ModRpcs3Tone.Pending,
                "qwark-rpcs3 has not looked for this mod in the game yet.");
        }

        if (console.Loaded)
        {
            return Enabled(ModRpcs3State.Loaded, "Loaded", ModRpcs3Tone.Good,
                "qwark-rpcs3 finds this mod's words in game memory.");
        }

        if (disk.Hash.AppliedAtBoot(entry.Description))
        {
            return Enabled(ModRpcs3State.NotInGameMemory, "Not in game memory", ModRpcs3Tone.Bad,
                "RPCS3 applied this mod when the game booted, but qwark-rpcs3 does not find its words in game memory: "
                + "something else wrote over them. Another patch for this game in RPCS3 is the likely cause.");
        }

        return Enabled(ModRpcs3State.EnabledRestartNeeded, "Enabled - restart needed", ModRpcs3Tone.Pending,
            "Enabled, and not in the game yet. RPCS3 applies patches when the game boots, so restart the game in RPCS3 to load it.");
    }

    private static ModRpcs3Status Enabled(ModRpcs3State state, string text, ModRpcs3Tone tone, string tooltip) =>
        new(state, text, tone, tooltip, true, true, string.Empty);

    /// <summary>What makes a mod impossible to enable at all, from the library, MOD_LIST or an earlier MOD_PATCH.</summary>
    private static ModRpcs3Status? Blocker(ModRpcs3Facts facts)
    {
        if (facts.Mod.NeedsLua || facts.Console is { NeedsLua: true } || facts.Refusal == ModPatchRefusal.NeedsLua)
        {
            return new(ModRpcs3State.NeedsLua, "Needs Lua", ModRpcs3Tone.Quiet,
                "This mod has a Lua automation, which qwark cannot run, so it cannot go in as an RPCS3 patch.",
                false, false, string.Empty);
        }

        if (facts.Console is { ParseError: true } || facts.Refusal == ModPatchRefusal.ParseError)
        {
            return new(ModRpcs3State.ParseError, "Parse error", ModRpcs3Tone.Bad,
                "qwark-rpcs3 could not parse this mod's patch.txt, or a .bin it names is missing.",
                false, false, string.Empty);
        }

        if (facts.Refusal == ModPatchRefusal.TooLarge)
        {
            return new(ModRpcs3State.TooLarge, "Too large for RPCS3", ModRpcs3Tone.Quiet,
                "This mod is more than qwark-rpcs3 can hand over in one reply, so it cannot go in as an RPCS3 patch.",
                false, false, string.Empty);
        }

        // qwark-rpcs3 never looks for such a mod in the game, so it would never read as loaded.
        if ((facts.Mod.PatchWordCount == 0 && facts.Mod.BinFiles.Count == 0) || facts.Refusal == ModPatchRefusal.NothingToPatch)
        {
            return new(ModRpcs3State.NothingToPatch, "Nothing to patch", ModRpcs3Tone.Quiet,
                "This mod has no patch words and no code caves, so there is nothing for RPCS3 to apply.",
                false, false, string.Empty);
        }

        return null;
    }

    // ---------------------------------------------------------------- what the user asks for

    /// <summary>
    /// The Enabled checkbox was clicked. Ticking enables the mod, after asking when it needs mods
    /// that are not enabled; unticking switches it off, after asking when enabled mods need it.
    /// </summary>
    public void Toggle(LocalMod mod, bool wanted)
    {
        if (Writing)
        {
            _state.AddToast("Wait for the change being written to RPCS3 to finish.", ToastKind.Error);
            return;
        }

        var library = _state.LocalMods;
        if (wanted)
        {
            var (order, problem) = ModDependencies.Resolve(mod, library);
            if (problem.Length > 0)
            {
                _state.AddToast($"{mod.Name} was not enabled: {problem}", ToastKind.Error);
                return;
            }

            var needed = order.Where(m => m != mod && !IsEnabled(m)).ToList();
            if (needed.Count > 0)
            {
                Pending = new ModsConfirmation(ModsAction.Enable, "Enable dependencies?",
                    ModDependencies.EnableQuestion(mod, needed), needed.Count == 1 ? "Enable both" : "Enable all",
                    needed.Append(mod).ToList());
                return;
            }

            StartWrite(new[] { mod }, update: false);
            return;
        }

        var dependants = ModDependencies.Dependants(mod, library, IsEnabled);
        if (dependants.Count > 0)
        {
            Pending = new ModsConfirmation(ModsAction.Disable, "Disable dependants?",
                ModDependencies.DisableQuestion(mod, dependants), dependants.Count == 1 ? "Disable both" : "Disable all",
                dependants.Prepend(mod).ToList());
            return;
        }

        StartDisable(new[] { mod });
    }

    /// <summary>The Update button: always a question first, since the user did not change the mod here.</summary>
    public void RequestUpdate(LocalMod mod)
    {
        var (order, problem) = ModDependencies.Resolve(mod, _state.LocalMods);
        if (problem.Length > 0)
        {
            _state.AddToast($"{mod.Name} was not updated: {problem}", ToastKind.Error);
            return;
        }

        var needed = order.Where(m => m != mod && !IsEnabled(m)).ToList();
        string version = string.IsNullOrEmpty(mod.Version) ? string.Empty : $" (version {mod.Version})";
        string message = $"Rewrite {mod.Name}'s RPCS3 patch from the copy in the library{version}? "
                         + (needed.Count > 0
                             ? $"It now needs {Rpcs3Patches.JoinNames(needed.Select(m => m.Name).ToList())}, which will be enabled too. "
                             : string.Empty)
                         + "Restart the game in RPCS3 afterwards to load it.";

        Pending = new ModsConfirmation(ModsAction.Update, "Update mod?", message, "Update", needed.Append(mod).ToList());
    }

    /// <summary>The dialog's yes.</summary>
    public void Confirm()
    {
        if (Pending is not { } pending) return;
        Pending = null;

        switch (pending.Action)
        {
            case ModsAction.Enable:
                StartWrite(pending.Mods, update: false);
                break;

            case ModsAction.Update:
                StartWrite(pending.Mods, update: true);
                break;

            case ModsAction.Disable:
                StartDisable(pending.Mods);
                break;
        }
    }

    /// <summary>The dialog's no: nothing is changed.</summary>
    public void Cancel() => Pending = null;

    /// <summary>Whether the last look says this mod's entry for the running executable is switched on.</summary>
    public bool IsEnabled(LocalMod mod)
    {
        if (Disk is not { Hash.Hash: { } hash, File: { Kind: PatchFileKind.Ours } file } disk || disk.TitleId != Title) return false;
        return file.ModEntryFor(hash, mod.DirName) is { } entry && disk.IsEnabled(entry.Description);
    }

    // ---------------------------------------------------------------- the writes

    /// <summary>
    /// Enables (or updates) <paramref name="mods"/>, dependencies first, in one write: each is
    /// uploaded when qwark-rpcs3's copy differs from the library's, its words are asked for with
    /// MOD_PATCH, and then all of them go into the patch file and are switched on together, or
    /// none is. The overlap check happens in the write, against the files as they are then.
    /// </summary>
    private void StartWrite(IReadOnlyList<LocalMod> mods, bool update)
    {
        var target = mods[^1];
        string verb = update ? "updated" : "enabled";

        if (Disk?.Folder.Folder is not { } folder)
        {
            _state.AddToast($"{target.Name} was not {verb}: {(DiskProblem.Length > 0 ? DiskProblem : "RPCS3's folder has not been found yet.")}",
                ToastKind.Error);
            return;
        }

        // qwark-rpcs3 answers MOD_PATCH in a game and at the XMB, where it still holds the mods of
        // the game that just stopped: enabling a mod between two boots of the game is the natural
        // way to do it. It answers BUSY while a game is starting or stopping.
        if (!_state.Connected || _state.ConsoleBusy)
        {
            _state.AddToast($"{target.Name} was not {verb}: "
                            + (_state.Connected ? "the game is starting or stopping in RPCS3. Try again in a moment."
                                : "connect to qwark-rpcs3 first."), ToastKind.Error);
            return;
        }

        string title = Title;
        string game = _state.Ingame ? _state.Session.GameName : _state.DescribedGame.DisplayName();
        uint generation = _state.Session.Generation;
        byte build = _state.Hello?.QwarkVersion ?? _state.Session.QwarkVersion;
        var client = _state.Client;
        var library = _state.Mods;
        var writer = _state.Rpcs3Writer;

        foreach (var mod in mods) _busy.Add(mod.DirName);

        _ = Task.Run(async () =>
        {
            string? error = null;
            string? hash = null;
            var refusals = new List<(LocalMod Mod, ModPatchRefusal Refusal)>();
            try
            {
                var lookup = Rpcs3Patches.FindExecutableHash(folder, title);
                hash = lookup.Hash ?? throw new Rpcs3PatchException(lookup.Problem);

                var entries = new List<PatchFileEntry>();
                foreach (var mod in mods)
                {
                    var (entry, refusal, why) = await PatchFor(client, library, title, game, hash, build, mod).ConfigureAwait(false);
                    if (refusal != ModPatchRefusal.None) refusals.Add((mod, refusal));
                    if (entry is null) throw new Rpcs3PatchException(why);
                    entries.Add(entry);
                }

                string executable = hash;
                writer.Commit(() => Rpcs3Patches.PlanMods(folder, title, executable, entries));
            }
            catch (QwarkStatusException ex)
            {
                error = $"qwark-rpcs3 answered {ex.Status} to {ex.Opcode}";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            _state.Post(() =>
            {
                foreach (var mod in mods) _busy.Remove(mod.DirName);
                foreach (var (mod, refusal) in refusals) _refusals[RefusalKey(mod)] = refusal;

                string names = Rpcs3Patches.JoinNames(mods.Select(m => m.Name).ToList());
                if (error is null)
                {
                    foreach (var mod in mods) _rewritten[$"{hash}|{mod.DirName}"] = (title, generation);
                    _state.AddToast(update
                            ? $"Updated {names}. Restart the game in RPCS3 to load it."
                            : $"Enabled {names}. Restart the game in RPCS3 to load {(mods.Count == 1 ? "it" : "them")}.",
                        ToastKind.Success);
                }
                else
                {
                    _state.AddToast($"{names} {(mods.Count == 1 ? "was" : "were")} not {verb}: {error}", ToastKind.Error);
                }

                _bump++;
                _state.RefreshConsoleMods(quiet: true);
            });
        });
    }

    /// <summary>
    /// One mod's entry: the upload when qwark-rpcs3's copy is not the library's, then MOD_LIST for
    /// its index (a rescan can move it) and MOD_PATCH. A refusal that makes the mod impossible to
    /// enable is handed back to be remembered, with the reason in words.
    /// </summary>
    private static async Task<(PatchFileEntry? Entry, ModPatchRefusal Refusal, string Why)> PatchFor(
        QwarkClient client, ModLibrary library, string title, string game, string hash, byte build, LocalMod mod)
    {
        var before = await client.ModListAsync().ConfigureAwait(false);
        uint consoleHash = Row(before, mod)?.Hash ?? 0;
        await library.EnsureUploadedAsync(client, title, mod, consoleHash).ConfigureAwait(false);

        var row = Row(await client.ModListAsync().ConfigureAwait(false), mod);
        if (row is null) return (null, ModPatchRefusal.None, $"qwark-rpcs3 does not list {mod.Name} even after the upload.");

        PatchReply reply;
        try
        {
            reply = await client.ModPatchAsync(row.Index).ConfigureAwait(false);
            if (reply.Words.Length == 0 && reply.Bytes.Length == 0)
            {
                return (null, ModPatchRefusal.NothingToPatch,
                    $"{mod.Name} has no patch words and no code caves, so there is nothing for RPCS3 to apply.");
            }
        }
        catch (QwarkStatusException ex) when (ex.Opcode == Opcode.ModPatch)
        {
            return ex.Status switch
            {
                Status.Unsupported => (null, ModPatchRefusal.NeedsLua,
                    $"{mod.Name} has a Lua automation, which qwark cannot run, so it cannot go in as an RPCS3 patch."),
                Status.IoError => (null, ModPatchRefusal.ParseError,
                    $"qwark-rpcs3 could not parse {mod.Name}, or a .bin it names is missing."),
                Status.Full => (null, ModPatchRefusal.TooLarge,
                    $"{mod.Name} is more than qwark-rpcs3 can hand over in one reply."),
                Status.NotFound => (null, ModPatchRefusal.None, $"qwark-rpcs3 no longer lists {mod.Name}. Rescan and try again."),
                Status.UnknownOp => (null, ModPatchRefusal.None,
                    $"this qwark-rpcs3 cannot hand out mods; it needs build {QwarkClient.ExpectedQwarkBuild}."),
                Status.NotIngame => (null, ModPatchRefusal.None, "qwark-rpcs3 has no game to hand out the mod for. Start the game in RPCS3."),
                Status.Busy => (null, ModPatchRefusal.None, "the game is starting or stopping. Try again once it is running."),
                _ => (null, ModPatchRefusal.None, $"qwark-rpcs3 answered {ex.Status} to MOD_PATCH."),
            };
        }

        uint libraryHash = ModLibrary.ComputeHash(mod);
        var entry = new PatchFileEntry(hash, string.IsNullOrWhiteSpace(game) ? title : game, reply.Words, reply.Bytes,
            Rpcs3Patches.NotesForMod(libraryHash, build, reply.Stamp),
            Rpcs3Patches.ModDescription(mod.Name, mod.DirName),
            string.IsNullOrWhiteSpace(mod.Author) ? Rpcs3Patches.ModAuthorFallback : mod.Author);
        return (entry, ModPatchRefusal.None, string.Empty);
    }

    private static ModEntry? Row(IEnumerable<ModEntry> list, LocalMod mod) =>
        list.FirstOrDefault(m => string.Equals(m.DirName, mod.DirName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Switches <paramref name="mods"/> off in patch_config.yml, all together.</summary>
    private void StartDisable(IReadOnlyList<LocalMod> mods)
    {
        var target = mods[0];
        if (Disk?.Folder.Folder is not { } folder)
        {
            _state.AddToast($"{target.Name} was not disabled: {(DiskProblem.Length > 0 ? DiskProblem : "RPCS3's folder has not been found yet.")}",
                ToastKind.Error);
            return;
        }

        string title = Title;
        var writer = _state.Rpcs3Writer;
        var dirs = mods.Select(m => m.DirName).ToList();
        foreach (var dir in dirs) _busy.Add(dir);

        _ = Task.Run(() =>
        {
            string? error = null;
            try
            {
                var lookup = Rpcs3Patches.FindExecutableHash(folder, title);
                string hash = lookup.Hash ?? throw new Rpcs3PatchException(lookup.Problem);
                writer.Commit(() => Rpcs3Patches.PlanDisable(folder, title, hash, dirs));
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            _state.Post(() =>
            {
                foreach (var dir in dirs) _busy.Remove(dir);

                string names = Rpcs3Patches.JoinNames(mods.Select(m => m.Name).ToList());
                if (error is null)
                {
                    _state.AddToast($"Disabled {names}. Restart the game in RPCS3 to take {(mods.Count == 1 ? "it" : "them")} out of the game.",
                        ToastKind.Success);
                }
                else
                {
                    _state.AddToast($"{names} {(mods.Count == 1 ? "was" : "were")} not disabled: {error}", ToastKind.Error);
                }

                _bump++;
            });
        });
    }
}
