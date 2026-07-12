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
/// Does NOT feed a docking-access fallback (removed 2026-07-12, confirmed via real-world testing
/// against a live restricted carrier) - a successful dock only proves *that specific visitor*
/// could get in, and for a friends/squadron-restricted carrier that visitor is disproportionately
/// likely to be a friend, squadron member, or the owner, not a representative random commander.
/// Treating that as a general "Yes" produced real false positives (confirmed: a carrier that
/// showed "Yes" from this signal turned out to require friends/squadron membership, denying the
/// reporting user). commodity-v3's actual policy field is the only "Yes" source now; see
/// DockingDeniedHandler for the "No" side, which doesn't have this same bias.
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
    }
}
