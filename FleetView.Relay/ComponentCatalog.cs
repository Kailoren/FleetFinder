using System.Text.Json;

namespace FleetView.Relay;

/// <summary>
/// Resolves a normalized component key back to its human-readable display name (e.g.
/// "geneticrepairmeds" -> "Genetic Repair Meds"), loaded once from a copy of FleetFinder's
/// Data/catalog.json. Needed because EDDN's FCMaterials messages carry unlocalised internal
/// keys, not display strings - see <see cref="Eddn.FcMaterialsHandler"/>.
/// </summary>
public sealed class ComponentCatalog
{
    private readonly Dictionary<string, string> _displayNameByKey;

    public ComponentCatalog(string catalogJsonPath)
    {
        _displayNameByKey = new Dictionary<string, string>();
        if (!File.Exists(catalogJsonPath)) return;

        using var doc = JsonDocument.Parse(File.ReadAllText(catalogJsonPath));
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            string? key = entry.GetProperty("key").GetString();
            string? name = entry.GetProperty("name").GetString();
            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(name))
                _displayNameByKey[key] = name;
        }
    }

    /// <summary>Display name for a normalized key, or the key itself if not in the catalog.</summary>
    public string DisplayName(string key) => _displayNameByKey.GetValueOrDefault(key, key);
}
