using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>The three requests a feature control sends, which are the three a CONFIRM flag can stand in front of.</summary>
public enum FeatureRequest
{
    /// <summary>FEATURE_TRIGGER of an ACTION.</summary>
    Trigger,

    /// <summary>FEATURE_SET, of a TOGGLE's 1 or 0 or any other kind's value.</summary>
    Set,

    /// <summary>FEATURE_SET_AUTO, a toggle's "on boot" box.</summary>
    SetAuto,
}

/// <summary>
/// One question waiting for the user's answer: the feature, the request that waits behind it, and
/// the console's wording for it once FEATURE_CONFIRM_TEXT has answered.
/// </summary>
public sealed class PendingConfirm
{
    internal PendingConfirm(Feature feature, FeatureRequest request, Func<Task> send, string? successMessage,
        GameId game, string title, uint generation)
    {
        Feature = feature;
        Request = request;
        Send = send;
        SuccessMessage = successMessage;
        Game = game;
        Title = title;
        Generation = generation;
    }

    public Feature Feature { get; }

    public FeatureRequest Request { get; }

    /// <summary>The request itself, sent on Continue and never otherwise.</summary>
    internal Func<Task> Send { get; }

    internal string? SuccessMessage { get; }

    /// <summary>The game the question was asked in. An answer only counts for that same boot.</summary>
    internal GameId Game { get; }

    internal string Title { get; }

    internal uint Generation { get; }

    /// <summary>True until FEATURE_CONFIRM_TEXT has answered or failed. Continue waits for it.</summary>
    public bool Loading { get; internal set; } = true;

    /// <summary>The console's own wording, or null while loading and when it could not be read.</summary>
    public string? Text { get; internal set; }

    /// <summary>Why the console's wording could not be read (the status, or the error), or null.</summary>
    public string? Problem { get; internal set; }

    /// <summary>What the dialog says: the console's wording, or the generic question when there is none.</summary>
    public string Body => Text is { } text && !string.IsNullOrWhiteSpace(text)
        ? text
        : FeatureConfirmations.Fallback(Feature.Label);
}

/// <summary>
/// The "confirm first" rule of revision 1.21. A feature the console flags CONFIRM does something
/// RaCMAN cannot take back (UYA's trophy reset deletes the console's trophy record), so the user is
/// asked before a FEATURE_TRIGGER of such an ACTION, and before a FEATURE_SET or FEATURE_SET_AUTO that
/// turns such a TOGGLE on. Turning one off, and every request for a feature without the flag, goes
/// straight out as it always did.
/// <para>
/// Every control that draws a feature sends through here rather than through the client, so the
/// rule holds wherever the layout or MANIP_LIST put the feature: a Game page section, a heading in
/// the manips area, an Unlocks tab, the quick block. The question itself is
/// <see cref="Panels.FeatureConfirmModal"/>, which reads <see cref="Pending"/>.
/// </para>
/// <para>
/// Nothing is sent until Continue: not the request, and nothing in its place. The checkbox a
/// toggle or an "on boot" box is drawn with comes from telemetry, so a question that is cancelled
/// leaves it exactly as it was. A question outlives nothing: a game that changes, reboots or goes
/// away while it is open cancels it, since the answer was about that game.
/// </para>
/// </summary>
public sealed class FeatureConfirmations
{
    private readonly AppState _state;

    public FeatureConfirmations(AppState state)
    {
        _state = state;
    }

    /// <summary>The question on screen, or null.</summary>
    public PendingConfirm? Pending { get; private set; }

    /// <summary>How many questions were asked this run, for the headless summary and the tests.</summary>
    public int Asked { get; private set; }

    /// <summary>How many were answered Continue, and so sent.</summary>
    public int Continued { get; private set; }

    /// <summary>How many were cancelled, by the user or by the game going away.</summary>
    public int Cancelled { get; private set; }

    /// <summary>"asked/continued/cancelled", for the headless summary.</summary>
    public string Summary => $"{Asked}/{Continued}/{Cancelled}";

    /// <summary>
    /// The question when the console's own wording cannot be had: an older module that does not
    /// know FEATURE_CONFIRM_TEXT, a refusal, a timeout.
    /// </summary>
    public static string Fallback(string label) => $"{label}: this can't be undone from RaCMAN. Continue?";

