using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FleetView;

/// <summary>
/// Carries the DataContext into places that aren't in the visual tree (e.g. DataGridColumn),
/// so their properties can bind to the view model. Add as a resource with Data="{Binding}".
/// </summary>
public sealed class BindingProxy : Freezable
{
    protected override Freezable CreateInstanceCore() => new BindingProxy();

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy),
            new UIPropertyMetadata(null));
}

/// <summary>Inverts a boolean (used to bind the "Buying" radio to !IsSelling).</summary>
public sealed class InverseBool : IValueConverter
{
    public static readonly InverseBool Instance = new();

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}

/// <summary>Maps true -> Visible, false -> Collapsed.</summary>
public sealed class BoolToVis : IValueConverter
{
    public static readonly BoolToVis Instance = new();

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}
