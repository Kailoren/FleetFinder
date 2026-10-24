using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using FleetView.Services;

namespace FleetView.Theming;

/// <summary>A theme as it travels in a share code: its name, the commander who made it (may be
/// empty), and every role's colour.</summary>
public sealed record SharedTheme(string Name, string Commander, IReadOnlyDictionary<string, Color> Colours);

/// <summary>Why a pasted share code could not be used.</summary>
public enum ThemeCodeProblem
{
    None,
    Empty,
    NotACode,
    BadCharacter,
    Incomplete,
    Newer,
    TooLong,
    Damaged,
    NoName,
}

/// <summary>What <see cref="ThemeCode.Decode"/> made of a paste: the theme, or the reason there is
/// none and a sentence to show for it.</summary>
public sealed record ThemeCodeResult(SharedTheme? Theme, ThemeCodeProblem Problem)
{
    public bool Ok => Theme != null;
    public string Message => ThemeCode.Describe(Problem);
}

/// <summary>
/// Turns a theme into one line of text that can be pasted to someone else, and back. No server is
/// involved: the code is the theme, so it works offline and forever.
/// </summary>
/// <remarks>
/// <para>
/// A code is <c>FFTheme</c>, one character for the format version (<c>1</c>), then the theme in
/// Base58. Every character after the prefix is a letter or a digit, so Discord has nothing to
/// read as formatting (it eats <c>_ * ~</c> and backticks) and a double-click selects the whole
/// code. Base58 is the Bitcoin alphabet, which leaves out 0, O, I and l, the characters that look
/// like each other.
/// </para>
/// <para>
/// The bytes under the Base58, version 1:
/// name length, name (UTF-8); commander length, commander (UTF-8); a palette size P; then either
/// P colours (three bytes each) followed by one palette index per role, packed into the fewest
/// bits that hold P-1 (zero bits when every role is one colour), or, when P is 0, every role's
/// three bytes in order; then one alpha byte for each role in <see cref="V1AlphaKeys"/>; then the
/// first four bytes of the SHA-256 of the prefix, the version character and everything before it.
/// A theme rarely uses more than about twenty distinct colours, which is what makes the palette
/// form short (Ali's theme is about 220 characters). A theme whose colours are nearly all
/// different would be longer that way, so the encoder writes whichever form is shorter.
/// </para>
/// <para>
/// There is deliberately no compression stage. The palette already does better than Deflate on
/// data this small, and without a decompressor there is no way to make a code that unpacks into
/// something huge: the most a code can ask of the decoder is the 1,000 characters it is allowed
/// to be.
/// </para>
/// <para>
/// The role order and the alpha roles are frozen here for version 1 (<see cref="V1Keys"/>) and
/// never follow <see cref="ThemeRoles"/>, which can be reordered or grow. Reading a v1 code in a
/// later build, a role added since gets its default, and a role since retired is skipped. A later
/// format gets its own version character and list, and the v1 reader stays as it is.
/// </para>
/// <para>
/// Reading never throws. Everything a paste can be, from empty to a megabyte of junk, a code cut
/// off halfway, one with a character changed, or one from a newer build, comes back as a
/// <see cref="ThemeCodeProblem"/> with a sentence for it. The name and commander are a stranger's
/// text, so they go through <see cref="ThemeText"/>, and every colour through the same check
/// theme.json uses.
/// </para>
/// </remarks>
public static class ThemeCode
{
    public const string Prefix = "FFTheme";
    public const char Version1 = '1';

    /// <summary>Pasted text longer than this is refused before it is even searched.</summary>
    private const int MaxInputLength = 100_000;

    /// <summary>
    /// Longest code accepted. The longest a v1 code can be is about 700 characters: every colour
    /// different, and both names at their cap in characters that take three bytes each. Checked
    /// before the Base58 is decoded, which is the one step whose cost grows faster than the text.
    /// </summary>
    private const int MaxCodeLength = 1_000;

