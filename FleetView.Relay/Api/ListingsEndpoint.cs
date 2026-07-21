using FleetView.Relay.Storage;

namespace FleetView.Relay.Api;

public static class ListingsEndpoint
{
    /// <summary>
    /// GET /listings?keys=chemicalcatalyst,compressionliquefiedgas&amp;direction=selling|buying
    /// keys are normalized component keys (FleetFinder's Component.Key, already lower-case
    /// alphanumeric) - no ID translation needed since the relay normalizes EDDN names the same way.
    /// Returns ListingRow directly (matches FleetFinder's CarrierListing fields) rather than
    /// mapping to a separate DTO record, since the two shapes are otherwise identical.
    /// </summary>
    public static void MapListingsEndpoint(this WebApplication app)
    {
        app.MapGet("/listings", (string keys, string? direction, RelayDb db) =>
        {
            var keyList = keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (keyList.Length == 0) return Results.Ok(Array.Empty<ListingRow>());

            string dir = string.Equals(direction, "buying", StringComparison.OrdinalIgnoreCase)
                ? "Buying" : "Selling";

            return Results.Ok(db.QueryListings(keyList, dir));
        });
    }
}
