namespace FleetView.Models;

/// <summary>
/// The four kinds of on-foot item the ship locker holds, in the order the Ship Locker tab shows
/// them. The game calls them Items, Components, Data and Consumables; the first two are Goods and
/// Assets everywhere a player sees them, so those are the names used here.
/// </summary>
public enum LockerGroup { Goods, Assets, Data, Consumables }

/// <summary>One kind of item and how many of it the locker holds.</summary>
public sealed record LockerItem(string Name, int Count);

/// <summary>Everything the locker holds of one group, sorted by name.</summary>
public sealed record LockerSection(LockerGroup Group, IReadOnlyList<LockerItem> Items)
{
    /// <summary>Total count across the group, held to int range.</summary>
    public int Held => (int)Math.Min(Items.Sum(i => (long)i.Count), int.MaxValue);

    public int Kinds => Items.Count;
}

/// <summary>What a read of the ship locker came to.</summary>
public enum LockerState
{
    /// <summary>The locker was read. It may still be empty.</summary>
    Ready,

    /// <summary>Neither the journal nor ShipLocker.json has a locker in it.</summary>
    NoLocker,

    /// <summary>The only locker on disk belongs to a different commander than the one who logged
    /// in last.</summary>
    OtherCommander,

    /// <summary>ShipLocker.json is there but could not be read, and the journal has nothing to
    /// stand in for it.</summary>
    Unreadable,
}

/// <summary>Where a locker that was read came from.</summary>
public enum LockerSource { None, JournalEvent, File }

/// <summary>
/// One read of the ship locker for the commander who logged in last: the four groups, when the
/// game wrote it, and where it came from.
/// </summary>
/// <param name="Commander">The latest login's commander name, or empty when no journal has one.</param>
/// <param name="AsOfUtc">The game's own timestamp on the locker that was read.</param>
/// <param name="OtherCommander">For <see cref="LockerState.OtherCommander"/>: whose locker the file holds.</param>
public sealed record ShipLockerContents(
    LockerState State,
    string Commander,
    DateTime? AsOfUtc,
    LockerSource Source,
    IReadOnlyList<LockerSection> Sections,
    string OtherCommander = "")
{
    public int ItemsHeld => (int)Math.Min(Sections.Sum(s => (long)s.Held), int.MaxValue);

    public int Kinds => Sections.Sum(s => s.Kinds);

    /// <summary>The section for one group; every read has all four, empty or not.</summary>
    public LockerSection this[LockerGroup group] => Sections.First(s => s.Group == group);

    /// <summary>Four empty sections, for a read that found nothing.</summary>
    public static IReadOnlyList<LockerSection> EmptySections { get; } =
        Enum.GetValues<LockerGroup>().Select(g => new LockerSection(g, [])).ToList();
}
