using System.Text.Json.Serialization;

namespace FleetView.Models;

/// <summary>
/// One on-foot engineer as Data/engineers.json describes them: where they are, how to meet them,
/// who they lead to, and the suit and weapon modifications they do.
/// </summary>
/// <remarks>
/// The file carries no coordinates: distances come from looking each system up, the same way
/// carrier systems are, and stay unknown until the lookup answers.
/// </remarks>
public sealed class Engineer
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>"Bubble" or "Colonia".</summary>
    [JsonPropertyName("region")]
    public string Region { get; set; } = "";

    [JsonPropertyName("system")]
    public string System { get; set; } = "";

    /// <summary>The settlement the engineer works from, e.g. "The Jackrabbit".</summary>
    [JsonPropertyName("settlement")]
    public string Settlement { get; set; } = "";

    /// <summary>How the engineer becomes known: "Common knowledge." or who refers them.</summary>
    [JsonPropertyName("find")]
    public string Find { get; set; } = "";

    /// <summary>What earns the invitation.</summary>
    [JsonPropertyName("invite")]
    public string Invite { get; set; } = "";

    /// <summary>The task that makes this engineer tell you about the next one; empty when they
    /// refer nobody.</summary>
    [JsonPropertyName("referral")]
    public string Referral { get; set; } = "";

    /// <summary>The engineers the referral task leads to.</summary>
    [JsonPropertyName("refersTo")]
    public List<string> RefersTo { get; set; } = new();

    [JsonPropertyName("suitModules")]
    public List<string> SuitModules { get; set; } = new();

    [JsonPropertyName("weaponModules")]
    public List<string> WeaponModules { get; set; } = new();

    [JsonIgnore]
    public bool IsColonia => string.Equals(Region, "Colonia", StringComparison.OrdinalIgnoreCase);
}
