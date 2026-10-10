using FleetView.Relay.Storage;

namespace FleetView.Relay.Api;

public static class ListingsEndpoint
{
    // The real catalog is 90 components, so a legitimate client never sends more than that in one
    // request. Cap well above real usage but far below anything that could bloat the SQL IN-clause
    // or the query itself into a meaningful resource-abuse vector.
    private const int MaxKeys = 200;

    // Normalized keys are lower-case alphanumeric derived from short material names - real ones are
    // well under this. Anything longer isn't a real key and is dropped before it reaches SQL.
    private const int MaxKeyLength = 64;

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
            string dir;
            if (string.Equals(direction, "selling", StringComparison.OrdinalIgnoreCase)) dir = "Selling";
            else if (string.Equals(direction, "buying", StringComparison.OrdinalIgnoreCase)) dir = "Buying";
            else return Results.BadRequest("direction must be 'selling' or 'buying'");

            var keyList = keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(k => k.Length <= MaxKeyLength)
                .Take(MaxKeys)
                .ToArray();
            if (keyList.Length == 0) return Results.Ok(Array.Empty<ListingRow>());

            return Results.Ok(db.QueryListings(keyList, dir));
        });
        // Rate limited by the global limiter in Api/RateLimiting.cs (per client, then the old
        // global window), which applies to this path only. It replaced the "listings" policy.
    }
}
