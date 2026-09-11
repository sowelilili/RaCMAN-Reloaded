using System.Net.Sockets;
using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>Which part of the PS3 connect sequence a message belongs to.</summary>
public enum Ps3ConnectStep
{
    /// <summary>The attempt at qwark's own port.</summary>
    Connect,

    /// <summary>Asking webMAN which VSH slot, if any, holds the module. The first step in webMAN mode.</summary>
    Ask,

    /// <summary>Sending the SPRX through webMAN and asking it to load it.</summary>
    Load,

    /// <summary>The attempt that follows the load, once the module has had time to come up.</summary>
    Reconnect,
}

/// <summary>Whether the sequence ended connected, where it stopped, and what to say about it.</summary>
public readonly record struct Ps3ConnectOutcome(bool Connected, Ps3ConnectStep Step, string Message);

/// <summary>
/// What pressing Connect against a console does: in webMAN mode, ask which slot holds the module,
/// load it when no slot does, and connect; in standalone mode, connect and nothing else. It is
/// written as a sequence of delegates rather than against <see cref="AppState"/> so the order of
/// the steps, and what each failure means, can be checked without a console and without touching
/// the network.
/// <para>
/// The question comes before the connect attempt. A console whose qwark is not loaded does not
/// refuse the connection, it drops it, so connecting first meant waiting out the TCP timeout
/// before anything useful happened. webMAN answers in milliseconds, and the answer says what to
/// do next, so it is asked first and the probe has a short deadline of its own.
/// </para>
/// </summary>
public static class Ps3Connect
{
    /// <summary>How long webMAN's load gets to bring the module up before the second attempt.</summary>
    public static readonly TimeSpan LoadWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The shortest gap between two webMAN detours on the reconnect loop. The loop itself retries
    /// every second or two, and an FTP upload at that rate against a console that is simply off
    /// would be neither polite nor useful.
    /// </summary>
    public static readonly TimeSpan DetourInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether a reconnect attempt takes the webMAN detour or is a plain connect, given when the
    /// last detour ran (null when none has) and the time now, both in milliseconds from any clock
    /// that only goes forwards. A pure rule so the rate limit can be read without a console: one
    /// detour, then plain attempts until <see cref="DetourInterval"/> has passed.
    /// </summary>
    public static bool ShouldDetour(long? lastDetourMs, long nowMs) =>
        lastDetourMs is not { } last || nowMs - last >= (long)DetourInterval.TotalMilliseconds;

    /// <summary>
    /// The qwark.sprx a load sends: the settings' path, or the copy the release puts beside the
    /// executable when that path is empty or relative. Not a file check, only a full path, so the
    /// caller can say where it looked when there is nothing there.
    /// </summary>
    public static string ResolveSprx(string? path)
    {
        var trimmed = (path ?? string.Empty).Trim().Trim('"');
        if (trimmed.Length == 0) trimmed = WebManLoader.SprxName;
        return Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(AppContext.BaseDirectory, trimmed);
    }

    /// <summary>
    /// True for the failures that mean nothing is listening on qwark's port: the console refused
    /// the connection, or never answered. A module that answered and then refused something (a
    /// handshake it did not like, a protocol mismatch) is not one of these, and loading a second
    /// copy of it would not help, so those are left to the caller.
    /// </summary>
    public static bool NothingListening(Exception error) =>
        error is SocketException or TimeoutException or OperationCanceledException;

    /// <summary>
    /// The connect the user's mode asks for: the webMAN sequence below, or the plain connect that
    /// never mentions webMAN at all. The mode is the only difference between the two, so it is
    /// decided here rather than at each of the places that connect.
    /// </summary>
    public static Task<Ps3ConnectOutcome> RunAsync(
        bool webMan,
        string ip,
        Func<Task> connect,
        Func<Task<VshPluginStatus>> pluginStatus,
        Func<Task> load,
        Func<TimeSpan, Task> wait,
        Action<Ps3ConnectStep, string> say) =>
        webMan
            ? RunAsync(ip, connect, pluginStatus, load, wait, say)
            : ConnectOnlyAsync(ip, connect, say);

    /// <summary>
    /// Standalone mode: one attempt at qwark's port and nothing else. Nothing is asked of webMAN,
    /// not even whether the module is there, because a console that boots qwark itself has no
    /// webMAN step to take and may have no webMAN at all. A port that does not answer is the whole
    /// of the failure; anything the module itself threw is left to propagate, as it is above.
    /// </summary>
    public static async Task<Ps3ConnectOutcome> ConnectOnlyAsync(
        string ip,
        Func<Task> connect,
        Action<Ps3ConnectStep, string> say)
    {
        say(Ps3ConnectStep.Connect, $"Connecting to qwark on {ip}");
        try
        {
            await connect().ConfigureAwait(false);
            return new Ps3ConnectOutcome(true, Ps3ConnectStep.Connect, $"Connected to {ip}");
        }
        catch (Exception error) when (NothingListening(error))
        {
            return new Ps3ConnectOutcome(false, Ps3ConnectStep.Connect,
                $"Nothing answered on {ip}: {error.Message}");
        }
    }

