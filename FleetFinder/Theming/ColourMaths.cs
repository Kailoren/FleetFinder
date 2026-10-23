using System.Globalization;
using System.Windows.Media;

namespace FleetView.Theming;

/// <summary>Hue in degrees, 0 up to (not including) 360; saturation and value 0 to 1.</summary>
public readonly record struct Hsv(double H, double S, double V);

/// <summary>Hue in degrees, 0 up to (not including) 360; saturation and lightness 0 to 1.</summary>
public readonly record struct Hsl(double H, double S, double L);

/// <summary>Cyan, magenta, yellow and black, each 0 to 1.</summary>
public readonly record struct Cmyk(double C, double M, double Y, double K);

/// <summary>
/// Conversions between the colour models the picker shows (hex, RGB, HSV, HSL, CMYK), and the
/// text forms it shows them in.
/// </summary>
/// <remarks>
/// <para>
/// The conversions work in doubles and only round when going back to bytes, so any 24-bit colour
/// survives a trip out to HSV, HSL or CMYK and back exactly. The self-test checks that across the
/// whole colour cube. The display text is rounded to whole numbers, so it does not round trip
/// exactly, and is not meant to: the picker keeps its own hue and saturation rather than
/// re-deriving them from the text or from the RGB, which is what stops a drag through grey from
/// throwing the hue away.
/// </para>
/// <para>
/// The display forms match the reference picker the design was drawn from: HEX <c>#fcba03</c>,
/// RGB <c>252, 186, 3</c>, CMYK <c>0%, 26%, 99%, 1%</c>, HSV <c>44°, 99%, 99%</c>, HSL
/// <c>44°, 98%, 50%</c>. Parsing takes the same text with or without the <c>%</c> and <c>°</c>
/// signs, separated by commas or spaces.
/// </para>
/// <para>
/// Alpha is not part of any of this. A role whose default carries alpha (the overlay dim) keeps
/// it, and only its colour is edited, so every parse here produces an opaque colour except the
/// eight-digit hex form, which says what it wants.
/// </para>
/// </remarks>
public static class ColourMaths
{
    // ---- hex ------------------------------------------------------------------------------------

    /// <summary>
    /// Reads <c>#rgb</c>, <c>#rrggbb</c> or <c>#aarrggbb</c>, with or without the <c>#</c>, either
    /// case, surrounding spaces ignored.
    /// </summary>
    public static bool TryParseHex(string? text, out Color colour) => TryParseHex(text, out colour, out _);

    /// <summary>As <see cref="TryParseHex(string?, out Color)"/>, also saying whether the text
    /// carried its own alpha (the eight-digit form). The theme store needs to know, because a
    /// six-digit value for a role with alpha keeps that role's alpha rather than going opaque.</summary>
    public static bool TryParseHex(string? text, out Color colour, out bool hadAlpha)
    {
        colour = default;
        hadAlpha = false;
        if (text is null) return false;

        var s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        if (s.Length is not (3 or 6 or 8) || !s.All(Uri.IsHexDigit)) return false;

        if (s.Length == 3)
        {
            // Each digit doubles up: #fa3 is #ffaa33.
            byte Nibble(char c) => (byte)(Convert.ToByte(c.ToString(), 16) * 17);
            colour = Color.FromRgb(Nibble(s[0]), Nibble(s[1]), Nibble(s[2]));
            return true;
        }

        uint v = uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (s.Length == 6)
        {
            colour = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }

        colour = Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        hadAlpha = true;
        return true;
    }

    /// <summary>The picker's hex text: <c>#fcba03</c>, lower case, never with alpha.</summary>
    public static string FormatHex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>
    /// The form written to theme.json: <c>#RRGGBB</c>, or <c>#AARRGGBB</c> for a role with alpha.
    /// Upper case, the way MainWindow.xaml always wrote its colours, so a value from one can be
    /// searched for in the other.
    /// </summary>
    public static string ToStorageHex(Color c, bool withAlpha) =>
        withAlpha ? $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ---- RGB ------------------------------------------------------------------------------------