    private const int ChecksumLength = 4;

    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The roles a v1 code carries, in the order it carries them. This is the role list as it was
    /// when the format was made (2026-10-24, 93 roles), copied rather than read from
    /// <see cref="ThemeRoles"/>, and it must never change: every v1 code ever shared depends on it.
    /// The self-test pins it.
    /// </summary>
    public static IReadOnlyList<string> V1Keys { get; } =
    [
        "WindowBackground", "MainText", "MutedText",
        "TitleBarBackground", "TitleBarText", "TitleBarVersion", "CaptionButtonGlyph",
        "CaptionButtonHover", "CaptionButtonHoverGlyph", "CloseButtonHover",
        "CloseButtonHoverGlyph",
        "BannerBackground", "BannerText", "BannerButtonBackground", "BannerButtonText",
        "TabText", "TabHoverBackground", "SelectedTabBackground", "SelectedTabOutline",
        "SelectedTabText",
        "PanelBackground", "PanelBorder", "PanelText", "Divider", "TotalsBoxBackground",
        "TotalsBoxBorder",
        "GridBackground", "GridBorder", "GridRow", "GridAlternateRow", "GridText", "GridLines",
        "GridHeaderBackground", "GridHeaderText", "GridHeaderLines", "GroupHeaderText",
        "RowOutline",
        "NeededCount", "HaveCount", "GoodValue", "BadValue", "UnknownValue", "LinkText",
        "LinkHoverText", "SummaryText", "UnmatchedText", "BarterResult", "BarterPoints",
        "ButtonBackground", "ButtonText", "ButtonDisabledBackground", "ButtonDisabledText",
        "SecondaryButtonBackground", "SecondaryButtonText", "StepperBackground", "StepperText",
        "StepperHover",
        "InputBackground", "InputText", "InputBorder", "InputHoverBorder", "InputFocusBorder",
        "CheckboxBackground", "CheckboxBorder", "CheckboxHoverBorder", "CheckboxTicked",
        "TextSelection",
        "ToolTipBackground", "ToolTipBorder", "ToolTipText", "MenuBackground", "MenuBorder",
        "MenuText", "MenuDisabledText", "MenuHighlight", "MenuHighlightBorder",
        "MenuDisabledHighlight", "MenuDisabledHighlightBorder", "MenuGutter", "MenuGutterLine",
        "MenuGutterLineHighlight", "MenuShadow",
        "ScrollbarThumb", "ScrollbarThumbHover", "ProgressTrack", "ProgressFill",
        "StatusBarBackground", "StatusText", "StatusDetailText",
        "OverlayDim", "DialogBackground", "DialogBorder", "DialogTitle",
    ];

    /// <summary>The v1 roles whose alpha travels with them, in the order their alpha bytes come.
    /// Every other role is carried as opaque RGB. Frozen like <see cref="V1Keys"/>.</summary>
    public static IReadOnlyList<string> V1AlphaKeys { get; } =
    [
        "MenuHighlight", "MenuDisabledHighlight", "MenuDisabledHighlightBorder", "MenuShadow", "OverlayDim",
    ];

    private static readonly HashSet<string> V1Alpha = new(V1AlphaKeys, StringComparer.Ordinal);

    /// <summary>The sentence to show for a problem, written for whoever pasted the code.</summary>
    public static string Describe(ThemeCodeProblem problem) => problem switch
    {
        ThemeCodeProblem.None => "",
        ThemeCodeProblem.Empty => "Paste a theme code first.",
        ThemeCodeProblem.NotACode => "That isn't a FleetFinder theme code. Theme codes start with FFTheme.",
        ThemeCodeProblem.BadCharacter => "That theme code has a character in it that theme codes never use. Copy it again in one piece.",
        ThemeCodeProblem.Incomplete => "That isn't a complete FleetFinder theme code. Copy it again in one piece.",
        ThemeCodeProblem.Newer => "This theme was made by a newer FleetFinder. Update FleetFinder to use it.",
        ThemeCodeProblem.TooLong => "That's far too long to be a FleetFinder theme code.",
        ThemeCodeProblem.NoName => "That theme code has no theme name in it, so it can't be used.",
        _ => "That theme code is damaged and can't be used.",
    };

