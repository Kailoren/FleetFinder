using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using FleetView.Services;

namespace FleetView.Theming;

/// <summary>One saved theme: its file id, name, the commander credited, when it was saved, and
/// every role's colour.</summary>
public sealed record SavedTheme(string Id, string Name, string Commander, DateTime Created,
    IReadOnlyDictionary<string, Color> Colours);

/// <summary>
/// The user's saved themes, one JSON file each in <c>Data\themes\</c> beside the exe (so a copied
/// install takes them along, never <c>%LOCALAPPDATA%</c>), with a PNG snapshot of the preview
/// beside each.
/// </summary>
/// <remarks>
/// <para>
/// A file is <c>&lt;id&gt;.json</c>, holding <c>{ "version": 1, "id", "name", "cmdr", "created",
/// "colours": { "&lt;role key&gt;": "#RRGGBB", ... } }</c>, and its snapshot is <c>&lt;id&gt;.png</c>.
/// The id is sixteen random lower-case hex digits made here, and it is the only thing a file name
/// is ever built from. The theme's name is the user's own text, or a stranger's for an imported
/// theme, and putting it in a path would hand that text a say in where files go.
/// </para>
/// <para>
/// Reading is as forgiving as theme.json: a file that is missing, too big, not JSON, has no name
/// or no colours is skipped and noted in the log, and never stops the rest loading. Colours go
/// through the same reader as theme.json, so unknown keys are ignored and missing or unusable ones
/// get their default. Names and commanders go through <see cref="ThemeText"/> on the way in, since
/// a file in this folder may have come from anywhere. A missing snapshot is fine: the page draws a
/// new one.
/// </para>
/// <para>
/// Writes go to a temporary file first and are then moved over the real one, so a rename that
/// fails halfway leaves the old file whole rather than a broken one.
/// </para>
/// </remarks>
public sealed partial class ThemeLibrary
{
    public const int CurrentVersion = 1;

    /// <summary>A theme file is a few kilobytes; anything this size is not one.</summary>
    private const long MaxFileBytes = 64 * 1024;

    /// <summary>A preview snapshot is a few hundred kilobytes at most.</summary>
    private const int MaxSnapshotBytes = 8 * 1024 * 1024;

