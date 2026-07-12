using FleetView.Models;

namespace FleetView.ViewModels;

/// <summary>
/// One row in the "Where to buy" results grid: a single carrier or station, with every matching
/// commodity it sells (from the current search) listed together instead of duplicating the row
/// once per commodity.
/// </summary>
public sealed class CarrierGroupRow
{
    public string StationName { get; init; } = "";
    public string Callsign { get; init; } = "";
    public bool IsFleetCarrier => !string.IsNullOrEmpty(Callsign);
    public string System { get; init; } = "";
    public string DockingAccess { get; init; } = "Unknown";
    public double? DistanceLy { get; init; }

    /// <summary>The matching commodities this carrier sells, one per line in the UI.</summary>
    public IReadOnlyList<CarrierListing> Items { get; init; } = Array.Empty<CarrierListing>();

    /// <summary>Cheapest matching commodity price at this carrier (used to sort the Price column).</summary>
    public long MinPrice => Items.Count == 0 ? 0 : Items.Min(i => i.Price);

    /// <summary>Freshest data age among this carrier's matching commodities (used to sort Updated).</summary>
    public TimeSpan MinAge => Items.Count == 0 ? TimeSpan.MaxValue : Items.Min(i => i.Age ?? TimeSpan.MaxValue);

    /// <summary>Total units across all matching commodities (used to sort the Qty column).</summary>
    public int TotalAmount => Items.Sum(i => i.Amount);

    /// <summary>
    /// The freshest "x ago" text among this carrier's matching commodities, shown once — the
    /// update time reflects the carrier's data snapshot, not any single commodity.
    /// </summary>
    public string UpdatedText => Items.Count == 0
        ? ""
        : Items.OrderBy(i => i.Age ?? TimeSpan.MaxValue).First().UpdatedText;
}