    // ---- writing --------------------------------------------------------------------------------

    /// <summary>
    /// The share code for a theme. The name and commander are cleaned as for saving; roles the map
    /// leaves out are written at their default. Throws <see cref="ArgumentException"/> only when the
    /// name is empty once cleaned, which the theme library never lets a saved theme be.
    /// </summary>
    public static string Encode(string name, string commander, IReadOnlyDictionary<string, Color> colours)
    {
        var cleanName = ThemeText.CleanName(name);
        if (cleanName.Length == 0)
            throw new ArgumentException("A theme needs a name before it can be shared.", nameof(name));

        var body = new List<byte>(320);
        WriteText(body, cleanName);
        WriteText(body, ThemeText.CleanCommander(commander));

        int count = V1Keys.Count;
        var rgb = new int[count];
        var alphas = new List<byte>(V1AlphaKeys.Count);
        for (int i = 0; i < count; i++)
        {
            var key = V1Keys[i];
            // A key no longer in ThemeRoles has no colour to send; a v1 reader of this build skips
            // it anyway. Zero bytes keep the layout.
            var c = colours.TryGetValue(key, out var given) ? given : ThemeRoles.Find(key)?.Default ?? default;
            rgb[i] = (c.R << 16) | (c.G << 8) | c.B;
        }
        foreach (var key in V1AlphaKeys)
        {
            var c = colours.TryGetValue(key, out var given) ? given : ThemeRoles.Find(key)?.Default ?? default;
            alphas.Add(c.A);
        }

        // Palette in order of first use, so the same theme always gives the same code.
        var palette = new List<int>();
        var index = new Dictionary<int, int>();
        foreach (var v in rgb)
            if (index.TryAdd(v, palette.Count)) palette.Add(v);

        int bits = BitsFor(palette.Count);
        int paletteForm = 3 * palette.Count + (count * bits + 7) / 8;
        if (paletteForm < 3 * count)
        {
            body.Add((byte)palette.Count);
            foreach (var v in palette) WriteRgb(body, v);
            var packed = new byte[(count * bits + 7) / 8];
            int bit = 0;
            foreach (var v in rgb)
            {
                int ix = index[v];
                for (int b = bits - 1; b >= 0; b--, bit++)
                    if (((ix >> b) & 1) != 0) packed[bit >> 3] |= (byte)(0x80 >> (bit & 7));
            }
            body.AddRange(packed);
        }
        else
        {
            body.Add(0);
            foreach (var v in rgb) WriteRgb(body, v);
        }
        body.AddRange(alphas);

        var bytes = body.ToArray();
        var withSum = new byte[bytes.Length + ChecksumLength];
        bytes.CopyTo(withSum, 0);
        Checksum(Version1, bytes).CopyTo(withSum, bytes.Length);
        return Prefix + Version1 + ToBase58(withSum);
    }

    private static void WriteText(List<byte> body, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        // ThemeText caps both at 40 and 32 UTF-16 units, which is at most 120 bytes of UTF-8.
        body.Add(checked((byte)bytes.Length));
        body.AddRange(bytes);
    }

    private static void WriteRgb(List<byte> body, int v)
    {
        body.Add((byte)(v >> 16));
        body.Add((byte)(v >> 8));
        body.Add((byte)v);
    }

    // ---- reading --------------------------------------------------------------------------------

