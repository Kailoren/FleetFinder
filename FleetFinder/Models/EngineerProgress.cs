namespace FleetView.Models;

/// <summary>How far the commander has got with one engineer, from the journal's
/// <c>EngineerProgress</c> events.</summary>
/// <remarks>"Not unlocked yet" in the Show filter is every state but Unlocked.</remarks>
public enum EngineerState
{
    /// <summary>No entry for this engineer in the journal: never heard of.</summary>
    NotMet,
    /// <summary>Known about but not yet invited. The game writes "Known" or "Acquainted".</summary>
    Known,
    Invited,
    /// <summary>The game can bar a commander from an engineer's workshop.</summary>
    Barred,
    Unlocked,
}

/// <summary>One engineer's entry for one commander, as the latest event left it.</summary>
/// <param name="Rank">The engineer's grade when the event carries one. On-foot engineers have no
/// grades and the game sends none for them, so this is normally null here.</param>
/// <param name="RankProgress">Percent towards the next grade, when the event carries it.</param>
/// <param name="Utc">When the game wrote the event that set this.</param>
public sealed record EngineerStanding(EngineerState State, int? Rank, int? RankProgress, DateTime Utc);

/// <summary>
/// Every engineer the journal has an entry for, for the commander of the latest login.
/// </summary>
/// <param name="Commander">The commander of the latest login; empty when no journal has a login.</param>
/// <param name="Standings">By the engineer's name as the game writes it, ignoring case. Includes
/// ship engineers and any name FleetFinder does not know; the tab only looks up its own.</param>
/// <param name="HasProgress">Whether any EngineerProgress event for this commander was found at all,
/// so the tab can tell "not met anyone" from "the journal has not said yet".</param>
public sealed record EngineerProgressSnapshot(string Commander, IReadOnlyDictionary<string, EngineerStanding> Standings, bool HasProgress)
{
    public static EngineerProgressSnapshot None { get; } =
        new("", new Dictionary<string, EngineerStanding>(StringComparer.OrdinalIgnoreCase), false);

    /// <summary>The standing for one engineer, Not met when the journal never mentioned them.</summary>
    public EngineerStanding? For(string engineer) => Standings.TryGetValue(engineer, out var s) ? s : null;
}
