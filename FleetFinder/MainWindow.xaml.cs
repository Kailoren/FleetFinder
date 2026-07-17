using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using FleetView.Services;
using FleetView.ViewModels;

namespace FleetView;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateMaximizeRestoreGlyph();
        UpdateMaximizeRestoreGlyph();

        RestoreWindowBounds();
        Closing += (_, _) =>
        {
            SaveWindowBounds();
            (DataContext as MainViewModel)?.SavePendingSearch();
        };
    }

    /// <summary>
    /// Applies the last-saved position/size (if any and still on-screen) and each tab's
    /// left/right splitter position (independent of whether the window position was valid).
    /// </summary>
    private void RestoreWindowBounds()
    {
        var saved = WindowStateStore.Load();
        if (saved == null) return;

        // Guard against restoring off whatever's currently connected (e.g. a since-disconnected
        // second monitor the window was last on) — fall back to the XAML defaults instead. This
        // only gates position/size; splitter positions below are independent of it.
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var savedRect = new Rect(saved.Left, saved.Top, saved.Width, saved.Height);
        if (virtualScreen.IntersectsWith(savedRect))
        {
            Left = saved.Left;
            Top = saved.Top;
            Width = saved.Width;
            Height = saved.Height;
            if (saved.Maximized)
                WindowState = WindowState.Maximized;
        }

        RestoreSplit(FindCarriersLeftColumn, saved.FindCarriersSplit);
        RestoreSplit(ModificationsLeftColumn, saved.ModificationsSplit);
        RestoreSplit(ImportLeftColumn, saved.ImportSplit);
        RestoreTabOrder(saved.TabOrder);

        static void RestoreSplit(ColumnDefinition column, double? width)
        {
            if (width is double w && w >= column.MinWidth)
                column.Width = new GridLength(w);
        }
    }

    /// <summary>Reorders MainTabs.Items to match a saved Header-text sequence. Any tab the saved
    /// order doesn't mention (new since that save, or renamed) keeps its original relative
    /// position at the end rather than being dropped.</summary>
    private void RestoreTabOrder(string[]? order)
    {
        if (order == null || order.Length == 0) return;

        var remaining = MainTabs.Items.Cast<TabItem>().ToList();
        var reordered = new List<TabItem>();
        foreach (var header in order)
        {
            var match = remaining.FirstOrDefault(t => (string)t.Header == header);
            if (match == null) continue;
            reordered.Add(match);
            remaining.Remove(match);
        }
        reordered.AddRange(remaining);

        MainTabs.Items.Clear();
        foreach (var tab in reordered) MainTabs.Items.Add(tab);
    }

    /// <summary>
    /// Saves the window's *restored* (non-maximized) bounds plus whether it was maximized (so
    /// reopening maximized still remembers the size/position Restore would return to), plus each
    /// tab's current left/right splitter position.
    /// </summary>
    /// <remarks>
    /// RestoreBounds is only trustworthy while actually <see cref="WindowState.Maximized"/> -
    /// that's when it correctly holds the pre-maximize size to return to. Confirmed by direct
    /// testing across a 4-monitor setup: a Windows Snap (dragging/Win+Arrow to half a screen)
    /// keeps WindowState at Normal throughout, but on some monitors RestoreBounds still goes
    /// stale and keeps reporting whatever the window's size was *before* the snap, not the
    /// snapped size - saving that stale value is what caused a snapped window to reopen shorter
    /// than the full screen it was snapped to. The window's own live bounds were correct in
    /// every case tested, snap or not, so they're what's used outside of true Maximized.
    /// </remarks>
    private void SaveWindowBounds()
    {
        bool maximized = WindowState == WindowState.Maximized;
        var r = maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        if (r.IsEmpty) r = new Rect(Left, Top, Width, Height);
        WindowStateStore.Save(new WindowBounds(r.Left, r.Top, r.Width, r.Height,
            maximized,
            FindCarriersLeftColumn.Width.Value,
            ModificationsLeftColumn.Width.Value,
            ImportLeftColumn.Width.Value,
            MainTabs.Items.Cast<TabItem>().Select(t => (string)t.Header).ToArray()));
    }

    // ---- Tab drag-reordering ---------------------------------------------------------------
    //
    // TabControl has no built-in support for this. The approach: on a left-button-down over a
    // tab header, remember which TabItem it was (via VisualTreeHelper ancestry from whatever
    // element was actually hit); if the mouse then moves past the system drag threshold while
    // still down, kick off a real WPF drag/drop carrying that TabItem as the payload; on Drop,
    // find whichever TabItem is under the drop point the same way and move the dragged one to
    // that position in MainTabs.Items directly (these are XAML-declared children, not bound via
    // ItemsSource, so direct Items manipulation is safe here). Using "Preview" (tunneling) events
    // rather than the bubbling ones means this never has to fight the TabItem's own built-in
    // click-to-select handling - both just run independently off the same physical click.

    private Point _tabDragStartPoint;
    private TabItem? _tabDragCandidate;

    private void MainTabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStartPoint = e.GetPosition(null);
        _tabDragCandidate = FindAncestorTabItem(e.OriginalSource as DependencyObject);
    }

    private void MainTabs_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _tabDragCandidate == null) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _tabDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _tabDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var dragged = _tabDragCandidate;
        _tabDragCandidate = null; // one DoDragDrop per press - avoid re-entering while it's active
        DragDrop.DoDragDrop(dragged, dragged, DragDropEffects.Move);
    }

    private void MainTabs_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TabItem)) is not TabItem dragged) return;
        var target = FindAncestorTabItem(e.OriginalSource as DependencyObject);
        if (target == null || ReferenceEquals(target, dragged)) return;

        int targetIndex = MainTabs.Items.IndexOf(target);
        if (targetIndex < 0) return;

        MainTabs.Items.Remove(dragged);
        MainTabs.Items.Insert(targetIndex, dragged);
        dragged.IsSelected = true;
        SaveWindowBounds();
    }

    private static TabItem? FindAncestorTabItem(DependencyObject? source)
    {
        while (source != null && source is not TabItem)
            source = VisualTreeHelper.GetParent(source);
        return source as TabItem;
    }

    /// <summary>Swaps the maximize/restore button's glyph and tooltip to match WindowState —
    /// there's one button (double-click-to-maximize convention), not two.</summary>
    private void UpdateMaximizeRestoreGlyph()
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        string label = maximized ? "Restore" : "Maximize";
        MaximizeRestoreButton.ToolTip = label;
        AutomationProperties.SetName(MaximizeRestoreButton, label);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.CloseWindow(this);
}