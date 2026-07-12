using FleetView.Relay.Storage;

namespace FleetView.Relay.Api;

/// <summary>JSON shape returned by GET /listings, matching FleetFinder's CarrierListing fields.</summary>
public sealed record ListingDto(
    string Component, string StationName, string Callsign, string System,
    string Direction, int Amount, long Price, DateTime UpdatedAt, string DockingAccess);

public static class ListingsEndpoint
{
    /// <summary>
    /// GET /listings?keys=chemicalcatalyst,compressionliquefiedgas&amp;direction=selling|buying
    /// keys are normalized component keys (FleetFinder's Component.Key, already lower-case
    /// alphanumeric) - no ID translation needed since the relay normalizes EDDN names the same way.
    /// </summary>
    public static void MapListingsEndpoint(this WebApplication app)
    {
        app.MapGet("/listings", (string keys, string? direction, RelayDb db) =>
        {
            var keyList = keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (keyList.Length == 0) return Results.Ok(Array.Empty<ListingDto>());

            string dir = string.Equals(direction, "buying", StringComparison.OrdinalIgnoreCase)
                ? "Buying" : "Selling";

            var rows = db.QueryListings(keyList, dir);
            var dtos = rows.Select(r => new ListingDto(
                r.Component, r.StationName, r.Callsign, r.System,
                r.Direction, r.Amount, r.Price, r.UpdatedAt, r.DockingAccess));

            return Results.Ok(dtos);
        });
    }
}
