using System.IO;
using System.Text.Json;
using System.Windows.Media;
using FleetView.Services;

namespace FleetView.Theming;

/// <summary>
/// Reads and writes the user's colours, <c>Data\theme.json</c> beside the exe, so a copied
/// install takes its colours with it. Never <c>%LOCALAPPDATA%</c>.
/// </summary>
/// <remarks>
/// The file looks like <c>{ "version": 1, "colours": { "PanelBackground": "#120E08", ... } }</c>.
/// Loading is forgiving in every direction, because nothing about a colour file is worth failing
/// to start over: a key this build does not know (from a newer or older build) is skipped, a role
/// the file leaves out gets its default, and a value that is not a colour is noted in the log and
/// that role gets its default. A missing, unreadable or broken file means all defaults.
/// </remarks>
public static class ThemeStore
{
    public const int CurrentVersion = 1;

    /// <summary>A full theme is a few kilobytes. Anything this size is not one of ours, and it is
    /// refused before it is read rather than after.</summary>
    private const long MaxFileBytes = 64 * 1024;

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "Data", "theme.json");

    /// <summary>Every role's colour: the saved one where the file has a usable value, the default
    /// otherwise.</summary>
    public static Dictionary<string, Color> Load() => Load(FilePath);

    /// <inheritdoc cref="Load()"/>
    public static Dictionary<string, Color> Load(string path)
    {
        var colours = ThemeRoles.Defaults();
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return colours;
            if (file.Length > MaxFileBytes)
            {
                DiagnosticLog.Note($"Theme file is {file.Length:N0} bytes, over the {MaxFileBytes:N0} byte limit; using the default colours.");
                return colours;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("colours", out var map)
                || map.ValueKind != JsonValueKind.Object)
            {
                DiagnosticLog.Note("Theme file has no colours in it; using the default colours.");
                return colours;
            }

            // The key is ours and safe to record; the value is whatever was in the file and is
            // left out. DiagnosticLog holds an identical note back for a while, so a broken value
            // costs one line rather than one per launch.
            return ReadColours(map, key => $"Theme colour for {key} is not a usable colour; using its default.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Note($"Theme file could not be read ({ex.GetType().Name}); using the default colours.");
            // A fresh set, not the half-filled one: a file that broke partway is not trusted for
            // the part that happened to parse first.
            return ThemeRoles.Defaults();
        }
    }

    /// <summary>
    /// Every role's colour from a JSON object of role keys to hex strings, the way theme.json holds
    /// them: unknown keys skipped, missing roles at their default, and a value that is not a usable
    /// colour noted (with the text <paramref name="note"/> makes from the role's key) and left at
    /// the default. The saved-theme library reads its files through this too, so a theme file and
    /// theme.json can never disagree about what counts as a colour.
    /// </summary>
    internal static Dictionary<string, Color> ReadColours(JsonElement map, Func<string, string> note)
    {
        var colours = ThemeRoles.Defaults();
        foreach (var entry in map.EnumerateObject())
        {
            var role = ThemeRoles.Find(entry.Name);
            if (role is null) continue;

            if (entry.Value.ValueKind == JsonValueKind.String
                && TryReadColour(role, entry.Value.GetString(), out var colour))
            {
                colours[role.Key] = colour;
            }
            else
            {
                DiagnosticLog.Note(note(role.Key));
            }
        }
        return colours;
    }

    /// <summary>
    /// The value for one role. Hex in any form <see cref="ColourMaths.TryParseHex(string?, out Color)"/>
    /// takes, then <see cref="TryAccept"/>.
    /// </summary>
    private static bool TryReadColour(ThemeRole role, string? text, out Color colour)
    {
        colour = default;
        return ColourMaths.TryParseHex(text, out var read, out bool hadAlpha)
               && TryAccept(role, read, hadAlpha, out colour);
    }

    /// <summary>
    /// Whether a colour that came from outside the app (a file, a share code) can be this role's,
    /// and what it becomes if so. A role with alpha given a colour with no alpha of its own
    /// (<paramref name="hadAlpha"/> false) keeps the role's default alpha, since the picker never
    /// edits alpha and an opaque overlay would hide the tabs completely. A role without alpha
    /// refuses anything that is not fully opaque.
    /// </summary>
    public static bool TryAccept(ThemeRole role, Color colour, bool hadAlpha, out Color accepted)
    {
        accepted = colour;
        if (role.HasAlpha)
        {
            if (!hadAlpha) accepted.A = role.Default.A;
            return true;
        }
        return colour.A == 255;
    }

    /// <summary>
    /// Writes every role's colour, the defaults included, so the file is a complete palette that
    /// reads the same whatever later builds change their defaults to. Returns false if it could
    /// not be written; the reason is in the log.
    /// </summary>
    public static bool Save(IReadOnlyDictionary<string, Color> colours) => Save(FilePath, colours);

    /// <inheritdoc cref="Save(IReadOnlyDictionary{string, Color})"/>
    public static bool Save(string path, IReadOnlyDictionary<string, Color> colours)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in ThemeRoles.All)
        {
            var colour = colours.TryGetValue(role.Key, out var c) ? c : role.Default;
            map[role.Key] = ColourMaths.ToStorageHex(colour, role.HasAlpha);
        }

        var json = JsonSerializer.Serialize(new ThemeFile(CurrentVersion, map),
            new JsonSerializerOptions { WriteIndented = true });
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Same reasoning as WindowStateStore: a save that always fails (a read-only install
            // folder) looks exactly like one that works unless something says so.
            DiagnosticLog.Note($"Theme could not be saved ({ex.GetType().Name}).");
            return false;
        }
    }

    private sealed record ThemeFile(
        [property: System.Text.Json.Serialization.JsonPropertyName("version")] int Version,
        [property: System.Text.Json.Serialization.JsonPropertyName("colours")] Dictionary<string, string> Colours);
}
