using System.Globalization;
using RaCMAN.Protocol;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.App;

/// <summary>
/// Drives the in-process fake console through a session change while the window is up, so the
/// "side panels never outlive the game" rule can be watched rather than argued about.
/// Format: <c>--fake-script "2:quit,3:xmb,5:boot,8:rac2,10:drop"</c>, seconds from start.
/// <para>
/// The run-event steps emit an autosplit event, ring and UDP push included, so the whole chain
/// from the console's detection to a LiveSplit command can be exercised headless:
/// <c>--fake-script "5:split:1:3"</c> emits a SPLIT for code 1 with planet index 3 at five seconds.
/// </para>
/// </summary>
public static class FakeScript
{
    public const string Usage = "seconds:step, comma separated. Steps: quit, xmb, booting, boot, rac2, rac4, "
                                + "unknown, drop, combos-off, combos-on, start, split[:code[:arg]], reset, "
                                + "load[:code[:ms]], loadend[:code[:ms]], pause[:code[:ms]], resume[:code[:ms]]";

    public static IReadOnlyList<(double At, string Step)> Parse(string script)
    {
        var steps = new List<(double, string)>();
        foreach (var part in (script ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var halves = part.Split(':', 2);
            if (halves.Length != 2) continue;
            if (!double.TryParse(halves[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double at)) continue;
            steps.Add((at, halves[1].Trim().ToLowerInvariant()));
        }

        steps.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return steps;
    }

    public static Task RunAsync(FakeQwarkServer fake, string script, CancellationToken cancellationToken = default)
    {
        var steps = Parse(script);
        return Task.Run(async () =>
        {
            double elapsed = 0;
            foreach (var (at, step) in steps)
            {
                var wait = TimeSpan.FromSeconds(Math.Max(0, at - elapsed));
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                elapsed = at;

                Apply(fake, step);
                Console.WriteLine($"fake-script t={at:0.##}s {step}: state={fake.Session.State.DisplayName()} game={fake.Session.Game} " +
                                  $"title={(fake.Session.TitleId.Length == 0 ? "-" : fake.Session.TitleId)} " +
                                  $"generation={fake.Session.Generation}");
            }
        }, cancellationToken);
    }

    /// <summary>The run-event steps, by the name they are written with. Everything else is a session step.</summary>
    private static readonly Dictionary<string, AutosplitKind> EventSteps = new(StringComparer.Ordinal)
    {
        ["start"] = AutosplitKind.Start,
        ["split"] = AutosplitKind.Split,
        ["reset"] = AutosplitKind.Reset,
        ["pause"] = AutosplitKind.Pause,
        ["resume"] = AutosplitKind.Resume,
        ["load"] = AutosplitKind.LoadStart,
        ["loadend"] = AutosplitKind.LoadEnd,
    };

    /// <summary>
    /// The kinds whose third field is the event's own millisecond stamp rather than an argument:
    /// the two halves of a load and the two halves of a pause. Scripting both halves of a pair
    /// with an explicit stamp is how a normalised load of an exactly known length is staged
    /// without the script having to sit through it.
    /// </summary>
    private static bool StepCarriesTime(AutosplitKind kind) =>
        kind is AutosplitKind.LoadStart or AutosplitKind.LoadEnd or AutosplitKind.Pause or AutosplitKind.Resume;

    public static void Apply(FakeQwarkServer fake, string step)
    {
        // "split:1:3" is one step: the kind, then the reason code and its argument. For a timing
        // step, "load:8:9000", the third field is the module clock reading instead.
        var parts = step.Split(':');
        if (EventSteps.TryGetValue(parts[0], out var kind))
        {
            byte code = parts.Length > 1 && byte.TryParse(parts[1], out var c) ? c : (byte)0;
            uint third = parts.Length > 2 && uint.TryParse(parts[2], out var a) ? a : 0u;
            bool timed = StepCarriesTime(kind);
            uint? timeMs = timed && parts.Length > 2 ? third : null;

            var emitted = fake.EmitAutosplitEvent(kind, code, timed ? 0u : third, timeMs);
            Console.WriteLine($"fake-script emit seq={emitted.Seq} kind={kind} code={code} " +
                              $"arg={emitted.Arg} time={emitted.TimeMs}ms");
            return;
        }

        var session = fake.Session;
        switch (step)
        {
            // Both halves of what a real quit is: the session says QUITTING, and, per PROTOCOL.md
            // section 1.1, every request but the five control ops is answered BUSY until a game is
            // up again. "booting" is the other side of the same coin, the launch itself.
            case "quit":
                fake.Quitting = true;
                break;

            case "booting":
                fake.Booting = true;
                break;

            case "xmb":
                fake.Session = session with
                {
                    State = SessionState.Xmb,
                    Game = GameId.None,
                    TitleId = string.Empty,
                    CurrentPlanet = 0,
                };
                break;

            case "boot":
                // Same title, new process: the generation is what tells the client it rebooted.
                fake.Session = session with
                {
                    State = SessionState.Ingame,
                    Game = GameId.Rac1,
                    TitleId = "NPEA00385",
                    Generation = session.Generation + 1,
                };
                break;

            case "rac2":
                fake.Describe = fake.Describe with { Game = GameId.Rac2 };
                fake.Session = session with
                {
                    State = SessionState.Ingame,
                    Game = GameId.Rac2,
                    TitleId = "NPEA00386",
                    Generation = session.Generation + 1,
                };
                break;

            // Deadlocked, so a headless run can exercise the game whose route file and game-time
            // normalisation differ most from the other three.
            case "rac4":
                fake.Describe = fake.Describe with { Game = GameId.Rac4 };
                fake.Session = session with
                {
                    State = SessionState.Ingame,
                    Game = GameId.Rac4,
                    TitleId = "NPEA00423",
                    Generation = session.Generation + 1,
                };
                break;

            // A title the module has no game for: INGAME, game 0, a title id, and every game op
            // answered UNSUPPORTED. The client is down to its memory tools here.
            case "unknown":
                fake.UnknownGame = true;
                break;

            // The console's combo switch, which the client only ever sees as flags bit3. Nothing
            // else about the session moves, so a headless run can be pointed at the Combos panel
            // with the combos held off and watched drawing that.
            case "combos-off":
                fake.CombosEnabled = false;
                break;

            case "combos-on":
                fake.CombosEnabled = true;
                break;

            case "drop":
                fake.DropClients();
                break;

            default:
                Console.Error.WriteLine($"fake-script: unknown step '{step}'. {Usage}");
                break;
        }
    }
}
