using System.Net.Sockets;
using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>Which part of the PS3 connect sequence a message belongs to.</summary>
public enum Ps3ConnectStep
{
    /// <summary>The first attempt at qwark's own port, which is all a loaded console ever needs.</summary>
    Connect,

    /// <summary>Asking webMAN whether the module is loaded, once nothing has answered.</summary>
    Ask,

    /// <summary>Sending the SPRX through webMAN and asking it to load it.</summary>
    Load,

    /// <summary>The attempt that follows the load, once the module has had time to come up.</summary>
    Reconnect,
}

/// <summary>Whether the sequence ended connected, where it stopped, and what to say about it.</summary>
public readonly record struct Ps3ConnectOutcome(bool Connected, Ps3ConnectStep Step, string Message);

/// <summary>
/// What pressing Connect against a console does: try qwark first, and only when nothing answers go
/// round through webMAN, load the module and try again. It is written as a sequence of delegates
/// rather than against <see cref="AppState"/> so the order of the steps, and what each failure
/// means, can be checked without a console and without touching the network.
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
        Func<Task<bool>> isLoaded,
        Func<Task> load,
        Func<TimeSpan, Task> wait,
        Action<Ps3ConnectStep, string> say) =>
        webMan
            ? RunAsync(ip, connect, isLoaded, load, wait, say)
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
    /// Runs the sequence. <paramref name="say"/> is called with each step as it begins, so the user
    /// can see which of them is taking the time; the outcome carries the last word, and names the
    /// step it stopped at when that word is a failure. Anything the first attempt throws that is
    /// not <see cref="NothingListening"/> is left to propagate: the module answered, and what it
    /// said is the error worth showing.
    /// </summary>
    public static async Task<Ps3ConnectOutcome> RunAsync(
        string ip,
        Func<Task> connect,
        Func<Task<bool>> isLoaded,
        Func<Task> load,
        Func<TimeSpan, Task> wait,
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
            say(Ps3ConnectStep.Ask, $"Nothing answered on {ip}: asking webMAN whether qwark is loaded");
        }

        bool loaded;
        try
        {
            loaded = await isLoaded().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return new Ps3ConnectOutcome(false, Ps3ConnectStep.Ask,
                $"Could not ask webMAN whether qwark is loaded: {error.Message}");
        }

        // A module webMAN already lists is not loaded a second time: it is there, and the port may
        // simply not have been open yet when the first attempt went out. It gets the same wait and
        // the same second attempt, and if that fails too the message says which of the two it was.
        if (loaded)
        {
            say(Ps3ConnectStep.Load, "webMAN reports qwark.sprx loaded, so it is not sent again");
        }
        else
        {
            say(Ps3ConnectStep.Load, "webMAN does not list qwark.sprx: loading it");
            try
            {
                await load().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return new Ps3ConnectOutcome(false, Ps3ConnectStep.Load,
                    $"Loading qwark through webMAN failed: {error.Message}");
            }
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
            return new Ps3ConnectOutcome(false, Ps3ConnectStep.Reconnect, loaded
                ? $"webMAN says qwark.sprx is loaded, but {ip} did not answer: {error.Message}"
                : $"qwark was loaded through webMAN but {ip} still did not answer: {error.Message}");
        }
    }
}
