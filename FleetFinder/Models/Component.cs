using System.Text.Json.Serialization;

namespace FleetView.Models;

/// <summary>
/// One tradeable Odyssey commodity from the reference catalog.
/// Mirrors an entry in Data/catalog.json.
/// </summary>
public sealed class Component
{
    /// <summary>Normalised lookup key, e.g. "chemicalcatalyst".</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    /// <summary>Display name, e.g. "Chemical Catalyst". Also used (normalised) to match
    /// ShipLocker.json entries, so no separate internal-name field is needed.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Top-level group: Assets, Data or Goods.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    /// <summary>Sub-group under Assets: Chemicals, Circuits or Tech. Empty for Data/Goods.</summary>
    [JsonPropertyName("subCategory")]
    public string SubCategory { get; set; } = "";

    /// <summary>Inara component group id.</summary>
    [JsonPropertyName("inaraTypeId")]
    public int InaraTypeId { get; set; }

    /// <summary>Inara component item id.</summary>
    [JsonPropertyName("inaraItemId")]
    public int InaraItemId { get; set; }

    /// <summary>Quantity needed to max every blueprint that uses this component.</summary>
    [JsonPropertyName("targetQty")]
    public int TargetQty { get; set; }

    /// <summary>
    /// Bartender point cost/value for this component, only present for Assets (Chemicals/
    /// Circuits/Tech) - null for Goods and Data, which aren't part of the in-game point-barter
    /// system (they can only be sold to the bartender for a flat credit price instead).
    /// </summary>
    [JsonPropertyName("barterValue")]
    public int? BarterValue { get; set; }
}
