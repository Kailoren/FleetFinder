using System.IO;
using System.Text.Json;

namespace FleetView.Services;

/// <summary>One component still short when the app was last closed with active buy targets.</summary>
public sealed record PendingSearchEntry(string Name, int Target);

/// <summary>One component still ticked in the Sell column when the app was last closed - no
/// Target concept on the sell side, so just the name is enough.</summary>
public sealed record PendingSellEntry(string Name);

/// <summary>Everything persisted across launches by <see cref="PendingSearchStore"/>.</summary>
public sealed record PendingSearchData(List<PendingSearchEntry> Buy, List<PendingSellEntry> Sell);

/// <summary>
/// Persists an in-progress "shopping list" (buy targets set by applying modifications or
/// importing an EDOMH wishlist, plus any manually-ticked Sell selections) across launches, so
/// closing mid-search before finishing doesn't lose it. Overwritten on every close - both lists
/// empty means nothing was left incomplete.
/// </summary>
public static class PendingSearchStore
{
    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "pending-search.json");

    public static PendingSearchData? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<PendingSearchData>(File.ReadAllText(FilePath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Overwrites the cache with the current incomplete set, or clears it if both are empty.</summary>
    public static void Save(IReadOnlyList<PendingSearchEntry> buy, IReadOnlyList<PendingSellEntry> sell)
    {
        try
        {
            if (buy.Count == 0 && sell.Count == 0)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return;
            }
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Data"));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new PendingSearchData(buy.ToList(), sell.ToList())));
        }
        catch { /* best effort */ }
    }
}
