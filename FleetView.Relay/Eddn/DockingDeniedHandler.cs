using System.Text.Json;
using FleetView.Relay.Storage;

namespace FleetView.Relay.Eddn;

/// <summary>
/// Handles "dockingdenied/1" messages at fleet carriers with <c>Reason == "RestrictedAccess"</c> -
/// the schema doesn't constrain Reason to an enum, so this exact string match (confirmed against
/// EDDN's own docs) is the best available signal. Sets a soft "No" fallback via the same
/// non-overriding semantics as the Docked-based fallback (see JournalHandler) - never overrides an
/// existing authoritative commodity-v3 value. This signal is much rarer than a successful Docked
/// event, so expect "No" coverage to grow more slowly than "Yes" coverage does.
/// </summary>
public static class DockingDeniedHandler
{
    public static void Handle(JsonElement message, RelayDb db)
    {
        string? reason = message.GetStringAny("Reason", "reason");
        if (!string.Equals(reason, "RestrictedAccess", StringComparison.OrdinalIgnoreCase)) return;
        if (!message.IsFleetCarrierStationType()) return;

        long? marketId = message.GetInt64Any("MarketID", "marketId");
        if (marketId is null) return;

        db.UpsertCarrierDockingAccessFallback(marketId.Value, "No", message.GetTimestampUtc());
    }
}
