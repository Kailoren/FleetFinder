using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FleetView.Controls;
using FleetView.Services;
using FleetView.Theming;

namespace FleetView.Settings;

/// <summary>
/// The Settings page: the colour list, the live preview, the saved themes, and Save, Revert and
/// Reset to defaults. All the decisions about colours are in <see cref="ThemeDraft"/>; this class
/// wires it to the list, the preview and the picker. Copy and paste over the list
/// (<see cref="HoverClipboard"/>), click to find in the preview (<see cref="RoleFinder"/>,
/// <see cref="PreviewTag"/>) and the saved themes (<see cref="ThemeShelf"/>,
/// <see cref="SaveThemeForm"/>, <see cref="ThemeImport"/>) keep their rules in those classes too.
/// </summary>
public partial class SettingsPage : UserControl
{
    /// <summary>Room to leave above and below rows the list scrolls to, so they do not end up
    /// jammed against its edge.</summary>
    private const double ComfortMargin = 60;

    /// <summary>The height of the row of theme cards: one card, and the sideways scrollbar under
    /// it. Fixed, so the strip is the same height with no cards as with many.</summary>
    public const double CardRowHeight = 270;

    /// <summary>The most of the right column the themes strip may take. Past this (a short
    /// window) it is scaled down, so the preview is never squeezed out.</summary>
    private const double MaxThemesShare = 0.5;

    private enum ThemeDialog { None, SaveTheme, Import }

    private readonly Dictionary<string, ColourRow> _rows;
    private readonly HoverClipboard _hoverClipboard;
    private readonly ThemeShelf _shelf;
    private ColourRow? _editing;
    private Button? _editingBox;
    private bool _saveFailed;
    private ColourRow? _hovered;
    private Window? _host;
    private List<RoleRegion>? _regions;
    private int _noteCount;
    private bool _themesLoaded;
    private ThemeDialog _dialog;
    private Button? _dialogOpener;
    private SaveThemeForm? _saveForm;
    private ThemeImport? _import;
    private byte[]? _importPng;
    private bool _fillingBoxes;
    private bool _commanderTyped;
    private int _commanderAsk;
    private string _lastCommander = "";
    private ContextMenu? _openCardMenu;
    private ThemePreview? _snapshotPreview;
    private bool _snapshotsQueued;
    private readonly HashSet<string> _snapshotTried = new(StringComparer.Ordinal);
    private SharedTheme? _importShown;

