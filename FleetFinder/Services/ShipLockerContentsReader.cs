using System.Globalization;
using System.IO;
using System.Text.Json;
using FleetView.Models;

namespace FleetView.Services;

/// <summary>
/// Reads everything in the ship locker for the commander who logged in last, for the Ship Locker
/// tab: four groups of (name, count), when the game wrote it, and where it came from.
/// </summary>
/// <remarks>
/// <para>
/// The game records the locker in two places. The journal gets a <c>ShipLocker</c> event, which
/// carries the full list at login but is written empty every time the locker changes after that,
/// and ShipLocker.json, which the game rewrites in full each time. So the journal's event is
/// read first and the file stands in whenever the event is empty; when both have a full list the
/// newer one wins, which is what keeps the tab current while the game plays.
/// </para>
/// <para>
/// ShipLocker.json does not say whose locker it is, and Ali plays two commanders on one Windows
/// account. The file is credited to whoever had logged in most recently at the time it was
/// written, by the journal's login events. If that is not the commander of the latest login, the
/// tab says so rather than show one commander's locker under the other's name.
/// </para>
/// <para>
/// It reads journal files, so call it off the UI thread. Journals other than the newest are kept
/// parsed between calls (only their size or write time changing reads them again), so a read
/// while the game plays costs one journal and one small file.
/// </para>
/// </remarks>
public sealed class ShipLockerContentsReader
{
    /// <summary>How many journals are looked through, newest first. The same limit as the
    /// commander lookup: enough to reach back past menu-only sessions, not the whole history.</summary>
    private const int JournalFiles = 25;

    /// <summary>
    /// Longest journal line read. A full ShipLocker event lists every item on one line, which for a
    /// locker holding one of everything is about 30 KB; this leaves room for growth and longer
    /// localised names without letting one bad line take what memory it likes.
    /// </summary>
    private const int MaxEventLength = 1024 * 1024;

    /// <summary>Longest item name shown. The longest real one is about thirty characters.</summary>
    private const int MaxNameLength = 64;

    private const int MaxCommanderLength = 64;

    private static readonly (string Property, LockerGroup Group)[] Sections =
    [
        ("Items", LockerGroup.Goods),
        ("Components", LockerGroup.Assets),
        ("Data", LockerGroup.Data),
        ("Consumables", LockerGroup.Consumables),
    ];

    private readonly JournalReader _journal;
    private readonly string _lockerPath;
    private readonly IReadOnlyDictionary<string, string> _catalogNames;
    private readonly Dictionary<string, ParsedJournal> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <param name="journal">The journal folder, the same one the rest of the app reads.</param>
    /// <param name="lockerPath">Where ShipLocker.json is.</param>
    /// <param name="catalog">The component catalog, for a name when the game gives none.</param>
    public ShipLockerContentsReader(JournalReader journal, string lockerPath, IEnumerable<Component>? catalog)
    {
        _journal = journal;
        _lockerPath = lockerPath;
        _catalogNames = CatalogNames(catalog ?? []);
    }

    /// <summary>The newest locker for the commander of the latest login. Never throws for anything
    /// on disk; the state says what was found.</summary>
    public ShipLockerContents Read()
    {
        lock (_gate)
        {
            var journals = ParseJournals();
            var logins = journals.SelectMany(j => j.Entries).Where(e => e.Kind == EntryKind.Login).ToList();

            // The latest login: the last one in the newest journal that has one.
            var latest = journals.Select(j => j.Entries.LastOrDefault(e => e.Kind == EntryKind.Login))
                .FirstOrDefault(e => e != null);
            string commander = latest?.Commander ?? "";

            // The newest locker event of that commander's that has a list in it.
            Entry? eventWithBody = null;
            foreach (var journal in journals)
            {
                Entry? owner = null;
                Entry? found = null;
                foreach (var e in journal.Entries)
                {
                    if (e.Kind == EntryKind.Login) owner = e;
                    else if (e.Body != null && owner != null && (latest == null || SameCommander(owner, latest)))
                        found = e;
                    else if (e.Body != null && owner == null && latest == null)
                        found = e;
                }
                if (found != null)
                {
                    eventWithBody = found;
                    break;
                }
            }

            var file = ReadFile(out bool fileUnreadable);
            string fileOwner = "";
            if (file != null && latest != null)
            {
                var ownerLogin = logins.Where(l => l.Utc <= file.Utc).OrderBy(l => l.Utc).LastOrDefault();
                if (ownerLogin != null)
                {
                    if (!SameCommander(ownerLogin, latest))
                    {
                        fileOwner = ownerLogin.Commander;
                        file = null;
                    }
                }
                else if (!logins.All(l => SameCommander(l, latest)))
                {
                    // Older than every login on record, and more than one commander has logged in
                    // since: there is no telling whose it is.
                    fileOwner = "another commander";
                    file = null;
                }
            }

            ShipLockerContents FromEvent(Entry e)
            {
                using var doc = JsonDocument.Parse(e.Body!);
                return new ShipLockerContents(LockerState.Ready, commander, e.Utc, LockerSource.JournalEvent,
                    ParseSections(doc.RootElement, _catalogNames));
            }

            if (eventWithBody != null && (file == null || eventWithBody.Utc >= file.Utc))
                return FromEvent(eventWithBody);
            if (file != null)
                return new ShipLockerContents(LockerState.Ready, commander, file.Utc, LockerSource.File, file.Sections);
            if (fileOwner.Length > 0)
                return new ShipLockerContents(LockerState.OtherCommander, commander, null, LockerSource.None,
                    ShipLockerContents.EmptySections, fileOwner);
            return new ShipLockerContents(fileUnreadable ? LockerState.Unreadable : LockerState.NoLocker,
                commander, null, LockerSource.None, ShipLockerContents.EmptySections);
        }
    }

