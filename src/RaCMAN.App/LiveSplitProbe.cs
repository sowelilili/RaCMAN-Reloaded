using System.Net.Sockets;

namespace RaCMAN.App;

/// <summary>
/// Looks for LiveSplit's TCP server while the autosplitter is on and "Connect automatically" is
/// ticked, and hands the port over to <see cref="LiveSplitClient"/> the moment something answers on
/// it. It is what makes starting LiveSplit and starting a run two separate chores again: neither
/// has to come first, and neither has to be followed by a press of Connect.
/// <para>
/// A look is a bare TCP connect with a short timeout and not one byte written. It costs a socket
/// that is thrown away again, and when it fails <b>nobody is told</b>: no popup, no toast, no
/// status line of its own. That silence is the whole point. LiveSplit not being up yet is the
/// everyday case and is nobody's mistake; the popup belongs to an attempt the user made and to a
/// LiveSplit that answers and cannot be driven, and neither of those comes through here.
/// </para>
/// <para>
/// One look at a time, and one look only: <see cref="Looking"/> is what stops a slow connect from
/// stacking a second one behind it. The first look after the switch comes on — or after a
/// connection drops, which is the same thing a moment later — happens at once, and every look after
/// that is <see cref="Interval"/> apart, which is a calm rate for something that runs for as long
/// as the client does.
/// </para>
/// <para>
/// Nothing here touches ImGui: the look runs on a background task and the connection it decides on
/// is started through the post hook, which is <see cref="AppState.Post"/> in the app.
/// </para>
/// </summary>
public sealed class LiveSplitProbe
{
    /// <summary>How long between two looks once one has come back empty.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long one look may take. A server on this machine answers in microseconds or not at all,
    /// and a look that is still waiting after this is one that has failed.
    /// </summary>
    public static readonly TimeSpan LookTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Settings _settings;
    private readonly LiveSplitClient _liveSplit;
    private readonly Action<Action> _post;
    private readonly TimeSpan _interval;

    private double _sinceLook;
    private int _looking;
    private int _looks;
    private int _found;

    /// <param name="post">
    /// Hands the connection back to the render thread, which is where the panel reads the client's
    /// status. In the app this is <see cref="AppState.Post"/>.
    /// </param>
    /// <param name="interval">
    /// How long between looks, for a test that cannot sit through <see cref="Interval"/>. The app
    /// leaves it alone.
    /// </param>
    public LiveSplitProbe(Settings settings, LiveSplitClient liveSplit, Action<Action> post, TimeSpan? interval = null)
    {
        _settings = settings;
        _liveSplit = liveSplit;
        _post = post;
        _interval = interval ?? Interval;

        // The first tick that finds something to look for looks straight away.
        _sinceLook = _interval.TotalSeconds;
    }

    /// <summary>How many looks have finished, whatever they found. Debug aid and test hook.</summary>
    public int Looks => Volatile.Read(ref _looks);

    /// <summary>How many of those found something listening and handed it to the client.</summary>
    public int Found => Volatile.Read(ref _found);

    /// <summary>True while a look is in flight, which is why a second one is never started.</summary>
    public bool Looking => Volatile.Read(ref _looking) != 0;

    /// <summary>Drives the looking off the render loop, the way the autosplitter's own poll is driven.</summary>
    public void Tick(double deltaSeconds)
    {
        var autosplit = _settings.Autosplit;

        // Nothing to look for: the autosplitter is off, the user does not want this, LiveSplit is
        // already there, or the build on the other end is one this client has turned away and would
        // turn away again — that one is not retried, here or anywhere else, because it will not have
        // changed by the time the next look came round. The clock is left due rather than reset, so
        // a connection that drops is looked for on the very next frame and not an interval later.
        if (!autosplit.Enabled || !autosplit.ConnectsAutomatically
            || _liveSplit.IsConnected || _liveSplit.TooOld)
        {
            _sinceLook = _interval.TotalSeconds;
            return;
        }

        _sinceLook += deltaSeconds;
        if (_sinceLook < _interval.TotalSeconds) return;
        if (Interlocked.CompareExchange(ref _looking, 1, 0) != 0) return;

        _sinceLook = 0;
        string host = autosplit.Host;
        int port = autosplit.Port;
        _ = Task.Run(() => LookAsync(host, port));
    }

    /// <summary>
    /// One look. The socket is opened, found to be answered or not, and closed again either way:
    /// the connection that matters is the client's own, handshake and all, because everything this
    /// client goes on to say belongs to the LiveSplit development build and the handshake is where
    /// that is settled.
    /// </summary>
    private async Task LookAsync(string host, int port)
    {
        try
        {
            using var socket = new TcpClient { NoDelay = true };
            using var timeout = new CancellationTokenSource(LookTimeout);
            await socket.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _found);
        }
        catch (Exception)
        {
            // Nothing is listening, which is what "LiveSplit is not running yet" looks like from
            // here. It is not a failure anybody is told about: the next look is an interval away.
            return;
        }
        finally
        {
            Interlocked.Increment(ref _looks);
            Volatile.Write(ref _looking, 0);
        }

        // Something answered. One attempt, and nothing counted if it has gone again in the
        // meantime: this probe is what looks next, and it does that without a word.
        _post(() => _liveSplit.Start(host, port, retry: false, countFailures: false));
    }
}
