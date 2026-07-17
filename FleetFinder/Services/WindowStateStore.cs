using System.IO;
using System.Text.Json;

namespace FleetView.Services;

/// <summary>
/// Saved window position/size plus each tab's left/right splitter position and the tabs' own
/// left-to-right order, read back on the next launch. All of these are nullable so older save
/// files (from before they existed) still deserialize fine - null just means "use the XAML
/// default". TabOrder holds each TabItem's Header text in display order; any tab it doesn't
/// mention (a save from before that tab existed, or one renamed since) is appended at the end
/// in its original XAML position rather than dropped.
/// </summary>
public sealed record WindowBounds(
    double Left, double Top, double Width, double Height, bool Maximized,
    double? FindCarriersSplit = null, double? ModificationsSplit = null, double? ImportSplit = null,
    string[]? TabOrder = null);

/// <summary>Persists the main window's bounds across launches, alongside the app's other Data/ files.</summary>
public static class WindowStateStore
{
    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "window-state.json");

    public static WindowBounds? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<WindowBounds>(File.ReadAllText(FilePath));
        }
        catch
        {
            return null; // missing/corrupt file -> just use the XAML defaults
        }
    }

    public static void Save(WindowBounds bounds)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Data"));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(bounds));
        }
        catch { /* best effort */ }
    }
}
