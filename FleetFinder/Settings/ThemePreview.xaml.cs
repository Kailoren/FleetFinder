using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FleetView.Theming;

namespace FleetView.Settings;

/// <summary>
/// The live preview on the Settings page: a small copy of Find Carriers plus samples, drawn in the
/// draft colours rather than the app's.
/// </summary>
/// <remarks>
/// The draft brushes go in this control's own <see cref="FrameworkElement.Resources"/>, one per role
/// from <see cref="ThemeService"/>, so every DynamicResource inside finds them before the app's.
/// Changing a colour replaces one brush in that dictionary, which moves this control and nothing
/// else. Each role gets a brush object of its own even when two roles share a colour, which is
/// what lets the self-test tell which roles the preview really draws with.
/// </remarks>
public partial class ThemePreview : UserControl
{
    public ThemePreview()
    {
        InitializeComponent();
        VersionText.Text = "v" + (typeof(ThemePreview).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
        ComponentGrid.ItemsSource = PreviewSamples.Components();
        BuyGrid.ItemsSource = PreviewSamples.Buy;
        BuyGrid.SelectedIndex = PreviewSamples.SelectedBuyRow;
        SellGrid.ItemsSource = PreviewSamples.Sell;
        Resources = ThemeService.BuildDictionary(ThemeService.Current);
    }

    /// <summary>Shows these colours, every role (missing ones get their default).</summary>
    public void ShowColours(IReadOnlyDictionary<string, Color> colours) => ThemeService.ApplyTo(Resources, colours);

    /// <summary>Shows one role in a new colour. For the picker's live drag.</summary>
    public void ShowColour(string roleKey, Color colour) => ThemeService.SetBrush(Resources, roleKey, colour);
}
