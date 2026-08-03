using System.IO;
using System.Text.Json;

namespace FleetView.Services;

/// <summary>
/// Saved window position/size plus each tab's left/right splitter position, read back on the
/// next launch. The three splitter fields are nullable so older save files (from before they
/// existed) still deserialize fine - null just means "use the XAML default width".
/// </summary>
public sealed record WindowBounds(
    double Left, double Top, double Width, double Height, bool Maximized,
    double? FindCarriersSplit = null, double? ModificationsSplit = null, double? ImportSplit = null);

/// <summary>Persists the main window's bounds across launches, alongside the app's other Data/ files.</summary>
public static class WindowStateStore
{
    /// <summary>The window's own XAML minimum. A saved size under this is not a window this app
    /// can present, whatever the file says.</summary>
    private const double MinWidth = 960;
    private const double MinHeight = 480;

    /// <summary>
    /// Absolute ceiling for any saved coordinate or size, well past any real multi-monitor desktop.
    /// Bounds the enormous-but-finite values that <see cref="double"/> permits and JSON accepts.
    /// </summary>
    private const double MaxExtent = 100_000;

    private static string FilePath =>
        Path.Combine(AppContext.BaseDirectory, "Data", "window-state.json");

    public static WindowBounds? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            var bounds = JsonSerializer.Deserialize<WindowBounds>(File.ReadAllText(FilePath));
            if (bounds is null) return null;

            // Parsing succeeding is not the same as the numbers being usable. The try/catch below
            // only ever covered malformed JSON; a syntactically perfect file holding a zero width,
            // a negative height or 1e18 for Top deserialized cleanly and went straight to window
            // setup. MainWindow checks the rectangle against the virtual screen, which catches a
            // window placed off every monitor but not one that is valid and unusable.
            if (!IsUsable(bounds))
            {
                DiagnosticLog.Note("Saved window bounds were out of range; using the defaults.");
                return null;
            }

            return bounds with
            {
                FindCarriersSplit = UsableSplit(bounds.FindCarriersSplit),
                ModificationsSplit = UsableSplit(bounds.ModificationsSplit),
                ImportSplit = UsableSplit(bounds.ImportSplit),
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // missing/corrupt file -> just use the XAML defaults
        }
    }

    private static bool IsUsable(WindowBounds b) =>
        double.IsFinite(b.Left) && Math.Abs(b.Left) <= MaxExtent
        && double.IsFinite(b.Top) && Math.Abs(b.Top) <= MaxExtent
        && b.Width >= MinWidth && b.Width <= MaxExtent
        && b.Height >= MinHeight && b.Height <= MaxExtent;

    /// <summary>A splitter width is dropped rather than rejecting the whole file - the columns
    /// have their own MinWidth and fall back to the XAML default individually.</summary>
    private static double? UsableSplit(double? value) =>
        value is double w && double.IsFinite(w) && w > 0 && w <= MaxExtent ? w : null;

    public static void Save(WindowBounds bounds)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Data"));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(bounds));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still best effort - losing a window position is not worth interrupting a close over.
            // It is recorded now, though: a read-only install location made every save fail and
            // look identical to success, so the window silently never remembered anything.
            DiagnosticLog.Note($"Window state could not be saved ({ex.GetType().Name}).");
        }
    }
}
