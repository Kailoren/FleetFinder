using System.Text.Json;
using FleetView.Relay.Storage;

namespace FleetView.Relay.Eddn;

/// <summary>
/// Handles "commodity/3" messages. Fleet carriers post their ship-commodity market here too, and
/// unlike the FCMaterials schemas this one carries StarSystem/StationName - it's the only source
/// on EDDN that lets a bare MarketID (all the FCMaterials handler has) be joined to a real system.
/// It also carries an optional carrierDockingAccess field, which is the docking-access signal.
/// </summary>
public static class CommodityV3Handler
{
    public static void Handle(JsonElement message, RelayDb db)
    {
        long? marketId = message.GetInt64Any("marketId", "MarketID");
        if (marketId is null) return;

        string? stationName = message.GetStringAny("stationName", "StationName");
        string? systemName = message.GetStringAny("systemName", "StarSystem");
        if (string.IsNullOrEmpty(stationName) || string.IsNullOrEmpty(systemName)) return;

        string? stationType = message.GetStringAny("stationType", "StationType");
        bool hasDockingAccess = message.TryGetAny(out var dockingEl, "carrierDockingAccess", "CarrierDockingAccess");

        bool isCarrier = hasDockingAccess
            || string.Equals(stationType, "FleetCarrier", StringComparison.OrdinalIgnoreCase);
        if (!isCarrier) return;

        string dockingAccess = "Unknown";
        if (hasDockingAccess && dockingEl.ValueKind == JsonValueKind.String)
        {
            string raw = dockingEl.GetString() ?? "";
            dockingAccess = raw.Equals("none", StringComparison.OrdinalIgnoreCase) ? "No" : "Yes";

            // TEMPORARY diagnostic (2026-07-13): capturing the raw carrierDockingAccess string for
            // AURORA/HFP-46K (MarketID 3707664640, Kutkha) - confirmed friends-only in-game, but the
            // stored "No" so far came from a dockingdenied fallback, not a real commodity-v3 report,
            // so the raw value here has never actually been seen. Checking whether EDDN's raw field
            // distinguishes "friends only" from "none" (fully closed) or collapses both the same way.
            // Remove this block once that's answered.
            if (marketId == 3707664640)
            {
                Console.WriteLine($"[DIAG] AURORA/HFP-46K raw carrierDockingAccess = \"{raw}\"");
            }
        }

        DateTime seenUtc = message.TryGetAny(out var tsEl, "timestamp", "Timestamp")
            && tsEl.ValueKind == JsonValueKind.String
            && DateTime.TryParse(tsEl.GetString(), out var ts)
            ? ts.ToUniversalTime()
            : DateTime.UtcNow;

        db.UpsertCarrierFromCommodity(marketId.Value, stationName, systemName, dockingAccess, seenUtc);
    }
}
