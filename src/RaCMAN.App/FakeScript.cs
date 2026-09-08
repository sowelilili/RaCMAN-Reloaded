using System.Globalization;
using RaCMAN.Protocol;
using RaCMAN.Protocol.Testing;

namespace RaCMAN.App;

/// <summary>
/// Drives the in-process fake console through a session change while the window is up, so the
/// "side panels never outlive the game" rule can be watched rather than argued about.
/// Format: <c>--fake-script "2:quit,3:xmb,5:boot,8:rac2,10:drop"</c>, seconds from start.
/// </summary>
public static class FakeScript
{
    public const string Usage = "seconds:step, comma separated. Steps: quit, xmb, boot, rac2, drop";

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
                Console.WriteLine($"fake-script t={at:0.##}s {step}: state={fake.Session.State} game={fake.Session.Game} " +
                                  $"title={(fake.Session.TitleId.Length == 0 ? "-" : fake.Session.TitleId)} " +
                                  $"generation={fake.Session.Generation}");
            }
        }, cancellationToken);
    }

    public static void Apply(FakeQwarkServer fake, string step)
    {
        var session = fake.Session;
        switch (step)
        {
            case "quit":
                fake.Session = session with { State = SessionState.Quitting };
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

            case "drop":
                fake.DropClients();
                break;

            default:
                Console.Error.WriteLine($"fake-script: unknown step '{step}'. {Usage}");
                break;
        }
    }
}
