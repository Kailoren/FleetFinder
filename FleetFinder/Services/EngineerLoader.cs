using System.IO;
using System.Text.Json;
using FleetView.Models;

namespace FleetView.Services;

/// <summary>
/// What <see cref="EngineerLoader"/> found: the engineers it could use, and a sentence about
/// anything it could not (empty when the file was fine).
/// </summary>
/// <param name="ModificationNames">For a module the file names the game's way, the name the
/// modification list uses ("Improved Hip Fire Accuracy" is "Higher Accuracy" there), keyed
/// ignoring case. Empty when the file has none.</param>
public sealed record EngineerCatalog(IReadOnlyList<Engineer> Engineers, string Problem,
    IReadOnlyDictionary<string, string>? ModificationNames = null)
{
    public static EngineerCatalog Empty(string problem) => new([], problem);

    /// <summary>The name the modification list uses for a module: the file's own mapping, or the
    /// module's name as it is.</summary>
    public string ModificationNameFor(string module) =>
        ModificationNames != null && ModificationNames.TryGetValue(module, out var name) ? name : module;
}

/// <summary>Loads the on-foot engineers from Data/engineers.json.</summary>
/// <remarks>
/// <para>
/// Unlike the component catalog, a bad engineers file must not stop the app: it feeds one tab, and
/// everything else works without it. So this never throws. A file that is missing or is not the
/// right shape gives no engineers and a <see cref="EngineerCatalog.Problem"/> the tab can show; an
/// entry that is wrong on its own is left out and the rest still load. Either way it is written to
/// the diagnostic log once per load.
/// </para>
/// <para>
/// Text is cleaned and cut to a sensible length on the way in, the same as anything read from a
/// journal, since the file sits beside the exe where anyone can edit it.
/// </para>
/// </remarks>
public static class EngineerLoader
{
    /// <summary>The real file is about 7 KB. Anything far past that is not this file.</summary>
    private const long MaxFileBytes = 1024 * 1024;

    private const int MaxNameLength = 64;
    private const int MaxTextLength = 400;
    private const int MaxModules = 40;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class FileShape
    {
        public List<Engineer?>? Engineers { get; set; }

        public Dictionary<string, string?>? ModificationNames { get; set; }
    }

    /// <summary>The file beside the exe.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Data", "engineers.json");

    /// <param name="path">Which file; the one beside the exe when null. The self-test passes its own.</param>
    public static EngineerCatalog Load(string? path = null)
    {
        path ??= DefaultPath;
        var result = LoadCore(path);
        if (result.Problem.Length > 0)
            DiagnosticLog.Note($"Engineer data: {result.Problem} ({Path.GetFileName(path)})");
        return result;
    }

    private static EngineerCatalog LoadCore(string path)
    {
        FileShape? shape;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return EngineerCatalog.Empty("engineers.json is missing from the Data folder.");
            if (info.Length > MaxFileBytes)
                return EngineerCatalog.Empty("engineers.json is far bigger than it should be, so it was not read.");
            shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            return EngineerCatalog.Empty("engineers.json is damaged and could not be read.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            return EngineerCatalog.Empty($"engineers.json could not be opened ({ex.GetType().Name}).");
        }

        if (shape?.Engineers is not { Count: > 0 } raw)
            return EngineerCatalog.Empty("engineers.json has no engineers in it.");

        var kept = new List<Engineer>(raw.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int skipped = 0;
        foreach (var entry in raw)
        {
            var clean = Clean(entry);
            if (clean == null || !seen.Add(ShipLockerReader.Normalize(clean.Name)))
            {
                skipped++;
                continue;
            }
            kept.Add(clean);
        }

        // A referral to someone the file does not have leads nowhere, so it is dropped rather than
        // shown as a name the tab cannot open.
        var names = new HashSet<string>(kept.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        int unknownReferrals = 0;
        foreach (var e in kept)
            unknownReferrals += e.RefersTo.RemoveAll(n => !names.Contains(n));

        if (kept.Count == 0)
            return EngineerCatalog.Empty("engineers.json has no usable engineers in it.");

        var problems = new List<string>();
        if (skipped > 0)
            problems.Add($"{skipped} {(skipped == 1 ? "entry was" : "entries were")} left out as incomplete or repeated.");
        if (unknownReferrals > 0)
            problems.Add($"{unknownReferrals} referral{(unknownReferrals == 1 ? "" : "s")} to an engineer not in the file {(unknownReferrals == 1 ? "was" : "were")} dropped.");
        return new EngineerCatalog(kept, string.Join(" ", problems), ModificationNames(shape.ModificationNames));
    }

    /// <summary>The module name map cleaned like everything else; pairs with a blank side are
    /// left out.</summary>
    private static Dictionary<string, string> ModificationNames(Dictionary<string, string?>? raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in raw ?? [])
        {
            var key = SafeText.Clean(from, MaxNameLength).Trim();
            var value = SafeText.Clean(to ?? "", MaxNameLength).Trim();
            if (key.Length > 0 && value.Length > 0 && map.Count < MaxModules * 2) map[key] = value;
        }
        return map;
    }

    /// <summary>One entry made safe, or null when it lacks what a card needs: a name, a system and
    /// a region it can be filtered by.</summary>
    private static Engineer? Clean(Engineer? e)
    {
        if (e == null) return null;
        string name = SafeText.Clean(e.Name, MaxNameLength).Trim();
        string system = SafeText.Clean(e.System, MaxNameLength).Trim();
        string region = SafeText.Clean(e.Region, 16).Trim();
        if (name.Length == 0 || system.Length == 0) return null;
        if (string.Equals(region, "Bubble", StringComparison.OrdinalIgnoreCase)) region = "Bubble";
        else if (string.Equals(region, "Colonia", StringComparison.OrdinalIgnoreCase)) region = "Colonia";
        else return null;

        return new Engineer
        {
            Name = name,
            Region = region,
            System = system,
            Settlement = SafeText.Clean(e.Settlement, MaxNameLength).Trim(),
            Find = SafeText.Clean(e.Find, MaxTextLength).Trim(),
            Invite = SafeText.Clean(e.Invite, MaxTextLength).Trim(),
            Referral = SafeText.Clean(e.Referral, MaxTextLength).Trim(),
            RefersTo = Names(e.RefersTo),
            SuitModules = Names(e.SuitModules),
            WeaponModules = Names(e.WeaponModules),
        };
    }

    /// <summary>A list of names cleaned, without blanks or repeats, in the file's order.</summary>
    private static List<string> Names(List<string>? raw)
    {
        var list = new List<string>();
        foreach (var n in raw ?? [])
        {
            var clean = SafeText.Clean(n, MaxNameLength).Trim();
            if (clean.Length > 0 && !list.Contains(clean, StringComparer.OrdinalIgnoreCase)) list.Add(clean);
            if (list.Count == MaxModules) break;
        }
        return list;
    }
}
