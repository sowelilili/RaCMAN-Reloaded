using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>What the last look at the console's boot_plugins.txt found.</summary>
public enum BootPluginState
{
    /// <summary>Not looked at yet, or the mode is not standalone.</summary>
    Unknown,

    /// <summary>A look is under way.</summary>
    Checking,

    /// <summary>boot_plugins.txt names a qwark.sprx: the console loads qwark at boot.</summary>
    Listed,

    /// <summary>boot_plugins.txt names none, or there is no list: qwark is not installed to boot.</summary>
    NotListed,

    /// <summary>The list could not be read (no webMAN answering, the console off).</summary>
    Failed,
}

/// <summary>
/// Whether the console boots qwark, for the Settings page's Connection mode row: standalone mode
/// expects the console to load qwark itself, and the boot plugin list is how it does. The list is
/// read with the client's own reading of it (<see cref="SprxUpdate.ListedBootPath"/>, the rule the
/// standalone update and the webMAN load go by) through qwark's file ops while a console is
/// connected and over webMAN's FTP otherwise, once at a time and only when asked
/// (<see cref="AppState.CheckBootPlugins"/>): on connecting, on opening the Console tab, on picking
/// Standalone and after an install or a removal, never on a timer. The state is the render
/// thread's; the read itself is not.
/// </summary>
public sealed class BootPluginCheck
{
    public BootPluginState State { get; private set; } = BootPluginState.Unknown;

    /// <summary>The path the list names for qwark, when it names one.</summary>
    public string? ListedPath { get; private set; }

    /// <summary>Why the last look failed, when it did.</summary>
    public string? Problem { get; private set; }

    /// <summary>The console the last look was at.</summary>
    public string? Host { get; private set; }

    /// <summary>How many looks have been started, for the tests that hold it to "not every frame".</summary>
    public int Started { get; private set; }

    /// <summary>Whether the row warns: only on a list that was read and does not name qwark.</summary>
    public bool WarnNotInstalled => State == BootPluginState.NotListed;

    /// <summary>The verdict on a boot_plugins.txt's text; null or empty is a console with no list.</summary>
    public static (BootPluginState State, string? Path) Judge(string? text)
    {
        string? path = SprxUpdate.ListedBootPath(text);
        return path is null ? (BootPluginState.NotListed, null) : (BootPluginState.Listed, path);
    }

    /// <summary>A look at <paramref name="host"/> starts; false while one is already under way.</summary>
    public bool Begin(string host)
    {
        if (State == BootPluginState.Checking) return false;
        State = BootPluginState.Checking;
        Host = host;
        Problem = null;
        Started++;
        return true;
    }

    /// <summary>The list as read.</summary>
    public void Complete(string? text)
    {
        (State, ListedPath) = Judge(text);
        Problem = null;
    }

    /// <summary>The list could not be read.</summary>
    public void Fail(string problem)
    {
        State = BootPluginState.Failed;
        ListedPath = null;
        Problem = problem;
    }

    /// <summary>Back to not known: the mode left standalone, or the client is now pointed elsewhere.</summary>
    public void Forget()
    {
        if (State == BootPluginState.Checking) return;
        State = BootPluginState.Unknown;
        ListedPath = null;
        Problem = null;
    }
}
