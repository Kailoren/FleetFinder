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
            // Only "all" is genuinely open to any commander. "friends"/"squadron"/"squadronfriends"
            // restrict docking to a subset of players, the same false-positive risk already found
            // and removed from the old Docked-based fallback (see JournalHandler) - collapsing them
            // to "Yes" here would reintroduce that exact bug at the authoritative source instead.
            dockingAccess = raw.ToLowerInvariant() switch
            {
                "none" => "No",
                "all" => "Yes",
                _ => "Restricted"
            };
        }

        DateTime seenUtc = message.GetTimestampUtc();

        db.UpsertCarrierFromCommodity(marketId.Value, stationName, systemName, dockingAccess, seenUtc);
    }
}
