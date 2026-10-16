using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>
/// What one manip event says on screen: the sentence, and the toast it is, or null for one that
/// only shows on the manips area's status line.
/// </summary>
public readonly record struct ManipNotice(ManipEventKind Kind, string Text, ToastKind? Toast);

/// <summary>
/// Turns MANIP_LIST's last event into at most one toast per event. The console keeps one event and
/// a sequence number that moves on with every new one, so the client remembers the last number it
/// saw and speaks only when it changes.
/// <para>
/// qwark's number belongs to the module, not to a game or a boot: it starts again only when the
/// module does, and a game boot clears the event's kind and group but not the number (PROTOCOL.md
/// section 5.14). So the number is followed for as long as one connection lasts, across reboots and
/// game switches, and every change in that time is a new event. A new connection starts from
/// scratch (<see cref="Forget"/>), because nothing on this side can tell the module it reached from
/// a restarted one, whose numbers may meet the old ones by chance: the first answer after it is the
/// starting point and says nothing. An event from before that answer happened while this client was
/// not looking, and the console showed its own notification for it.
/// </para>
/// </summary>
public sealed class ManipEvents
{
    private ushort? _lastSeq;

    /// <summary>
    /// The session a MANIP_LIST answer was asked for: the title and the boot. An answer that comes
    /// back after the session has moved on describes a game that is no longer running and is not
    /// taken; it has nothing to do with which events are new.
    /// </summary>
    public static string KeyFor(SessionInfo session) => $"{session.TitleId}#{session.Generation}";

    /// <summary>A new connection: the next answer is the starting point, whatever its number.</summary>
    public void Forget() => _lastSeq = null;

    /// <summary>
    /// The event this answer brings that has not been seen before, or null: the first answer since
    /// <see cref="Forget"/>, the same sequence number again, or a new event with nothing to say.
    /// </summary>
    public ManipNotice? Observe(ManipList list)
    {
        if (_lastSeq is not { } last)
        {
            _lastSeq = list.EventSeq;
            return null;
        }

        // Not "greater than": the number is sixteen bits and wraps, and any change is a new event.
        if (last == list.EventSeq) return null;

        _lastSeq = list.EventSeq;
        return NoticeFor(list);
    }

    /// <summary>The list's last event in words, whether or not it is new. Null for kind 0 and unknown kinds.</summary>
    public static ManipNotice? NoticeFor(ManipList list) =>
        NoticeFor(list.EventKind, list.GroupName(list.EventGroup));

    /// <summary>
    /// The sentence for each kind. The refusals are errors: the runner asked for manips and has
    /// not got them. A group's first application in a boot is worth a line. Kind 2 is any later
    /// one, the per-run writes at a run's start or a setup put in again by Apply now or by picking
    /// the group again, so it says only that the group's manips went in, and is left to the status
    /// line.
    /// </summary>
    public static ManipNotice? NoticeFor(ManipEventKind kind, string group) => kind switch
    {
        ManipEventKind.Activated => new(kind, $"{group} manips activated", ToastKind.Info),
        ManipEventKind.PerRunApplied => new(kind, Applied(group), null),
        ManipEventKind.RefusedNotChallengeFile =>
            new(kind, $"{group} wasn't applied: this file isn't a challenge-mode file", ToastKind.Error),
        ManipEventKind.RefusedChallengeFile =>
            new(kind, $"{group} wasn't applied: this file is a challenge-mode file", ToastKind.Error),
        ManipEventKind.RefusedNoFile => new(kind, $"{group} wasn't applied: load a file first", ToastKind.Error),
        _ => null,
    };

    /// <summary>What Apply now says when it went through and the console reported nothing to toast.</summary>
    public static string Applied(string group) => $"{group} manips applied";

    /// <summary>What Apply now says when the console refused it and no event said why.</summary>
    public static string NotApplied(string group) => $"{group} wasn't applied";
}
