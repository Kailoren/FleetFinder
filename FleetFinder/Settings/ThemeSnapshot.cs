using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FleetView.Settings;

/// <summary>
/// The small picture of the preview a theme card shows, drawn as PNG bytes.
/// </summary>
/// <remarks>
/// The preview is drawn at its own full size first and then scaled down in one smooth step.
/// Drawing it straight at card size would put each hairline (grid lines, borders) on or off a
/// pixel by chance, so some would vanish; averaging the full picture keeps every line, faintly.
/// </remarks>
public static class ThemeSnapshot
{
    /// <summary>The picture's size on a card, in the page's own units. The preview is close to
    /// square, so this is too.</summary>
    public const double CardWidth = 160;
    public const double CardHeight = 160;

    /// <summary>The bigger picture the Import code box shows, as wide as the box's content, so a
    /// shared theme can be judged properly before it is added. The card it becomes still gets
    /// its own card-sized picture.</summary>
    public const double ImportWidth = 400;
    public const double ImportHeight = 400;

    /// <summary>
    /// The element as it is laid out now, scaled to fit a picture of <paramref name="width"/> by
    /// <paramref name="height"/> (the card's, unless given) and encoded as PNG.
    /// <paramref name="dpiScale"/> is the screen's scale (1.25 at 125%), so the picture has as many
    /// pixels as it is shown at and stays sharp. Null if the element has not been laid out.
    /// </summary>
    public static byte[]? Png(FrameworkElement source, double dpiScale = 1, double width = CardWidth, double height = CardHeight)
    {
        int fullWidth = (int)Math.Ceiling(source.ActualWidth), fullHeight = (int)Math.Ceiling(source.ActualHeight);
        if (fullWidth <= 0 || fullHeight <= 0) return null;

        // The element's own box, not wherever it sits in its parent.
        var brush = new VisualBrush(source)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, fullWidth, fullHeight),
        };
        var whole = new DrawingVisual();
        using (var dc = whole.RenderOpen())
            dc.DrawRectangle(brush, null, new Rect(0, 0, fullWidth, fullHeight));
        var full = new RenderTargetBitmap(fullWidth, fullHeight, 96, 96, PixelFormats.Pbgra32);
        full.Render(whole);

        double scale = Math.Min(width * dpiScale / fullWidth, height * dpiScale / fullHeight);
        int pixelWidth = Math.Max(1, (int)Math.Round(fullWidth * scale)), pixelHeight = Math.Max(1, (int)Math.Round(fullHeight * scale));
        var small = Shrink(full, pixelWidth, pixelHeight);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(small));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Scales a picture down by averaging every source pixel into the target pixel it falls in,
    /// weighted by how much of it falls there. Done by hand rather than left to WPF's scaler, whose
    /// result in an off-screen render depends on the rendering path.
    /// </summary>
    private static BitmapSource Shrink(BitmapSource source, int width, int height)
    {
        int sw = source.PixelWidth, sh = source.PixelHeight, stride = sw * 4;
        var src = new byte[stride * sh];
        source.CopyPixels(src, stride, 0);

        var sums = new double[width * height * 4];
        var weights = new double[width * height];
        double fx = (double)width / sw, fy = (double)height / sh;
        for (int y = 0; y < sh; y++)
        {
            // The source row covers [y, y+1), which maps to [y0, y1) in the target.
            double y0 = y * fy, y1 = (y + 1) * fy;
            int ty0 = (int)y0, ty1 = Math.Min(height - 1, (int)Math.Ceiling(y1) - 1);
            for (int x = 0; x < sw; x++)
            {
                double x0 = x * fx, x1 = (x + 1) * fx;
                int tx0 = (int)x0, tx1 = Math.Min(width - 1, (int)Math.Ceiling(x1) - 1);
                int si = y * stride + x * 4;
                for (int ty = ty0; ty <= ty1; ty++)
                {
                    double wy = Math.Min(y1, ty + 1) - Math.Max(y0, ty);
                    if (wy <= 0) continue;
                    for (int tx = tx0; tx <= tx1; tx++)
                    {
                        double w = wy * (Math.Min(x1, tx + 1) - Math.Max(x0, tx));
                        if (w <= 0) continue;
                        int t = ty * width + tx;
                        weights[t] += w;
                        sums[t * 4] += src[si] * w;
                        sums[t * 4 + 1] += src[si + 1] * w;
                        sums[t * 4 + 2] += src[si + 2] * w;
                        sums[t * 4 + 3] += src[si + 3] * w;
                    }
                }
            }
        }

        var dst = new byte[width * height * 4];
        for (int t = 0; t < weights.Length; t++)
        {
            double w = weights[t] > 0 ? weights[t] : 1;
            for (int c = 0; c < 4; c++)
                dst[t * 4 + c] = (byte)Math.Clamp(Math.Round(sums[t * 4 + c] / w), 0, 255);
        }
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, dst, width * 4);
        result.Freeze();
        return result;
    }
}