    /// <summary><c>252, 186, 3</c>.</summary>
    public static string FormatRgb(Color c) => $"{c.R}, {c.G}, {c.B}";

    /// <summary>Three whole numbers 0 to 255. A fraction is refused rather than rounded, since no
    /// RGB field anywhere shows one.</summary>
    public static bool TryParseRgb(string? text, out Color colour)
    {
        colour = default;
        if (!TryParseNumbers(text, 3, out var n)) return false;
        foreach (var x in n)
            if (x < 0 || x > 255 || x != Math.Floor(x)) return false;
        colour = Color.FromRgb((byte)n[0], (byte)n[1], (byte)n[2]);
        return true;
    }

    // ---- HSV ------------------------------------------------------------------------------------

    public static Hsv ToHsv(Color c)
    {
        var (r, g, b, max, min) = Channels(c);
        double delta = max - min;
        return new Hsv(Hue(r, g, b, max, delta), max == 0 ? 0 : delta / max, max);
    }

    public static Color FromHsv(Hsv hsv)
    {
        double s = Clamp01(hsv.S), v = Clamp01(hsv.V);
        double chroma = v * s;
        return FromHueChroma(NormaliseHue(hsv.H), chroma, v - chroma);
    }

    /// <summary><c>44°, 99%, 99%</c>.</summary>
    public static string FormatHsv(Hsv hsv) => $"{Degrees(hsv.H)}°, {Percent(hsv.S)}%, {Percent(hsv.V)}%";

    public static bool TryParseHsv(string? text, out Hsv hsv)
    {
        hsv = default;
        if (!TryParseNumbers(text, 3, out var n) || !IsHue(n[0]) || !IsPercent(n[1]) || !IsPercent(n[2]))
            return false;
        hsv = new Hsv(NormaliseHue(n[0]), n[1] / 100, n[2] / 100);
        return true;
    }

    // ---- HSL ------------------------------------------------------------------------------------

    public static Hsl ToHsl(Color c)
    {
        var (r, g, b, max, min) = Channels(c);
        double delta = max - min;
        double l = (max + min) / 2;
        // Saturation is undefined for black and white; delta is 0 for every grey, which covers them.
        double s = delta == 0 ? 0 : Clamp01(delta / (1 - Math.Abs(2 * l - 1)));
        return new Hsl(Hue(r, g, b, max, delta), s, l);
    }

    public static Color FromHsl(Hsl hsl)
    {
        double s = Clamp01(hsl.S), l = Clamp01(hsl.L);
        double chroma = (1 - Math.Abs(2 * l - 1)) * s;
        return FromHueChroma(NormaliseHue(hsl.H), chroma, l - chroma / 2);
    }

    /// <summary><c>44°, 98%, 50%</c>.</summary>
    public static string FormatHsl(Hsl hsl) => $"{Degrees(hsl.H)}°, {Percent(hsl.S)}%, {Percent(hsl.L)}%";

    public static bool TryParseHsl(string? text, out Hsl hsl)
    {
        hsl = default;
        if (!TryParseNumbers(text, 3, out var n) || !IsHue(n[0]) || !IsPercent(n[1]) || !IsPercent(n[2]))
            return false;
        hsl = new Hsl(NormaliseHue(n[0]), n[1] / 100, n[2] / 100);
        return true;
    }

    // ---- CMYK -----------------------------------------------------------------------------------

    /// <summary>The plain screen formula, no ink profile: black is all K, white is all zero.</summary>
    public static Cmyk ToCmyk(Color c)
    {
        var (r, g, b, max, _) = Channels(c);
        double k = 1 - max;
        if (max == 0) return new Cmyk(0, 0, 0, 1);
        return new Cmyk((1 - r - k) / max, (1 - g - k) / max, (1 - b - k) / max, k);
    }

