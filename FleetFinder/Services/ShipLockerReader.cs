using System.Buffers;
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
    /// <summary>
    /// Ceiling on the inventory file. A full on-foot locker is tens of kilobytes; this is far
    /// above any real one and exists because the file is written by another process, so its size
    /// is not this app's to assume.
    /// </summary>
    private const long MaxFileBytes = 8 * 1024 * 1024;

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
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    // TryGetProperty establishes that "Count" is there, not that it is a number.
                    // GetInt32 throws on a string, a fraction or anything past Int32, and the only
                    // try block around this disposes the document rather than handling anything -
                    // so a file that is valid JSON with one odd value took the whole read down.
                    if (!item.TryGetProperty("Count", out var countEl)) continue;
                    if (countEl.ValueKind != JsonValueKind.Number) continue;
                    if (!countEl.TryGetInt32(out int count) || count < 0) continue;

                    // Key by both the internal name and the localised display name so a
                    // modification's display-name commodity matches whatever the journal used.
                    // They usually normalise to the same string ("Chemical Catalyst" and
                    // "chemicalcatalyst" both reduce to the latter), so the count is added once
                    // per distinct key - adding it per property would double every such entry.
                    string nameKey = KeyOf(item, "Name");
                    string localKey = KeyOf(item, "Name_Localised");

                    Add(result, nameKey, count);
                    if (!string.Equals(localKey, nameKey, StringComparison.Ordinal))
                        Add(result, localKey, count);
                }
            }
        }
        return result;

        static string KeyOf(JsonElement item, string property) =>
            item.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
                ? Normalize(el.GetString())
                : "";

        // Accumulates rather than assigns. The file lists an entry per stack, so the same item can
        // appear more than once (mission-specific holdings are separate entries), and Normalize is
        // lossy enough that two different names can reduce to one key. Assigning meant the last
        // occurrence won and every earlier one was discarded, under-reporting what the commander
        // actually holds and understating it in exactly the direction that matters here - the app
        // would say something was still needed when it was not.
        static void Add(Dictionary<string, int> map, string key, int count)
        {
            if (key.Length == 0) return;
            map[key] = map.TryGetValue(key, out int running)
                ? (int)Math.Min((long)running + count, int.MaxValue)
                : count;
        }
    }

    /// <summary>Lower-cases and strips non-alphanumerics so display and internal names match.</summary>
    /// <remarks>
    /// The stack buffer is capped and falls back to a pooled array. The names reaching here are
    /// not this app's to trust: as well as ShipLocker.json, <see cref="EdsmCoordinateSource"/>
    /// passes system names straight out of edsm.net's HTTP response. Sizing a stackalloc from
    /// one of those made a long enough name a stack overflow, which unlike a failed heap
    /// allocation cannot be caught and takes the process down with it - and the caller's
    /// catch-all would have looked like it covered that, while catching nothing.
    /// </remarks>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        char[]? rented = s.Length <= 256 ? null : ArrayPool<char>.Shared.Rent(s.Length);
        try
        {
            Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : rented;
            int n = 0;
            foreach (var ch in s)
                if (char.IsLetterOrDigit(ch)) buf[n++] = char.ToLowerInvariant(ch);
            return new string(buf[..n]);
        }
        finally
        {
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Reads and parses <paramref name="path"/> as JSON, retrying while the file is locked or
    /// (briefly) empty from the game truncating it mid-rewrite. Never throws; returns false if
    /// valid JSON still can't be obtained after retrying, so callers can just skip the refresh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retrying is for a file being written right now. A file that is simply not valid JSON is not
    /// going to become valid by waiting, and this runs on the UI thread from the one-second
    /// inventory poll - so a permanently malformed ShipLocker.json cost six attempts and six tenths
    /// of a second of frozen window, every second, forever. The file's size and write time are
    /// checked between attempts: unchanged means the writer is not mid-write, so a parse failure is
    /// the file's actual content and there is nothing to wait for.
    /// </para>
    /// <para>
    /// Giving up is also recorded now. Returning an empty dictionary made an unreadable file look
    /// exactly like an empty locker, which is the one failure that produces a confidently wrong
    /// answer rather than a missing one - the app would say a component was still needed while the
    /// commander was carrying it.
    /// </para>
    /// </remarks>
    private static bool TryReadJson(string path, out JsonDocument? doc, int attempts = 6)
    {
        doc = null;
        (long Length, DateTime Written) previous = (-1, DateTime.MinValue);

        for (int i = 0; i < attempts; i++)
        {
            if (i > 0) Thread.Sleep(120);

            bool unchangedSinceLastAttempt = false;

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return false;

                var current = (info.Length, info.LastWriteTimeUtc);
                unchangedSinceLastAttempt = current == previous;
                previous = current;

                // Checked before opening, and enforced again while reading. This file's size is
                // whatever another process wrote, the read used to be a ReadToEnd into a string
                // with no ceiling at all, and it happens six times over in the retry loop below -
                // so an oversized file was six unbounded allocations, not one. The stream wrapper
                // is what makes the limit hold if the file grows between the check and the read.
                if (info.Length > MaxFileBytes)
                {
                    DiagnosticLog.Note(
                        $"ShipLocker file is {info.Length:N0} bytes, over the {MaxFileBytes:N0} " +
                        "byte limit; inventory not read.");
                    return false;
                }

                if (info.Length == 0) continue; // caught mid truncate-then-rewrite, retry

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var bounded = BoundedHttp.Limit(fs, MaxFileBytes);
                doc = JsonDocument.Parse(bounded);
                return true;
            }
            catch (IOException)
            {
                continue; // locked for writing right now, retry
            }
            catch (JsonException)
            {
                // A file that has not changed since the previous attempt is not being written, so
                // this is what it contains rather than a snapshot taken mid-write.
                if (!unchangedSinceLastAttempt) continue;

                DiagnosticLog.Note(
                    "ShipLocker file is not valid JSON and is not currently being written; " +
                    "inventory not read.");
                return false;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                                           or System.Security.SecurityException)
            {
                // Neither derives from IOException, so both used to escape this method entirely
                // and out of ReadAllCounts, which has no handler either. File.Exists returning
                // true says the file is there, not that this process may open it.
                DiagnosticLog.Note($"ShipLocker file could not be opened ({ex.GetType().Name}).");
                return false;
            }
        }

        // Every attempt was spent on a file that kept changing under us, or that stayed locked.
        // Recorded for the same reason as the malformed case: an empty result reads as an empty
        // locker, and the two need telling apart.
        DiagnosticLog.Note(
            $"ShipLocker file could not be read after {attempts} attempts; inventory not read.");
        return false;
    }

    private static string DefaultPath()
    {
        string saved = GetSavedGamesFolder();
        return Path.Combine(saved, "Frontier Developments", "Elite Dangerous", "ShipLocker.json");
    }

    private static string GetSavedGamesFolder()
    {
        // FOLDERID_SavedGames, not exposed by Environment.SpecialFolder.
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(FolderIdSavedGames, 0, IntPtr.Zero, out buffer) == 0)
            {
                string path = Marshal.PtrToStringUni(buffer) ?? "";
                if (!string.IsNullOrEmpty(path))
                    return path;
            }
        }
        catch
        {
            // fall through to profile-based path
        }
        finally
        {
            // Freed on every path, not only the successful one: the API's contract is that the
            // caller owns the buffer whenever it is non-null, including on a failure return, and
            // the free used to sit inside the success branch with the catch below it able to skip
            // it entirely.
            if (buffer != IntPtr.Zero) Marshal.FreeCoTaskMem(buffer);
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
