using System.Windows.Media;
using FleetView.Theming;

namespace FleetView.Controls;

/// <summary>
/// Everything the colour picker knows, with no WPF controls in it: the hue, saturation, value and
/// alpha it holds, the text each field shows, and what typing in a field or pointing at the
/// square or the strip does to them. <see cref="ColourPicker"/> draws this and feeds it the
/// mouse and the keyboard; the self-test drives it directly.
/// </summary>
/// <remarks>
/// <para>
/// The picker keeps its own hue and saturation rather than working them out again from the RGB
/// every time. That is the whole point of the class. Every grey has no hue, and black has neither
/// hue nor saturation, so a picker that re-derived them would snap the hue strip to red the moment
/// a drag touched the grey edge or the bottom of the square, and the colour would not come back
/// when the drag did. Here the square only ever sets saturation and value, the strip only ever
/// sets hue, and a colour that arrives from outside (the host, or typed into HEX, RGB or CMYK)
/// changes only the parts it actually says something about.
/// </para>
/// <para>
/// Alpha is carried, never edited. A colour set from outside brings its alpha with it and every
/// colour handed back keeps it; the fields show and take opaque colours only. The overlay dim is
/// the one theme role with alpha, and the picker is meant to change its colour and leave its
/// transparency alone.
/// </para>
/// </remarks>
public sealed class ColourPickerState
{
    /// <summary>The five text fields, in the order the picker lays them out.</summary>
    public enum Field { Hex, Rgb, Cmyk, Hsv, Hsl }

    /// <summary>Hue in degrees, 0 to 360. 360 is kept rather than folded to 0 so a handle dragged
    /// to the right end of the strip stays there; the colour is red either way.</summary>
    public double Hue { get; private set; }

    /// <summary>HSV saturation, 0 to 1: the square's horizontal position.</summary>
    public double Saturation { get; private set; }

    /// <summary>HSV value (brightness), 0 to 1: the square's vertical position, 1 at the top.</summary>
    public double Value { get; private set; }

    /// <summary>The alpha of the last colour set from outside. A new picker is opaque.</summary>
    public byte Alpha { get; private set; } = byte.MaxValue;

    /// <summary>The colour picked, opaque. What the swatch and every field describe.</summary>
    public Color OpaqueColour => ColourMaths.FromHsv(new Hsv(Hue, Saturation, Value));

    /// <summary>The colour picked, with the alpha it came in with. What the host gets back.</summary>
    public Color Colour
    {
        get
        {
            var c = OpaqueColour;
            return Color.FromArgb(Alpha, c.R, c.G, c.B);
        }
    }

    /// <summary>The current hue at full saturation and value: the top right corner of the square
    /// and the colour under the strip's handle.</summary>
    public Color PureHue => ColourMaths.FromHsv(new Hsv(Hue, 1, 1));

    /// <summary>The square handle's position as fractions of the square, 0,0 at the top left.</summary>
    public double SquareX => Saturation;

    /// <inheritdoc cref="SquareX"/>
    public double SquareY => 1 - Value;

    /// <summary>The strip handle's position as a fraction of the strip, 0 at the left (red).</summary>
    public double StripX => Hue / 360;

    // ---- from outside -------------------------------------------------------------------------

    /// <summary>
    /// A colour from the host. Takes its alpha, and its hue and saturation only where it has them.
    /// </summary>
    public void SetColour(Color colour)
    {
        Alpha = colour.A;
        SetRgb(colour);
    }

    /// <summary>
    /// The RGB part of a colour, alpha ignored. If it is the colour already showing, nothing
    /// moves: the host echoing back what the picker just gave it (a two-way binding does exactly
    /// that on every drag step) must not re-derive the hue and saturation from rounded bytes.
    /// </summary>
    private void SetRgb(Color colour)
    {
        var opaque = Color.FromRgb(colour.R, colour.G, colour.B);
        if (opaque == OpaqueColour) return;

        var hsv = ColourMaths.ToHsv(opaque);
        Value = hsv.V;
        // Black says nothing about hue or saturation, so both stay where they were: the handle
        // drops to the bottom of the square without sliding sideways.
        if (hsv.V == 0) return;
        Saturation = hsv.S;
        // A grey says nothing about hue. Its saturation really is 0, so the handle goes to the
        // left edge, but the strip stays put.
        if (hsv.S == 0) return;
        Hue = hsv.H;
    }

