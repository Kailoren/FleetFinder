using System.Globalization;
using System.Windows.Media;
using FleetView.Theming;
using FleetView.ViewModels;

namespace FleetView.Settings;

/// <summary>One group heading in the Settings colour list and the rows under it.</summary>
public sealed record ColourGroup(string Name, IReadOnlyList<ColourRow> Rows);

/// <summary>
/// One line of the Settings colour list: a role's label, its draft colour as a box, and the hex.
/// </summary>
/// <remarks>
/// The box is filled from the draft, never from the role's resource key, because the list sits
/// outside the preview and a resource lookup there would find the app's saved colour instead.
/// </remarks>
public sealed class ColourRow : ObservableObject
{
    private Color _colour;
    private bool _isEditing;

    public ColourRow(ThemeRole role, Color colour)
    {
        Role = role;
        _colour = colour;
    }

    public ThemeRole Role { get; }
    public string Key => Role.Key;
    public string Label => Role.Label;

    /// <summary>The small line under the label, or null. Roles drawn see-through say how much,
    /// since what shows is lighter than the colour picked.</summary>
    public string? Note => Role.Note ?? AlphaNote(Role, _colour);
    public bool HasNote => Note != null;

    public Color Colour
    {
        get => _colour;
        set
        {
            if (!SetProperty(ref _colour, value)) return;
            OnPropertyChanged(nameof(Swatch));
            OnPropertyChanged(nameof(Hex));
            OnPropertyChanged(nameof(Note));
        }
    }

    /// <summary>The colour box's fill, alpha and all, so a see-through role looks see-through.</summary>
    public Brush Swatch
    {
        get
        {
            var brush = new SolidColorBrush(_colour);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>The same text the picker's HEX field shows for this colour.</summary>
    public string Hex => ColourMaths.FormatHex(_colour);

    /// <summary>The picker is open on this row; its box stays outlined meanwhile.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    private static string? AlphaNote(ThemeRole role, Color colour) =>
        role.HasAlpha
            ? string.Format(CultureInfo.InvariantCulture, "Shows at {0:0}% over what is behind it", colour.A * 100.0 / 255)
            : null;
}