    /// <summary>More themes than anyone keeps. Past this the oldest-named files are not read, so a
    /// folder someone filled by mistake cannot stall the Settings page.</summary>
    private const int MaxThemes = 500;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary><c>Data\themes</c> beside the exe.</summary>
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "Data", "themes");

    /// <summary>The app's own library.</summary>
    public static ThemeLibrary ForApp() => new(DefaultFolder);

    /// <param name="folder">Where the theme files live. Created on the first save.</param>
    public ThemeLibrary(string folder)
    {
        Folder = Path.GetFullPath(folder);
    }

    public string Folder { get; }

    /// <summary>True for a string this library could have made as an id. Anything else is refused
    /// by every method that takes one, so no id can reach outside the folder.</summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    [GeneratedRegex("^[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    // ---- reading --------------------------------------------------------------------------------

    /// <summary>
    /// Every saved theme that can be read, oldest first (so a new one lands at the end), ties by
    /// id. Broken files are skipped and noted. An empty list when the folder does not exist yet.
    /// </summary>
    public IReadOnlyList<SavedTheme> List()
    {
        var themes = new List<SavedTheme>();
        string[] files;
        try
        {
            if (!Directory.Exists(Folder)) return themes;
            files = Directory.GetFiles(Folder, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Note($"Saved themes folder could not be listed ({ex.GetType().Name}).");
            return themes;
        }

        var ours = new List<string>();
        bool foreign = false;
        foreach (var file in files)
        {
            if (IsValidId(Path.GetFileNameWithoutExtension(file))) ours.Add(file);
            else foreign = true;
        }
        // The name is left out: it is whatever someone called a file they put there.
        if (foreign) DiagnosticLog.Note("Saved themes folder has a file FleetFinder did not make; it was left alone.");

        ours.Sort(StringComparer.Ordinal);
        if (ours.Count > MaxThemes)
        {
            DiagnosticLog.Note($"Saved themes folder has {ours.Count} themes; only {MaxThemes} were read.");
            ours.RemoveRange(MaxThemes, ours.Count - MaxThemes);
        }

        foreach (var file in ours)
            if (Read(file) is { } theme) themes.Add(theme);

        themes.Sort((a, b) => a.Created != b.Created
            ? a.Created.CompareTo(b.Created)
            : string.CompareOrdinal(a.Id, b.Id));
        return themes;
    }

    /// <summary>One theme by id, or null if there is none or it cannot be read.</summary>
    public SavedTheme? Load(string id) => IsValidId(id) ? Read(JsonPath(id)) : null;

    /// <summary>Where a theme's snapshot is (or would be), or null for an id that is not valid.
    /// The file may not exist; then the page renders a new one and saves it with
    /// <see cref="SaveSnapshot"/>.</summary>
    public string? SnapshotPath(string id) => IsValidId(id) ? Path.Combine(Folder, id + ".png") : null;

    private string JsonPath(string id) => Path.Combine(Folder, id + ".json");

    private static SavedTheme? Read(string path)
    {
        var id = Path.GetFileNameWithoutExtension(path);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            if (file.Length > MaxFileBytes)
            {
                DiagnosticLog.Note($"Saved theme {id} is {file.Length:N0} bytes, over the {MaxFileBytes:N0} byte limit; skipped.");
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                DiagnosticLog.Note($"Saved theme {id} is not a theme file; skipped.");
                return null;
            }

            var name = ThemeText.CleanName(StringOrNull(root, "name"));
            if (name.Length == 0)
            {
                DiagnosticLog.Note($"Saved theme {id} has no name; skipped.");
                return null;
            }
            if (!root.TryGetProperty("colours", out var map) || map.ValueKind != JsonValueKind.Object)
            {
                DiagnosticLog.Note($"Saved theme {id} has no colours in it; skipped.");
                return null;
            }

            var colours = ThemeStore.ReadColours(map,
                key => $"Saved theme {id} colour for {key} is not a usable colour; using its default.");

            // The saved date only orders the cards, so a missing or odd one is not worth losing
            // the theme over: the file's own time stands in.
            var created = DateTime.TryParse(StringOrNull(root, "created"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)
                ? DateTime.SpecifyKind(when, DateTimeKind.Utc)
                : file.LastWriteTimeUtc;

            return new SavedTheme(id, name, ThemeText.CleanCommander(StringOrNull(root, "cmdr")), created, colours);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Note($"Saved theme {id} could not be read ({ex.GetType().Name}); skipped.");
            return null;
        }
    }

    private static string? StringOrNull(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ---- writing --------------------------------------------------------------------------------

    /// <summary>
    /// Saves a new theme and, when <paramref name="snapshotPng"/> is given, its snapshot. The name
    /// and commander are cleaned first; roles the map leaves out get their default. Returns the
    /// theme as stored (cleaned name, new id), or null if the name is empty once cleaned or the
    /// file could not be written (the reason is in the log). A snapshot that cannot be written
    /// does not undo the save: the theme is there, and its snapshot can be drawn again.
    /// </summary>
    public SavedTheme? Save(string name, string commander, IReadOnlyDictionary<string, Color> colours,
        byte[]? snapshotPng)
    {
        var cleanName = ThemeText.CleanName(name);
        if (cleanName.Length == 0) return null;

        string id;
        do id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        while (File.Exists(JsonPath(id)));

        // Whole seconds, because that is what the file keeps; the theme handed back is then the
        // same one Load would give.
        var now = DateTime.UtcNow;
        var created = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        var theme = new SavedTheme(id, cleanName, ThemeText.CleanCommander(commander), created, Complete(colours));
        if (!Write(theme)) return null;
        if (snapshotPng != null) SaveSnapshot(id, snapshotPng);
        return theme;
    }

    /// <summary>Gives a saved theme a new name. False if there is no such theme, the name is empty
    /// once cleaned, or the file could not be written.</summary>
    public bool Rename(string id, string newName)
    {
        var cleanName = ThemeText.CleanName(newName);
        if (cleanName.Length == 0 || Load(id) is not { } theme) return false;
        return Write(theme with { Name = cleanName });
    }

    /// <summary>Removes a theme and its snapshot. False if there was no such theme or it could not
    /// be removed.</summary>
    public bool Delete(string id)
    {
        if (!IsValidId(id)) return false;
        try
        {
            var json = JsonPath(id);
            if (!File.Exists(json)) return false;
            File.Delete(json);
            File.Delete(Path.Combine(Folder, id + ".png"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Note($"Saved theme {id} could not be deleted ({ex.GetType().Name}).");
            return false;
        }
    }

    /// <summary>
    /// Writes (or replaces) a theme's snapshot. The bytes must be a PNG of a sensible size, and the
    /// theme must exist, so no picture is left without its theme. False otherwise, or if it could
    /// not be written.
    /// </summary>
    public bool SaveSnapshot(string id, byte[] png)
    {
        if (!IsValidId(id) || !File.Exists(JsonPath(id))) return false;
        if (png is null || png.Length > MaxSnapshotBytes || !png.AsSpan().StartsWith(PngSignature))
        {
            DiagnosticLog.Note($"Snapshot for saved theme {id} is not a usable PNG; not saved.");
            return false;
        }
        return WriteFile(Path.Combine(Folder, id + ".png"), png, id);
    }

    private bool Write(SavedTheme theme)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in ThemeRoles.All)
            map[role.Key] = ColourMaths.ToStorageHex(theme.Colours[role.Key], role.HasAlpha);

        var file = new ThemeFile(CurrentVersion, theme.Id, theme.Name, theme.Commander,
            theme.Created.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), map);
        return WriteFile(JsonPath(theme.Id), JsonSerializer.SerializeToUtf8Bytes(file, WriteOptions), theme.Id);
    }

    private bool WriteFile(string path, byte[] bytes, string id)
    {
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Note($"Saved theme {id} could not be written ({ex.GetType().Name}).");
            try { File.Delete(temp); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    /// <summary>Every role, with the default where the map has none, and opaque where the role has
    /// no alpha: what the file will hold once written, so the record and the file agree.</summary>
    private static Dictionary<string, Color> Complete(IReadOnlyDictionary<string, Color> colours)
    {
        var complete = ThemeRoles.Defaults();
        foreach (var role in ThemeRoles.All)
        {
            if (!colours.TryGetValue(role.Key, out var c)) continue;
            if (!role.HasAlpha) c.A = 255;
            complete[role.Key] = c;
        }
        return complete;
    }

    private sealed record ThemeFile(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("cmdr")] string Commander,
        [property: JsonPropertyName("created")] string Created,
        [property: JsonPropertyName("colours")] Dictionary<string, string> Colours);
}