    // ---- the square and the strip -------------------------------------------------------------

    /// <summary>
    /// A point on the square, as fractions of its width and height from the top left. Anything
    /// outside is pulled back to the nearest edge, which is what a drag past the edge wants.
    /// </summary>
    public void SetSquare(double x, double y)
    {
        Saturation = Clamp01(x);
        Value = 1 - Clamp01(y);
    }

    /// <summary>A point on the hue strip, as a fraction of its width from the left.</summary>
    public void SetStrip(double x) => Hue = Clamp01(x) * 360;

    // ---- the fields ---------------------------------------------------------------------------

    /// <summary>
    /// What a field shows. RGB, HEX and CMYK describe the colour. HSV and HSL describe it too,
    /// except for the parts a colour can lose: the hue is always the strip's, and black's HSV
    /// saturation is the square's, so the numbers agree with the handles and typing them back
    /// changes nothing.
    /// </summary>
    public string Text(Field field)
    {
        var c = OpaqueColour;
        switch (field)
        {
            case Field.Hex: return ColourMaths.FormatHex(c);
            case Field.Rgb: return ColourMaths.FormatRgb(c);
            case Field.Cmyk: return ColourMaths.FormatCmyk(ColourMaths.ToCmyk(c));
            case Field.Hsv:
                var hsv = ColourMaths.ToHsv(c);
                return ColourMaths.FormatHsv(new Hsv(Hue, hsv.V == 0 ? Saturation : hsv.S, hsv.V));
            case Field.Hsl:
                var hsl = ColourMaths.ToHsl(c);
                return ColourMaths.FormatHsl(new Hsl(Hue, hsl.S, hsl.L));
            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    /// <summary>
    /// Text typed into a field, on Enter or on leaving it. False means it could not be read, and
    /// the field should go back to <see cref="Text"/>. Text the field already shows is accepted
    /// and changes nothing, so tabbing through the fields never nudges the colour by a rounding
    /// step. Alpha is never taken from the text: an eight-digit hex keeps the picker's own.
    /// </summary>
    public bool Commit(Field field, string? text)
    {
        if (text is null) return false;
        if (text.Trim() == Text(field)) return true;

        switch (field)
        {
            case Field.Hex:
                if (!ColourMaths.TryParseHex(text, out var hex)) return false;
                SetRgb(hex);
                return true;

            case Field.Rgb:
                if (!ColourMaths.TryParseRgb(text, out var rgb)) return false;
                SetRgb(rgb);
                return true;

            case Field.Cmyk:
                if (!ColourMaths.TryParseCmyk(text, out var cmyk)) return false;
                SetRgb(ColourMaths.FromCmyk(cmyk));
                return true;

            case Field.Hsv:
                // Taken as typed, hue and all, even for a grey: the hue someone types is the hue
                // they want the strip to show.
                if (!ColourMaths.TryParseHsv(text, out var hsv)) return false;
                Hue = hsv.H;
                Saturation = hsv.S;
                Value = hsv.V;
                return true;

            case Field.Hsl:
                if (!ColourMaths.TryParseHsl(text, out var hsl)) return false;
                // HSL to HSV in doubles, keeping the typed hue. Lightness 0 is black, where HSV
                // saturation means nothing, so the square's own stays.
                double v = hsl.L + hsl.S * Math.Min(hsl.L, 1 - hsl.L);
                Hue = hsl.H;
                if (v > 0) Saturation = Clamp01(2 * (1 - hsl.L / v));
                Value = Clamp01(v);
                return true;

            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    private static double Clamp01(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
}
