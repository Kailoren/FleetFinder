using System.Text.Json.Serialization;

namespace FleetView.Models;

/// <summary>One commodity requirement of a modification.</summary>
public sealed class ModRequirement
{
    [JsonPropertyName("commodity")]
    public string Commodity { get; set; } = "";

    [JsonPropertyName("amount")]
    public int Amount { get; set; }
}

/// <summary>
/// A suit or weapon engineering modification and the commodities it consumes.
/// Mirrors an entry in Data/modifications.json.
/// </summary>
public sealed class Modification
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>"Suit" or "Weapon".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("requirements")]
    public List<ModRequirement> Requirements { get; set; } = new();
}
