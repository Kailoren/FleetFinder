using System.IO;
using System.Text.Json;
using FleetView.Models;

namespace FleetView.Services;

/// <summary>Loads the bundled component reference catalog (Data/catalog.json).</summary>
public static class CatalogLoader
{
    public static IReadOnlyList<Component> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "catalog.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Component catalog not found at {path}");

        var json = File.ReadAllText(path);
        var list = JsonSerializer.Deserialize<List<Component>>(json)
                   ?? throw new InvalidDataException("catalog.json could not be parsed.");
        return list;
    }
}
