using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Field = FleetView.Controls.ColourPickerState.Field;

namespace FleetView.Controls;

/// <summary>
/// A hand-drawn colour picker (WPF has none): swatch, saturation and brightness square, hue strip,
/// and HEX, RGB, CMYK, HSV and HSL fields. Bind <see cref="SelectedColor"/>; it is two-way by
/// default and changes live while a handle is being dragged.
/// </summary>
/// <remarks>
/// <para>
/// All the colour logic is in <see cref="ColourPickerState"/>; this class draws it and turns mouse
/// and keyboard into calls on it. The mouse handlers only capture and release the mouse and pass
/// the position on to <see cref="PickSquareAt"/> and <see cref="PickHueAt"/>, which are public so
/// a test or a screenshot harness can drive the picker without a real mouse.
/// </para>
/// <para>
/// Alpha comes in with <see cref="SelectedColor"/> and goes back out unchanged; nothing in the
/// picker edits it. Everything is opaque on screen.
/// </para>
/// <para>
/// To reuse it in another app, copy the three ColourPicker files in Controls\ (this one, which
/// also holds OutlineGapConverter, its XAML and the state class) plus Theming\ColourMaths.cs,
/// fix up the namespaces, and make sure the app has brushes
/// under the role keys the XAML uses: InputBackground, InputText, InputBorder, InputHoverBorder,
/// InputFocusBorder, TextSelection and MutedText. Without them the fields simply draw without
/// those colours. A ContextMenu under TextBoxMenu, if the app has one, becomes the fields'
/// right-click menu; without it they keep WPF's own.
/// </para>
/// </remarks>
public partial class ColourPicker : UserControl
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(Color), typeof(ColourPicker),
        // A new picker's own starting colour (opaque black), so an unbound picker hands back
        // opaque colours rather than default(Color), which is fully transparent.
        new FrameworkPropertyMetadata(new ColourPickerState().Colour,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

    public static readonly RoutedEvent SelectedColorChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(SelectedColorChanged), RoutingStrategy.Bubble,
        typeof(RoutedPropertyChangedEventHandler<Color>), typeof(ColourPicker));

    private readonly ColourPickerState _state = new();
    private readonly Dictionary<TextBox, Field> _fields;
    private readonly DispatcherTimer _copiedTimer;

    /// <summary>True while the picker itself is setting <see cref="SelectedColor"/>, so the
    /// change handler does not feed the picker's own colour back into its state.</summary>
    private bool _pushing;

    public ColourPicker()
    {
        InitializeComponent();

        _fields = new Dictionary<TextBox, Field>
        {
            [HexBox] = Field.Hex,
            [RgbBox] = Field.Rgb,
            [CmykBox] = Field.Cmyk,
            [HsvBox] = Field.Hsv,
            [HslBox] = Field.Hsl,
        };
        foreach (var box in _fields.Keys)
        {
            box.KeyDown += Field_KeyDown;
            box.LostKeyboardFocus += (_, _) => CommitField(box);
        }

        Square.SizeChanged += (_, _) => PlaceHandles();
        Strip.SizeChanged += (_, _) => PlaceHandles();

        _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _copiedTimer.Tick += (_, _) =>
        {
            _copiedTimer.Stop();
            CopiedIcon.Visibility = Visibility.Collapsed;
            CopyIcon.Visibility = Visibility.Visible;
        };

        _state.SetColour(SelectedColor);
        Refresh();
    }

    /// <summary>The colour picked, with the alpha it was given. Two-way by default.</summary>
    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    /// <summary>Raised on every change of <see cref="SelectedColor"/>, from inside or outside,
    /// including each step of a drag.</summary>
    public event RoutedPropertyChangedEventHandler<Color> SelectedColorChanged
    {
        add => AddHandler(SelectedColorChangedEvent, value);
        remove => RemoveHandler(SelectedColorChangedEvent, value);
    }

    /// <summary>The hue the strip shows, 0 to 360. Kept through greys and black.</summary>
    public double Hue => _state.Hue;

    /// <summary>The square's saturation, 0 to 1. Kept through black.</summary>
    public double Saturation => _state.Saturation;

    /// <summary>The square's brightness (HSV value), 0 to 1.</summary>
    public double Brightness => _state.Value;

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ColourPicker)d;
        if (!picker._pushing)
        {
            picker._state.SetColour((Color)e.NewValue);
            picker.Refresh();
        }
        picker.RaiseEvent(new RoutedPropertyChangedEventArgs<Color>((Color)e.OldValue, (Color)e.NewValue,
            SelectedColorChangedEvent));
    }

    // ---- mouse --------------------------------------------------------------------------------

    /// <summary>
    /// Picks the point <paramref name="position"/> on the square, in the square's own coordinates
    /// (0,0 is its top left corner). Points outside are pulled in to the nearest edge.
    /// </summary>
    public void PickSquareAt(Point position)
    {
        double w = Square.ActualWidth, h = Square.ActualHeight;
        if (w <= 0 || h <= 0) return;
        _state.SetSquare(position.X / w, position.Y / h);
        Push();
    }

    /// <summary>Picks the hue under <paramref name="position"/> on the strip, in the strip's own
    /// coordinates. Only the X matters.</summary>
    public void PickHueAt(Point position)
    {
        double w = Strip.ActualWidth;
        if (w <= 0) return;
        _state.SetStrip(position.X / w);
        Push();
    }

    // A press anywhere on the square or the strip jumps the handle there and starts a drag. The
    // mouse is captured so the drag carries on past the edge (pinned to it) and the release is
    // seen wherever it happens.

    private void Square_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Square.CaptureMouse();
        PickSquareAt(e.GetPosition(Square));
        e.Handled = true;
    }

    private void Square_MouseMove(object sender, MouseEventArgs e)
    {
        if (Square.IsMouseCaptured) PickSquareAt(e.GetPosition(Square));
    }

    private void Square_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!Square.IsMouseCaptured) return;
        Square.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Strip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Strip.CaptureMouse();
        PickHueAt(e.GetPosition(Strip));
        e.Handled = true;
    }

    private void Strip_MouseMove(object sender, MouseEventArgs e)
    {
        if (Strip.IsMouseCaptured) PickHueAt(e.GetPosition(Strip));
    }

    private void Strip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!Strip.IsMouseCaptured) return;
        Strip.ReleaseMouseCapture();
        e.Handled = true;
    }

    // ---- fields -------------------------------------------------------------------------------

    /// <summary>
    /// Applies anything typed into a field but not yet entered (no Enter, and the field still has
    /// the keyboard). A host calls this before taking the colour on OK or when its popup closes,
    /// so a value typed a moment earlier is not lost. Unreadable text is put back, as it would be
    /// on leaving the field.
    /// </summary>
    public void CommitPendingEdits()
    {
        foreach (var (box, field) in _fields)
        {
            if (box.Text == _state.Text(field)) continue;
            CommitField(box);
            // Only one field can have been typed in, and committing it rewrites all the others.
            return;
        }
    }

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var box = (TextBox)sender;
        // Nothing typed: leave Enter alone, so a host's default button (OK) still gets it. The
        // first Enter applies what was typed, the second one accepts.
        if (box.Text == _state.Text(_fields[box])) return;
        CommitField(box);
        box.SelectAll();
        e.Handled = true;
    }

    /// <summary>Reads a field into the picker, or puts the field back if it cannot be read.</summary>
    private void CommitField(TextBox box)
    {
        var field = _fields[box];
        if (_state.Commit(field, box.Text))
            Push();
        else
            box.Text = _state.Text(field);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_state.Text(Field.Hex));
        }
        catch (ExternalException)
        {
            // Another program is holding the clipboard open. The tick not appearing is the only
            // sign, and clicking again works once it lets go.
            return;
        }
        CopyIcon.Visibility = Visibility.Collapsed;
        CopiedIcon.Visibility = Visibility.Visible;
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    // ---- drawing ------------------------------------------------------------------------------

    /// <summary>The state changed inside the picker: redraw, then tell the host.</summary>
    private void Push()
    {
        Refresh();
        _pushing = true;
        try { SelectedColor = _state.Colour; }
        finally { _pushing = false; }
    }

    private void Refresh()
    {
        var colour = Solid(_state.OpaqueColour);
        Swatch.Background = colour;
        SquareHandle.Fill = colour;
        HueStop.Color = _state.PureHue;
        StripHandle.Fill = Solid(_state.PureHue);
        PlaceHandles();
        foreach (var (box, field) in _fields)
            box.Text = _state.Text(field);
    }

    private void PlaceHandles()
    {
        Canvas.SetLeft(SquareHandle, _state.SquareX * Square.ActualWidth - SquareHandle.Width / 2);
        Canvas.SetTop(SquareHandle, _state.SquareY * Square.ActualHeight - SquareHandle.Height / 2);
        Canvas.SetLeft(StripHandle, _state.StripX * Strip.ActualWidth - StripHandle.Width / 2);
        Canvas.SetTop(StripHandle, (Strip.ActualHeight - StripHandle.Height) / 2);
    }

    private static SolidColorBrush Solid(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// The clip that cuts a gap in a picker field's outline where its label sits on the top edge.
/// Values: the outline's width and height, the label element, then the label's width and
/// alignment (only there so the clip is worked out again when either changes).
/// </summary>
public sealed class OutlineGapConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3 || values[0] is not double w || values[1] is not double h
            || values[2] is not FrameworkElement label || w <= 0 || h <= 0 || label.ActualWidth <= 0)
        {
            return null;
        }

        // The label and the outline share a Grid and a left edge, so the label's left margin (or
        // centring) places it in the outline's coordinates too.
        double x = label.HorizontalAlignment == HorizontalAlignment.Center
            ? (w - label.ActualWidth) / 2
            : label.Margin.Left;
        var clip = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(-1, -1, w + 2, h + 2)),
            new RectangleGeometry(new Rect(x, -1, label.ActualWidth, 4)));
        clip.Freeze();
        return clip;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
