using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>
/// The lines under the ACTIONs that name a status readout (UYA's Reset trophies, ToD's Cache
/// cutscenes), as the module words them (ACTION_STATUS, qwark revision 1.34). The client keeps no
/// layout of a status word: it asks the module once for each word the readout carries, keeps the
/// answer until the word moves, and draws the last answer meanwhile. A module that does not know the
/// op (UNKNOWN_OP, a build before 59) is not asked again on that connection, and no line is drawn.
/// </summary>
public sealed partial class AppState
{
    private readonly Dictionary<byte, (uint Word, ActionStatusLine Line)> _actionLines = new();
    private readonly HashSet<byte> _actionAsking = new();
    private SessionInfo? _actionLinesFor;
    private bool _actionStatusUnsupported;

    /// <summary>
    /// The line under <paramref name="feature"/>'s button, or null for none: not an ACTION with a
    /// status readout, nothing to say, or no answer yet. Asks the module when the word has moved.
    /// Render thread.
    /// </summary>
    public ActionStatusLine? ActionLine(Feature feature)
    {
        if (feature.StatusReadout is not { } index || Session.ReadoutAt(index) is not { } word) return null;

        // A new connection forgets what the last one said, and whether it knew the op.
        if (!ReferenceEquals(_actionLinesFor, Hello))
        {
            _actionLinesFor = Hello;
            _actionLines.Clear();
            _actionAsking.Clear();
            _actionStatusUnsupported = false;
        }

        _actionLines.TryGetValue(feature.Id, out var known);
        bool fresh = known.Line is not null && known.Word == word;
        if (!fresh && Connected && Ingame && !_actionStatusUnsupported && _actionAsking.Add(feature.Id))
            AskActionLine(feature.Id, word);

        return known.Line is { Tone: not StatusTone.None } line && line.Text.Length > 0 ? line : null;
    }

    private void AskActionLine(byte id, uint word)
    {
        var hello = Hello;
        RunQuiet(async () =>
        {
            try
            {
                var line = await Client.ActionStatusAsync(id, word).ConfigureAwait(false);
                Post(() =>
                {
                    if (ReferenceEquals(hello, _actionLinesFor)) _actionLines[id] = (word, line);
                });
            }
            catch (QwarkStatusException ex) when (ex.Status == Status.UnknownOp)
            {
                Post(() =>
                {
                    if (ReferenceEquals(hello, _actionLinesFor)) _actionStatusUnsupported = true;
                });
            }
            catch (QwarkStatusException)
            {
                // NOT_FOUND, NOT_INGAME, UNSUPPORTED: nothing to say for that word; asked again when it moves.
                Post(() =>
                {
                    if (ReferenceEquals(hello, _actionLinesFor)) _actionLines[id] = (word, new ActionStatusLine(StatusTone.None, ""));
                });
            }
            finally
            {
                Post(() => _actionAsking.Remove(id));
            }
        });
    }
}