    /// <summary>
    /// Reads a pasted code. The paste may have other text around it ("here's mine: FFTheme1..."):
    /// the code is the first run of letters and digits that starts with <see cref="Prefix"/>.
    /// Never throws.
    /// </summary>
    public static ThemeCodeResult Decode(string? text)
    {
        try
        {
            return DecodeCore(text);
        }
        catch (Exception ex)
        {
            // Every expected way for a code to be wrong is handled below without an exception;
            // this is the guarantee that an unexpected one still reaches the user as a sentence.
            DiagnosticLog.Note($"Theme code could not be read ({ex.GetType().Name}).");
            return new ThemeCodeResult(null, ThemeCodeProblem.Damaged);
        }
    }

    private static ThemeCodeResult DecodeCore(string? text)
    {
        static ThemeCodeResult Fail(ThemeCodeProblem p) => new(null, p);

        if (string.IsNullOrWhiteSpace(text)) return Fail(ThemeCodeProblem.Empty);
        if (text.Length > MaxInputLength) return Fail(ThemeCodeProblem.TooLong);

        int start = text.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0) return Fail(ThemeCodeProblem.NotACode);
        int end = start;
        while (end < text.Length && char.IsAsciiLetterOrDigit(text[end])) end++;
        int length = end - start;

        if (length > MaxCodeLength) return Fail(ThemeCodeProblem.TooLong);
        if (length == Prefix.Length) return Fail(ThemeCodeProblem.Incomplete);

        char version = text[start + Prefix.Length];
        if (version != Version1)
            return Fail(version is (>= '2' and <= '9') or (>= 'A' and <= 'Z')
                ? ThemeCodeProblem.Newer
                : ThemeCodeProblem.NotACode);

        var payload = text.AsSpan(start + Prefix.Length + 1, length - Prefix.Length - 1);
        if (payload.IsEmpty) return Fail(ThemeCodeProblem.Incomplete);
        if (!TryFromBase58(payload, out var bytes)) return Fail(ThemeCodeProblem.BadCharacter);
        if (bytes.Length <= ChecksumLength) return Fail(ThemeCodeProblem.Incomplete);

        ReadOnlySpan<byte> body = bytes.AsSpan(0, bytes.Length - ChecksumLength);
        ReadOnlySpan<byte> sum = bytes.AsSpan(bytes.Length - ChecksumLength);
        ReadOnlySpan<byte> expected = Checksum(Version1, body);
        if (!sum.SequenceEqual(expected)) return Fail(ThemeCodeProblem.Incomplete);

        // From here the bytes are exactly what some encoder wrote, so anything wrong with them was
        // put there on purpose or by a different program, not by a bad paste.
        if (!TryReadV1(body, out var rawName, out var rawCommander, out var colours))
            return Fail(ThemeCodeProblem.Damaged);

