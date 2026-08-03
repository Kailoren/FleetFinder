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
    /// <summary>The catalog is 90 components, so a list several times that is already not
    /// something this app wrote. Bounds what one file can make the app rebuild on startup.</summary>
    private const int MaxEntries = 500;

    /// <summary>Longest component name accepted. Every real one is well under this; names longer
    /// than the catalog's own would not match a row anyway.</summary>
    private const int MaxNameLength = 120;

    /// <summary>Sanity ceiling on a saved target, far above any real shopping list.</summary>
    private const int MaxTarget = 100_000;

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "pending-search.json");

    /// <summary>
    /// Returns the saved list, or null if there is nothing usable to resume.
    /// </summary>
    /// <remarks>
    /// The deserialized entries are filtered rather than returned as they arrive. This file lives
    /// beside the executable, so anything able to write to the install directory can write it, and
    /// the result was previously handed to the caller with no check on the lists being present,
    /// their length, or the values inside. Names are matched against the catalog downstream and
    /// out-of-range entries would mostly be dropped there, but "mostly, somewhere else" is not
    /// where a file's contents should stop being arbitrary.
    /// </remarks>
    public static PendingSearchData? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            var data = JsonSerializer.Deserialize<PendingSearchData>(File.ReadAllText(FilePath));
            if (data is null) return null;

            var buy = (data.Buy ?? [])
                .Where(e => e is not null && IsUsableName(e.Name) && e.Target >= 0 && e.Target <= MaxTarget)
                .Take(MaxEntries)
                .ToList();

            var sell = (data.Sell ?? [])
                .Where(e => e is not null && IsUsableName(e.Name))
                .Take(MaxEntries)
                .ToList();

            return buy.Count == 0 && sell.Count == 0 ? null : new PendingSearchData(buy, sell);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // corrupt or unreadable -> start with nothing to resume
        }
    }

    private static bool IsUsableName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= MaxNameLength
        && !name.Any(char.IsControl);

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort by design - losing a resume prompt is not worth interrupting a close
            // over. Caught by type so an unexpected failure still surfaces.
        }
    }
}