    public SettingsPage()
    {
        InitializeComponent();

        Draft = ThemeDraft.ForApp();
        _hoverClipboard = new HoverClipboard(Draft, SystemClipboardText.Instance);
        _shelf = new ThemeShelf(ThemeLibrary.ForApp(), Draft, SystemClipboardText.Instance);
        Groups = ThemeRoles.Groups
            .Select(g => new ColourGroup(g, ThemeRoles.All.Where(r => r.Group == g)
                .Select(r => new ColourRow(r, Draft[r.Key])).ToList()))
            .ToList();
        _rows = Groups.SelectMany(g => g.Rows).ToDictionary(r => r.Key, StringComparer.Ordinal);
        ColourList.ItemsSource = Groups;
        CardList.ItemsSource = _shelf.Cards;
        _shelf.Cards.CollectionChanged += (_, _) => UpdateThemesEmpty();
        UpdateThemesEmpty();
        // The name's cap is the share code's; the CMDR box leaves room for a typed "CMDR ", which
        // the credit drops.
        ThemeNameBox.MaxLength = ThemeText.MaxNameLength;
        ThemeCommanderBox.MaxLength = ThemeText.MaxCommanderLength + "CMDR ".Length;
        Preview.ShowColours(Draft.Colours);

        Draft.ColourChanged += OnDraftColourChanged;
        Draft.PropertyChanged += (_, _) => UpdateFoot();
        Picker.SelectedColorChanged += Picker_SelectedColorChanged;
        // Leaving the page with the picker open (another tab, the cog again) keeps what was
        // picked, as a click anywhere else would. A theme dialog open is cancelled, and a card
        // asking whether to delete stops asking.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) return;
            ClosePicker(keep: true);
            CloseDialog();
            if (_shelf.Confirming is { } asking) _shelf.CancelDelete(asking);
            _hovered = null;
            OutlineInPreview(null);
        };
        SizeChanged += (_, _) =>
        {
            PlacePicker();
            FitDialog();
        };
        // Ctrl+C and Ctrl+V over the list are caught at the window, because the keyboard is
        // usually somewhere else (the cog that opened Settings, a colour box), and only while
        // this page is in the window: the tab control takes it out when another tab is chosen.
        Loaded += (_, _) =>
        {
            if (_host != null) _host.PreviewKeyDown -= Host_PreviewKeyDown;
            _host = Window.GetWindow(this);
            if (_host != null) _host.PreviewKeyDown += Host_PreviewKeyDown;
            // The saved themes are read the first time the page shows, not when the window is
            // built, so a start-up never waits on the themes folder.
            if (!_themesLoaded) LoadThemes();
            QueueMissingSnapshots();
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

    /// <summary>A theme dialog (Save as theme, Import code) is open over the page.</summary>
    public bool IsDialogOpen => _dialog != ThemeDialog.None;

    /// <summary>A theme card's right-click menu is open.</summary>
    public bool IsCardMenuOpen => _openCardMenu is { IsOpen: true };

    /// <summary>Something is open over the page, so the keys over the list are left alone: the
    /// picker, a theme dialog, or a card's right-click menu.</summary>
    public bool IsOverlayOpen => IsPickerOpen || IsDialogOpen || IsCardMenuOpen;

    /// <summary>The clipboard hover copy and paste and Copy share code use: the real one, unless a
    /// test hands in its own.</summary>
    public IClipboardText ClipboardText
    {
        get => _hoverClipboard.Clipboard;
        set
        {
            _hoverClipboard.Clipboard = value;
            _shelf.Clipboard = value;
        }
    }

    /// <summary>Where the saved themes are kept: the app's own <c>Data\themes</c>, unless a test
    /// hands in a folder of its own. Setting it reads that folder's themes at once.</summary>
    public ThemeLibrary ThemeLibrary
    {
        get => _shelf.Library;
        set
        {
            _shelf.UseLibrary(value);
            _themesLoaded = true;
        }
    }

    /// <summary>The theme cards as shown, oldest first.</summary>
    public IReadOnlyList<ThemeCard> ThemeCards => _shelf.Cards;

    /// <summary>
    /// Finds the commander to credit when saving a theme: the main window hands in the journal's
    /// last login. It reads files, so the page always calls it off the UI thread. Null means
    /// there is no journal to ask, and the CMDR box starts empty.
    /// </summary>
    public Func<string>? CommanderLookup { get; set; }

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
        if (HandlePageKey(e.Key)) e.Handled = true;
    }

