using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MainViewModel vm)
                vm.ScrollToRowRequested += OnScrollToRowRequested;
        };
        Closing += (_, _) =>
        {
            SaveWindowBounds();
            (DataContext as MainViewModel)?.SavePendingSearch();
        };
    }

    private void OnScrollToRowRequested(ComponentRow row) => ComponentGrid.ScrollIntoView(row);

    /// <summary>
    /// The text box menu (TextBoxMenu in the XAML) has just opened over a text box: point Cut,
    /// Copy and Paste at that box and have them ask it, there and then, whether they can run.
    /// </summary>
    /// <remarks>
    /// WPF's own text box menu got this for free, because it built new items aimed at the box on
    /// every right-click. These items are made once, and a menu item only asks its command again
    /// when the command itself changes, or when WPF next re-checks every command after some input,
    /// which comes after the menu has already been drawn. So Cut and Copy would open greyed out
    /// with text selected. Setting the command again is the one public way to make an item ask
    /// now, and it happens before the menu is first drawn.
    /// </remarks>
    private void TextBoxMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: IInputElement box } menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.CommandTarget = box;
            var command = item.Command;
            item.Command = null;
            item.Command = command;
        }
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
        // second monitor the window was last on) - fall back to the XAML defaults instead. This
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

        static void RestoreSplit(ColumnDefinition column, double? width)
        {
            if (width is double w && w >= column.MinWidth)
                column.Width = new GridLength(w);
        }
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
            ImportLeftColumn.Width.Value));
    }

    /// <summary>Swaps the maximize/restore button's glyph and tooltip to match WindowState -
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

    /// <summary>The cog opens Settings, a tab with no header of its own; any tab takes it away.</summary>
    private void SettingsCogButton_Click(object sender, RoutedEventArgs e) =>
        MainTabs.SelectedItem = SettingsTab;

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