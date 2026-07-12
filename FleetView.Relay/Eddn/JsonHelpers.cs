using System.Text.Json;

namespace FleetView.Relay.Eddn;

/// <summary>
/// EDDN field casing isn't perfectly consistent across schemas (e.g. commodity-v3 uses camelCase
/// like "marketId"/"systemName" while the FCMaterials schemas pass the journal's own PascalCase
/// field names through mostly as-is, like "MarketID"). Rather than hard-code one casing per field
/// and risk silently reading nothing back, look up a property under any of the names it might
/// plausibly appear as. The live-capture spike (see plan) confirms/corrects which names actually
/// show up on the real firehose.
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
}
