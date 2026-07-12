using System.IO;
using System.Text.Json;
using FleetView.Models;

namespace FleetView.Services;

/// <summary>Loads the bundled suit/weapon modification catalog (Data/modifications.json).</summary>
public static class ModificationLoader
{
    public static IReadOnlyList<Modification> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "modifications.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Modifications catalog not found at {path}");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<Modification>>(json)
               ?? throw new InvalidDataException("modifications.json could not be parsed.");
    }
}
