using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>One thing an uninstall does, in the order it does them.</summary>
public enum UninstallStepKind
{
    /// <summary>MOD_UNLOAD, on a console, for a mod qwark reports loaded.</summary>
    Unload,

    /// <summary>MOD_SET_AUTO off, for a mod qwark reports flagged to load at boot.</summary>
    AutoOff,

    /// <summary>Under RPCS3: the mod's entries out of RaCMAN's patch file, and their switches out of patch_config.yml.</summary>
    RemoveRpcs3Patch,

    /// <summary>DIR_DELETE of the mod's folder on the console, by its whole name.</summary>
    DeleteConsoleFolder,

    /// <summary>The mod's folder in the user's library.</summary>
    DeleteLibraryFolder,
}

/// <summary>One line of the confirmation, and the step it describes.</summary>
public sealed record UninstallStep(UninstallStepKind Kind, string Text);

/// <summary>
/// What RPCS3's folder holds that an uninstall under RPCS3 has to know: the folder, when it was
/// found, and RaCMAN's patch file for the title as it was read when the question was asked.
/// </summary>
public sealed record Rpcs3UninstallLook(Rpcs3Folder? Folder, PatchFileState? File, string Problem);

/// <summary>
/// Everything an uninstall will do, worked out before it is asked about, so the question lists
/// exactly that. <see cref="Notes"/> are what it will not do and why.
/// </summary>
public sealed record UninstallPlan(
    string TitleId,
    ModRow Row,
    bool Rpcs3,
    IReadOnlyList<UninstallStep> Steps,
    IReadOnlyList<string> Notes,
    Rpcs3Folder? Rpcs3Folder)
{
    public string ModName => Row.Name;

    public bool NothingToDo => Steps.Count == 0;
}

/// <summary>
/// How an uninstall went: what was done, in words, and the step it stopped at with the reason,
/// or null when every step was done. <see cref="RescanProblem"/> is what the console answered to
/// the MOD_RESCAN that follows, when it was not OK.
/// </summary>
public sealed record UninstallOutcome(IReadOnlyList<string> Done, string? Failure, string? RescanProblem)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Taking a mod off the console and off this PC: the right-click Uninstall of the Mods panel. The
/// plan is worked out from the row and asked about first; the steps then run in order and the first
/// one that fails stops the rest, since each is only safe once the one before it is done (a folder
/// is not deleted under a mod that is still loaded from it). No ImGui: the panel draws the plan and
/// calls <see cref="Start"/> when the user says yes.
/// </summary>
public static class ModUninstall
{
    /// <summary>
    /// The plan for one row. On a console a loaded mod is unloaded first; a mod flagged to load at
    /// boot has the flag turned off; under RPCS3 its entries leave RaCMAN's patch file and their
    /// switches patch_config.yml; its folder on the console is deleted by the whole name DIR_LIST
    /// gave; and a mod of the user's library has its folder there deleted. A shipped mod's folder
    /// is part of the application and stays, which the notes say.
    /// </summary>
    public static UninstallPlan Plan(ModRow row, string titleId, bool rpcs3, Rpcs3UninstallLook? rpcs3Look = null,
        bool shippedCopyBehind = false)
    {
        var steps = new List<UninstallStep>();
        var notes = new List<string>();
        string where = rpcs3 ? "qwark-rpcs3's folder" : "the console";

        if (!rpcs3 && row.Console is { Loaded: true })
        {
            steps.Add(new(UninstallStepKind.Unload, "Unload it from the running game."));
        }

        if (row.Console is { Auto: true })
        {
            steps.Add(new(UninstallStepKind.AutoOff, "Turn its auto flag off, so it no longer loads by itself when the game boots."));
        }

        Rpcs3Folder? rpcs3Folder = null;
        if (rpcs3)
        {
            if (rpcs3Look?.Folder is not { } folder)
            {
                string why = rpcs3Look?.Problem is { Length: > 0 } problem ? $" ({problem.TrimEnd('.')})" : string.Empty;
                notes.Add($"RPCS3's folder was not found{why}, so no RPCS3 patch is changed.");
            }
            else if (rpcs3Look.File is { Kind: PatchFileKind.Broken } broken)
            {
                // Somebody else's patch file cannot hold one of RaCMAN's entries; a broken one of
                // RaCMAN's might, and cannot be rewritten without losing the rest of it.
                notes.Add($"{broken.Problem.TrimEnd('.')}. Its RPCS3 patch, if it has one, is left as it is.");
            }
            else if (rpcs3Look.File is { Kind: PatchFileKind.Ours } file && file.ModEntries(row.FolderName) is { Count: > 0 } entries)
            {
                rpcs3Folder = folder;
                steps.Add(new(UninstallStepKind.RemoveRpcs3Patch,
                    $"Remove its RPCS3 patch from {file.Path} ({entries.Count} {(entries.Count == 1 ? "entry" : "entries")}) "
                    + "and its switch from patch_config.yml. Every other entry stays as it is."));
            }
        }

        if (row.ConsoleFolder is { } consoleFolder)
        {
            steps.Add(new(UninstallStepKind.DeleteConsoleFolder,
                $"Delete its folder {ModLibrary.ConsoleFolder(titleId, consoleFolder)} from {where}."));
        }
        else if (row.Console is not null)
        {
            notes.Add($"qwark lists it, but its folder is already gone from {where}. A rescan of the console's mod folder drops it from the list"
                      + (row.Loaded && !rpcs3 ? " once it is unloaded." : "."));
        }
        else if (row.Local is not null)
        {
            notes.Add(rpcs3 ? "qwark-rpcs3's folder has no copy of it." : "The console has no copy of it.");
        }

        if (row.Local is { Shipped: false } mine)
        {
            steps.Add(new(UninstallStepKind.DeleteLibraryFolder, $"Delete its folder from your mods folder: {mine.Directory}"));
            if (shippedCopyBehind)
            {
                notes.Add("A mod of the same folder name ships with RaCMAN Reloaded; it takes this one's place in the list.");
            }
        }
        else if (row.Local is { Shipped: true })
        {
            notes.Add("It ships with RaCMAN Reloaded, so it cannot be deleted from the application folder: only "
                      + (rpcs3 ? "the copy in qwark-rpcs3's folder and its RPCS3 patch are" : "the console's copy is")
                      + " removed, and the mod stays available here"
                      + (rpcs3 ? " to enable again." : ", shown as not uploaded."));
        }

        return new UninstallPlan(titleId, row, rpcs3, steps, notes, rpcs3Folder);
    }

