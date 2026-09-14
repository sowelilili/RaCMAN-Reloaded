using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>
/// What a refused request is allowed to say on screen. The repo rule is that every control shows
/// the status it got back and nothing swallows a non-OK status; this is the whole of the exception
/// to it, kept in one pure function so the rule can be read, and tested, in one place.
/// <para>
/// The case it exists for is PROTOCOL.md section 1.1. While a game is starting or ending, qwark has
/// no request buffers to spare: everything but HELLO, HEARTBEAT, SUBSCRIBE, UNSUBSCRIBE and
/// GET_STATE is answered BUSY until the session is INGAME. A launch therefore refuses every
/// background read the client happens to make during it, and a client that toasted each one buried
/// the user in "The console refused that: Busy" every time a game booted. Work nobody asked for
/// says nothing; a button still answers to the person who pressed it, in words rather than in a
/// status code.
/// </para>
/// </summary>
public static class StatusToast
{
    /// <summary>
    /// The statuses a background read is entitled to get and not report: the session moved under
    /// it (NOT_INGAME), this game has no such table (UNSUPPORTED) or this module has no such op
    /// (UNKNOWN_OP), and the launch refusal above (BUSY).
    /// </summary>
    public static bool IsQuietStatus(Status status) => status is
        Status.NotIngame or Status.Unsupported or Status.UnknownOp or Status.Busy;

    /// <summary>
    /// What a BUSY means to the user when the console is not in a game: not a full ring and not a
    /// save transfer, but a game on its way in or out.
    /// </summary>
    public static string WhileBusy(SessionState state) => state switch
    {
        SessionState.Booting => "The console is starting a game; try again in a moment",
        SessionState.Quitting => "The console is putting a game away; try again in a moment",
        _ => "The console is busy; try again in a moment",
    };

    /// <summary>
    /// The toast for a status that came back, or null for "say nothing at all".
    /// <paramref name="quiet"/> marks work nobody asked for: a timed re-read, a probe, the catch-up
    /// after a reboot. <paramref name="state"/> is the session the answer arrived in, and
    /// <paramref name="debug"/> is the "Show debug information" setting, which puts the opcode in
    /// front of the status for the one person who wants to know which request it was.
    /// </summary>
    public static string? For(Status status, Opcode opcode, SessionState state, bool quiet, bool debug)
    {
        if (status == Status.Ok) return null;
        if (quiet && IsQuietStatus(status)) return null;

        // A button, and a console with no game up: the raw BUSY is section 1.1 rather than the ring
        // or a transfer, and "Busy" on its own is not something anyone can act on.
        if (status == Status.Busy && state != SessionState.Ingame) return WhileBusy(state);

        return debug ? $"{opcode}: {status}" : $"The console refused that: {status}";
    }
}

