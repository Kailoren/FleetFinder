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
    /// <summary>
    /// Ceiling on a name handed back for a key that is not in the catalog. The fallback echoes a
    /// value that arrived from EDDN, so it gets the same kind of bound the catalog's own entries
    /// have by being written by us.
    /// </summary>
    private const int MaxFallbackLength = 64;

    private readonly Dictionary<string, string> _displayNameByKey;

    /// <summary>
    /// Loads the catalog, or leaves it empty if the file is missing or unusable.
    /// </summary>
    /// <remarks>
    /// Every failure mode ends the same way as the missing-file case, deliberately. The old
    /// version guarded only absence, so a truncated file, a JSON object where an array was
    /// expected, or one entry without a "key" each threw out of the constructor and took the whole
    /// relay's startup with it. An empty catalog costs display names - keys are shown raw instead -
    /// and nothing else, which is not worth refusing to run over.
    /// </remarks>
    public ComponentCatalog(string catalogJsonPath)
    {
        _displayNameByKey = new Dictionary<string, string>();
        if (!File.Exists(catalogJsonPath)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(catalogJsonPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("key", out var keyEl)
                    || keyEl.ValueKind != JsonValueKind.String) continue;
                if (!entry.TryGetProperty("name", out var nameEl)
                    || nameEl.ValueKind != JsonValueKind.String) continue;

                string? key = keyEl.GetString();
                string? name = nameEl.GetString();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(name))
                    _displayNameByKey[key] = name;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _displayNameByKey.Clear();
        }
    }

    /// <summary>
    /// Display name for a normalized key, or a bounded copy of the key itself if it is not in the
    /// catalog. Keys reaching here come from EDDN (see <see cref="Eddn.FcMaterialsHandler"/>), so
    /// the fallback is length-capped rather than returned as given - the catalog lookup looks like
    /// an allow-list, and a fallback that echoes its input is the one path that isn't one.
    /// </summary>
    public string DisplayName(string key)
    {
        if (_displayNameByKey.TryGetValue(key, out var name)) return name;
        return key.Length <= MaxFallbackLength ? key : key[..MaxFallbackLength];
    }
}
