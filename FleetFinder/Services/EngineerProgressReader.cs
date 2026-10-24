using System.Globalization;
using System.IO;
using System.Text.Json;
using FleetView.Models;

namespace FleetView.Services;

/// <summary>
/// Reads the journal's <c>EngineerProgress</c> events for the commander who logged in last, for
/// the Engineers tab: which engineers that commander knows, has been invited by, or has unlocked.
/// </summary>
/// <remarks>
/// <para>
/// The game writes the event two ways. At login it lists every engineer the commander has an entry
/// for, under <c>Engineers</c>; after that, each change comes as a single engineer with the fields
/// at the top level. So the reader starts from a full list and lays the single updates after it on
/// top, in the order the game wrote them. An engineer the journal never names is Not met.
/// </para>
/// <para>
/// Events belong to the login before them, matched by Frontier ID when both logins carry one and
/// by name otherwise, the same rule the Ship Locker tab uses. The login is carried from one journal
/// into the next, so a session the game split into a second part keeps its owner; an
/// EngineerProgress event never comes before the login in a new session, so nothing is misplaced
/// by that.
/// </para>
/// <para>
/// It reads journal files, so call it off the UI thread. Journals other than one that changed are
/// kept parsed between calls, so a read while the game plays costs the growing journal only.
/// </para>
/// </remarks>
public sealed class EngineerProgressReader
{
    /// <summary>How many journals are looked through, newest first. The login list is written at
    /// every login, so the newest few nearly always hold it; this reaches back past menu-only
    /// sessions the same way the commander lookup does.</summary>
    private const int JournalFiles = 25;

    private const int MaxNameLength = 64;
    private const int MaxCommanderLength = 64;

    /// <summary>A real full list has about forty entries (every ship and on-foot engineer). Past
    /// this an event is not something the game wrote, and the rest of it is ignored.</summary>
    private const int MaxEntriesPerEvent = 200;

    private readonly JournalReader _journal;
    private readonly Dictionary<string, ParsedJournal> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public EngineerProgressReader(JournalReader journal)
    {
        _journal = journal;
    }

    /// <summary>The engineer standings of the latest login's commander. Never throws for anything
    /// on disk; a journal that cannot be read is skipped.</summary>
    public EngineerProgressSnapshot Read()
    {
        lock (_gate)
        {
            var journals = ParseJournals(); // newest first

            var latest = journals.Select(j => j.Entries.OfType<Login>().LastOrDefault())
                .FirstOrDefault(l => l != null);

            var standings = new Dictionary<string, EngineerStanding>(StringComparer.OrdinalIgnoreCase);
            bool any = false;
            Login? owner = null;
            for (int f = journals.Count - 1; f >= 0; f--)
            {
                foreach (var entry in journals[f].Entries)
                {
                    if (entry is Login login)
                    {
                        owner = login;
                        continue;
                    }
                    if (entry is not Progress progress) continue;

                    // With no login anywhere there is only one commander to credit it to.
                    bool ours = latest == null || (owner != null && SameCommander(owner, latest));
                    if (!ours) continue;
                    any = true;

                    if (progress.Full) standings.Clear();
                    foreach (var item in progress.Items)
                    {
                        if (item.State is EngineerState state)
                            standings[item.Name] = new EngineerStanding(state, item.Rank, item.RankProgress, progress.Utc);
                        else if (standings.TryGetValue(item.Name, out var known))
                            // A grade going up arrives without a Progress; the state stays as it was.
                            standings[item.Name] = known with
                            {
                                Rank = item.Rank ?? known.Rank,
                                RankProgress = item.RankProgress ?? known.RankProgress,
                                Utc = progress.Utc,
                            };
                    }
                }
            }

            return new EngineerProgressSnapshot(latest?.Commander ?? "", standings, any);
        }
    }

    // ---- the journal ----------------------------------------------------------------------------

    private abstract record Entry(DateTime Utc);

    private sealed record Login(DateTime Utc, string Commander, string Fid) : Entry(Utc);

    /// <param name="Full">The login list, which replaces everything before it.</param>
    private sealed record Progress(DateTime Utc, bool Full, IReadOnlyList<Item> Items) : Entry(Utc);

    /// <param name="State">Null when the event did not say (a grade change), or said something
    /// this app does not know.</param>
    private sealed record Item(string Name, EngineerState? State, int? Rank, int? RankProgress);