    // ---- ShipLocker.json ----------------------------------------------------------------------

    private sealed record LockerFile(DateTime Utc, IReadOnlyList<LockerSection> Sections);

    /// <summary>
    /// ShipLocker.json, or null when it is missing or cannot be read. The read retries while the
    /// game has the file open or half written, the same way the Find Carriers inventory does.
    /// </summary>
    private LockerFile? ReadFile(out bool unreadable)
    {
        unreadable = false;
        if (!File.Exists(_lockerPath)) return null;
        if (!ShipLockerReader.TryReadJson(_lockerPath, out var doc) || doc == null)
        {
            unreadable = true;
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                unreadable = true;
                return null;
            }
            // The game's own timestamp, not the file's: it is what the journal's logins are
            // compared with, and a copy or a sync tool can move the file's write time.
            var utc = Timestamp(root) ?? SafeWriteTime(_lockerPath);
            return new LockerFile(utc, ParseSections(root, _catalogNames));
        }
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    // ---- the journal ----------------------------------------------------------------------------

    private enum EntryKind { Login, Locker }

    /// <param name="Body">For a locker event with a list in it, the whole line; null for an
    /// empty one and for logins.</param>
    private sealed record Entry(EntryKind Kind, DateTime Utc, string Commander, string Fid, string? Body);

    private sealed record ParsedJournal(string Path, long Length, DateTime Written, IReadOnlyList<Entry> Entries);

    /// <summary>The newest journals, parsed down to their logins and locker events, newest first.</summary>
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

        // Journals that fell out of the window are not needed again.
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
            while (JournalReader.ReadBoundedLine(sr, out var line, out bool oversize, MaxEventLength))
            {
                if (oversize) continue;
                // Cheap tests first: most lines are other events and are never parsed.
                bool login = line.Contains("\"Commander\"", StringComparison.Ordinal)
                             || line.Contains("\"LoadGame\"", StringComparison.Ordinal);
                bool locker = line.Contains("\"ShipLocker\"", StringComparison.Ordinal);
                if (!login && !locker) continue;
                if (TryParseEntry(line) is { } entry) entries.Add(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            DiagnosticLog.Note($"Journal file could not be read for the ship locker ({ex.GetType().Name}): {Path.GetFileName(path)}");
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
                    return new Entry(EntryKind.Login, utc, name, SafeText.Clean(Text(root, "FID"), 32), null);
                case "ShipLocker":
                    bool hasBody = Sections.Any(s => root.TryGetProperty(s.Property, out var a) && a.ValueKind == JsonValueKind.Array);
                    return new Entry(EntryKind.Locker, utc, "", "", hasBody ? line : null);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null; // a line the game was still writing, or not an event at all
        }
    }

    /// <summary>
    /// Whether two logins are the same commander: by Frontier ID when both carry one, since two
    /// commanders can share a name, otherwise by name.
    /// </summary>
    private static bool SameCommander(Entry a, Entry b) =>
        a.Fid.Length > 0 && b.Fid.Length > 0
            ? string.Equals(a.Fid, b.Fid, StringComparison.OrdinalIgnoreCase)
            : string.Equals(a.Commander, b.Commander, StringComparison.OrdinalIgnoreCase);

    // ---- items --------------------------------------------------------------------------------