    /// <summary>
    /// Enter or Escape that reached the page without anything handling it on the way. Escape
    /// cancels the picker, a theme dialog, or a card asking whether to delete; Enter in a theme
    /// dialog is Save or Add. Returns whether the key was used. The self-test calls it directly.
    /// </summary>
    public bool HandlePageKey(Key key)
    {
        if (key == Key.Escape)
        {
            if (IsPickerOpen)
            {
                ClosePicker(keep: false);
                return true;
            }
            if (IsDialogOpen)
            {
                CloseDialog();
                return true;
            }
            if (_shelf.Confirming is { } asking)
            {
                _shelf.CancelDelete(asking);
                return true;
            }
            return false;
        }
        if (key == Key.Enter && IsDialogOpen)
        {
            if (_dialog == ThemeDialog.SaveTheme) SubmitSaveTheme();
            else AddImport();
            return true;
        }
        return false;
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

    /// <summary>The note beside the row's colour box, just left of it and over the end of the label.</summary>
    private void ShowNote(ColourRow row, string text) => ShowNote(text, size =>
    {
        if (ColourBoxOf(row) is not { } box) return null;
        var boxRect = box.TransformToAncestor(this).TransformBounds(new Rect(box.RenderSize));
        var list = ListPanel.TransformToAncestor(this).TransformBounds(new Rect(ListPanel.RenderSize));
        double left = Math.Max(list.Left + ListPanel.Padding.Left, boxRect.Left - 8 - size.Width);
        return new Point(left, boxRect.Top + (boxRect.Height - size.Height) / 2);
    });

    /// <summary>
    /// A short note on the page for a second at full strength, then fading out over half a second,
    /// at the top-left corner <paramref name="place"/> works out from its size (null: wherever it
    /// was last).
    /// </summary>
    private void ShowNote(string text, Func<Size, Point?> place)
    {
        int note = ++_noteCount;
        CopyNoteText.Text = text;
        CopyNote.BeginAnimation(OpacityProperty, null);
        CopyNote.Opacity = 1;
        CopyNote.Visibility = Visibility.Visible;

        CopyNote.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (place(CopyNote.DesiredSize) is { } at)
        {
            Canvas.SetLeft(CopyNote, Math.Round(at.X));
            Canvas.SetTop(CopyNote, Math.Round(at.Y));
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

    // ---- saved themes (C8): the strip and its cards ----------------------------------------------

    private void LoadThemes()
    {
        _themesLoaded = true;
        _shelf.Reload();
    }

    private void UpdateThemesEmpty()
    {
        bool none = _shelf.Cards.Count == 0;
        ThemesEmpty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        CardScroll.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The strip keeps its own size and the preview shrinks into what is left, unless the window is
    /// so short that the strip would take more than half the column; then the strip is scaled down
    /// to that half, as the picker is scaled on a short page.
    /// </summary>
    private void RightColumn_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double height = RightColumn.ActualHeight;
        if (height <= 0) return;
        ThemesScale.ScaleX = ThemesScale.ScaleY = 1;
        ThemesPanel.Measure(new Size(RightColumn.ActualWidth, double.PositiveInfinity));
        double natural = ThemesPanel.DesiredSize.Height;
        double scale = natural <= 0 ? 1 : Math.Min(1, height * MaxThemesShare / natural);
        ThemesScale.ScaleX = ThemesScale.ScaleY = scale;
    }

    /// <summary>The mouse wheel scrolls the row of cards sideways, there being nothing to scroll
    /// up and down.</summary>
    private void CardScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CardScroll.ScrollableWidth <= 0) return;
        CardScroll.ScrollToHorizontalOffset(CardScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>The card a button or menu item belongs to. A menu item finds it through the menu's
    /// target, the card it was opened on, should it not have the card's data itself.</summary>
    private static ThemeCard? CardOf(object sender) => sender switch
    {
        FrameworkElement { DataContext: ThemeCard card } => card,
        MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: ThemeCard card } } } => card,
        _ => null,
    };

    /// <summary>The element that holds a card in the strip.</summary>
    public FrameworkElement? CardElementOf(ThemeCard card) =>
        Descendants<Border>(CardList).FirstOrDefault(b => b.Name == "CardBorder" && ReferenceEquals(b.DataContext, card));

    private void ScrollToCard(ThemeCard? card)
    {
        if (card == null) return;
        UpdateLayout();
        CardElementOf(card)?.BringIntoView();
    }