    /// <summary>Whether <paramref name="request"/> of <paramref name="value"/> on <paramref name="feature"/> has to be confirmed first.</summary>
    public static bool Needed(Feature feature, FeatureRequest request, uint value) => request switch
    {
        FeatureRequest.Trigger => feature.ConfirmsTrigger,
        FeatureRequest.Set => feature.ConfirmsSet(value),
        FeatureRequest.SetAuto => feature.ConfirmsAuto(value != 0),
        _ => false,
    };

    /// <summary>FEATURE_TRIGGER, asked about first for a CONFIRM action.</summary>
    public void Trigger(Feature feature)
    {
        byte id = feature.Id;
        Send(feature, FeatureRequest.Trigger, 1, () => _state.Client.FeatureTriggerAsync(id));
    }

    /// <summary>FEATURE_SET, asked about first when it turns a CONFIRM toggle on.</summary>
    public void Set(Feature feature, uint value)
    {
        byte id = feature.Id;
        Send(feature, FeatureRequest.Set, value, () => _state.Client.FeatureSetAsync(id, value));
    }

    /// <summary>FEATURE_SET_AUTO, asked about first when it ticks a CONFIRM toggle's "on boot".</summary>
    public void SetAuto(Feature feature, bool on)
    {
        byte id = feature.Id;
        Send(feature, FeatureRequest.SetAuto, on ? 1u : 0u, () => _state.Client.FeatureSetAutoAsync(id, on));
    }

    /// <summary>
    /// The general form, for a control whose request does more than the one op (the set-aside
    /// buttons, the Unlocks panel's re-read): <paramref name="send"/> goes out through
    /// <c>AppState.Run</c> at once when no question is needed, and on
    /// Continue when one is. A question already open is replaced, and counts as cancelled.
    /// </summary>
    public void Send(Feature feature, FeatureRequest request, uint value, Func<Task> send, string? successMessage = null)
    {
        if (!Needed(feature, request, value))
        {
            _state.Run(send, successMessage);
            return;
        }

        if (Pending is not null)
        {
            Pending = null;
            Cancelled++;
        }

        var session = _state.Session;
        var question = new PendingConfirm(feature, request, send, successMessage,
            session.Game, session.TitleId, session.Generation);

        Pending = question;
        Asked++;
        FetchText(question);
    }

    /// <summary>
    /// Asks the console for its wording, once per question. A refusal is no reason to toast: the
    /// dialog shows the generic question instead, and says beneath it what the console answered.
    /// </summary>
    private void FetchText(PendingConfirm question)
    {
        byte id = question.Feature.Id;
        _state.Run(async () =>
        {
            string? text = null;
            string? problem = null;
            try
            {
                text = await _state.Client.FeatureConfirmTextAsync(id).ConfigureAwait(false);
            }
            catch (QwarkStatusException refused)
            {
                problem = refused.Status.ToString();
            }
            catch (Exception ex)
            {
                problem = ex.Message;
            }

            _state.Post(() =>
            {
                question.Loading = false;
                question.Text = text;
                question.Problem = problem;
            });
        });
    }

    /// <summary>The user said yes: the request goes out, as the control would have sent it.</summary>
    public void Continue(PendingConfirm question)
    {
        if (!ReferenceEquals(question, Pending)) return;

        Pending = null;
        Continued++;
        _state.Run(question.Send, question.SuccessMessage);
    }

    /// <summary>The user said no, or closed the dialog: nothing is sent.</summary>
    public void Cancel(PendingConfirm question)
    {
        if (!ReferenceEquals(question, Pending)) return;

        Pending = null;
        Cancelled++;
    }

    /// <summary>
    /// Once a frame: a question about a game that is no longer the running one is cancelled, with
    /// a line saying so, rather than left on screen to be answered for whatever runs now.
    /// </summary>
    public void Tick()
    {
        if (Pending is not { } question) return;

        var session = _state.Session;
        bool same = _state.Ingame
                    && session.Game == question.Game
                    && string.Equals(session.TitleId, question.Title, StringComparison.Ordinal)
                    && session.Generation == question.Generation;
        if (same) return;

        Pending = null;
        Cancelled++;
        _state.AddToast($"{question.Feature.Label} was not sent: the game changed before it was confirmed");
    }
}
