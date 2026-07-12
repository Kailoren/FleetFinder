using FleetView.Models;

namespace FleetView.Services;

/// <summary>
/// Deterministic offline data source used for UI testing (sorting, copy-to-clipboard) without
/// hitting the real relay server. Enabled only when the environment variable FLEETVIEW_MOCK=1.
/// Never used in normal operation.
/// </summary>
public sealed class MockMarketSource : ICarrierMarketSource
{
    public Task<IReadOnlyList<CarrierListing>> GetListingsAsync(
        IReadOnlyList<Component> components, MarketDirection direction, CancellationToken ct = default)
    {
        // Varied prices / distances / ages / access / carrier-vs-station so the grid is testable.
        // One set of listings per requested component, mirroring how a real batched relay
        // response carries rows for every ticked commodity in one payload — the same carrier
        // reappears once per component, letting the grouping logic collapse it into one row
        // with several items, same as it would with real multi-commodity data.
        var data = new List<CarrierListing>();
        foreach (var component in components)
        {
            data.Add(New(component, "Deep Space Depot", "DSD-01X", "Sol",        20, 40_000, 12.5, "2 minutes ago", TimeSpan.FromMinutes(2), "Yes"));
            data.Add(New(component, "Bargain Bin",      "BGN-99Z", "Rhea",       5,     155, 228.0, "3 hours ago",   TimeSpan.FromHours(3),   "No"));
            data.Add(New(component, "Coriolis Station", "",        "Narenses",   99,    800,  25.0, "1 day ago",     TimeSpan.FromDays(1),    "Yes"));
            data.Add(New(component, "The Wandering Sou","WSL-42Y", "HIP 50515",  8,     999, 461.0, "now",           TimeSpan.Zero,           "Unknown"));
            data.Add(New(component, "Grumpy Dragon",    "G2F-7VW", "Lalande 10797", 18,  300,  71.0, "9 hours ago",  TimeSpan.FromHours(9),   "Yes"));
        }
        return Task.FromResult<IReadOnlyList<CarrierListing>>(data);
    }

    private static CarrierListing New(Component c, string station, string callsign, string system,
        int amount, long price, double dist, string updated, TimeSpan age, string access) => new()
    {
        Component = c.Name,
        StationName = station,
        Callsign = callsign,
        System = system,
        Amount = amount,
        Price = price,
        DistanceLy = dist,
        UpdatedText = updated,
        UpdatedAt = DateTime.Now - age,
        Age = age,
        DockingAccess = access
    };
}
