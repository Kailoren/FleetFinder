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
                FindCarriersSplit = UsableSplit(bounds.FindCarriersSplit, bounds.Width),
                ModificationsSplit = UsableSplit(bounds.ModificationsSplit, bounds.Width),
                ImportSplit = UsableSplit(bounds.ImportSplit, bounds.Width),
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

    /// <summary>Smallest remainder a splitter must leave for the pane on the other side of it.</summary>
    private const double MinRemainingPane = 200;

    /// <summary>
    /// A splitter width is dropped rather than rejecting the whole file - the columns have their
    /// own MinWidth and fall back to the XAML default individually.
    /// </summary>
    /// <remarks>
    /// Bounded against the window it belongs to, not against <see cref="MaxExtent"/>. That constant
    /// is a whole-desktop coordinate ceiling, so a splitter of 90,000 passed as "in range" while
    /// being far wider than any window it could sit in, and the comment claiming the columns'
    /// MinWidth compensated described a control that only works the other way - MinWidth stops a
    /// pane being squeezed to nothing, not the pane beside it being pushed off the edge.
    /// </remarks>
    /// <summary>
    /// Drops any splitter width that is not a finite number, so the three nullable fields cannot
    /// carry a NaN into the serializer after <see cref="IsUsable"/> has cleared the four that
    /// matter. A column that has never been measured reports NaN for its width.
    /// </summary>
    private static WindowBounds Finite(WindowBounds b) => b with
    {
        FindCarriersSplit = FiniteOrNull(b.FindCarriersSplit),
        ModificationsSplit = FiniteOrNull(b.ModificationsSplit),
        ImportSplit = FiniteOrNull(b.ImportSplit),
    };

    private static double? FiniteOrNull(double? value) =>
        value is double v && double.IsFinite(v) ? v : null;

    private static double? UsableSplit(double? value, double windowWidth) =>
        value is double w
        && double.IsFinite(w)
        && w > 0
        && w <= MaxExtent
        && w <= windowWidth - MinRemainingPane
            ? w
            : null;

    /// <summary>
    /// Writes the current bounds, or does nothing if they are not values worth restoring.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The finite check is not decoration. A <see cref="Window"/> that has not been laid out yet
    /// reports NaN for Left, Top, Width and Height, and <c>JsonSerializer</c> refuses to write NaN
    /// or an infinity - it throws <see cref="ArgumentException"/>, which is not in the type list
    /// below, so it escaped from a method documented as best effort, out of the Closing handler
    /// that calls it, and into the dispatcher. A window that failed to lay out is exactly the case
    /// where the app is already in trouble, and adding an error dialog to the way out is no help.
    /// </para>
    /// <para>
    /// Refusing to write is also the correct outcome on its own terms: <see cref="Load"/> rejects
    /// non-finite values anyway, so the alternative is a file written now to be discarded on the
    /// next launch, replacing whatever usable bounds were saved before it.
    /// </para>
    /// </remarks>
    public static void Save(WindowBounds bounds)
    {
        if (!IsUsable(bounds)) return;

        try
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Data"));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Finite(bounds)));
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
