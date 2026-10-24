using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FleetView.Settings;

/// <summary>
/// Which colour roles an element of the Settings preview is drawn with, so that clicking it can
/// light up its rows in the colour list (C9). ThemePreview.xaml sets it as
/// <c>settings:PreviewTag.Roles="PanelBackground PanelBorder"</c>, on the element itself or in a
/// style, at the finest level there is: a text block carries its text colour, and the panel round
/// it carries the panel's background and border.
/// </summary>
/// <remarks>
/// A few parts of the preview are not written in its XAML at all. WPF builds the grids' column
/// headers and rows, the check boxes' boxes, the scrollbar's thumb and the progress bar's fill from
/// the window's own styles, and the preview cannot style them without replacing those styles. So
/// those are tagged here by kind instead (<see cref="PartRoles"/>), going by the same state the
/// window's styles go by: a ticked box is drawn in Ticked checkbox, an odd row in Grid alternate
/// row. The self-test checks every tag is a real role and every role can be found by a click.
/// </remarks>
public static class PreviewTag
{
    public static readonly DependencyProperty RolesProperty = DependencyProperty.RegisterAttached(
        "Roles", typeof(string), typeof(PreviewTag), new PropertyMetadata(null));

    public static string? GetRoles(DependencyObject element) => (string?)element.GetValue(RolesProperty);

    public static void SetRoles(DependencyObject element, string? value) => element.SetValue(RolesProperty, value);

    /// <summary>The role keys in a tag, in the order written.</summary>
    public static IReadOnlyList<string> Split(string? roles) =>
        roles is null ? [] : roles.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The roles an element is drawn with: its tag, or for a part WPF builds, its kind's.</summary>
    public static IReadOnlyList<string> RolesOf(DependencyObject element) =>
        Split(GetRoles(element) ?? PartRoles(element));

    /// <summary>
    /// The parts the preview's XAML cannot reach. Each matches what the window's styles draw that
    /// part with (MainWindow.xaml: the DataGrid and DataGridColumnHeader styles, the CheckBox
    /// template, RoundedScrollBarThumb) or, for the progress bar, WPF's own template, which draws
    /// its fill in the bar's Foreground.
    /// </summary>
    private static string? PartRoles(DependencyObject element) => element switch
    {
        DataGridColumnHeader => "GridHeaderBackground GridHeaderText GridHeaderLines",
        DataGridRow row => (row.IsSelected ? "RowOutline " : "")
                           + (ItemsControl.GetAlternationIndex(row) % 2 == 1 ? "GridAlternateRow" : "GridRow")
                           + " GridLines",
        CheckBox { IsChecked: true } => "CheckboxTicked",
        CheckBox => "CheckboxBackground CheckboxBorder",
        Thumb { TemplatedParent: ScrollBar } => "ScrollbarThumb",
        FrameworkElement { Name: "PART_Indicator", TemplatedParent: ProgressBar } => "ProgressFill",
        _ => null,
    };

    /// <summary>
    /// Every tagged element under <paramref name="root"/> that shows, in the order WPF paints them,
    /// with its bounds in <paramref name="root"/>'s coordinates, cut to whatever clips it (a grid's
    /// rows scrolled half out of sight only count where they show).
    /// </summary>
    public static List<RoleRegion> Regions(Visual root)
    {
        var found = new List<RoleRegion>();
        Walk(root, root, null, found);
        return found;
    }

    private static void Walk(Visual root, DependencyObject node, Rect? clip, List<RoleRegion> found)
    {
        // Visibility rather than IsVisible: the self-test lays the page out without showing it,
        // and IsVisible is false for everything in a window that is not on screen.
        if (node is UIElement { Visibility: not Visibility.Visible } or UIElement { Opacity: <= 0 }) return;

        if (node is Visual visual)
        {
            if (VisualTreeHelper.GetClip(visual) is { } geometry && !ReferenceEquals(visual, root))
            {
                var own = visual.TransformToAncestor(root).TransformBounds(geometry.Bounds);
                clip = clip is { } outer ? Rect.Intersect(outer, own) : own;
            }

            if (node is FrameworkElement element && RolesOf(element) is { Count: > 0 } roles)
            {
                var bounds = ReferenceEquals(visual, root)
                    ? new Rect(element.RenderSize)
                    : visual.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                if (clip is { } c) bounds = Rect.Intersect(bounds, c);
                if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
                    found.Add(new RoleRegion(bounds, roles, element));
            }
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            Walk(root, VisualTreeHelper.GetChild(node, i), clip, found);
    }
}