    private void CardApply_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) ApplyTheme(card);
    }

    /// <summary>Apply on a card: its colours go into the draft, so the preview shows them and
    /// "Unsaved changes" shows. Save keeps them, Revert undoes them; the theme itself is untouched.</summary>
    public void ApplyTheme(ThemeCard card)
    {
        ClosePicker(keep: true);
        ClearFound();
        _shelf.Apply(card);
    }

    private void CardMenu_Opened(object sender, RoutedEventArgs e) => _openCardMenu = sender as ContextMenu;

    private void CardMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _openCardMenu)) _openCardMenu = null;
    }

    private void CardCopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) CopyShareCode(card);
    }

    /// <summary>Copy share code: the theme's code on the clipboard, and a note on the card saying
    /// so. Returns the note.</summary>
    public string CopyShareCode(ThemeCard card)
    {
        var note = _shelf.CopyCode(card);
        ShowCardNote(card, note);
        return note;
    }

    private void CardDelete_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) AskDelete(card);
    }

    /// <summary>Delete from the card's menu: the card asks, in place of its name and Apply, with
    /// the keyboard on Keep so a stray Enter deletes nothing.</summary>
    public void AskDelete(ThemeCard card)
    {
        _shelf.AskDelete(card);
        UpdateLayout();
        if (CardElementOf(card) is { } element
            && Descendants<Button>(element).FirstOrDefault(b => b.Name == "CardKeepButton") is { } keep)
            keep.Focus();
    }

    private void CardKeep_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) _shelf.CancelDelete(card);
    }

    private void CardConfirmDelete_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) DeleteTheme(card);
    }

    /// <summary>Delete, confirmed: the theme, its picture and its card go. False if the files could
    /// not be removed; the card says so and stays.</summary>
    public bool DeleteTheme(ThemeCard card)
    {
        bool deleted = _shelf.Delete(card);
        if (!deleted) ShowCardNote(card, ThemeShelf.NotDeletedNote);
        return deleted;
    }

    /// <summary>A note over the bottom of a card's picture.</summary>
    private void ShowCardNote(ThemeCard card, string text) => ShowNote(text, size =>
    {
        if (CardElementOf(card) is not { } element || Descendants<Image>(element).FirstOrDefault() is not { } image) return null;
        var r = image.TransformToAncestor(this).TransformBounds(new Rect(image.RenderSize));
        return new Point(r.Left + (r.Width - size.Width) / 2, r.Bottom - size.Height - 8);
    });

    // ---- saved themes: pictures -------------------------------------------------------------------

    private double DpiScale() => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>
    /// A picture of the preview in these colours, as PNG bytes, drawn on a second preview that is
    /// never shown (the real one shows the draft). Null if it could not be laid out.
    /// </summary>
    public byte[]? RenderSnapshot(IReadOnlyDictionary<string, Color> colours)
    {
        if (_snapshotPreview == null)
        {
            _snapshotPreview = new ThemePreview { Visibility = Visibility.Collapsed };
            SnapshotHost.Children.Add(_snapshotPreview);
        }
        var preview = _snapshotPreview;
        preview.ShowColours(colours);
        preview.Visibility = Visibility.Visible;
        preview.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        preview.Arrange(new Rect(preview.DesiredSize));
        preview.UpdateLayout();
        var png = ThemeSnapshot.Png(preview, DpiScale());
        // Out of the way again, so it costs nothing while the page is in use.
        preview.Visibility = Visibility.Collapsed;
        return png;
    }

    /// <summary>A picture of the preview as it is now, the draft's colours, for Save as theme.</summary>
    public byte[]? RenderDraftSnapshot() => ThemeSnapshot.Png(Preview, DpiScale());

    /// <summary>Draws a picture for every card that has none (its PNG is missing or unreadable),
    /// from the theme's own colours, and saves it beside the theme. Returns how many were drawn.</summary>
    public int DrawMissingSnapshots()
    {
        int drawn = 0;
        foreach (var card in _shelf.MissingSnapshots)
            if (DrawSnapshot(card)) drawn++;
        return drawn;
    }

    private bool DrawSnapshot(ThemeCard card)
    {
        _snapshotTried.Add(card.Id);
        if (RenderSnapshot(card.Theme.Colours) is not { } png) return false;
        _shelf.StoreSnapshot(card, png);
        return true;
    }

    /// <summary>Missing pictures are drawn one at a time once the page has shown, so opening
    /// Settings never waits on them.</summary>
    private void QueueMissingSnapshots()
    {
        if (_snapshotsQueued || !_shelf.MissingSnapshots.Any(c => !_snapshotTried.Contains(c.Id))) return;
        _snapshotsQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _snapshotsQueued = false;
            if (!IsVisible) return;
            if (_shelf.MissingSnapshots.FirstOrDefault(c => !_snapshotTried.Contains(c.Id)) is { } card) DrawSnapshot(card);
            QueueMissingSnapshots();
        }));
    }

    // ---- saved themes: the dialogs ----------------------------------------------------------------

    private void OpenDialog(ThemeDialog which, Button opener, TextBox focus)
    {
        _dialog = which;
        _dialogOpener = opener;
        _hovered = null;
        OutlineInPreview(null);
        SaveThemeCard.Visibility = which == ThemeDialog.SaveTheme ? Visibility.Visible : Visibility.Collapsed;
        ImportCard.Visibility = which == ThemeDialog.Import ? Visibility.Visible : Visibility.Collapsed;
        DialogLayer.Visibility = Visibility.Visible;
        UpdateLayout();
        FitDialog();
        focus.Focus();
        focus.SelectAll();
    }

    /// <summary>Closes whichever theme dialog is open, saving and adding nothing.</summary>
    public void CloseDialog()
    {
        if (_dialog == ThemeDialog.None) return;
        _dialog = ThemeDialog.None;
        // A commander lookup still running fills nothing in now.
        _commanderAsk++;
        DialogLayer.Visibility = Visibility.Collapsed;
        _saveForm = null;
        _import = null;
        _importPng = null;
        _importShown = null;
        ImportImage.Source = null;
        var opener = _dialogOpener;
        _dialogOpener = null;
        if (opener is { IsVisible: true }) opener.Focus();
    }

    private void DialogCancel_Click(object sender, RoutedEventArgs e) => CloseDialog();

    /// <summary>Scales the open dialog down to fit a short page, as the picker is.</summary>
    private void FitDialog()
    {
        if (!IsDialogOpen || ActualWidth <= 0 || ActualHeight <= 0) return;
        var card = _dialog == ThemeDialog.SaveTheme ? SaveThemeCard : ImportCard;
        const double edge = 8;
        DialogScale.ScaleX = DialogScale.ScaleY = 1;
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var natural = card.DesiredSize;
        double scale = Math.Min(1, Math.Min((ActualWidth - 2 * edge) / natural.Width, (ActualHeight - 2 * edge) / natural.Height));
        DialogScale.ScaleX = DialogScale.ScaleY = Math.Max(0.2, scale);
    }

    // Save as theme.

    private void SaveTheme_Click(object sender, RoutedEventArgs e) => OpenSaveTheme();

    /// <summary>Opens Save as theme: the name empty, the CMDR the last one the journal had, and
    /// the journal asked again (off the UI thread) in case another commander has logged in since.</summary>
    public void OpenSaveTheme()
    {
        ClosePicker(keep: true);
        CloseDialog();
        _saveForm = new SaveThemeForm(_shelf, _lastCommander);
        _commanderTyped = false;
        SetBoxText(ThemeNameBox, "");
        SetBoxText(ThemeCommanderBox, _saveForm.Commander);
        ShowSaveForm();
        OpenDialog(ThemeDialog.SaveTheme, SaveThemeButton, ThemeNameBox);
        AskCommander();
    }

    private void AskCommander()
    {
        if (CommanderLookup is not { } lookup) return;
        int ask = ++_commanderAsk;
        Task.Run(() =>
        {
            try
            {
                return lookup();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Note($"Commander for the theme credit could not be read ({ex.GetType().Name}).");
                return "";
            }
        }).ContinueWith(t =>
        {
            var found = t.Status == TaskStatus.RanToCompletion ? t.Result : "";
            Dispatcher.BeginInvoke(new Action(() => GotCommander(ask, found)));
        }, TaskScheduler.Default);
    }

    /// <summary>The journal's answer. Fills the CMDR box only if it is still the same dialog and
    /// nothing has been typed there meanwhile; an empty answer leaves the box as it is.</summary>
    private void GotCommander(int ask, string found)
    {
        var name = ThemeText.CleanCommander(found);
        if (name.Length > 0) _lastCommander = name;
        if (ask != _commanderAsk || _dialog != ThemeDialog.SaveTheme || _saveForm == null || _commanderTyped || name.Length == 0) return;
        _saveForm.Commander = name;
        SetBoxText(ThemeCommanderBox, name);
    }

    /// <summary>Puts text in a dialog box without it counting as typing.</summary>
    private void SetBoxText(TextBox box, string text)
    {
        if (box.Text == text) return;
        _fillingBoxes = true;
        box.Text = text;
        box.CaretIndex = text.Length;
        _fillingBoxes = false;
    }

    private void ThemeNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_saveForm == null || _fillingBoxes) return;
        _saveForm.Name = ThemeNameBox.Text;
        ShowSaveForm();
    }

    private void ThemeCommanderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_saveForm == null || _fillingBoxes) return;
        _commanderTyped = true;
        _saveForm.Commander = ThemeCommanderBox.Text;
    }

    private void ShowSaveForm()
    {
        if (_saveForm == null) return;
        SaveThemeOkButton.Content = _saveForm.SaveLabel;
        SaveThemeMessage.Text = _saveForm.Message;
        SaveThemeMessage.Visibility = _saveForm.Message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FitDialog();
    }

    private void SaveThemeOk_Click(object sender, RoutedEventArgs e) => SubmitSaveTheme();

    /// <summary>
    /// Save (or Enter) in Save as theme: saves the draft's colours with a picture of the preview, or
    /// asks for a name, or asks "Replace it?" for a name already taken. Returns what happened, or
    /// null when the box is not open.
    /// </summary>
    public SaveThemeOutcome? SubmitSaveTheme()
    {
        if (_saveForm is not { } form || _dialog != ThemeDialog.SaveTheme) return null;
        form.Name = ThemeNameBox.Text;
        form.Commander = ThemeCommanderBox.Text;
        var outcome = form.Submit(RenderDraftSnapshot);
        if (outcome == SaveThemeOutcome.Saved)
        {
            var card = form.Saved;
            CloseDialog();
            ScrollToCard(card);
            return outcome;
        }
        // What the boxes show is what will be saved: the cleaned name and credit.
        if (outcome != SaveThemeOutcome.NeedsName) SetBoxText(ThemeNameBox, form.CleanName);
        SetBoxText(ThemeCommanderBox, form.CleanCommander);
        ShowSaveForm();
        if (outcome == SaveThemeOutcome.NeedsName) ThemeNameBox.Focus();
        return outcome;
    }

    // Import code.

    private void ImportCode_Click(object sender, RoutedEventArgs e) => OpenImport();

    /// <summary>Opens Import code with an empty box.</summary>
    public void OpenImport()
    {
        ClosePicker(keep: true);
        CloseDialog();
        _import = new ThemeImport(_shelf);
        _importPng = null;
        _importShown = null;
        SetBoxText(ImportCodeBox, "");
        ShowImport();
        OpenDialog(ThemeDialog.Import, ImportCodeButton, ImportCodeBox);
    }

    private void ImportCodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_import == null || _fillingBoxes) return;
        ReadImport();
    }

    /// <summary>Enter is Add rather than a new line. Pasted line breaks still go in.</summary>
    private void ImportCodeBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddImport();
        e.Handled = true;
    }

    /// <summary>Reads the code box as it is now. A good code gets its picture drawn here from its
    /// colours (once: typing round a code that still reads the same draws nothing new).</summary>
    public void ReadImport()
    {
        if (_import == null) return;
        _import.Read(ImportCodeBox.Text);
        var theme = _import.Theme;
        if (theme == null)
        {
            _importPng = null;
            _importShown = null;
        }
        else if (_importShown == null || ThemeRoles.All.Any(r => _importShown.Colours[r.Key] != theme.Colours[r.Key]))
        {
            _importPng = RenderSnapshot(theme.Colours);
            _importShown = theme;
        }
        ShowImport();
    }

    private void ShowImport()
    {
        if (_import == null) return;
        ImportMessage.Text = _import.Message;
        ImportMessage.Visibility = _import.Message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // The empty box's prompt is a hint; anything else said is about the paste.
        ImportMessage.SetResourceReference(TextBlock.ForegroundProperty, _import.State == ImportState.Empty ? "MutedText" : "MainText");
        bool found = _import.Theme != null;
        ImportFound.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
        ImportImage.Source = found && _importPng != null ? ThemeShelf.ReadSnapshot(_importPng) : null;
        ImportCredit.Text = _import.Credit;
        ImportAddButton.IsEnabled = _import.CanAdd;
        FitDialog();
    }

    private void ImportAdd_Click(object sender, RoutedEventArgs e) => AddImport();

    /// <summary>Add to my themes (or Enter): saves the imported theme with its picture and closes
    /// the box; it is a card from then on. Null, and nothing done, unless the code is good and new.</summary>
    public ThemeCard? AddImport()
    {
        if (_import is not { CanAdd: true } import || _dialog != ThemeDialog.Import) return null;
        var card = import.Add(_importPng);
        if (card == null)
        {
            ShowImport();
            return null;
        }
        CloseDialog();
        ScrollToCard(card);
        return card;
    }
}
