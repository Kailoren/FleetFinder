using System.Text.Json;
using FleetView.Relay.Storage;

namespace FleetView.Relay.Eddn;

/// <summary>
/// Handles "dockingdenied/1" messages - specifically denials at fleet carriers with
/// <c>Reason == "RestrictedAccess"</c> (confirmed via EDDN's own docs as the real access-denial
/// value; the schema itself doesn't constrain Reason to an enum, so this exact string match is
/// the best available signal, not a guess). Sets a soft "No" fallback via the same
/// non-overriding semantics as the Docked-based "Yes" fallback (see JournalHandler) - never
/// overrides an existing authoritative commodity-v3 value.
///
/// Confirmed via live capture that this is a much rarer signal than a successful Docked event:
/// none of 8 real denials captured in a 4-minute window were even at a fleet carrier (all were
/// "TooLarge"/"Distance" at regular outposts/settlements) - expect this to grow "No" coverage
/// more slowly than the "Yes" side grew.
/// </summary>
public static class DockingDeniedHandler
{
    public static void Handle(JsonElement message, RelayDb db)
    {
        string? reason = message.GetStringAny("Reason", "reason");
        if (!string.Equals(reason, "RestrictedAccess", StringComparison.OrdinalIgnoreCase)) return;

        string? stationType = message.GetStringAny("StationType", "stationType");
        if (!string.Equals(stationType, "FleetCarrier", StringComparison.OrdinalIgnoreCase)) return;

        long? marketId = message.GetInt64Any("MarketID", "marketId");
        if (marketId is null) return;

        DateTime seenUtc = message.TryGetAny(out var tsEl, "timestamp", "Timestamp")
            && tsEl.ValueKind == JsonValueKind.String
            && DateTime.TryParse(tsEl.GetString(), out var ts)
            ? ts.ToUniversalTime()
            : DateTime.UtcNow;

        db.UpsertCarrierDockingAccessFallback(marketId.Value, "No", seenUtc);
    }
}
