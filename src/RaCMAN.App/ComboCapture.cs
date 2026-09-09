using System.Numerics;

namespace RaCMAN.App;

/// <summary>
/// Turns the telemetry pad mask into one combo. Telemetry is a 30 Hz sample of what is held right
/// now, so a press of two buttons arrives as a run of masks and the fingers rarely leave the pad in
/// the same frame; keeping the last non-zero mask therefore stored whichever button happened to be
/// let go of last. The fullest mask of the press is the combo the user meant, so that is the one
/// kept, and it is committed when the pad returns to 0, which is when qwark re-arms a combo.
/// </summary>
public sealed class ComboCapture
{
    /// <summary>
    /// The fullest mask seen since the pad was last empty, so the panel can show what it has so
    /// far. 0 while nothing has been pressed and again after a commit.
    /// </summary>
    public uint Captured { get; private set; }

    /// <summary>Throws away a capture in flight, for Cancel and for a session change.</summary>
    public void Reset() => Captured = 0;

    /// <summary>
    /// Feeds one telemetry frame's pad mask. Returns the combo when the pad returns to 0 and null
    /// on every other frame, so the caller stores exactly once per press.
    /// </summary>
    public uint? Feed(uint mask)
    {
        if (mask != 0)
        {
            // Most buttons held at once wins, and an equally full mask that arrives later replaces
            // the earlier one, so a press that moves from one button to another keeps the newer.
            if (BitOperations.PopCount(mask) >= BitOperations.PopCount(Captured)) Captured = mask;
            return null;
        }

        if (Captured == 0) return null;

        uint captured = Captured;
        Captured = 0;
        return captured;
    }
}
