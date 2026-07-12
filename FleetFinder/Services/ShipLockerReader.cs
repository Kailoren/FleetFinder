using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FleetView.Services;

/// <summary>
/// Reads the player's on-foot inventory from Elite Dangerous' ShipLocker.json,
/// returning current counts keyed by normalised item name (e.g. "chemicalcatalyst").
/// </summary>
public sealed class ShipLockerReader
{
    public string FilePath { get; }

    public ShipLockerReader(string? filePath = null)
    {
        FilePath = filePath ?? DefaultPath();
    }

    /// <summary>True if the ShipLocker.json file currently exists.</summary>
    public bool Exists => File.Exists(FilePath);

    /// <summary>Last time the journal file was written, or null if it doesn't exist.</summary>
    public DateTime? LastWriteUtc => Exists ? File.GetLastWriteTimeUtc(FilePath) : null;

    /// <summary>
    /// Returns counts for every on-foot item across all categories (Items, Components,
    /// Consumables, Data), keyed by <see cref="Normalize"/>d name. This lets modifications
    /// show held counts for non-tradeable commodities (Data/Goods) too.
    /// </summary>
    public Dictionary<string, int> ReadAllCounts()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!Exists)
            return result;

        if (!TryReadJson(FilePath, out var doc))
            return result;

        using (doc)
        {
            foreach (var section in new[] { "Items", "Components", "Consumables", "Data" })
            {
                if (!doc!.RootElement.TryGetProperty(section, out var arr)
                    || arr.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in arr.EnumerateArray())
                {
                    if (!item.TryGetProperty("Count", out var countEl)) continue;
                    int count = countEl.GetInt32();

                    // Key by both the internal name and the localised display name so a
                    // modification's display-name commodity matches whatever the journal used.
                    if (item.TryGetProperty("Name", out var nEl))
                        Add(result, nEl.GetString(), count);
                    if (item.TryGetProperty("Name_Localised", out var lEl))
                        Add(result, lEl.GetString(), count);
                }
            }
        }
        return result;

        static void Add(Dictionary<string, int> map, string? name, int count)
        {
            var key = Normalize(name);
            if (key.Length > 0) map[key] = count;
        }
    }

    /// <summary>Lower-cases and strips non-alphanumerics so display and internal names match.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        Span<char> buf = stackalloc char[s.Length];
        int n = 0;
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) buf[n++] = char.ToLowerInvariant(ch);
        return new string(buf[..n]);
    }

    /// <summary>
    /// Reads and parses <paramref name="path"/> as JSON, retrying while the file is locked or
    /// (briefly) empty from the game truncating it mid-rewrite. Returns false — never throws —
    /// if valid JSON still can't be obtained after retrying, so callers can just skip the refresh.
    /// </summary>
    private static bool TryReadJson(string path, out JsonDocument? doc, int attempts = 6)
    {
        for (int i = 0; i < attempts; i++)
        {
            if (i > 0) Thread.Sleep(120);

            string content;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                content = sr.ReadToEnd();
            }
            catch (IOException)
            {
                continue; // locked for writing right now — retry
            }

            if (string.IsNullOrWhiteSpace(content))
                continue; // caught the file mid truncate-then-rewrite — retry

            try
            {
                doc = JsonDocument.Parse(content);
                return true;
            }
            catch (JsonException)
            {
                continue; // partially-written JSON — retry
            }
        }
        doc = null;
        return false;
    }

    private static string DefaultPath()
    {
        string saved = GetSavedGamesFolder();
        return Path.Combine(saved, "Frontier Developments", "Elite Dangerous", "ShipLocker.json");
    }

    private static string GetSavedGamesFolder()
    {
        // FOLDERID_SavedGames — not exposed by Environment.SpecialFolder.
        try
        {
            if (SHGetKnownFolderPath(FolderIdSavedGames, 0, IntPtr.Zero, out IntPtr p) == 0)
            {
                string path = Marshal.PtrToStringUni(p) ?? "";
                Marshal.FreeCoTaskMem(p);
                if (!string.IsNullOrEmpty(path))
                    return path;
            }
        }
        catch
        {
            // fall through to profile-based path
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
    }

    private static readonly Guid FolderIdSavedGames =
        new("4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