    private sealed record ParsedJournal(string Path, long Length, DateTime Written, IReadOnlyList<Entry> Entries);

    private List<ParsedJournal> ParseJournals()
    {
        var files = _journal.FilesNewestFirst().Take(JournalFiles).ToList();
        var result = new List<ParsedJournal>(files.Count);
        foreach (var path in files)
        {
            long length;
            DateTime written;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                length = info.Length;
                written = info.LastWriteTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!_cache.TryGetValue(path, out var parsed) || parsed.Length != length || parsed.Written != written)
            {
                parsed = new ParsedJournal(path, length, written, ParseJournal(path));
                _cache[path] = parsed;
            }
            result.Add(parsed);
        }

        foreach (var gone in _cache.Keys.Except(files, StringComparer.OrdinalIgnoreCase).ToList())
            _cache.Remove(gone);
        return result;
    }

    private static List<Entry> ParseJournal(string path)
    {
        var entries = new List<Entry>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            while (JournalReader.ReadBoundedLine(sr, out var line, out bool oversize))
            {
                if (oversize) continue;
                // Cheap tests first: nearly every line is some other event and is never parsed.
                bool login = line.Contains("\"Commander\"", StringComparison.Ordinal)
                             || line.Contains("\"LoadGame\"", StringComparison.Ordinal);
                bool progress = line.Contains("\"EngineerProgress\"", StringComparison.Ordinal);
                if (!login && !progress) continue;
                if (TryParseEntry(line) is { } entry) entries.Add(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            DiagnosticLog.Note($"Journal file could not be read for engineer progress ({ex.GetType().Name}): {Path.GetFileName(path)}");
        }
        return entries;
    }

    private static Entry? TryParseEntry(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Timestamp(root) is not DateTime utc) return null;
            string ev = Text(root, "event");
            switch (ev)
            {
                case "Commander":
                case "LoadGame":
                    string name = SafeText.Clean(Text(root, ev == "Commander" ? "Name" : "Commander"), MaxCommanderLength);
                    if (name.Length == 0) return null;
                    return new Login(utc, name, SafeText.Clean(Text(root, "FID"), 32));
                case "EngineerProgress":
                    return ParseProgress(root, utc);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null; // a line the game was still writing, or not an event at all
        }
    }

    private static Progress? ParseProgress(JsonElement root, DateTime utc)
    {
        if (root.TryGetProperty("Engineers", out var list))
        {
            // A login list. One that is not a list at all is not something to wipe the standings
            // with, so it counts as no event.
            if (list.ValueKind != JsonValueKind.Array) return null;
            var items = new List<Item>();
            foreach (var el in list.EnumerateArray())
            {
                if (items.Count == MaxEntriesPerEvent) break;
                if (el.ValueKind == JsonValueKind.Object && ParseItem(el) is { } item) items.Add(item);
            }
            return new Progress(utc, true, items);
        }

        var single = ParseItem(root);
        return single == null ? null : new Progress(utc, false, [single]);
    }

    private static Item? ParseItem(JsonElement el)
    {
        string name = SafeText.Clean(Text(el, "Engineer"), MaxNameLength).Trim();
        if (name.Length == 0) return null;
        EngineerState? state = Text(el, "Progress") switch
        {
            "Unlocked" => EngineerState.Unlocked,
            "Invited" => EngineerState.Invited,
            "Known" or "Acquainted" => EngineerState.Known,
            "Barred" => EngineerState.Barred,
            _ => null,
        };
        return new Item(name, state, Whole(el, "Rank", 0, 10), Whole(el, "RankProgress", 0, 100));
    }

    /// <summary>A whole number inside a range, or null for anything else.</summary>
    private static int? Whole(JsonElement obj, string property, int min, int max) =>
        obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.Number
        && el.TryGetInt32(out int v) && v >= min && v <= max
            ? v
            : null;

    private static bool SameCommander(Login a, Login b) =>
        a.Fid.Length > 0 && b.Fid.Length > 0
            ? string.Equals(a.Fid, b.Fid, StringComparison.OrdinalIgnoreCase)
            : string.Equals(a.Commander, b.Commander, StringComparison.OrdinalIgnoreCase);

    private static string Text(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static DateTime? Timestamp(JsonElement root) =>
        DateTime.TryParse(Text(root, "timestamp"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
            ? utc
            : null;
}