        var name = ThemeText.CleanName(rawName);
        if (name.Length == 0) return Fail(ThemeCodeProblem.NoName);
        return new ThemeCodeResult(new SharedTheme(name, ThemeText.CleanCommander(rawCommander), colours),
            ThemeCodeProblem.None);
    }

    private static bool TryReadV1(ReadOnlySpan<byte> body, out string name, out string commander,
        out Dictionary<string, Color> colours)
    {
        name = commander = "";
        colours = ThemeRoles.Defaults();
        int pos = 0;

        if (!TryReadText(body, ref pos, out name) || !TryReadText(body, ref pos, out commander)) return false;
        if (pos >= body.Length) return false;

        int count = V1Keys.Count;
        var rgb = new int[count];
        int paletteSize = body[pos++];
        if (paletteSize == 0)
        {
            if (body.Length - pos < 3 * count) return false;
            for (int i = 0; i < count; i++, pos += 3)
                rgb[i] = (body[pos] << 16) | (body[pos + 1] << 8) | body[pos + 2];
        }
        else
        {
            // The encoder never writes a palette with more colours than there are roles.
            if (paletteSize > count || body.Length - pos < 3 * paletteSize) return false;
            var palette = new int[paletteSize];
            for (int i = 0; i < paletteSize; i++, pos += 3)
                palette[i] = (body[pos] << 16) | (body[pos + 1] << 8) | body[pos + 2];

            int bits = BitsFor(paletteSize);
            int packedLength = (count * bits + 7) / 8;
            if (body.Length - pos < packedLength) return false;
            var packed = body.Slice(pos, packedLength);
            int bit = 0;
            for (int i = 0; i < count; i++)
            {
                int ix = 0;
                for (int b = 0; b < bits; b++, bit++)
                    ix = (ix << 1) | ((packed[bit >> 3] >> (7 - (bit & 7))) & 1);
                if (ix >= paletteSize) return false;
                rgb[i] = palette[ix];
            }
            // The bits after the last index are always zero; anything else was not written by us.
            for (; bit < packedLength * 8; bit++)
                if (((packed[bit >> 3] >> (7 - (bit & 7))) & 1) != 0) return false;
            pos += packedLength;
        }

        if (body.Length - pos != V1AlphaKeys.Count) return false;
        var alpha = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var key in V1AlphaKeys) alpha[key] = body[pos++];

        for (int i = 0; i < count; i++)
        {
            var key = V1Keys[i];
            var role = ThemeRoles.Find(key);
            if (role is null) continue;
            bool hasAlpha = V1Alpha.Contains(key);
            var c = Color.FromArgb(hasAlpha ? alpha[key] : (byte)255, (byte)(rgb[i] >> 16), (byte)(rgb[i] >> 8), (byte)rgb[i]);
            if (ThemeStore.TryAccept(role, c, hasAlpha, out var accepted)) colours[key] = accepted;
        }
        return true;
    }

    private static bool TryReadText(ReadOnlySpan<byte> body, ref int pos, out string text)
    {
        text = "";
        if (pos >= body.Length) return false;
        int length = body[pos++];
        if (body.Length - pos < length) return false;
        try
        {
            text = StrictUtf8.GetString(body.Slice(pos, length));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        pos += length;
        return true;
    }

    // ---- shared ---------------------------------------------------------------------------------

    /// <summary>Bits per palette index: enough to count to size - 1, and none at all for a
    /// palette of one.</summary>
    private static int BitsFor(int size) => size <= 1 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)(size - 1));

    /// <summary>Covers the prefix and version character as well as the bytes, so a code whose
    /// version character was changed fails here rather than being read as a different format.</summary>
    private static byte[] Checksum(char version, ReadOnlySpan<byte> body)
    {
        var head = Encoding.ASCII.GetBytes(Prefix + version);
        var all = new byte[head.Length + body.Length];
        head.CopyTo(all, 0);
        body.CopyTo(all.AsSpan(head.Length));
        return SHA256.HashData(all)[..ChecksumLength];
    }

    private static string ToBase58(byte[] data)
    {
        int zeros = 0;
        while (zeros < data.Length && data[zeros] == 0) zeros++;
        var value = new BigInteger(data, isUnsigned: true, isBigEndian: true);
        var digits = new StringBuilder();
        while (!value.IsZero)
        {
            value = BigInteger.DivRem(value, 58, out var remainder);
            digits.Append(Alphabet[(int)remainder]);
        }
        // Leading zero bytes have no value of their own, so each is written as the zero digit.
        digits.Append('1', zeros);
        var chars = digits.ToString().ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    private static bool TryFromBase58(ReadOnlySpan<char> text, out byte[] bytes)
    {
        bytes = [];
        var value = BigInteger.Zero;
        foreach (char c in text)
        {
            int digit = Alphabet.IndexOf(c);
            if (digit < 0) return false;
            value = value * 58 + digit;
        }
        int zeros = 0;
        while (zeros < text.Length && text[zeros] == '1') zeros++;
        byte[] tail = value.IsZero ? [] : value.ToByteArray(isUnsigned: true, isBigEndian: true);
        bytes = new byte[zeros + tail.Length];
        tail.CopyTo(bytes, zeros);
        return true;
    }
}
