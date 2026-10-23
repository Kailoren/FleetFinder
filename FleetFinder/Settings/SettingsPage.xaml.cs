using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FleetView.Controls;
using FleetView.Theming;

namespace FleetView.Settings;

/// <summary>
/// The Settings page: the colour list, the live preview, and Save, Revert and Reset to defaults.
/// All the decisions about colours are in <see cref="ThemeDraft"/>; this class wires it to the
/// list, the preview and the picker.
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly Dictionary<string, ColourRow> _rows;
    private ColourRow? _editing;
    private Button? _editingBox;
    private bool _saveFailed;

    public SettingsPage()
    {
        InitializeComponent();

        Draft = ThemeDraft.ForApp();
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
            if (!(bool)e.NewValue) ClosePicker(keep: true);
        };
        SizeChanged += (_, _) => PlacePicker();
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
}
