using System.IO;
using System.Text.Json;
using FleetView.Models;

namespace FleetView.Services;

/// <summary>Loads the bundled suit/weapon modification catalog (Data/modifications.json).</summary>
public static class ModificationLoader
{
    /// <inheritdoc cref="CatalogLoader"/>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<Modification> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "modifications.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Modifications catalog not found at {path}");

        List<Modification>? list;
        try
        {
            list = JsonSerializer.Deserialize<List<Modification>>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"modifications.json is not valid JSON: {ex.Message}", ex);
        }

        if (list is null || list.Count == 0)
            throw new InvalidDataException("modifications.json parsed but contained no modifications.");

        // Same reasoning as CatalogLoader: the "?? throw" this replaced only ever fired on a
        // top-level JSON null, so a file whose property names did not match deserialized into
        // well-formed modifications with blank names and no requirements, and the app went on to
        // present that as the catalog.
        for (int i = 0; i < list.Count; i++)
        {
            var m = list[i];
            if (string.IsNullOrWhiteSpace(m.Name))
                throw new InvalidDataException($"modifications.json entry {i} has no \"name\".");
            if (string.IsNullOrWhiteSpace(m.Kind))
                throw new InvalidDataException($"modification \"{m.Name}\" has no \"kind\".");
            if (m.Requirements is null)
                throw new InvalidDataException($"modification \"{m.Name}\" has no \"requirements\" list.");

            foreach (var r in m.Requirements)
            {
                if (r is null || string.IsNullOrWhiteSpace(r.Commodity))
                    throw new InvalidDataException(
                        $"modification \"{m.Name}\" has a requirement with no \"commodity\".");
                if (r.Amount < 0)
                    throw new InvalidDataException(
                        $"modification \"{m.Name}\" requires a negative amount of \"{r.Commodity}\".");
            }
        }

        return list;
    }
}
