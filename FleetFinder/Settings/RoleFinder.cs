using System.Windows;

namespace FleetView.Settings;

/// <summary>
/// One tagged element of the Settings preview: where it is, in the preview's own coordinates and
/// cut to whatever clips it, and the colour roles it is drawn with.
/// </summary>
public sealed record RoleRegion(Rect Bounds, IReadOnlyList<string> Roles, object? Source = null);

/// <summary>
/// Click to find (C9): which tagged element of the preview a point lands on, and how far to
/// scroll the colour list to show its rows. Plain geometry, no WPF controls, so the self-test can
/// check it on made-up regions as well as on the real preview.
/// </summary>
public static class RoleFinder
{
    /// <summary>
    /// The smallest target a region offers, in preview pixels. A one-pixel line (the dividers, the
    /// menu's strip lines) is widened to this round its middle so it can be hit; the preview is
    /// often shown smaller than its natural size, where a one-pixel line is under a pixel wide.
    /// </summary>
    public const double MinHitSize = 5;

    /// <summary>
    /// The region a click at <paramref name="point"/> picks, or null. Regions come in the order WPF
    /// paints them, parents before children and earlier siblings before later ones, so the last one
    /// under the point is both the one on top and the deepest: the text rather than the panel it
    /// sits on, the dim over a dialog's background rather than the rows under the dim.
    /// </summary>
    public static RoleRegion? At(IReadOnlyList<RoleRegion> regions, Point point)
    {
        for (int i = regions.Count - 1; i >= 0; i--)
            if (HitRect(regions[i].Bounds).Contains(point))
                return regions[i];
        return null;
    }

    /// <summary>The area a region answers clicks in: its bounds, widened to <see cref="MinHitSize"/>
    /// in each direction it is thinner than that.</summary>
    public static Rect HitRect(Rect bounds)
    {
        if (bounds.IsEmpty) return bounds;
        double x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height;
        if (w < MinHitSize) { x -= (MinHitSize - w) / 2; w = MinHitSize; }
        if (h < MinHitSize) { y -= (MinHitSize - h) / 2; h = MinHitSize; }
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// A point where a click picks region <paramref name="index"/>, or null when everything painted
    /// after it covers it completely. Works out exactly what is left of its hit area once every later
    /// region's is taken away, and returns the middle of the biggest piece.
    /// </summary>
    public static Point? ExposedPoint(IReadOnlyList<RoleRegion> regions, int index)
    {
        var pieces = new List<Rect> { HitRect(regions[index].Bounds) };
        for (int j = index + 1; j < regions.Count && pieces.Count > 0; j++)
        {
            var cover = HitRect(regions[j].Bounds);
            var next = new List<Rect>(pieces.Count);
            foreach (var piece in pieces)
                Subtract(piece, cover, next);
            pieces = next;
        }
        if (pieces.Count == 0) return null;
        var best = pieces.MaxBy(p => p.Width * p.Height);
        return new Point(best.X + best.Width / 2, best.Y + best.Height / 2);
    }

    /// <summary>What is left of <paramref name="piece"/> outside <paramref name="cover"/>, as up to
    /// four rectangles. Slivers with no area are dropped: a click on a shared edge goes to the later
    /// region anyway.</summary>
    private static void Subtract(Rect piece, Rect cover, List<Rect> into)
    {
        var overlap = Rect.Intersect(piece, cover);
        if (overlap.IsEmpty || overlap.Width <= 0 || overlap.Height <= 0)
        {
            into.Add(piece);
            return;
        }
        void Add(double left, double top, double right, double bottom)
        {
            if (right - left > 0 && bottom - top > 0) into.Add(new Rect(left, top, right - left, bottom - top));
        }
        Add(piece.Left, piece.Top, piece.Right, overlap.Top);
        Add(piece.Left, overlap.Bottom, piece.Right, piece.Bottom);
        Add(piece.Left, overlap.Top, overlap.Left, overlap.Bottom);
        Add(overlap.Right, overlap.Top, piece.Right, overlap.Bottom);
    }

    /// <summary>
    /// Where to scroll the colour list so the found rows show comfortably. Rows already in view,
    /// with <paramref name="margin"/> to spare at both ends, stay put. Otherwise rows that fit are
    /// centred, and a run of rows too tall to fit starts <paramref name="margin"/> below the top.
    /// </summary>
    /// <param name="offset">The list's current scroll offset.</param>
    /// <param name="viewport">How much of the list shows.</param>
    /// <param name="extent">The whole list's height.</param>
    /// <param name="top">The first found row's top, measured from the top of the whole list.</param>
    /// <param name="bottom">The last found row's bottom, the same way.</param>
    /// <param name="margin">Room to leave between the rows and the edge.</param>
    public static double ScrollTarget(double offset, double viewport, double extent, double top, double bottom, double margin)
    {
        double max = Math.Max(0, extent - viewport);
        margin = Math.Max(0, Math.Min(margin, viewport / 4));
        if (top >= offset + margin && bottom <= offset + viewport - margin) return Math.Clamp(offset, 0, max);
        double target = bottom - top <= viewport - 2 * margin
            ? (top + bottom) / 2 - viewport / 2
            : top - margin;
        return Math.Clamp(target, 0, max);
    }
}
