using System.Text.Json;
using FleetView.Relay.Storage;

namespace FleetView.Relay.Eddn;

/// <summary>
/// Handles "journal/1" messages for fleet carriers - specifically "Docked" and "CarrierJump",
/// which fire far more often than commodity-v3's carrier market posts (every dock, not just when
/// someone's commodity market happens to get uploaded), closing the MarketID-to-location gap
/// faster.
///
/// Deliberately does NOT feed a docking-access fallback: a successful dock only proves *that
/// specific visitor* could get in, and for a friends/squadron-restricted carrier that visitor is
/// disproportionately likely to be a friend, squadron member, or the owner, not a representative
/// random commander - treating that as a general "Yes" caused real false positives. commodity-v3's
/// own policy field is the only "Yes" source now; see DockingDeniedHandler for the "No" side,
/// which doesn't have this same bias.
/// </summary>
public static class JournalHandler
{
    public static void Handle(JsonElement message, RelayDb db)
    {
        string? evt = message.GetStringAny("event", "Event");
        if (evt != "Docked" && evt != "CarrierJump") return;
        if (!message.IsFleetCarrierStationType()) return;

        long? marketId = message.GetInt64Any("MarketID", "marketId");
        string? callsign = message.GetStringAny("StationName", "stationName");
        string? starSystem = message.GetStringAny("StarSystem", "starSystem");
        if (marketId is null || string.IsNullOrEmpty(callsign) || string.IsNullOrEmpty(starSystem)) return;

        db.UpsertCarrierLocation(marketId.Value, callsign, starSystem, message.GetTimestampUtc());
    }
}