    /// <summary>
    /// Runs the plan's steps in order and stops at the first that fails, then asks the console to
    /// rescan its mod folder whatever happened, so its list is the folder's again. A folder the
    /// console no longer has when it is deleted is already gone, which is what was asked for.
    /// Never throws: every failure is in the outcome, with the status the console answered.
    /// </summary>
    public static async Task<UninstallOutcome> RunAsync(UninstallPlan plan, QwarkClient client, ModLibrary library,
        Rpcs3PatchWriter writer, CancellationToken cancellationToken = default)
    {
        var done = new List<string>();
        string? failure = null;
        var row = plan.Row;

        foreach (var step in plan.Steps)
        {
            try
            {
                switch (step.Kind)
                {
                    case UninstallStepKind.Unload:
                        await client.ModUnloadAsync(row.ListedName!, cancellationToken).ConfigureAwait(false);
                        done.Add("unloaded");
                        break;

                    case UninstallStepKind.AutoOff:
                        await client.ModSetAutoAsync(row.ListedName!, false, cancellationToken).ConfigureAwait(false);
                        done.Add("auto flag off");
                        break;

                    case UninstallStepKind.RemoveRpcs3Patch:
                        writer.Commit(() => Rpcs3Patches.PlanRemoveMod(plan.Rpcs3Folder!, plan.TitleId, row.FolderName));
                        done.Add("RPCS3 patch removed");
                        break;

                    case UninstallStepKind.DeleteConsoleFolder:
                        try
                        {
                            await client.DirDeleteAsync(ModLibrary.ConsoleFolder(plan.TitleId, row.ConsoleFolder!), cancellationToken)
                                .ConfigureAwait(false);
                            done.Add(plan.Rpcs3 ? "deleted from qwark-rpcs3's folder" : "deleted from the console");
                        }
                        catch (QwarkStatusException ex) when (ex.Status == Status.NotFound)
                        {
                            done.Add(plan.Rpcs3 ? "already gone from qwark-rpcs3's folder" : "already gone from the console");
                        }

                        break;

                    case UninstallStepKind.DeleteLibraryFolder:
                        library.DeleteUserFolder(plan.TitleId, row.Local!);
                        done.Add("deleted from your mods folder");
                        break;
                }
            }
            catch (QwarkStatusException ex)
            {
                failure = $"\"{step.Text}\" failed: {(plan.Rpcs3 ? "qwark-rpcs3" : "the console")} answered {ex.Status} to {ex.Opcode}";
                break;
            }
            catch (Exception ex)
            {
                failure = $"\"{step.Text}\" failed: {ex.Message.TrimEnd('.')}";
                break;
            }
        }

        string? rescan = null;
        try
        {
            await client.ModRescanAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (QwarkStatusException ex)
        {
            rescan = $"{ex.Status} to {ex.Opcode}";
        }
        catch (Exception ex)
        {
            rescan = ex.Message.TrimEnd('.');
        }

        return new UninstallOutcome(done, failure, rescan);
    }

    /// <summary>The toast an outcome makes: what was done, or where it stopped and what had been done by then.</summary>
    public static string Describe(UninstallPlan plan, UninstallOutcome outcome)
    {
        string done = string.Join(", ", outcome.Done);
        string rescan = outcome.RescanProblem is { } problem ? $" The rescan that follows was answered {problem}." : string.Empty;

        if (outcome.Succeeded)
        {
            return outcome.Done.Count == 0
                ? $"{plan.ModName}: there was nothing to uninstall.{rescan}"
                : $"{plan.ModName} uninstalled: {done}.{rescan}";
        }

        return $"{plan.ModName} was not uninstalled: {outcome.Failure}. "
               + (outcome.Done.Count == 0 ? "Nothing was changed." : $"Done before that: {done}.")
               + rescan;
    }

    /// <summary>
    /// Runs a confirmed plan off the render thread, then toasts the outcome and reads everything it
    /// may have changed again: the library, MOD_LIST, the console's mod folder and, under RPCS3,
    /// RPCS3's files. <paramref name="finished"/> runs on the render thread once it is over.
    /// </summary>
    public static void Start(AppState state, UninstallPlan plan, Action? finished = null)
    {
        var client = state.Client;
        var library = state.Mods;
        var writer = state.Rpcs3Writer;

        state.Run(async () =>
        {
            var outcome = await RunAsync(plan, client, library, writer).ConfigureAwait(false);
            state.Post(() =>
            {
                state.AddToast(Describe(plan, outcome), outcome.Succeeded ? ToastKind.Success : ToastKind.Error);
                state.RescanLocalMods();
                state.RefreshMods();
                if (plan.Rpcs3) state.Rpcs3Mods.Recheck();
                finished?.Invoke();
            });
        });
    }
}
