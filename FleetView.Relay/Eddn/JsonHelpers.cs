using System.Text.Json;

namespace FleetView.Relay.Eddn;

/// <summary>
/// EDDN field casing isn't perfectly consistent across schemas (e.g. commodity-v3 uses camelCase
/// like "marketId"/"systemName" while the FCMaterials schemas pass the journal's own PascalCase
/// field names through mostly as-is, like "MarketID"). Rather than hard-code one casing per field
/// and risk silently reading nothing back, look up a property under any of the names it might
/// plausibly appear as.
/// </summary>
internal static class JsonHelpers
{
    public static bool TryGetAny(this JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out value))
                return true;
        }
        value = default;
        return false;
    }

    public static string? GetStringAny(this JsonElement element, params string[] names) =>
        element.TryGetAny(out var v, names) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long? GetInt64Any(this JsonElement element, params string[] names) =>
        element.TryGetAny(out var v, names) && v.TryGetInt64(out var n) ? n : null;

    public static int? GetInt32Any(this JsonElement element, params string[] names) =>
        element.TryGetAny(out var v, names) && v.TryGetInt32(out var n) ? n : null;

    /// <summary>EDDN's "timestamp"/"Timestamp" field parsed to UTC, or now if missing/malformed -
    /// every handler needs a timestamp regardless, so falling back rather than skipping the
    /// message keeps that decision in one place.</summary>
    public static DateTime GetTimestampUtc(this JsonElement message) =>
        message.TryGetAny(out var ts, "timestamp", "Timestamp")
            && ts.ValueKind == JsonValueKind.String
            && DateTime.TryParse(ts.GetString(), out var parsed)
            ? parsed.ToUniversalTime()
            : DateTime.UtcNow;

    /// <summary>True if the message's StationType/stationType field names a fleet carrier.</summary>
    public static bool IsFleetCarrierStationType(this JsonElement message) =>
        string.Equals(message.GetStringAny("StationType", "stationType"), "FleetCarrier",
            StringComparison.OrdinalIgnoreCase);
}
