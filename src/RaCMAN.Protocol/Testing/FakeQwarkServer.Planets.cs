namespace RaCMAN.Protocol.Testing;

/// <summary>
/// The planet lists qwark's PLANET_LIST sends for the four original games (build 58): the names at
/// their ids and, where a game has one, the menu order after them, each copied from the game's table
/// in qwark's <c>src/games/rac1.c</c> .. <c>rac4.c</c>. ToD's list is <see cref="TodPlanets"/>.
/// The client's planet combo draws from these, and the save list's order by planet keeps the same
/// order for when the console has not said it, which the tests hold the two to.
/// </summary>
public sealed partial class FakeQwarkServer
{
    /// <summary>RaC1's <c>rac1_planets</c>, in the game's order, so qwark sends the ids in turn.</summary>
    public static readonly string[] Rac1Planets =
    {
        "Veldin", "Novalis", "Aridia", "Kerwan", "Eudora", "Rilgar", "Blarg", "Umbris", "Batalia",
        "Gaspar", "Orxon", "Pokitaru", "Hoven", "Gemlik", "Oltanis", "Quartu", "Kalebo3", "Fleet",
        "Veldin2",
    };

    /// <summary>RaC2's <c>rac2_planets</c>, the game's numbering, which qwark sends in turn.</summary>
    public static readonly string[] Rac2Planets =
    {
        "Aranos", "Oozla", "Maktar", "Endako", "Barlow", "Feltzin", "Notak", "Siberius", "Tabora",
        "Dobbo", "Hrugis", "Joba", "Todano", "Boldan", "Aranos 2", "Gorn", "Snivelak", "Smolg",
        "Damosel", "Grelbin", "Yeedil", "Insomniac Museum", "Dobbo Orbit", "Damosel Orbit",
        "Slim Cognito", "Wupash", "Jamming Array",
    };

    /// <summary>UYA's <c>rac3_planets</c>: the game's numbering, its menu at 0 and two gaps.</summary>
    public static readonly string[] Rac3Planets =
    {
        "(none)", "Veldin", "Florana", "Starship Phoenix", "Marcadia", "Daxx", "Phoenix Rescue",
        "Annihilation Nation", "Aquatos", "Tyhrranosis", "Zeldrin Starport", "Obani Gemini",
        "Blackwater City", "Holostar", "Koros", "(unused)", "Metropolis", "Crash Site", "Aridia",
        "Qwark's Hideout", "Launch Site", "Obani Draco", "Command Center", "Holostar Clank",
        "Insomniac Museum", "(unused)", "Metropolis Rangers", "Aquatos Clank", "Aquatos Sewers",
        "Tyhrranosis Rangers", "Vid Comic 6", "Vid Comic 1", "Vid Comic 4", "Vid Comic 2",
        "Vid Comic 3", "Vid Comic 5", "Vid Comic 1 SE",
    };

    /// <summary>UYA's <c>rac3_planet_order</c>: the order the game visits them, the placeholders last.</summary>
    public static readonly byte[] Rac3PlanetOrder =
    {
        1, 2, 3, 4, 5, 11, 12, 7, 8, 27, 28, 9, 10, 13, 23, 21, 16, 17, 18, 19, 6, 14, 22, 20, 24,
        29, 26, 31, 33, 34, 32, 35, 30, 36, 15, 25, 0,
    };

    /// <summary>Deadlocked's <c>rac4_planets</c>: an unused id 0 and three infinite loops among the planets.</summary>
    public static readonly string[] Rac4Planets =
    {
        "(unused)", "DreadZone", "Catacrom", "(infinite loop)", "Sarathos", "Kronos", "Shaar", "Valix",
        "Orxon", "(infinite loop)", "Torval", "Stygia", "(infinite loop)", "Maraxus", "Ghost Station",
        "Interior",
    };

    /// <summary>Deadlocked's <c>rac4_planet_order</c>: the numbering but Orxon before Valix, the placeholders last.</summary>
    public static readonly byte[] Rac4PlanetOrder = { 1, 2, 4, 5, 6, 8, 7, 10, 11, 13, 14, 15, 0, 3, 9, 12 };

    /// <summary>
    /// What PLANET_LIST sends for <paramref name="game"/>: the names, and the order, or null where
    /// qwark sends the ids in turn. Empty names for a game qwark has no list for.
    /// </summary>
    public static (string[] Names, byte[]? Order) PlanetListOf(GameId game) => game switch
    {
        GameId.Rac1 => (Rac1Planets, null),
        GameId.Rac2 => (Rac2Planets, null),
        GameId.Rac3 => (Rac3Planets, Rac3PlanetOrder),
        GameId.Rac4 => (Rac4Planets, Rac4PlanetOrder),
        GameId.Tod => (TodPlanets, null),
        _ => (Array.Empty<string>(), null),
    };

    /// <summary>Answers PLANET_LIST with <paramref name="game"/>'s list from now on, as its module would.</summary>
    public void UsePlanetsOf(GameId game)
    {
        var (names, order) = PlanetListOf(game);
        lock (_gate)
        {
            Planets = (string[])names.Clone();
            PlanetOrder = order is null ? null : (byte[])order.Clone();
        }
    }
}
