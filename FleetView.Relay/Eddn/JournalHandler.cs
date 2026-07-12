using System.Text.Json;
using FleetView.Relay.Storage;

namespace FleetView.Relay.Eddn;

/// <summary>
/// Handles "journal/1" messages for fleet carriers - specifically "Docked" (a player docked at
/// the carrier) and "CarrierJump" (the carrier the player is docked at just jumped). Both fire
/// far more often than commodity-v3's carrier market posts (every dock, not just when someone's
/// commodity market happens to get uploaded), so this closes the MarketID-to-location gap much
/// faster. Confirmed via live capture: real messages carry StationName (=Callsign), StarSystem,
/// MarketID and StationType in PascalCase.
///
/// Also feeds a soft docking-access fallback: a successful dock proves *someone* could get in,
/// so it's used as a "Yes" guess only while commodity-v3's actual access policy is still unknown
/// (see RelayDb.UpsertCarrierDockingAccessFallback) - this schema has no real access-policy field
/// of its own, only commodity-v3 does, so this never overrides a real answer.
/// </summary>
public static class JournalHandler
{
    public static void Handle(JsonElement message, RelayDb db)
    {
        string? evt = message.GetStringAny("event", "Event");
        if (evt != "Docked" && evt != "CarrierJump") return;

        string? stationType = message.GetStringAny("StationType", "stationType");
        if (!string.Equals(stationType, "FleetCarrier", StringComparison.OrdinalIgnoreCase)) return;

        long? marketId = message.GetInt64Any("MarketID", "marketId");
        string? callsign = message.GetStringAny("StationName", "stationName");
        string? starSystem = message.GetStringAny("StarSystem", "starSystem");
        if (marketId is null || string.IsNullOrEmpty(callsign) || string.IsNullOrEmpty(starSystem)) return;

        DateTime seenUtc = message.TryGetAny(out var tsEl, "timestamp", "Timestamp")
            && tsEl.ValueKind == JsonValueKind.String
            && DateTime.TryParse(tsEl.GetString(), out var ts)
            ? ts.ToUniversalTime()
            : DateTime.UtcNow;

        db.UpsertCarrierLocation(marketId.Value, callsign, starSystem, seenUtc);
        db.UpsertCarrierDockingAccessFallback(marketId.Value, "Yes", seenUtc);
    }
}
