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
/// saw per session and speaks only when it changes.
/// <para>
/// The first answer of a session is taken as the starting point and says nothing: an event that
/// happened before this client looked (a connection made mid-run, a reconnect after the game
/// rebooted) is old news, and the console has already shown its own notification for it.
/// </para>
/// </summary>
public sealed class ManipEvents
{
    private readonly Dictionary<string, ushort> _lastSeq = new(StringComparer.Ordinal);

    /// <summary>
    /// A session: the title and the boot. A reboot is a new session, so its first answer starts
    /// from scratch however the console numbers its events across one.
    /// </summary>
    public static string KeyFor(SessionInfo session) => $"{session.TitleId}#{session.Generation}";

    /// <summary>
    /// The event this answer brings that has not been seen before, or null: the session's first
    /// answer, the same sequence number again, or a new event with nothing to say.
    /// </summary>
    public ManipNotice? Observe(string key, ManipList list)
    {
        if (!_lastSeq.TryGetValue(key, out var last))
        {
            _lastSeq[key] = list.EventSeq;
            return null;
        }

        // Not "greater than": the number is sixteen bits and wraps, and any change is a new event.
        if (last == list.EventSeq) return null;

        _lastSeq[key] = list.EventSeq;
        return NoticeFor(list);
    }

    /// <summary>The list's last event in words, whether or not it is new. Null for kind 0 and unknown kinds.</summary>
    public static ManipNotice? NoticeFor(ManipList list) =>
        NoticeFor(list.EventKind, list.GroupName(list.EventGroup));

    /// <summary>
    /// The sentence for each kind. The refusals are errors: the runner asked for manips and has
    /// not got them. The first activation of a boot is worth a line; a per-run reapply happens on
    /// every load and is left to the status line.
    /// </summary>
    public static ManipNotice? NoticeFor(ManipEventKind kind, string group) => kind switch
    {
        ManipEventKind.Activated => new(kind, $"{group} manips activated", ToastKind.Info),
        ManipEventKind.PerRunApplied => new(kind, $"{group} per-run manips applied", null),
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
