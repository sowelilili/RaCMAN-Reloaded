using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>
/// The Settings page's Console tab's state that is not a SessionInfo bit: whether the console boots
/// qwark (standalone mode's boot plugin list), and the console's Apply toggles on game reboot, which
/// only a REBOOT_APPLY read says.
/// </summary>
public sealed partial class AppState
{
    /// <summary>How long a look at boot_plugins.txt may take before it counts as no answer.</summary>
    public static readonly TimeSpan BootPluginReadTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Whether the console's boot_plugins.txt names qwark, as last looked at.</summary>
    public BootPluginCheck BootPlugins { get; } = new();

    /// <summary>
    /// Looks at the console's boot_plugins.txt once, when the console is set up as standalone:
    /// through qwark's own file ops while a console is connected (a standalone console need have no
    /// webMAN), over webMAN's FTP otherwise. Called on connecting, on opening the Console tab, on
    /// picking Standalone and after an install or a removal; never every frame. False when nothing
    /// was started: another mode, no address, or a look already under way.
    /// </summary>
    public bool CheckBootPlugins()
    {
        if (Settings.Rpcs3Target || !Settings.Ps3.StandaloneConnection)
        {
            BootPlugins.Forget();
            return false;
        }

        bool viaQwark = Connected && !Session.IsEmulator && Client.Host is not null;
        string host = ((viaQwark ? Client.Host : Panels.ConnectionPanel.CurrentHost(this)) ?? string.Empty).Trim();
        if (host.Length == 0 || !BootPlugins.Begin(host)) return false;

        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(BootPluginReadTimeout);
            try
            {
                string text = viaQwark
                    ? await SprxUpdate.ReadBootPluginsAsync(Client, cts.Token).ConfigureAwait(false)
                    : await WebMan.ReadBootPluginsAsync(host, cts.Token).ConfigureAwait(false);
                Post(() => BootPlugins.Complete(text));
            }
            catch (Exception ex)
            {
                string why = ex is OperationCanceledException ? "no answer" : ex.Message;
                Post(() => BootPlugins.Fail(viaQwark
                    ? $"couldn't read boot_plugins.txt from your console ({why})"
                    : $"couldn't read boot_plugins.txt through webMAN on {host} ({why})"));
            }
        });
        return true;
    }

    /// <summary>
    /// The console's <c>reboot.apply</c> as last read, or null before a read (or without a console
    /// that has it). The console acts on it, not this client: under Always it re-applies the previous
    /// session itself and under Never it drops it, so neither ever raises PREVIOUS_PENDING, and the
    /// previous-session prompt (<see cref="Panels.PreviousSessionModal"/>) is Ask's question.
    /// </summary>
    public RebootApplyMode? RebootApply { get; private set; }

    /// <summary>The connection and profile the last read was for, so a read happens once for each.</summary>
    private (SessionInfo? Hello, byte ProfileSeq)? _rebootApplyReadFor;

    /// <summary>
    /// Reads <c>reboot.apply</c> once per connection and once per profile change (it is a per-user
    /// key), from a module that has it (qwark revision 1.33); forgets it without a connection.
    /// </summary>
    private void TickRebootApply()
    {
        if (!Connected)
        {
            RebootApply = null;
            _rebootApplyReadFor = null;
            return;
        }

        if (!Session.HasSettings133 || Session.QwarkVersion == 0) return;

        var key = (Hello, Session.ProfileSeq);
        if (_rebootApplyReadFor == key) return;
        _rebootApplyReadFor = key;
        RefreshRebootApply();
    }

    /// <summary>One REBOOT_APPLY read; a background read, so its refusals say nothing.</summary>
    public void RefreshRebootApply() =>
        Run(() => Client.RebootApplyReadAsync(), mode => RebootApply = mode, quiet: true);

    /// <summary>
    /// The dropdown's pick: one REBOOT_APPLY, to a console that has it; the dropdown follows once the
    /// console has said OK. False when there was nothing to send.
    /// </summary>
    public bool SetRebootApply(RebootApplyMode mode)
    {
        if (!Connected || !Session.HasSettings133) return false;

        Run(async () =>
        {
            await Client.RebootApplyAsync(mode).ConfigureAwait(false);
            return mode;
        }, done => RebootApply = done, quiet: false);
        return true;
    }
}
