using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>
/// What a planet combo offers, and what the boxes beside it are for. Both are view decisions about
/// the same PLANET_LIST reply, so they live together and neither knows anything about the games
/// beyond what the console already told the client.
/// </summary>
public sealed class PlanetChoices
{
    private static PlanetChoices _cached = new(Array.Empty<string>(), Array.Empty<int>());
    private static IReadOnlyList<string>? _cachedFrom;

    private PlanetChoices(string[] labels, int[] indices)
    {
        Labels = labels;
        Indices = indices;
    }

    /// <summary>The names to draw, in list order and with the placeholders left out.</summary>
    public string[] Labels { get; }

    /// <summary>The planet index each label stands for, which is what a request carries.</summary>
    public int[] Indices { get; }

    public int Count => Labels.Length;

    /// <summary>
    /// True when a name is nothing but a parenthesised note. qwark's planet lists are indexed by
    /// the game's own planet id, so where a game has no planet at an id the list carries filler to
    /// keep the numbering: UYA's id 0 is "(none)", Deadlocked has "(unused)" and three
    /// "(infinite loop)" entries. Nobody wants to load one, and the ids around them still have to
    /// mean what they meant, so they are dropped from the combo rather than from the list.
    /// </summary>
    public static bool IsPlaceholder(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length >= 2 && trimmed[0] == '(' && trimmed[^1] == ')';
    }

    /// <summary>
    /// The combo's contents for a planet list. A list that is nothing but placeholders is offered
    /// whole: an empty combo would leave no way to pick a planet at all, which is worse than a
    /// filler name in it.
    /// </summary>
    public static PlanetChoices For(IReadOnlyList<string> planets)
    {
        // The panels ask once a frame and the list only changes when PLANET_LIST answers, so the
        // last answer is remembered by reference. Render thread only, like everything in a panel.
        if (ReferenceEquals(_cachedFrom, planets)) return _cached;

        var labels = new List<string>(planets.Count);
        var indices = new List<int>(planets.Count);
        for (int i = 0; i < planets.Count; i++)
        {
            if (IsPlaceholder(planets[i])) continue;
            labels.Add(planets[i]);
            indices.Add(i);
        }

        if (labels.Count == 0)
        {
            for (int i = 0; i < planets.Count; i++)
            {
                labels.Add(planets[i]);
                indices.Add(i);
            }
        }

        _cached = new PlanetChoices(labels.ToArray(), indices.ToArray());
        _cachedFrom = planets;
        return _cached;
    }

    /// <summary>Where a planet index sits in the combo, or -1 when it is one of the hidden ones.</summary>
    public int PositionOf(int planet) => Array.IndexOf(Indices, planet);

    /// <summary>The planet index a combo position stands for, or 0 when there is nothing to pick.</summary>
    public int PlanetAt(int position) =>
        position >= 0 && position < Indices.Length ? Indices[position] : 0;
}

/// <summary>
/// Which of the two boxes beside "Load planet" a game has. Both are things the game itself does on
/// the way into a planet, so a game that has neither must not be offered them: the request would
/// carry a flag the console has nothing to do with, and the box would be a promise the game does
/// not keep.
/// </summary>
public readonly record struct PlanetResetOptions(bool LevelFlags, bool SpecialBolts)
{
    /// <summary>
    /// RaC1 has special bolts but no level flags, Deadlocked has neither, and RaC2 and UYA have
    /// both. <paramref name="levelFlagsUnsupported"/> is the console's own answer to
    /// LEVELFLAGS_GET, so a game whose flags this build cannot reach loses the box as well.
    /// A game the client has no name for is offered neither, the same as Deadlocked.
    /// </summary>
    public static PlanetResetOptions For(GameId game, bool levelFlagsUnsupported) => new(
        LevelFlags: game is GameId.Rac2 or GameId.Rac3 && !levelFlagsUnsupported,
        SpecialBolts: game is GameId.Rac1 or GameId.Rac2 or GameId.Rac3);
}
