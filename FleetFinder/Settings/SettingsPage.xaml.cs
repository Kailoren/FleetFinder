using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FleetView.Controls;
using FleetView.Services;
using FleetView.Theming;

namespace FleetView.Settings;

/// <summary>
/// The Settings page: the colour list, the live preview, and Save, Revert and Reset to defaults.
/// All the decisions about colours are in <see cref="ThemeDraft"/>; this class wires it to the
/// list, the preview and the picker. Copy and paste over the list (<see cref="HoverClipboard"/>)
/// and click to find in the preview (<see cref="RoleFinder"/>, <see cref="PreviewTag"/>) keep
/// their rules in those classes too.
/// </summary>
public partial class SettingsPage : UserControl
{
    /// <summary>Room to leave above and below rows the list scrolls to, so they do not end up
    /// jammed against its edge.</summary>
    private const double ComfortMargin = 60;

    private readonly Dictionary<string, ColourRow> _rows;
    private readonly HoverClipboard _hoverClipboard;
    private ColourRow? _editing;
    private Button? _editingBox;
    private bool _saveFailed;
    private ColourRow? _hovered;
    private Window? _host;
    private List<RoleRegion>? _regions;
    private int _noteCount;

    public SettingsPage()
    {
        InitializeComponent();

        Draft = ThemeDraft.ForApp();
        _hoverClipboard = new HoverClipboard(Draft, SystemClipboardText.Instance);
        Groups = ThemeRoles.Groups
            .Select(g => new ColourGroup(g, ThemeRoles.All.Where(r => r.Group == g)
                .Select(r => new ColourRow(r, Draft[r.Key])).ToList()))
            .ToList();
        _rows = Groups.SelectMany(g => g.Rows).ToDictionary(r => r.Key, StringComparer.Ordinal);
        ColourList.ItemsSource = Groups;
        Preview.ShowColours(Draft.Colours);

        Draft.ColourChanged += OnDraftColourChanged;
        Draft.PropertyChanged += (_, _) => UpdateFoot();
        Picker.SelectedColorChanged += Picker_SelectedColorChanged;
        // Leaving the page with the picker open (another tab, the cog again) keeps what was
        // picked, as a click anywhere else would.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) return;
            ClosePicker(keep: true);
            _hovered = null;
            OutlineInPreview(null);
        };
        SizeChanged += (_, _) => PlacePicker();
        // Ctrl+C and Ctrl+V over the list are caught at the window, because the keyboard is
        // usually somewhere else (the cog that opened Settings, a colour box), and only while
        // this page is in the window: the tab control takes it out when another tab is chosen.
        Loaded += (_, _) =>
        {
            if (_host != null) _host.PreviewKeyDown -= Host_PreviewKeyDown;
            _host = Window.GetWindow(this);
            if (_host != null) _host.PreviewKeyDown += Host_PreviewKeyDown;
        };
        Unloaded += (_, _) =>
        {
            if (_host != null) _host.PreviewKeyDown -= Host_PreviewKeyDown;
            _host = null;
        };
        UpdateFoot();
    }

    /// <summary>The colours being edited. Lives as long as the page, so it outlasts switching
    /// tabs and goes when the app closes.</summary>
    public ThemeDraft Draft { get; }

    /// <summary>The list as shown: one group per heading, in the order of <see cref="ThemeRoles.All"/>.</summary>
    public IReadOnlyList<ColourGroup> Groups { get; }

    public ThemePreview PreviewControl => Preview;
    public ColourPicker PickerControl => Picker;
    public bool IsPickerOpen => PickerLayer.Visibility == Visibility.Visible;

    /// <summary>Something is open over the page, so the keys over the list are left alone. Only
    /// the picker so far; anything else that opens over the page belongs here too.</summary>
    public bool IsOverlayOpen => IsPickerOpen;

    /// <summary>The clipboard hover copy and paste use: the real one, unless a test hands in its own.</summary>
    public IClipboardText ClipboardText
    {
        get => _hoverClipboard.Clipboard;
        set => _hoverClipboard.Clipboard = value;
    }

    /// <summary>The row under the mouse, or null.</summary>
    public ColourRow? HoveredRow => _hovered;

    /// <summary>The note a hover copy or paste left, while it shows, or null.</summary>
    public string? NoteShowing => CopyNote.Visibility == Visibility.Visible ? CopyNoteText.Text : null;

    private void OnDraftColourChanged(string? key)
    {
        if (key is null)
        {
            Preview.ShowColours(Draft.Colours);
            foreach (var row in _rows.Values) row.Colour = Draft[row.Key];
        }
        else
        {
            Preview.ShowColour(key, Draft[key]);
            _rows[key].Colour = Draft[key];
        }
    }

    // ---- the foot -------------------------------------------------------------------------------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _saveFailed = !Draft.Save();
        UpdateFoot();
    }

    private void Revert_Click(object sender, RoutedEventArgs e) => Draft.Revert();

    private void Reset_Click(object sender, RoutedEventArgs e) => Draft.ResetToDefaults();

    /// <summary>
    /// Save only lights up when there is something to save. Revert and Reset stay clickable, as
    /// their own colours would not show them greyed out, and clicking either with nothing to undo
    /// does nothing.
    /// </summary>
    private void UpdateFoot()
    {
        SaveButton.IsEnabled = Draft.IsDirty;
        if (Draft.IsDirty) _saveFailed = false;
        FootNote.Text = Draft.IsDirty ? "Unsaved changes"
            : _saveFailed ? "These colours last until FleetFinder closes: theme.json could not be written."
            : "";
        FootNote.Visibility = FootNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- the picker -----------------------------------------------------------------------------

    private void ColourBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ColourRow row } box)
            OpenPicker(row, box);
    }

    private void OpenPicker(ColourRow row, Button box)
    {
        ClosePicker(keep: true);
        ClearFound();
        Draft.BeginEdit(row.Key);
        _editing = row;
        _editingBox = box;
        row.IsEditing = true;
        PickerTitle.Text = row.Label;
        // Before the layer shows, so the picker taking its starting colour is not an edit.
        Picker.SelectedColor = Draft[row.Key];
        PickerOkButton.IsDefault = true;
        PickerLayer.Visibility = Visibility.Visible;
        box.BringIntoView();
        UpdateLayout();
        PlacePicker();
        // Straight into HEX, so a colour can be typed or pasted at once. Escape cancels from
        // anywhere on the page, and Enter in a field with nothing new in it is OK.
        if (Picker.FindName("HexBox") is TextBox hex)
        {
            hex.Focus();
            hex.SelectAll();
        }
    }

    private void Picker_SelectedColorChanged(object sender, RoutedPropertyChangedEventArgs<Color> e)
    {
        if (_editing is { } row && IsPickerOpen)
            Draft.Set(row.Key, e.NewValue);
    }

    private void PickerOk_Click(object sender, RoutedEventArgs e) => ClosePicker(keep: true);

    private void PickerCancel_Click(object sender, RoutedEventArgs e) => ClosePicker(keep: false);

    /// <summary>A click on the layer itself, not on the card: outside the picker.</summary>
    private void PickerLayer_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, PickerLayer)) return;
        ClosePicker(keep: true);
        e.Handled = true;
    }

    private void Page_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !IsPickerOpen) return;
        ClosePicker(keep: false);
        e.Handled = true;
    }

    /// <summary>
    /// Closes the picker. Keeping takes anything typed into a field but not yet entered first;
    /// cancelling puts that one role back as it was when the picker opened.
    /// </summary>
    private void ClosePicker(bool keep)
    {
        if (!IsPickerOpen) return;
        if (keep)
        {
            Picker.CommitPendingEdits();
            Draft.EndEdit();
        }
        else
        {
            Draft.CancelEdit();
        }

        PickerOkButton.IsDefault = false;
        PickerLayer.Visibility = Visibility.Collapsed;
        if (_editing != null) _editing.IsEditing = false;
        var box = _editingBox;
        _editing = null;
        _editingBox = null;
        if (box is { IsVisible: true }) box.Focus();
    }

    /// <summary>
    /// Puts the card in the list's column, under the clicked box, or over it if there is no room
    /// below, and never across into the preview. On a page too short for the whole card it is
    /// scaled down to fit.
    /// </summary>
    private void PlacePicker()
    {
        if (!IsPickerOpen || _editingBox is not { IsVisible: true } box) return;

        const double gap = 4, edge = 8;
        PickerScale.ScaleX = PickerScale.ScaleY = 1;
        PickerCard.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var natural = PickerCard.DesiredSize;

        var list = ListPanel.TransformToAncestor(this).TransformBounds(new Rect(ListPanel.RenderSize));
        var inner = new Rect(list.Left + ListPanel.Padding.Left, list.Top,
            Math.Max(0, list.Width - ListPanel.Padding.Left - ListPanel.Padding.Right), list.Height);
        double scale = Math.Min(1, Math.Min(inner.Width / natural.Width, (ActualHeight - 2 * edge) / natural.Height));
        PickerScale.ScaleX = PickerScale.ScaleY = scale;
        double width = natural.Width * scale, height = natural.Height * scale;

        var boxRect = box.TransformToAncestor(this).TransformBounds(new Rect(box.RenderSize));
        double top = boxRect.Bottom + gap;
        if (top + height > ActualHeight - edge)
            top = boxRect.Top - gap - height;
        if (top < edge)
            top = Math.Max(edge, ActualHeight - edge - height);

        Canvas.SetLeft(PickerCard, inner.Right - width);
        Canvas.SetTop(PickerCard, top);
    }

    // ---- copy and paste over the list (C7) ------------------------------------------------------

    private void Row_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ColourRow row }) _hovered = row;
    }

    private void Row_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ColourRow row } && ReferenceEquals(row, _hovered)) _hovered = null;
    }

    /// <summary>The mouse is now over this role's row (null: over no row). What the rows' own
    /// mouse handlers do; the screenshot harness calls it, having no real mouse to move.</summary>
    public void HoverRow(string? key) => _hovered = key is null ? null : _rows[key];

    private void Host_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleHoverKey(e.Key, Keyboard.Modifiers, Keyboard.FocusedElement))
            e.Handled = true;
    }

    /// <summary>
    /// A key pressed anywhere in the window. Ctrl+C or Ctrl+V with the mouse over a row copies or
    /// pastes that row's colour and returns true; anything else returns false and is left for
    /// the window as before. <see cref="HoverClipboard.ActionFor"/> has the rules.
    /// </summary>
    public bool HandleHoverKey(Key key, ModifierKeys modifiers, object? focused)
    {
        var action = HoverClipboard.ActionFor(key, modifiers, settingsShowing: IsVisible, rowUnderMouse: _hovered != null,
            overlayOpen: IsOverlayOpen, typingInTextBox: focused is TextBoxBase or PasswordBox);
        return CopyOrPaste(action);
    }

    /// <summary>Copies or pastes on the row under the mouse and shows the note, without the
    /// checks <see cref="HandleHoverKey"/> makes first. The self-test calls it directly, since the
    /// page it builds is never on screen.</summary>
    public bool CopyOrPaste(HoverAction action)
    {
        if (action == HoverAction.None || _hovered is not { } row) return false;
        ShowNote(row, action == HoverAction.Copy ? _hoverClipboard.Copy(row.Key) : _hoverClipboard.Paste(row.Key));
        return true;
    }

    /// <summary>
    /// The note beside the row's colour box, just left of it and over the end of the label, for
    /// a second at full strength and then fading out over half a second.
    /// </summary>
    private void ShowNote(ColourRow row, string text)
    {
        int note = ++_noteCount;
        CopyNoteText.Text = text;
        CopyNote.BeginAnimation(OpacityProperty, null);
        CopyNote.Opacity = 1;
        CopyNote.Visibility = Visibility.Visible;

        if (ColourBoxOf(row) is { } box)
        {
            CopyNote.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var boxRect = box.TransformToAncestor(this).TransformBounds(new Rect(box.RenderSize));
            var list = ListPanel.TransformToAncestor(this).TransformBounds(new Rect(ListPanel.RenderSize));
            double left = Math.Max(list.Left + ListPanel.Padding.Left, boxRect.Left - 8 - CopyNote.DesiredSize.Width);
            Canvas.SetLeft(CopyNote, Math.Round(left));
            Canvas.SetTop(CopyNote, Math.Round(boxRect.Top + (boxRect.Height - CopyNote.DesiredSize.Height) / 2));
        }

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.0))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.5))));
        fade.Completed += (_, _) =>
        {
            // A newer note may have taken over in the meantime; only the latest one hides it.
            if (note != _noteCount) return;
            CopyNote.BeginAnimation(OpacityProperty, null);
            CopyNote.Visibility = Visibility.Collapsed;
        };
        CopyNote.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The element that holds a row in the list: the whole row, gaps included.</summary>
    private FrameworkElement? RowElementOf(ColourRow row) =>
        Descendants<Border>(ColourList).FirstOrDefault(b => b.Name == "RowHit" && ReferenceEquals(b.DataContext, row));

    private Button? ColourBoxOf(ColourRow row) =>
        RowElementOf(row) is { } element ? Descendants<Button>(element).FirstOrDefault() : null;

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    // ---- click to find (C9) ---------------------------------------------------------------------

    /// <summary>Every tagged element of the preview, worked out afresh. For the self-test, and for
    /// the outline, which keeps the last lot while the mouse moves.</summary>
    public IReadOnlyList<RoleRegion> PreviewRegions() => _regions = PreviewTag.Regions(Preview);

    /// <summary>
    /// A click at this point of the preview (in its own coordinates): lights up the rows of every
    /// colour the element under it is drawn with, and scrolls the list to them. Returns those roles,
    /// none when the point is off the preview, which also clears the last lot.
    /// </summary>
    public IReadOnlyList<string> FindInPreview(Point pointInPreview)
    {
        var roles = RoleFinder.At(PreviewRegions(), pointInPreview)?.Roles ?? [];
        foreach (var row in _rows.Values) row.IsFound = roles.Contains(row.Key);
        ScrollToFound();
        return roles;
    }

    private void ClearFound()
    {
        foreach (var row in _rows.Values) row.IsFound = false;
    }

    /// <summary>Scrolls the list so the found rows show, comfortably clear of its edges.</summary>
    private void ScrollToFound()
    {
        var found = Groups.SelectMany(g => g.Rows).Where(r => r.IsFound).ToList();
        if (found.Count == 0) return;
        UpdateLayout();
        if (RowElementOf(found[0]) is not { } first || RowElementOf(found[^1]) is not { } last) return;
        double top = first.TransformToAncestor(ColourList).Transform(new Point(0, 0)).Y;
        double bottom = last.TransformToAncestor(ColourList).Transform(new Point(0, last.ActualHeight)).Y;
        double target = RoleFinder.ScrollTarget(ListScroll.VerticalOffset, ListScroll.ViewportHeight, ListScroll.ExtentHeight,
            top, bottom, ComfortMargin);
        if (Math.Abs(target - ListScroll.VerticalOffset) >= 0.5)
        {
            ListScroll.ScrollToVerticalOffset(target);
            UpdateLayout();
        }
    }

    /// <summary>
    /// Outlines what a click at this point of the preview would pick, or hides the outline (null,
    /// or a point on nothing). Returns what it outlined.
    /// </summary>
    public RoleRegion? OutlineInPreview(Point? pointInPreview)
    {
        var hit = pointInPreview is { } p ? RoleFinder.At(_regions ??= PreviewTag.Regions(Preview), p) : null;
        if (hit == null)
        {
            HoverOutline.Visibility = HoverOutlineBack.Visibility = Visibility.Collapsed;
            return null;
        }

        // Two pixels clear of the element all round, on whole pixels so the dashes stay crisp.
        var r = Preview.TransformToVisual(OutlineLayer).TransformBounds(hit.Bounds);
        double left = Math.Floor(r.Left) - 2, top = Math.Floor(r.Top) - 2;
        double width = Math.Ceiling(r.Right) + 2 - left, height = Math.Ceiling(r.Bottom) + 2 - top;
        foreach (var outline in new[] { HoverOutlineBack, HoverOutline })
        {
            Canvas.SetLeft(outline, left);
            Canvas.SetTop(outline, top);
            outline.Width = width;
            outline.Height = height;
            outline.Visibility = Visibility.Visible;
        }
        return hit;
    }

    private void PreviewArea_MouseMove(object sender, MouseEventArgs e) => OutlineInPreview(e.GetPosition(Preview));

    private void PreviewArea_MouseLeave(object sender, MouseEventArgs e)
    {
        OutlineInPreview(null);
        // Worked out again next time the mouse comes back, in case anything moved meanwhile.
        _regions = null;
    }

    private void PreviewArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        FindInPreview(e.GetPosition(Preview));
        e.Handled = true;
    }

    private void PreviewArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _regions = null;
        OutlineInPreview(null);
    }
}
