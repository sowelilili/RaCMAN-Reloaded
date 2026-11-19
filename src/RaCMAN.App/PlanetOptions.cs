using RaCMAN.Protocol;

namespace RaCMAN.App;

/// <summary>
/// What a planet combo offers, and what the boxes beside it are for. Both are view decisions about
/// the same PLANET_LIST reply, so they live together and neither knows anything about the games
/// beyond what the console already told the client.
/// </summary>
public sealed class PlanetChoices
{
    /// <summary>The last answer and what it was for, swapped as one reference so a reader never sees half of one.</summary>
    private sealed record Cached(IReadOnlyList<string> From, IReadOnlyList<int>? Order, PlanetChoices Choices);

    private static Cached? _cached;

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
    /// keep the numbering: UYA's id 0 is "(none)" and its ids 15 and 25 are "(unused)" gaps in the
    /// game's own numbering, and Deadlocked has an "(unused)" id 0 and three "(infinite loop)"
    /// entries. Nobody wants to load one — there is nothing at the id to load — and the ids around
    /// them still have to mean what they meant, so they are dropped from the combo rather than
    /// from the list. One rule for every note, so a gap a later qwark words differently is dropped
    /// too without anything here having to be told about it.
    /// </summary>
    public static bool IsPlaceholder(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length >= 2 && trimmed[0] == '(' && trimmed[^1] == ')';
    }

    /// <summary>
    /// The combo's contents for a planet list, in the order the console says a menu lists them
    /// (PLANET_LIST since build 53: UYA and Deadlocked in the order the game visits them, the
    /// others in id order) and in id order without one. An order that is not every index of the
    /// list exactly once is not one, and the list goes in id order then too. A list that is
    /// nothing but placeholders is offered whole: an empty combo would leave no way to pick a
    /// planet at all, which is worse than a filler name in it.
    /// </summary>
    public static PlanetChoices For(IReadOnlyList<string> planets, IReadOnlyList<int>? order = null)
    {
        // The panels ask once a frame and the list only changes when PLANET_LIST answers, so the
        // last answer is remembered by reference. The panels call it on the render thread alone,
        // but the tests call it from several at once, so the answer and what it was for are one
        // reference and what is returned is never read back from the field.
        var last = _cached;
        if (last is not null && ReferenceEquals(last.From, planets) && ReferenceEquals(last.Order, order)) return last.Choices;

        var sequence = IsWholeOrder(order, planets.Count) ? order! : Enumerable.Range(0, planets.Count).ToArray();

        var labels = new List<string>(planets.Count);
        var indices = new List<int>(planets.Count);
        foreach (int i in sequence)
        {
            if (IsPlaceholder(planets[i])) continue;
            labels.Add(planets[i]);
            indices.Add(i);
        }

        if (labels.Count == 0)
        {
            foreach (int i in sequence)
            {
                labels.Add(planets[i]);
                indices.Add(i);
            }
        }

        var choices = new PlanetChoices(labels.ToArray(), indices.ToArray());
        _cached = new Cached(planets, order, choices);
        return choices;
    }

    /// <summary>True when <paramref name="order"/> names every index below <paramref name="count"/> exactly once.</summary>
    private static bool IsWholeOrder(IReadOnlyList<int>? order, int count)
    {
        if (order is null || order.Count != count) return false;

        var seen = new bool[count];
        foreach (int index in order)
        {
            if (index < 0 || index >= count || seen[index]) return false;
            seen[index] = true;
        }

        return true;
    }

    /// <summary>Where a planet index sits in the combo, or -1 when it is one of the hidden ones.</summary>
    public int PositionOf(int planet) => Array.IndexOf(Indices, planet);

    /// <summary>
    /// Where the combo should sit for a planet the console holds, or a setting remembers: its own
    /// row, and the first row of the list when the id has none. An id with no row is a hidden one
    /// — a stale setting pointing at an "(unused)" gap, or a console standing somewhere the combo
    /// does not offer — and a combo has to sit on something, so it sits on a real planet rather
    /// than on whatever row -1 would have drawn. Every picker asks this, so they all fall back the
    /// same way.
    /// </summary>
    public int PositionFor(int planet) => Math.Max(0, PositionOf(planet));