    /// <summary>
    /// Runs the webMAN sequence. <paramref name="say"/> is called with each step as it begins, so
    /// the user can see which of them is taking the time; the outcome carries the last word, and
    /// names the step it stopped at when that word is a failure. Anything a connect attempt throws
    /// that is not <see cref="NothingListening"/> is left to propagate: the module answered, and
    /// what it said is the error worth showing.
    /// <para>
    /// Three answers, three routes. No slot holds the module, so load it and connect. A slot holds
    /// it, so connect, and a silence then means a module that is loaded and not running rather than
    /// one that is missing. webMAN could not be asked, so try the port and fall back to loading it
    /// blind, which is the old behaviour and the only thing left to try.
    /// </para>
    /// </summary>
    public static async Task<Ps3ConnectOutcome> RunAsync(
        string ip,
        Func<Task> connect,
        Func<Task<VshPluginStatus>> pluginStatus,
        Func<Task> load,
        Func<TimeSpan, Task> wait,
        Action<Ps3ConnectStep, string> say)
    {
        say(Ps3ConnectStep.Ask, $"Asking webMAN on {ip} which slot holds {WebManLoader.SprxName}");

        VshPluginStatus status;
        string? askProblem = null;
        try
        {
            status = await pluginStatus().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            status = VshPluginStatus.Unknown;
            askProblem = error.Message;
        }

        if (status.State == VshPluginState.NotLoaded)
        {
            var loaded = await LoadThenConnectAsync(ip, connect, load, wait, say,
                $"No VSH slot holds {WebManLoader.SprxName}: loading it").ConfigureAwait(false);
            return loaded;
        }

        // Either it is in a slot, or webMAN could not be asked. Both mean something may already be
        // listening, so the port is tried before anything is uploaded.
        say(Ps3ConnectStep.Connect, status.IsLoaded
            ? $"{WebManLoader.SprxName} is in slot {status.SlotText}: connecting to {ip}"
            : $"Connecting to qwark on {ip}");

        try
        {
            await connect().ConfigureAwait(false);
            return new Ps3ConnectOutcome(true, Ps3ConnectStep.Connect, $"Connected to {ip}");
        }
        catch (Exception error) when (NothingListening(error))
        {
            if (status.IsLoaded)
            {
                // The module is in a slot and its port is shut. Sending another copy would land in
                // a different slot and fight this one for the port, so say what is wrong instead.
                return new Ps3ConnectOutcome(false, Ps3ConnectStep.Connect,
                    $"{WebManLoader.SprxName} is loaded in slot {status.SlotText}"
                    + (status.Path.Length > 0 ? $" from {status.Path}" : string.Empty)
                    + $", but nothing answered on {ip}: {error.Message}. The module is loaded and not "
                    + "running; unload it in webMAN, or restart the console, and try again.");
            }
        }

        return await LoadThenConnectAsync(ip, connect, load, wait, say,
            askProblem is null
                ? $"webMAN did not say whether {WebManLoader.SprxName} is loaded: loading it"
                : $"Could not ask webMAN ({askProblem}): loading {WebManLoader.SprxName} anyway")
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The other half of the sequence: send the module across, give it a moment, and connect. Used
    /// both when webMAN says no slot holds it and when webMAN could not be asked at all, which is
    /// why the reason it is being loaded is the caller's to word.
    /// </summary>
    private static async Task<Ps3ConnectOutcome> LoadThenConnectAsync(
        string ip,
        Func<Task> connect,
        Func<Task> load,
        Func<TimeSpan, Task> wait,
        Action<Ps3ConnectStep, string> say,
        string why)
    {
        say(Ps3ConnectStep.Load, why);
        try
        {
            await load().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return new Ps3ConnectOutcome(false, Ps3ConnectStep.Load,
                $"Loading qwark through webMAN failed: {error.Message}");
        }

        say(Ps3ConnectStep.Reconnect, "Waiting for qwark to come up");
        await wait(LoadWait).ConfigureAwait(false);

        try
        {
            await connect().ConfigureAwait(false);
            return new Ps3ConnectOutcome(true, Ps3ConnectStep.Reconnect, $"Connected to {ip}");
        }
        catch (Exception error) when (NothingListening(error))
        {
            return new Ps3ConnectOutcome(false, Ps3ConnectStep.Reconnect,
                $"qwark was loaded through webMAN but {ip} still did not answer: {error.Message}");
        }
    }
}
