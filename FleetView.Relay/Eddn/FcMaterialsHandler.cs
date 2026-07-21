using System.Text.Json;
using FleetView.Relay.Storage;

namespace FleetView.Relay.Eddn;

/// <summary>
/// Handles "fcmaterials_journal" (primary; also carries CarrierName) and "fcmaterials_capi"
/// (secondary/fallback; only CarrierID, and Items is shaped as purchases[]/sales rather than a
/// flat array) messages - the Odyssey bartender materials market itself. Neither schema carries a
/// star system, only MarketID; <see cref="CommodityV3Handler"/> is what supplies the join.
///
/// Confirmed against a live capture (not just the schema docs): fcmaterials_journal's "Name" field
/// is an unlocalised template string like "$circuitswitch_name;", not a display name or the bare
/// key - it must be unwrapped before use. fcmaterials_capi's "name" is already the bare key
/// (e.g. "chemicalsuperbase"). Either way, the unwrapped key matches catalog.json's "key" field
/// exactly, and <see cref="ComponentCatalog"/> resolves it back to a display name for the API.
/// </summary>
public static class FcMaterialsHandler
{
    public static void Handle(JsonElement message, RelayDb db, ComponentCatalog catalog)
    {
        long? marketId = message.GetInt64Any("MarketID", "marketId");
        if (marketId is null) return;

        DateTime updatedUtc = message.GetTimestampUtc();

        string? carrierName = message.GetStringAny("CarrierName", "CarrierName_Localised");
        db.UpsertCarrierName(marketId.Value, carrierName, updatedUtc);

        if (!message.TryGetAny(out var itemsEl, "Items", "items")) return;

        if (itemsEl.ValueKind == JsonValueKind.Array)
        {
            // fcmaterials_journal shape: flat array of { id, Name, Price, Stock, Demand }, each
            // entry covering both directions at once.
            var seenKeys = new HashSet<string>();
            foreach (var item in itemsEl.EnumerateArray())
            {
                string key = UpsertBothDirections(db, catalog, marketId.Value, item, updatedUtc,
                    nameNames: new[] { "Name_Localised", "Name", "name" },
                    priceNames: new[] { "Price", "price" },
                    sellNames: new[] { "Stock", "stock" },
                    buyNames: new[] { "Demand", "demand" });
                if (key.Length > 0) seenKeys.Add(key);
            }
            // Anything previously listed but missing from this fresh report is no longer offered
            // (see ClearUnreportedListings) - the game omits items entirely rather than listing
            // them at 0 once the whole bartender empties out.
            db.ClearUnreportedListings(marketId.Value, "Selling", seenKeys, updatedUtc);
            db.ClearUnreportedListings(marketId.Value, "Buying", seenKeys, updatedUtc);
        }
        else if (itemsEl.ValueKind == JsonValueKind.Object)
        {
            // fcmaterials_capi shape: { sales: [] | {"0": {...}, ...}, purchases: [{...}, ...] }.
            var sellKeys = new HashSet<string>();
            if (itemsEl.TryGetAny(out var salesEl, "sales", "Sales"))
                foreach (var item in EnumerateArrayOrObjectValues(salesEl))
                {
                    string key = UpsertOneDirection(db, catalog, marketId.Value, item, updatedUtc, "Selling",
                        nameNames: new[] { "name", "Name", "Name_Localised" },
                        priceNames: new[] { "price", "Price" },
                        amountNames: new[] { "stock", "Stock" });
                    if (key.Length > 0) sellKeys.Add(key);
                }
            db.ClearUnreportedListings(marketId.Value, "Selling", sellKeys, updatedUtc);

            var buyKeys = new HashSet<string>();
            if (itemsEl.TryGetAny(out var purchasesEl, "purchases", "Purchases"))
                foreach (var item in EnumerateArrayOrObjectValues(purchasesEl))
                {
                    string key = UpsertOneDirection(db, catalog, marketId.Value, item, updatedUtc, "Buying",
                        nameNames: new[] { "name", "Name", "Name_Localised" },
                        priceNames: new[] { "price", "Price" },
                        amountNames: new[] { "outstanding", "Outstanding" });
                    if (key.Length > 0) buyKeys.Add(key);
                }
            db.ClearUnreportedListings(marketId.Value, "Buying", buyKeys, updatedUtc);
        }
    }

    private static string UpsertBothDirections(
        RelayDb db, ComponentCatalog catalog, long marketId, JsonElement item, DateTime updatedUtc,
        string[] nameNames, string[] priceNames, string[] sellNames, string[] buyNames)
    {
        string key = ExtractKey(item.GetStringAny(nameNames));
        if (key.Length == 0) return "";
        string displayName = catalog.DisplayName(key);

        long price = item.GetInt64Any(priceNames) ?? 0;
        int stock = item.GetInt32Any(sellNames) ?? 0;
        int demand = item.GetInt32Any(buyNames) ?? 0;

        // Always upsert, even at 0 - see UpsertMaterialListing's doc comment for why a 0 still
        // needs to reach the DB (to correct an existing stale positive value) despite never
        // inserting a fresh all-zero row.
        db.UpsertMaterialListing(marketId, key, displayName, "Selling", stock, price, updatedUtc);
        db.UpsertMaterialListing(marketId, key, displayName, "Buying", demand, price, updatedUtc);
        return key;
    }

    private static string UpsertOneDirection(
        RelayDb db, ComponentCatalog catalog, long marketId, JsonElement item, DateTime updatedUtc, string direction,
        string[] nameNames, string[] priceNames, string[] amountNames)
    {
        string key = ExtractKey(item.GetStringAny(nameNames));
        if (key.Length == 0) return "";
        string displayName = catalog.DisplayName(key);

        long price = item.GetInt64Any(priceNames) ?? 0;
        int amount = item.GetInt32Any(amountNames) ?? 0;
        db.UpsertMaterialListing(marketId, key, displayName, direction, amount, price, updatedUtc);
        return key;
    }

    /// <summary>
    /// Unwraps a journal-style unlocalised template string ("$circuitswitch_name;" -> "circuitswitch")
    /// if present, then normalizes. Capi's already-bare "chemicalsuperbase"-style names pass through
    /// the normalize step unchanged (no "$"/"_name;" to strip).
    /// </summary>
    private static string ExtractKey(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (raw.StartsWith('$'))
        {
            int end = raw.LastIndexOf("_name;", StringComparison.OrdinalIgnoreCase);
            if (end > 1) raw = raw[1..end];
        }
        return ComponentKey.Normalize(raw);
    }

    private static IEnumerable<JsonElement> EnumerateArrayOrObjectValues(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray()) yield return item;
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject()) yield return prop.Value;
        }
    }
}
