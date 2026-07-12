using System.IO;
using System.Text.Json;

namespace FleetView.Services;

/// <summary>One component still short when the app was last closed with active targets.</summary>
public sealed record PendingSearchEntry(string Name, int Target);

/// <summary>
/// Persists an in-progress "shopping list" (targets set by applying modifications or importing
/// an EDOMH wishlist) across launches, so closing mid-search before buying everything doesn't
/// lose it. Overwritten on every close — empty/absent means nothing was left incomplete.
/// </summary>
public static class PendingSearchStore
{
    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "pending-search.json");

    public static List<PendingSearchEntry>? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<List<PendingSearchEntry>>(File.ReadAllText(FilePath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Overwrites the cache with the current incomplete set, or clears it if empty.</summary>
    public static void Save(IReadOnlyList<PendingSearchEntry> entries)
    {
        try
        {
            if (entries.Count == 0)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return;
            }
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Data"));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries));
        }
        catch { /* best effort */ }
    }
}
