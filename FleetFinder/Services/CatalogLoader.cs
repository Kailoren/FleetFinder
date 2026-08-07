using System.IO;
using System.Text.Json;
using FleetView.Models;

namespace FleetView.Services;

/// <summary>Loads the bundled component reference catalog (Data/catalog.json).</summary>
public static class CatalogLoader
{
    /// <summary>
    /// Stated rather than left to the defaults. Passing null options meant case-sensitive matching
    /// and no trailing-comma or comment tolerance, none of which was a decision anyone made - and
    /// the failure mode is silent: a property whose name differs only in case does not fail to
    /// parse, it parses to a default.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<Component> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "catalog.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Component catalog not found at {path}");

        List<Component>? list;
        try
        {
            list = JsonSerializer.Deserialize<List<Component>>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            // Surfaced as the same failure as every other bad-catalog case, so App's startup
            // handler shows one clear message instead of a raw parser exception.
            throw new InvalidDataException($"catalog.json is not valid JSON: {ex.Message}", ex);
        }

        if (list is null || list.Count == 0)
            throw new InvalidDataException("catalog.json parsed but contained no components.");

        // Parsing is not validation. A document whose objects have unrelated or differently-cased
        // property names deserializes into a perfectly well-formed list of components with every
        // field at its default, which the old null check could never catch - it only ever refused
        // a top-level JSON null. Each entry is checked here so a wrong file fails at startup with
        // its index named, instead of the app running on an empty catalog.
        // Uniqueness is checked alongside the per-entry rules rather than after them, so a file
        // with both problems reports the first one in file order either way.
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalisedNames = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (string.IsNullOrWhiteSpace(c.Key))
                throw new InvalidDataException($"catalog.json entry {i} has no \"key\".");
            if (string.IsNullOrWhiteSpace(c.Name))
                throw new InvalidDataException($"catalog.json entry \"{c.Key}\" has no \"name\".");
            if (c.TargetQty < 0)
                throw new InvalidDataException(
                    $"catalog.json entry \"{c.Key}\" has a negative \"targetQty\".");

            // The one rule that spans entries, and so the one the per-entry loop above could never
            // have caught. Case-insensitively, to match the deserializer this file deliberately
            // configured that way.
            if (!keys.Add(c.Key))
                throw new InvalidDataException(
                    $"catalog.json entry {i} repeats the key \"{c.Key}\", which must be unique.");

            // Names matter as much as keys, because they are what the catalog is joined to
            // inventory on: MainViewModel builds a lookup keyed by the normalised name, and two
            // entries reducing to one key throw there instead - once per second, on the background
            // inventory poll, a long way from the file that caused it.
            var normalised = ShipLockerReader.Normalize(c.Name);
            if (!normalisedNames.Add(normalised))
                throw new InvalidDataException(
                    $"catalog.json entry \"{c.Key}\" has a \"name\" of \"{c.Name}\", which is not " +
                    "distinct from an earlier entry's once punctuation and case are ignored.");
        }

        return list;
    }
}