    public static Color FromCmyk(Cmyk cmyk)
    {
        double k = Clamp01(cmyk.K);
        return Color.FromRgb(
            ToByte((1 - Clamp01(cmyk.C)) * (1 - k)),
            ToByte((1 - Clamp01(cmyk.M)) * (1 - k)),
            ToByte((1 - Clamp01(cmyk.Y)) * (1 - k)));
    }

    /// <summary><c>0%, 26%, 99%, 1%</c>.</summary>
    public static string FormatCmyk(Cmyk c) =>
        $"{Percent(c.C)}%, {Percent(c.M)}%, {Percent(c.Y)}%, {Percent(c.K)}%";

    public static bool TryParseCmyk(string? text, out Cmyk cmyk)
    {
        cmyk = default;
        if (!TryParseNumbers(text, 4, out var n) || !n.All(IsPercent)) return false;
        cmyk = new Cmyk(n[0] / 100, n[1] / 100, n[2] / 100, n[3] / 100);
        return true;
    }

    // ---- shared ---------------------------------------------------------------------------------

    private static (double R, double G, double B, double Max, double Min) Channels(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        return (r, g, b, Math.Max(r, Math.Max(g, b)), Math.Min(r, Math.Min(g, b)));
    }

    /// <summary>Hue in degrees from the largest channel. 0 for any grey, where hue means nothing;
    /// the picker holds on to its own hue for exactly that case.</summary>
    private static double Hue(double r, double g, double b, double max, double delta)
    {
        if (delta == 0) return 0;
        double h = max == r ? (g - b) / delta
                 : max == g ? (b - r) / delta + 2
                 : (r - g) / delta + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    /// <summary>Hue, chroma and the amount added to every channel, back to RGB. HSV and HSL differ
    /// only in how they arrive at chroma and that offset.</summary>
    private static Color FromHueChroma(double hue, double chroma, double offset)
    {
        double sector = hue / 60;
        double x = chroma * (1 - Math.Abs(sector % 2 - 1));
        var (r, g, b) = (int)sector switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        return Color.FromRgb(ToByte(r + offset), ToByte(g + offset), ToByte(b + offset));
    }

    private static byte ToByte(double unit) =>
        (byte)Math.Round(Clamp01(unit) * 255, MidpointRounding.AwayFromZero);

    private static double Clamp01(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);

    /// <summary>Any hue folded into 0 up to 360, so 360 and 0 are both red.</summary>
    private static double NormaliseHue(double h)
    {
        if (!double.IsFinite(h)) return 0;
        h %= 360;
        return h < 0 ? h + 360 : h;
    }

    /// <summary>Whole degrees for display. A hue of 359.6 rounds to 360, which is shown as 0 so
    /// the field never reads a value its own parser would fold anyway.</summary>
    private static int Degrees(double h)
    {
        int d = (int)Math.Round(NormaliseHue(h), MidpointRounding.AwayFromZero);
        return d == 360 ? 0 : d;
    }

    private static int Percent(double unit) =>
        (int)Math.Round(Clamp01(unit) * 100, MidpointRounding.AwayFromZero);

    private static bool IsHue(double v) => v >= 0 && v <= 360;

    private static bool IsPercent(double v) => v >= 0 && v <= 100;

    /// <summary>
    /// Exactly <paramref name="count"/> numbers separated by commas, semicolons or spaces, each
    /// allowed a trailing <c>%</c> or <c>°</c>. Invariant culture, so a decimal point is always a
    /// full stop; a comma is always a separator.
    /// </summary>
    private static bool TryParseNumbers(string? text, int count, out double[] numbers)
    {
        numbers = [];
        if (string.IsNullOrWhiteSpace(text)) return false;

        var tokens = text.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().TrimEnd('%', '°').Trim())
            .Where(t => t.Length > 0)
            .ToArray();
        if (tokens.Length != count) return false;

        numbers = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (!double.TryParse(tokens[i], NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out numbers[i]) || !double.IsFinite(numbers[i]))
                return false;
        }
        return true;
    }
}
