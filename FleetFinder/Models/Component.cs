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
    /// Bartender point cost to acquire (BUY) this component via barter, only present for Assets
    /// (Chemicals/Circuits/Tech) - null for Goods and Data, which aren't part of the in-game
    /// point-barter system (they can only be sold to the bartender for a flat credit price
    /// instead). This is what a "want" item costs in points, NOT what giving it up nets you -
    /// see <see cref="BarterSellValue"/> for that.
    /// </summary>
    [JsonPropertyName("barterValue")]
    public int? BarterValue { get; set; }

    /// <summary>
    /// Bartender point value (SELL/trade-in) gained when giving up one unit of this component -
    /// always lower than <see cref="BarterValue"/> (the BUY cost). This is the correct figure for
    /// what a "give" item actually contributes to the pot; only present for Assets, same as
    /// <see cref="BarterValue"/>.
    /// </summary>
    [JsonPropertyName("barterSellValue")]
    public int? BarterSellValue { get; set; }

    /// <summary>
    /// Flat credit price the bartender pays for this component, informational only (not used in
    /// the point-barter math) - only present for Assets.
    /// </summary>
    [JsonPropertyName("creditValue")]
    public int? CreditValue { get; set; }
}