    /// <summary>
    /// The four sections of a ShipLocker event or file, each merged by name and sorted.
    /// </summary>
    /// <remarks>
    /// The game lists a stack per owner and mission, so one kind can appear several times; those
    /// are added together. Entries whose count is not a whole, non-negative number are skipped
    /// rather than failing the read, the same rule as the Find Carriers inventory.
    /// </remarks>
    private static IReadOnlyList<LockerSection> ParseSections(JsonElement root, IReadOnlyDictionary<string, string> catalog)
    {
        var sections = new List<LockerSection>(Sections.Length);
        foreach (var (property, group) in Sections.OrderBy(s => s.Group))
        {
            // Keyed by the name shown, ignoring case: two symbols that come out as the same name are
            // the same thing to the player, and the first spelling seen is the one kept.
            var merged = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("Count", out var countEl) || countEl.ValueKind != JsonValueKind.Number
                        || !countEl.TryGetInt32(out int count) || count <= 0)
                        continue;
                    string name = DisplayName(Text(item, "Name"), Text(item, "Name_Localised"), catalog);
                    if (name.Length == 0) continue;
                    merged[name] = merged.GetValueOrDefault(name) + count;
                }
            }
            var items = merged
                .Select(kv => new LockerItem(kv.Key, (int)Math.Min(kv.Value, int.MaxValue)))
                .OrderBy(i => i.Name, NameOrder)
                .ToList();
            sections.Add(new LockerSection(group, items));
        }
        return sections;
    }

    /// <summary>The order items are listed in: by name, ignoring case, so "pH Neutraliser" sits
    /// among the P's rather than after every capital letter.</summary>
    public static IComparer<string> NameOrder { get; } = Comparer<string>.Create((a, b) =>
    {
        int c = StringComparer.OrdinalIgnoreCase.Compare(a, b);
        return c != 0 ? c : StringComparer.Ordinal.Compare(a, b);
    });

    /// <summary>
    /// The name to show for one item: the game's own localised name when it sent one, else the
    /// catalog's name for its symbol, else the symbol tidied into words. Never a raw
    /// <c>$..._name;</c> symbol, whichever field it turned up in.
    /// </summary>
    public static string DisplayName(string? symbol, string? localised, IReadOnlyDictionary<string, string> catalog)
    {
        var local = SafeText.Clean(localised, MaxNameLength);
        if (local.Length > 0 && !IsSymbol(local)) return local;

        var bare = Bare(symbol);
        if (bare.Length == 0) bare = Bare(local);
        if (bare.Length == 0) return "";
        if (catalog.TryGetValue(ShipLockerReader.Normalize(bare), out var known)) return known;

        // Tidied, so it at least reads as a name: "$weaponschematic_name;" becomes "Weaponschematic",
        // "amm_grenade_frag" becomes "Amm grenade frag".
        var words = bare.Replace('_', ' ').Trim();
        return SafeText.Clean(char.ToUpperInvariant(words[0]) + words[1..], MaxNameLength);
    }

    private static bool IsSymbol(string text) => text.StartsWith('$') || text.EndsWith(';');

    /// <summary>A symbol without the "$" and "_name;" the game wraps some of them in.</summary>
    private static string Bare(string? symbol)
    {
        var s = SafeText.Clean(symbol, MaxNameLength * 2);
        if (s.StartsWith('$')) s = s[1..];
        if (s.EndsWith(';')) s = s[..^1];
        if (s.EndsWith("_name", StringComparison.OrdinalIgnoreCase)) s = s[..^5];
        return s.Trim(' ', '_');
    }

    /// <summary>Catalog names by normalised key and by normalised name, so a symbol finds its name
    /// whichever of the two it matches.</summary>
    private static Dictionary<string, string> CatalogNames(IEnumerable<Component> catalog)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in catalog)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) continue;
            map.TryAdd(ShipLockerReader.Normalize(c.Key), c.Name);
            map.TryAdd(ShipLockerReader.Normalize(c.Name), c.Name);
        }
        map.Remove("");
        return map;
    }

    /// <summary>Catalog names as <see cref="DisplayName"/> wants them, for callers outside.</summary>
    public static IReadOnlyDictionary<string, string> CatalogNameMap(IEnumerable<Component> catalog) => CatalogNames(catalog);

    // ---- small JSON helpers ---------------------------------------------------------------------

    private static string Text(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static DateTime? Timestamp(JsonElement root) =>
        DateTime.TryParse(Text(root, "timestamp"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
            ? utc
            : null;
}
