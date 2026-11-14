using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>
/// The Settings page's Console tab's state that is not a SessionInfo bit: whether the console boots
/// qwark (standalone mode's boot plugin list), the console's Apply toggles on game boot, which only
/// a BOOT_APPLY read says, and a boot's question under its Ask.
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

    /// <summary>The console's <c>boot.apply</c> as last read, or null before a read (or without a console that has it).</summary>
    public BootApplyMode? BootApply { get; private set; }

    /// <summary>The connection and profile the last read was for, so a read happens once for each.</summary>
    private (SessionInfo? Hello, byte ProfileSeq)? _bootApplyReadFor;

    /// <summary>
    /// Reads <c>boot.apply</c> once per connection and once per profile change (it is a per-user
    /// key), from a module that has it (qwark revision 1.33); forgets it without a connection.
    /// </summary>
    private void TickBootApply()
    {
        if (!Connected)
        {
            BootApply = null;
            _bootApplyReadFor = null;
            return;
        }

        if (!Session.HasSettings133 || Session.QwarkVersion == 0) return;

        var key = (Hello, Session.ProfileSeq);
        if (_bootApplyReadFor == key) return;
        _bootApplyReadFor = key;
        RefreshBootApply();
    }

    /// <summary>One BOOT_APPLY read; a background read, so its refusals say nothing.</summary>
    public void RefreshBootApply() =>
        Run(() => Client.BootApplyReadAsync(), mode => BootApply = mode, quiet: true);

    /// <summary>
    /// The dropdown's pick: one BOOT_APPLY, to a console that has it; the dropdown follows once the
    /// console has said OK. False when there was nothing to send.
    /// </summary>
    public bool SetBootApply(BootApplyMode mode)
    {
        if (!Connected || !Session.HasSettings133) return false;

        Run(async () =>
        {
            await Client.BootApplyAsync(mode).ConfigureAwait(false);
            return mode;
        }, done => BootApply = done, quiet: false);
        return true;
    }

    /// <summary>Set when a boot's question should be put to the user; the modal takes it.</summary>
    public bool BootAskRequested { get; set; }

    private bool _lastBootAsk;

    /// <summary>
    /// A boot under Ask with this client connected (flags3 bit7): the question goes up once per
    /// boot, after the previous-session prompt if that one is up, and comes down when the console
    /// says it has been answered (by this client or another).
    /// </summary>
    private void TickBootAsk()
    {
        bool pending = Connected && Session.BootAskPending;
        if (pending && !_lastBootAsk && !ConsoleBusy && !UnknownGame)
        {
            _lastBootAsk = true;
            BootAskRequested = true;
        }
        else if (!pending && _lastBootAsk)
        {
            _lastBootAsk = false;
            BootAskRequested = false;
        }
    }

    /// <summary>The user's answer: one BOOT_ASK, its refusal toasted like any request.</summary>
    public void AnswerBootAsk(bool apply)
    {
        BootAskRequested = false;
        Run(() => Client.BootAskAsync(apply), apply ? "On-boot toggles and mods applied" : null);
    }
}
