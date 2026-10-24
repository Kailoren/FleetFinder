using System.Windows;
using System.Windows.Controls;

namespace FleetView.Controls;

/// <summary>
/// Lays cards out in equal-width columns, as many as fit at <see cref="MinColumnWidth"/>, row by
/// row. Each row is as tall as its tallest card and every card in it is stretched to that height,
/// so a row's edges line up, while a short row stays short. A UniformGrid would make every row as
/// tall as the tallest card anywhere; a WrapPanel would leave each card its own height.
/// </summary>
public sealed class CardRowsPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(CardRowsPanel),
        new FrameworkPropertyMetadata(380.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>The narrowest a column may be; the panel uses as many columns as fit.</summary>
    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    /// <summary>How many columns of at least <paramref name="minColumnWidth"/> fit in
    /// <paramref name="width"/>: always at least one, and one when the width is unbounded.</summary>
    public static int ColumnsFor(double width, double minColumnWidth)
    {
        if (double.IsInfinity(width) || double.IsNaN(width) || minColumnWidth <= 0) return 1;
        return Math.Max(1, (int)Math.Floor(width / minColumnWidth));
    }

    /// <summary>The columns used by the last layout pass, for the self-test and the harness.</summary>
    public int Columns { get; private set; } = 1;

    private readonly List<double> _rowHeights = new();

    protected override Size MeasureOverride(Size available)
    {
        var children = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        int columns = ColumnsFor(available.Width, MinColumnWidth);
        double columnWidth = double.IsInfinity(available.Width) ? double.PositiveInfinity : available.Width / columns;
        _rowHeights.Clear();
        double widest = 0;
        for (int start = 0; start < children.Count; start += columns)
        {
            double row = 0;
            foreach (var child in children.Skip(start).Take(columns))
            {
                child.Measure(new Size(columnWidth, double.PositiveInfinity));
                row = Math.Max(row, child.DesiredSize.Height);
                widest = Math.Max(widest, child.DesiredSize.Width);
            }
            _rowHeights.Add(row);
        }
        Columns = columns;
        double width = double.IsInfinity(available.Width) ? widest : available.Width;
        return new Size(width, _rowHeights.Sum());
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        int columns = ColumnsFor(final.Width, MinColumnWidth);
        double columnWidth = final.Width / columns;
        double y = 0;
        for (int i = 0, row = 0; i < children.Count; i += columns, row++)
        {
            double height = row < _rowHeights.Count ? _rowHeights[row] : 0;
            var inRow = children.Skip(i).Take(columns).ToList();
            if (row >= _rowHeights.Count) height = inRow.Max(c => c.DesiredSize.Height);
            for (int k = 0; k < inRow.Count; k++)
                inRow[k].Arrange(new Rect(k * columnWidth, y, columnWidth, height));
            y += height;
        }
        Columns = columns;
        return final;
    }
}