    /// <summary>The planet index a combo position stands for, or 0 when there is nothing to pick.</summary>
    public int PlanetAt(int position) =>
        position >= 0 && position < Indices.Length ? Indices[position] : 0;
}

/// <summary>
/// Which of the two boxes beside "Load planet" a game has. Each is a bit of the planet flags that
/// the game's own planet load acts on, so a game is offered exactly the bits its load uses: a box
/// for a bit the load ignores would be a promise the game does not keep, and a bit the load uses
/// with no box drawn for it would still act on every load, out of sight. qwark keeps the flags per
/// game from build 53 (<c>selected.&lt;game&gt;.planet_flags</c>, beside the planet they go with)
/// and reports the running game's, so a box shows and turns that game's bit and follows a game
/// switch with nothing kept here.
/// </summary>
public readonly record struct PlanetResetOptions(bool LevelFlags, bool SpecialBolts)
{
    /// <summary>
    /// The boxes for a game, as its module says (qwark revision 1.34, DESCRIBE's extension): the
    /// PLANET_LOAD flag bits the game's load acts on. The rule the modules follow is that a game with
    /// per-planet level flags acts on bit0 and one with per-planet special bolts on bit1 (RaC1's gold
    /// bolts, RaC2's platinum bolts, UYA's titanium bolts, ToD's gold bolts); Deadlocked has neither.
    /// <para>
    /// This is not the Level flags panel's question, and whether LEVELFLAGS_GET is answered does
    /// not come into it: qwark withholds RaC1's flag viewer, but RaC1's PLANET_LOAD still resets the
    /// flags, and the module says so.
    /// </para>
    /// <para>
    /// A module before the revision says nothing, and is out of date for this client, which offers
    /// the update. Until it is replaced, <see cref="OlderModule"/> stands in, the table this client
    /// kept until then: hiding the boxes instead would leave a bit ticked earlier acting on every
    /// load out of sight.
    /// </para>
    /// </summary>
    public static PlanetResetOptions For(DescribeExtension? extension, GameId game) =>
        extension is { } ext
            ? new(LevelFlags: (ext.PlanetFlags & PlanetFlags.ResetLevelFlags) != 0,
                  SpecialBolts: (ext.PlanetFlags & PlanetFlags.ResetSpecialBolts) != 0)
            : OlderModule(game);

    /// <summary>
    /// What a module before qwark revision 1.34 acts on, which it does not say: RaC1 and UYA both
    /// bits, RaC2 its level flags, Deadlocked and ToD neither (as their builds 53 to 58 load them).
    /// Compatibility alone, for the time a console runs such a module; nothing newer reads it.
    /// </summary>
    public static PlanetResetOptions OlderModule(GameId game) => new(
        LevelFlags: game is GameId.Rac1 or GameId.Rac2 or GameId.Rac3,
        SpecialBolts: game is GameId.Rac1 or GameId.Rac3);
    private static readonly PlanetResetBox LevelFlagsBox = new("Reset level flags", PlanetFlags.ResetLevelFlags);
    private static readonly PlanetResetBox SpecialBoltsBox = new("Reset special bolts", PlanetFlags.ResetSpecialBolts);

    private static readonly PlanetResetBox[] BothBoxes = { LevelFlagsBox, SpecialBoltsBox };
    private static readonly PlanetResetBox[] LevelFlagsOnly = { LevelFlagsBox };
    private static readonly PlanetResetBox[] SpecialBoltsOnly = { SpecialBoltsBox };

    /// <summary>
    /// The boxes the row draws, left to right: level flags, then special bolts beside it. The
    /// panel draws exactly this list, so what a game is offered is pinned here rather than in the
    /// drawing. Shared arrays, because the row asks once a frame.
    /// </summary>
    public IReadOnlyList<PlanetResetBox> Boxes() => (LevelFlags, SpecialBolts) switch
    {
        (true, true) => BothBoxes,
        (true, false) => LevelFlagsOnly,
        (false, true) => SpecialBoltsOnly,
        _ => Array.Empty<PlanetResetBox>(),
    };
}

/// <summary>One box beside "Load planet": what it says, and the planet flag it shows and turns.</summary>
public readonly record struct PlanetResetBox(string Label, PlanetFlags Bit);
