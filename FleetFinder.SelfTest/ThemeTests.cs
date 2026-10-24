using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using FleetView.Services;
using FleetView.Theming;

// The D1 sections (2026-10-24): Ali's colours as the shipped defaults and the right-click menu
// labels, then the logic under saved and shared themes. Everything that touches the disk does it
// in the suite's own temp folder.
internal sealed partial class SelfTest
{
    // ---- shipped defaults and labels ------------------------------------------------------------

    /// <summary>The 17 roles where Ali's palette (his theme.json of 2026-10-23) differs from the
    /// look the app first shipped with.</summary>
    private static readonly (string Key, string Hex)[] AliChanges =
    [
        ("CloseButtonHover", "#FF7500"), ("CloseButtonHoverGlyph", "#000000"),
        ("InputHoverBorder", "#FF7500"), ("InputFocusBorder", "#FF7500"), ("TextSelection", "#CF8613"),
        ("ToolTipBackground", "#120E08"), ("ToolTipBorder", "#FF7500"), ("ToolTipText", "#F6F4F2"),
        ("MenuBackground", "#000000"), ("MenuBorder", "#FF7500"), ("MenuText", "#FF7500"),
        ("MenuDisabledText", "#7A4F2A"), ("MenuHighlight", "#3D000000"), ("MenuHighlightBorder", "#FF7500"),
        ("MenuGutter", "#000000"), ("MenuGutterLine", "#FF7500"), ("MenuGutterLineHighlight", "#FF7500"),
    ];

    private void DefaultsTests()
    {
        foreach (var (key, hex) in AliChanges)
            Equal($"{key} ships as Ali's {hex}", Hex(hex), ThemeRoles.Find(key)!.Default);

        // His snapshot is kept out of git, so it is only here on his machine.
        var snapshot = RepoFile("ali-theme-2026-10-24.json");
        if (File.Exists(snapshot))
        {
            var saved = ThemeStore.Load(snapshot);
            var differ = ThemeRoles.All.Where(r => saved[r.Key] != r.Default).Select(r => r.Key).ToList();
            Check("every default is the colour in Ali's saved theme", differ.Count == 0, string.Join(", ", differ));
        }
        else
        {
            Console.WriteLine("   (Ali's theme snapshot is not here, so the full comparison is skipped)");
        }
        Equal("the same five roles still carry alpha",
            "MenuHighlight,MenuDisabledHighlight,MenuDisabledHighlightBorder,MenuShadow,OverlayDim",
            string.Join(",", ThemeRoles.All.Where(r => r.HasAlpha).Select(r => r.Key)));

        const string heading = "Tooltips and right-click menu";
        Check("the heading says right-click menu", ThemeRoles.Groups.Contains(heading) && !ThemeRoles.Groups.Contains("Tooltips and menus"));
        var menu = ThemeRoles.All.Where(r => r.Key.StartsWith("Menu", StringComparison.Ordinal)).ToList();
        Equal("there are twelve menu roles", 12, menu.Count);
        Check("all under that heading", menu.All(r => r.Group == heading));
        Check("and every one of their labels says right-click menu",
            menu.All(r => r.Label.Contains("right-click menu", StringComparison.OrdinalIgnoreCase)),
            string.Join(", ", menu.Where(r => !r.Label.Contains("right-click menu", StringComparison.OrdinalIgnoreCase)).Select(r => r.Label)));
        Check("no label under it starts with a bare Menu",
            ThemeRoles.All.Where(r => r.Group == heading).All(r => !r.Label.StartsWith("Menu", StringComparison.Ordinal)));
        Equal("for example the highlight", "Right-click menu item under the mouse", ThemeRoles.Find("MenuHighlight")!.Label);
        Equal("and the greyed one", "Right-click menu, greyed item under the mouse", ThemeRoles.Find("MenuDisabledHighlight")!.Label);
        Check("every menu label starts with Right-click menu",
            menu.All(r => r.Label.StartsWith("Right-click menu", StringComparison.Ordinal)),
            string.Join(", ", menu.Where(r => !r.Label.StartsWith("Right-click menu", StringComparison.Ordinal)).Select(r => r.Label)));
        Equal("the tooltips keep their labels", "Tooltip background", ThemeRoles.Find("ToolTipBackground")!.Label);
        Check("no label or note has an em dash",
            ThemeRoles.All.All(r => !r.Label.Contains('\u2014') && !(r.Note ?? "").Contains('\u2014')));
    }

    // ---- theme text -------------------------------------------------------------------------------

    private void ThemeTextTests()
    {
        Equal("line breaks and tabs become single spaces", "Red and Blue", ThemeText.CleanName("  Red\tand\r\n\r\nBlue  "));
        Equal("a right-to-left override is dropped", "Evilgnirts", ThemeText.CleanName("Evil\u202Egnirts"));
        Equal("isolates are dropped", "ab", ThemeText.CleanName("a\u2066\u2069b"));
        Equal("zero-width characters are dropped", "ab", ThemeText.CleanName("a\u200B\u200C\u200D\uFEFFb"));
        Equal("a soft hyphen is dropped", "ab", ThemeText.CleanName("a\u00ADb"));
        Equal("control characters are dropped", "ab", ThemeText.CleanName("a\u0007\u001B\u0000b"));
        Equal("line and paragraph separators become spaces", "a b", ThemeText.CleanName("a\u2028\u2029b"));
        Equal("invisible tag characters after an emoji are dropped", "flag \U0001F3F4", ThemeText.CleanName("flag \U0001F3F4\U000E0067\U000E0062\U000E007F"));
        Equal("a lone surrogate is dropped", "ab", ThemeText.CleanName("a\uD800b"));
        Equal("ordinary emoji and accents stay", "Gr\u00FCn \U0001F525", ThemeText.CleanName("Gr\u00FCn \U0001F525"));
        Equal("names stop at 40", 40, ThemeText.CleanName(new string('n', 60)).Length);
        Equal("and a cut never splits an emoji", new string('a', 39), ThemeText.CleanName(new string('a', 39) + "\U0001F600 more"));
        Equal("a name of nothing visible is empty", "", ThemeText.CleanName("\u202E\u200B \t\r\n"));
        Equal("null is empty", "", ThemeText.CleanName(null));
        Equal("a CMDR prefix is dropped", "Kailoren", ThemeText.CleanCommander("CMDR Kailoren"));
        Equal("whatever its case or spacing", "Kailoren", ThemeText.CleanCommander("  cmdr \t Kailoren"));
        Equal("CMDR on its own stays", "CMDR", ThemeText.CleanCommander("CMDR"));
        Equal("commanders stop at 32", 32, ThemeText.CleanCommander(new string('c', 50)).Length);
        Equal("a commander can be empty", "", ThemeText.CleanCommander(""));
    }

    // ---- share code --------------------------------------------------------------------------------

    /// <summary>The v1 role order as it was when the format was made. This must never change: every
    /// code shared since depends on it. If this check fails, ThemeCode.V1Keys was edited.</summary>
    private const string PinnedV1Keys =
        "WindowBackground,MainText,MutedText,TitleBarBackground,TitleBarText,TitleBarVersion,CaptionButtonGlyph," +
        "CaptionButtonHover,CaptionButtonHoverGlyph,CloseButtonHover,CloseButtonHoverGlyph,BannerBackground,BannerText," +
        "BannerButtonBackground,BannerButtonText,TabText,TabHoverBackground,SelectedTabBackground,SelectedTabOutline," +
        "SelectedTabText,PanelBackground,PanelBorder,PanelText,Divider,TotalsBoxBackground,TotalsBoxBorder," +
        "GridBackground,GridBorder,GridRow,GridAlternateRow,GridText,GridLines,GridHeaderBackground,GridHeaderText," +
        "GridHeaderLines,GroupHeaderText,RowOutline,NeededCount,HaveCount,GoodValue,BadValue,UnknownValue,LinkText," +
        "LinkHoverText,SummaryText,UnmatchedText,BarterResult,BarterPoints,ButtonBackground,ButtonText," +
        "ButtonDisabledBackground,ButtonDisabledText,SecondaryButtonBackground,SecondaryButtonText,StepperBackground," +
        "StepperText,StepperHover,InputBackground,InputText,InputBorder,InputHoverBorder,InputFocusBorder," +
        "CheckboxBackground,CheckboxBorder,CheckboxHoverBorder,CheckboxTicked,TextSelection,ToolTipBackground," +
        "ToolTipBorder,ToolTipText,MenuBackground,MenuBorder,MenuText,MenuDisabledText,MenuHighlight," +
        "MenuHighlightBorder,MenuDisabledHighlight,MenuDisabledHighlightBorder,MenuGutter,MenuGutterLine," +
        "MenuGutterLineHighlight,MenuShadow,ScrollbarThumb,ScrollbarThumbHover,ProgressTrack,ProgressFill," +
        "StatusBarBackground,StatusText,StatusDetailText,OverlayDim,DialogBackground,DialogBorder,DialogTitle";

    /// <summary>Codes made by the first v1 encoder (2026-10-24). They must read the same in every
    /// later build; the colours they hold come from <see cref="AllDifferent"/> and
    /// <see cref="SixColours"/>.</summary>
    private const string GoldenDirect =
        "FFTheme15aNmmHK8bXSCje9mkAEcwKjQa7QUVPGH8oGT4AH54Pk2ES9Dn8BjyvHz3qtPugrK7zi4jrEdDJCpgAwxUg2KEZJhAodd" +
        "jdUT52BKAq9GhL8nDwpjYpnxo5xpCQhhAQR4vc2UGrgj5vKphqJeCniFWKN1NSWtNenJcbASJcT9VeSfkowE4CLoqDsRganNpGou" +
        "YH16N8znZ6DKngLKj4xbtng3SeJLqaqKJQ2evRSDhTUHtUdqBKJgA7jdD9XzcRomvo2n6wqYLdRiSq8BWRFBAqNg7akUbbfyhLoG" +
        "5VCboZPqQvtZtoWFHwTvtB9XjpsKBnBeGhQVNPMvpVLr2rVHdKZNZtG36SRdu8AiuXrhfbMpeyuUcDBMKLUiV1eDKziogVZ7Ntmq" +
        "DJr297gYd8GsUz7X7qzFnvdApY6dWGaTsL";
    private const string GoldenPalette =
        "FFTheme15VexBMCwV7xu3piu7C22WBLH1gG1twPX1PvgWSK8LdF1xnZdQaHq8bMofE4oQS4pu5NW8mJWsHQxtWLh97DBpLRFcLmv" +
        "FGG6GwzY8suJZM4HzFyEPoDM";

    private const string Base58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    /// <summary>Every v1 role a different colour (red climbs by two), the alpha roles at 0x40 up.</summary>
    private static Dictionary<string, Color> AllDifferent()
    {
        var keys = ThemeCode.V1Keys;
        var colours = new Dictionary<string, Color>(StringComparer.Ordinal);
        for (int i = 0; i < keys.Count; i++)
            colours[keys[i]] = Color.FromArgb(255, (byte)(i * 2 + 1), (byte)(255 - i * 2), (byte)((i * 37) & 0xFF));
        for (int j = 0; j < ThemeCode.V1AlphaKeys.Count; j++)
        {
            var c = colours[ThemeCode.V1AlphaKeys[j]];
            colours[ThemeCode.V1AlphaKeys[j]] = Color.FromArgb((byte)(0x40 + j), c.R, c.G, c.B);
        }
        return colours;
    }

    /// <summary>Six colours round robin over the v1 roles, the alpha roles at 0x20 up in steps of 0x10.</summary>
    private static Dictionary<string, Color> SixColours()
    {
        var keys = ThemeCode.V1Keys;
        var colours = new Dictionary<string, Color>(StringComparer.Ordinal);
        for (int i = 0; i < keys.Count; i++)
        {
            int k = i % 6;
            colours[keys[i]] = Color.FromArgb(255, (byte)(k * 40 + 10), (byte)(200 - k * 30), (byte)((k * 77) & 0xFF));
        }
        for (int j = 0; j < ThemeCode.V1AlphaKeys.Count; j++)
        {
            var c = colours[ThemeCode.V1AlphaKeys[j]];
            colours[ThemeCode.V1AlphaKeys[j]] = Color.FromArgb((byte)(0x20 + j * 0x10), c.R, c.G, c.B);
        }
        return colours;
    }

    private static List<string> Differences(IReadOnlyDictionary<string, Color> expected, IReadOnlyDictionary<string, Color> actual) =>
        ThemeRoles.All.Where(r => !actual.TryGetValue(r.Key, out var a) || a != expected[r.Key]).Select(r => r.Key).ToList();

    /// <summary>
    /// A v1 code built here from raw bytes, with this file's own Base58 and checksum written from
    /// the format description rather than borrowed from ThemeCode, so a code ThemeCode accepts is
    /// one the description says is right. <paramref name="sumVersion"/> is the version character
    /// the checksum is computed over, for the check that the checksum covers it.
    /// </summary>
    private static string Craft(byte[] body, char version = '1', char sumVersion = '1')
    {
        var head = Encoding.ASCII.GetBytes("FFTheme" + sumVersion);
        byte[] sum = SHA256.HashData([.. head, .. body])[..4];
        byte[] all = [.. body, .. sum];
        var value = new BigInteger(all, isUnsigned: true, isBigEndian: true);
        var text = new StringBuilder();
        while (value > 0)
        {
            text.Insert(0, Base58[(int)(value % 58)]);
            value /= 58;
        }
        foreach (var b in all)
        {
            if (b != 0) break;
            text.Insert(0, '1');
        }
        return "FFTheme" + version + text;
    }

    private static byte[] Body(string name, string commander, params byte[][] parts)
    {
        var n = Encoding.UTF8.GetBytes(name);
        var c = Encoding.UTF8.GetBytes(commander);
        byte[] head = [(byte)n.Length, .. n, (byte)c.Length, .. c];
        return [.. head, .. parts.SelectMany(p => p)];
    }

    /// <summary>One palette colour for every role (no index bits at all) and the five alpha bytes.</summary>
    private static byte[] OneColour(byte r, byte g, byte b) => [1, r, g, b];

    private static readonly byte[] FiveAlphas = [0x10, 0x20, 0x30, 0x40, 0x50];

    private void ShareCodeTests()
    {
        // The frozen list.
        Equal("the v1 role list is frozen", PinnedV1Keys, string.Join(",", ThemeCode.V1Keys));
        Equal("v1 carries alpha for exactly these, in this order",
            "MenuHighlight,MenuDisabledHighlight,MenuDisabledHighlightBorder,MenuShadow,OverlayDim",
            string.Join(",", ThemeCode.V1AlphaKeys));
        Check("every v1 key is a role today (if one is retired, v1 codes skip it and this check goes)",
            ThemeCode.V1Keys.All(k => ThemeRoles.Find(k) != null));

        // Ali's theme, which is the defaults now.
        var ali = ThemeRoles.Defaults();
        var code = ThemeCode.Encode("Kailoren orange dark", "Kailoren", ali);
        Console.WriteLine($"   (Ali's theme, 20-character name, CMDR Kailoren: {code.Length} characters)");
        Check($"Ali's theme is 250 characters or less ({code.Length})", code.Length <= 250);
        Check("a code is FFTheme, version 1, then only Base58 letters and digits",
            Regex.IsMatch(code, "^FFTheme1[1-9A-HJ-NP-Za-km-z]+$"), code);
        Check("with none of the look-alikes 0, O, I or l after the prefix", code[7..].IndexOfAny(['0', 'O', 'I', 'l']) < 0);
        var back = ThemeCode.Decode(code);
        Check("it reads back", back.Ok, back.Message);
        Equal("with its name", "Kailoren orange dark", back.Theme?.Name);
        Equal("and its commander", "Kailoren", back.Theme?.Commander);
        Check("and every colour", back.Theme != null && Differences(ali, back.Theme.Colours).Count == 0);
        Equal("a good code has no message", "", back.Message);
        Equal("the same theme always makes the same code", code, ThemeCode.Encode("Kailoren orange dark", "Kailoren", ali));

        // Every colour different, the longest a theme's colours can make a code.
        var different = AllDifferent();
        var worst = ThemeCode.Encode("Kailoren orange dark", "Kailoren", different);
        Console.WriteLine($"   (every colour different, same name and commander: {worst.Length} characters)");
        var worstBack = ThemeCode.Decode(worst);
        Check($"a theme with every colour different reads back ({worst.Length} characters)",
            worstBack.Ok && Differences(different, worstBack.Theme!.Colours).Count == 0, worstBack.Message);
        var longest = ThemeCode.Encode(new string('\u4E2D', 60), new string('\u4E2D', 60), different);
        Console.WriteLine($"   (and with both names at their cap in three-byte characters: {longest.Length} characters)");
        Check($"the longest code there can be reads back ({longest.Length} characters)", ThemeCode.Decode(longest).Ok);

        // Palette edge cases.
        var flat = ThemeRoles.All.ToDictionary(r => r.Key, r => r.HasAlpha ? Color.FromArgb(r.Default.A, 9, 9, 9) : Color.FromRgb(9, 9, 9));
        var flatBack = ThemeCode.Decode(ThemeCode.Encode("Flat", "", flat));
        Check("a theme that is one colour throughout reads back", flatBack.Ok && Differences(flat, flatBack.Theme!.Colours).Count == 0);
        var two = ThemeRoles.Defaults();
        foreach (var r in ThemeRoles.All) two[r.Key] = Color.FromArgb(r.Default.A, 1, 2, (byte)(r.Key.Length % 2));
        var twoBack = ThemeCode.Decode(ThemeCode.Encode("Two", "", two));
        Check("and one that is two colours", twoBack.Ok && Differences(two, twoBack.Theme!.Colours).Count == 0);

        // Alpha.
        var alpha = ThemeRoles.Defaults();
        alpha["OverlayDim"] = Color.FromArgb(0x40, 0x11, 0x22, 0x33);
        alpha["PanelBackground"] = Color.FromArgb(0x40, 0x12, 0x34, 0x56);
        var alphaBack = ThemeCode.Decode(ThemeCode.Encode("Alpha", "", alpha)).Theme!.Colours;
        Equal("an alpha role keeps the alpha it was given", Color.FromArgb(0x40, 0x11, 0x22, 0x33), alphaBack["OverlayDim"]);
        Equal("a role without alpha travels opaque", Color.FromRgb(0x12, 0x34, 0x56), alphaBack["PanelBackground"]);

        // The golden codes.
        var gd = ThemeCode.Decode(GoldenDirect);
        Check("the first v1 code with every colour different still reads", gd.Ok, gd.Message);
        Equal("with its accented name", "Gr\u00FCn und Blau", gd.Theme?.Name);
        Equal("and commander", "T\u00EBster", gd.Theme?.Commander);
        Check("and its colours", gd.Theme != null && Differences(AllDifferent(), gd.Theme.Colours).Count == 0,
            gd.Theme == null ? "" : string.Join(", ", Differences(AllDifferent(), gd.Theme.Colours)));
        var gp = ThemeCode.Decode(GoldenPalette);
        Check("the first v1 palette code still reads", gp.Ok, gp.Message);
        Equal("with its name", "Golden palette", gp.Theme?.Name);
        Check("and its colours", gp.Theme != null && Differences(SixColours(), gp.Theme.Colours).Count == 0);
        Equal("and a new encode of it is the same code", GoldenPalette, ThemeCode.Encode("Golden palette", "Tester", SixColours()));
        Check("a decoded theme has every role today, whatever v1 carried",
            gp.Theme != null && ThemeRoles.All.All(r => gp.Theme.Colours.ContainsKey(r.Key)));

        // Pasted with other text around it.
        foreach (var (label, text) in new[]
                 {
                     ("in a sentence", $"here's mine: {code} enjoy"),
                     ("in backticks", $"`{code}`"),
                     ("in a code block", $"```\r\n{code}\r\n```"),
                     ("with a full stop after it", code + "."),
                     ("with spaces and a line break around it", $"  \r\n{code}\r\n "),
                 })
        {
            Check($"a code {label} is found", ThemeCode.Decode(text).Ok);
        }

        // Bad codes: each comes back as a problem and a sentence, never a theme, never a throw.
        void Bad(string label, string? text, ThemeCodeProblem expected)
        {
            var r = ThemeCode.Decode(text);
            Check(label, !r.Ok && r.Theme == null && r.Problem == expected && r.Message.Length > 0, $"got {r.Problem}");
        }
        Bad("nothing pasted", null, ThemeCodeProblem.Empty);
        Bad("an empty paste", "", ThemeCodeProblem.Empty);
        Bad("only spaces and line breaks", " \r\n\t ", ThemeCodeProblem.Empty);
        Bad("ordinary text", "this is my theme", ThemeCodeProblem.NotACode);
        Bad("a code with the prefix in the wrong case", "fftheme1" + code[8..], ThemeCodeProblem.NotACode);
        Bad("the prefix alone", "FFTheme", ThemeCodeProblem.Incomplete);
        Bad("the prefix and version alone", "FFTheme1", ThemeCodeProblem.Incomplete);
        Bad("one character after them", "FFTheme12", ThemeCodeProblem.Incomplete);
        Bad("a newer version", "FFTheme2" + code[8..], ThemeCodeProblem.Newer);
        Bad("a much newer version", "FFThemeK" + code[8..], ThemeCodeProblem.Newer);
        Bad("a version character no build will use", "FFThemex" + code[8..], ThemeCodeProblem.NotACode);
        Bad("far too long", "FFTheme1" + new string('z', 1200), ThemeCodeProblem.TooLong);

        var watch = Stopwatch.StartNew();
        Bad("a megabyte of junk", new string('z', 1_000_000), ThemeCodeProblem.TooLong);
        Bad("a long paste with a code-shaped run in it", new string(' ', 90_000) + "FFTheme1" + new string('z', 5_000), ThemeCodeProblem.TooLong);
        Bad("a code-shaped run just under the length limit", "FFTheme1" + new string('z', 990), ThemeCodeProblem.Incomplete);
        Check($"and none of those takes long ({watch.ElapsedMilliseconds} ms)", watch.ElapsedMilliseconds < 1000);

        int cut = 0, cutCaught = 0;
        for (int length = 8; length < code.Length; length++, cut++)
            if (ThemeCode.Decode(code[..length]).Problem == ThemeCodeProblem.Incomplete) cutCaught++;
        Equal($"cut off at every length ({cut}), it is never complete", cut, cutCaught);

        int changed = 0, changeCaught = 0;
        for (int i = 8; i < code.Length; i++, changed++)
        {
            var other = Base58[(Base58.IndexOf(code[i]) + 1) % Base58.Length];
            if (ThemeCode.Decode(code[..i] + other + code[(i + 1)..]).Problem == ThemeCodeProblem.Incomplete) changeCaught++;
        }
        Equal($"with any one character changed ({changed} tried), it is caught", changed, changeCaught);

        int swapped = 0, swapCaught = 0;
        for (int i = 8; i + 1 < code.Length; i++)
        {
            if (code[i] == code[i + 1]) continue;
            swapped++;
            if (!ThemeCode.Decode(code[..i] + code[i + 1] + code[i] + code[(i + 2)..]).Ok) swapCaught++;
        }
        Equal($"with two neighbours swapped ({swapped} tried), it is caught", swapped, swapCaught);

        int mid = code.Length / 2;
        foreach (var c in new[] { '0', 'O', 'I', 'l' })
            Bad($"a look-alike {c} in the middle", code[..mid] + c + code[mid..], ThemeCodeProblem.BadCharacter);
        foreach (var c in new[] { '_', '*', '~', '`', '-', ' ', '\u0410' })
            Bad($"a code broken by {(c == ' ' ? "a space" : c == '\u0410' ? "a Cyrillic A" : c.ToString())} in the middle",
                code[..mid] + c + code[mid..], ThemeCodeProblem.Incomplete);
        Bad("a code missing its middle", code[..mid] + code[(mid + 10)..], ThemeCodeProblem.Incomplete);
        Bad("a code pasted twice", code + code, ThemeCodeProblem.Incomplete);

        // Random junk after a real prefix, and random text altogether: no theme and no throw.
        var random = new Random(20261024);
        int junkOk = 0;
        for (int n = 0; n < 2000; n++)
        {
            var chars = new char[random.Next(1, 500)];
            for (int i = 0; i < chars.Length; i++) chars[i] = Base58[random.Next(Base58.Length)];
            if (ThemeCode.Decode("FFTheme1" + new string(chars)).Ok) junkOk++;
            for (int i = 0; i < chars.Length; i++) chars[i] = (char)random.Next(0, 0xFFFF);
            if (ThemeCode.Decode("FFTheme1" + new string(chars)).Ok) junkOk++;
        }
        Equal("4000 random pastes make no theme and throw nothing", 0, junkOk);

        // Codes with a correct checksum that were not made by FleetFinder.
        var hand = ThemeCode.Decode(Craft(Body("Hand made", "", OneColour(0x12, 0x34, 0x56), FiveAlphas)));
        Check("a code built by hand from the format description reads", hand.Ok, hand.Message);
        Equal("its plain roles are its one colour", Color.FromRgb(0x12, 0x34, 0x56), hand.Theme?.Colours["PanelBackground"] ?? default);
        Equal("and its alpha roles take their alpha bytes in order", Color.FromArgb(0x50, 0x12, 0x34, 0x56), hand.Theme?.Colours["OverlayDim"] ?? default);
        Equal("starting with the menu highlight", Color.FromArgb(0x10, 0x12, 0x34, 0x56), hand.Theme?.Colours["MenuHighlight"] ?? default);

        Bad("a checksum made for another version", Craft(Body("Hand made", "", OneColour(1, 2, 3), FiveAlphas), sumVersion: '2'),
            ThemeCodeProblem.Incomplete);
        // There is no decompression to bomb, so the nearest thing is a code whose sizes claim more
        // than it holds. Each is refused rather than read past its end or sized from the claim.
        byte[] nameClaims = [200, .. "short"u8.ToArray(), 0, .. OneColour(1, 2, 3), .. FiveAlphas];
        Bad("a name that claims more bytes than the code has", Craft(nameClaims), ThemeCodeProblem.Damaged);
        Bad("a palette of 255 colours", Craft(Body("Big", "", [255, 1, 2, 3], FiveAlphas)), ThemeCodeProblem.Damaged);
        Bad("a palette bigger than the role list", Craft(Body("Big", "", [94, .. new byte[94 * 3]], new byte[12 * 8], FiveAlphas)),
            ThemeCodeProblem.Damaged);
        Bad("no colours at all", Craft(Body("Empty", "")), ThemeCodeProblem.Damaged);
        Bad("colours that stop short", Craft(Body("Short", "", [0, .. new byte[90]])), ThemeCodeProblem.Damaged);
        // Three colours take two bits each; every index 3 points past the palette.
        Bad("an index past the end of the palette", Craft(Body("Index", "", [3, 1, 1, 1, 2, 2, 2, 3, 3, 3],
            Enumerable.Repeat((byte)0xFF, (93 * 2 + 7) / 8).ToArray(), FiveAlphas)), ThemeCodeProblem.Damaged);
        // Two colours take one bit each: 93 bits fill twelve bytes with three spare bits, always zero.
        var padded = new byte[12];
        padded[11] = 0x01;
        Bad("bits set after the last index", Craft(Body("Pad", "", [2, 1, 1, 1, 2, 2, 2], padded, FiveAlphas)), ThemeCodeProblem.Damaged);
        Bad("a byte left over at the end", Craft(Body("Extra", "", OneColour(1, 2, 3), FiveAlphas, [7])), ThemeCodeProblem.Damaged);
        Bad("an alpha byte missing", Craft(Body("Short", "", OneColour(1, 2, 3), FiveAlphas[..4])), ThemeCodeProblem.Damaged);
        byte[] badUtf8 = [3, 0x41, 0xFF, 0x42, 0, .. OneColour(1, 2, 3), .. FiveAlphas];
        Bad("a name that is not UTF-8", Craft(badUtf8), ThemeCodeProblem.Damaged);
        Bad("a theme with no name", Craft(Body("", "Someone", OneColour(1, 2, 3), FiveAlphas)), ThemeCodeProblem.NoName);
        Bad("a theme whose name is all invisible", Craft(Body("\u202E\u200B\u0007 \u2066", "", OneColour(1, 2, 3), FiveAlphas)),
            ThemeCodeProblem.NoName);

        // A stranger's name and commander, as another app would show them.
        var hostile = ThemeCode.Decode(Craft(Body("Evil\u202Egnirts\u0007\r\nline two " + new string('x', 60),
            "CMDR " + new string('k', 60) + "\u2067", OneColour(1, 2, 3), FiveAlphas))).Theme;
        Check("a hostile name still makes a theme", hostile != null);
        Equal("its name loses the invisible characters, and the line break becomes a space, cut at 40",
            ("Evilgnirts line two " + new string('x', 60))[..40], hostile?.Name);
        Equal("its commander loses the CMDR and is cut at 32", new string('k', 32), hostile?.Commander);

        // Writing.
        Check("a theme with no name cannot be shared", Throws<ArgumentException>(() => ThemeCode.Encode(" \u200B ", "x", ali)));
        Equal("a name is cleaned on the way out too", "Red and Blue", ThemeCode.Decode(ThemeCode.Encode("Red\r\nand\u202E Blue", "", ali)).Theme?.Name);
        var partial = new Dictionary<string, Color> { ["PanelBackground"] = Color.FromRgb(1, 2, 3) };
        var partialBack = ThemeCode.Decode(ThemeCode.Encode("Partial", "", partial)).Theme!.Colours;
        Check("roles a theme leaves out go out at their default",
            partialBack["PanelBackground"] == Color.FromRgb(1, 2, 3) && partialBack["GridRow"] == ThemeRoles.Find("GridRow")!.Default);

        // Colours from outside go through theme.json's own check.
        var overlay = ThemeRoles.Find("OverlayDim")!;
        var panel = ThemeRoles.Find("PanelBackground")!;
        Check("an alpha role given no alpha keeps its default alpha",
            ThemeStore.TryAccept(overlay, Color.FromRgb(1, 2, 3), false, out var a1) && a1 == Color.FromArgb(overlay.Default.A, 1, 2, 3));
        Check("an alpha role given alpha takes it", ThemeStore.TryAccept(overlay, Color.FromArgb(9, 1, 2, 3), true, out var a2) && a2.A == 9);
        Check("a plain role refuses see-through", !ThemeStore.TryAccept(panel, Color.FromArgb(9, 1, 2, 3), true, out _));
        Check("and takes opaque", ThemeStore.TryAccept(panel, Color.FromRgb(1, 2, 3), true, out var a3) && a3 == Color.FromRgb(1, 2, 3));

        // The sentences.
        var problems = Enum.GetValues<ThemeCodeProblem>().Where(p => p != ThemeCodeProblem.None).ToList();
        Check("every problem has a sentence", problems.All(p => ThemeCode.Describe(p).Length > 0));
        Check("and none has an em dash", problems.All(p => !ThemeCode.Describe(p).Contains('\u2014')));
        Check("a cut or changed code says it isn't complete",
            ThemeCode.Describe(ThemeCodeProblem.Incomplete).StartsWith("That isn't a complete FleetFinder theme code", StringComparison.Ordinal));
        Check("a newer code says so",
            ThemeCode.Describe(ThemeCodeProblem.Newer).StartsWith("This theme was made by a newer FleetFinder", StringComparison.Ordinal));
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    // ---- saved theme library ---------------------------------------------------------------------

    private void LibraryTests()
    {
        var folder = Path.Combine(_dir, "themes");
        var library = new ThemeLibrary(folder);
        Check("a library whose folder does not exist yet lists nothing", library.List().Count == 0 && !Directory.Exists(folder));
        Check("and loads nothing", library.Load("0123456789abcdef") == null);
        Check("Data\\themes is beside the exe", ThemeLibrary.DefaultFolder == Path.Combine(AppContext.BaseDirectory, "Data", "themes"));

        var colours = ThemeRoles.Defaults();
        colours["PanelBackground"] = Color.FromRgb(0x26, 0x73, 0x99);
        colours["OverlayDim"] = Color.FromArgb(0x40, 1, 2, 3);
        colours["GridRow"] = Color.FromArgb(0x10, 4, 5, 6);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

        var saved = library.Save("  Green and\r\nBlue \u202E ", "CMDR BlueGreen", colours, png);
        Check("a theme saves", saved != null);
        if (saved is null) return;
        Check($"with a generated id ({saved.Id})", ThemeLibrary.IsValidId(saved.Id));
        Equal("its name is cleaned", "Green and Blue", saved.Name);
        Equal("and its commander", "BlueGreen", saved.Commander);
        Check("saved just now, in UTC", saved.Created.Kind == DateTimeKind.Utc && (DateTime.UtcNow - saved.Created).TotalMinutes < 1);
        Check("the file is named by its id", File.Exists(Path.Combine(folder, saved.Id + ".json")));
        Check("with the snapshot beside it", File.Exists(Path.Combine(folder, saved.Id + ".png"))
            && File.ReadAllBytes(Path.Combine(folder, saved.Id + ".png")).SequenceEqual(png));
        Equal("SnapshotPath points at it", Path.Combine(folder, saved.Id + ".png"), library.SnapshotPath(saved.Id));
        Equal("and nothing else is in the folder", 2, Directory.GetFiles(folder).Length);
        Equal("a plain role given alpha is kept opaque", Color.FromRgb(4, 5, 6), saved.Colours["GridRow"]);

        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, saved.Id + ".json"))))
        {
            var root = doc.RootElement;
            Equal("the file says version 1", 1, root.GetProperty("version").GetInt32());
            Equal("and its id", saved.Id, root.GetProperty("id").GetString());
            Equal("and its name", "Green and Blue", root.GetProperty("name").GetString());
            Equal("and its commander", "BlueGreen", root.GetProperty("cmdr").GetString());
            Check("and when it was saved", DateTime.TryParse(root.GetProperty("created").GetString(), out _));
            Equal("and every role", ThemeRoles.All.Count, root.GetProperty("colours").EnumerateObject().Count());
            Equal("plain roles as #RRGGBB", "#267399", root.GetProperty("colours").GetProperty("PanelBackground").GetString());
            Equal("alpha roles as #AARRGGBB", "#40010203", root.GetProperty("colours").GetProperty("OverlayDim").GetString());
        }

        var loaded = library.Load(saved.Id);
        Check("it loads back the same",
            loaded != null && loaded.Name == saved.Name && loaded.Commander == saved.Commander
            && loaded.Created == saved.Created && Differences(saved.Colours, loaded.Colours).Count == 0);

        // Names never become paths.
        var sneaky = library.Save("..\\..\\evil", "", colours, null);
        Check("a name that looks like a path is only a name",
            sneaky != null && sneaky.Name == "..\\..\\evil" && File.Exists(Path.Combine(folder, sneaky.Id + ".json"))
            && !File.Exists(Path.Combine(_dir, "evil.json")) && !File.Exists(Path.Combine(folder, "..", "..", "evil.json")));
        Check("with no snapshot when none is given", sneaky != null && !File.Exists(library.SnapshotPath(sneaky.Id)));
        Check("and it still loads without one", sneaky != null && library.Load(sneaky.Id) != null);
        Check("a theme with no name is not saved", library.Save(" \u200B\t", "x", colours, null) == null);
        Equal("so the folder holds the two themes and one snapshot", 3, Directory.GetFiles(folder).Length);
        foreach (var id in new[] { "..\\theme", "../x", "0123456789ABCDEF", "0123456789abcde", "0123456789abcdef0", "", "theme" })
            Check($"\"{id}\" is not an id", !ThemeLibrary.IsValidId(id) && library.Load(id) == null && !library.Delete(id)
                && library.SnapshotPath(id) == null && !library.Rename(id, "x"));

        // Snapshots.
        Check("a snapshot that is not a PNG is refused", sneaky != null && !library.SaveSnapshot(sneaky.Id, [1, 2, 3]));
        Check("one for a theme that does not exist is refused", !library.SaveSnapshot("0123456789abcdef", png));
        Check("a real one is written", sneaky != null && library.SaveSnapshot(sneaky.Id, png) && File.Exists(library.SnapshotPath(sneaky.Id)));

        // Rename.
        Check("a theme can be renamed", library.Rename(saved.Id, "Blue and Green"));
        Equal("and keeps the new name", "Blue and Green", library.Load(saved.Id)?.Name);
        Check("and everything else", library.Load(saved.Id) is { } renamed && renamed.Commander == "BlueGreen"
            && renamed.Created == saved.Created && Differences(saved.Colours, renamed.Colours).Count == 0);
        Check("but not to nothing", !library.Rename(saved.Id, "\u202E"));
        Check("and not a theme that is not there", !library.Rename("0123456789abcdef", "x"));
        Check("renaming leaves no temporary file behind", !Directory.GetFiles(folder, "*.tmp").Any());

        // Order: oldest first.
        var older = Path.Combine(folder, "00000000000000aa.json");
        File.WriteAllText(older, """{ "version": 1, "name": "Older", "cmdr": "", "created": "2020-01-01T00:00:00Z", "colours": { "PanelBackground": "#010203" } }""");
        var list = library.List();
        Equal("List gives every theme", 3, list.Count);
        Equal("oldest first", "Older", list.FirstOrDefault()?.Name);
        Equal("a file's missing roles are defaults", ThemeRoles.Find("GridRow")!.Default, list.FirstOrDefault()?.Colours["GridRow"] ?? default);

        // Broken files are skipped and noted once; the good ones still load.
        var broken = new (string Id, string Content, string Note)[]
        {
            ("00000000000000b1", "{ not json", "Saved theme 00000000000000b1 could not be read"),
            ("00000000000000b2", "[1, 2, 3]", "Saved theme 00000000000000b2 is not a theme file"),
            ("00000000000000b3", """{ "colours": { "PanelBackground": "#010203" } }""", "Saved theme 00000000000000b3 has no name"),
            ("00000000000000b4", """{ "name": "\u202E\u200B", "colours": {} }""", "Saved theme 00000000000000b4 has no name"),
            ("00000000000000b5", """{ "name": "No colours" }""", "Saved theme 00000000000000b5 has no colours"),
            ("00000000000000b6", "", "Saved theme 00000000000000b6 could not be read"),
            ("00000000000000b7", "{ \"name\": \"Huge\", \"pad\": \"" + new string('x', 70_000) + "\" }", "Saved theme 00000000000000b7 is "),
        };
        foreach (var b in broken) File.WriteAllText(Path.Combine(folder, b.Id + ".json"), b.Content);
        File.WriteAllText(Path.Combine(folder, "my theme.json"), "{}");
        Directory.CreateDirectory(Path.Combine(folder, "00000000000000c1.json"));
        var before = broken.Select(b => LogCount(b.Note)).ToArray();
        int foreignBefore = LogCount("Saved themes folder has a file FleetFinder did not make");
        list = library.List();
        Equal("broken files are skipped and the good ones still load", 3, list.Count);
        Check("each broken file is noted",
            broken.Select((b, i) => LogCount(b.Note) == before[i] + 1).All(x => x),
            string.Join(", ", broken.Where((b, i) => LogCount(b.Note) != before[i] + 1).Select(b => b.Id)));
        Equal("a file FleetFinder did not make is left alone and noted", foreignBefore + 1, LogCount("Saved themes folder has a file FleetFinder did not make"));
        library.List();
        Check("and noted once, not again on the next look", broken.Select((b, i) => LogCount(b.Note) == before[i] + 1).All(x => x));
        Check("the foreign file is still there", File.Exists(Path.Combine(folder, "my theme.json")));

        // Untrusted text and colours in a file someone dropped in.
        var dropped = Path.Combine(folder, "00000000000000d1.json");
        File.WriteAllText(dropped, """{ "name": "Dropped\u202E in\u0007 ", "cmdr": "CMDR Other\u2066", "colours": { "PanelBackground": "#zzz", "OverlayDim": "#112233", "GridRow": "#80112233", "NotARole": "#FFFFFF" } }""");
        int malformedBefore = LogCount("Saved theme 00000000000000d1 colour for PanelBackground is not a usable colour");
        var d = library.Load("00000000000000d1");
        Equal("a name from a file is cleaned", "Dropped in", d?.Name);
        Equal("and its commander", "Other", d?.Commander);
        Equal("a broken colour gets its default", ThemeRoles.Find("PanelBackground")!.Default, d?.Colours["PanelBackground"] ?? default);
        Equal("and is noted", malformedBefore + 1, LogCount("Saved theme 00000000000000d1 colour for PanelBackground is not a usable colour"));
        Equal("six digits on an alpha role keep its alpha", Color.FromArgb(ThemeRoles.Find("OverlayDim")!.Default.A, 0x11, 0x22, 0x33), d?.Colours["OverlayDim"] ?? default);
        Equal("alpha on a plain role is refused", ThemeRoles.Find("GridRow")!.Default, d?.Colours["GridRow"] ?? default);
        Check("unknown keys are ignored", d != null && !d.Colours.ContainsKey("NotARole") && d.Colours.Count == ThemeRoles.All.Count);
        Check("a missing date falls back to the file's own", d != null && Math.Abs((d.Created - File.GetLastWriteTimeUtc(dropped)).TotalSeconds) < 2);

        // Delete.
        Check("a theme can be deleted", library.Delete(saved.Id));
        Check("with its snapshot", !File.Exists(Path.Combine(folder, saved.Id + ".json")) && !File.Exists(Path.Combine(folder, saved.Id + ".png")));
        Check("and deleting it again says there was nothing", !library.Delete(saved.Id));
        Check("a theme without a snapshot deletes too", library.Delete("00000000000000aa"));

        // A library that cannot write says so and does not throw.
        var blocked = Path.Combine(_dir, "a file, not a folder");
        File.WriteAllText(blocked, "");
        Check("saving where the folder is a file fails quietly", new ThemeLibrary(blocked).Save("x", "", colours, null) == null);
        Check("and lists nothing", new ThemeLibrary(blocked).List().Count == 0);

        Check(".gitignore keeps saved themes out of git", File.ReadAllLines(RepoFile(".gitignore")).Contains("themes/"));
    }

    // ---- commander from the journal -----------------------------------------------------------------

    private string Journal(string folder, string name, params string[] lines)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
        return path;
    }

    private static string Header => """{ "timestamp":"2026-10-24T08:00:00Z", "event":"Fileheader", "part":1, "language":"English/UK", "gameversion":"4.1.0" }""";
    private static string CommanderEvent(string name) => $$"""{ "timestamp":"2026-10-24T08:00:01Z", "event":"Commander", "FID":"F1234567", "Name":"{{name}}" }""";
    private static string LoadGame(string name) => $$"""{ "timestamp":"2026-10-24T08:00:02Z", "event":"LoadGame", "FID":"F1234567", "Commander":"{{name}}", "Horizons":true, "Odyssey":true, "Ship":"Cutter" }""";

    private void CommanderTests()
    {
        var root = Path.Combine(_dir, "journals");

        // Ali's case: two commanders, and the newest journal switches from one to the other.
        var two = Path.Combine(root, "two commanders");
        Journal(two, "Journal.2026-10-20T100000.01.log", Header, CommanderEvent("Older"), LoadGame("Older"));
        Journal(two, "Journal.2026-10-23T180000.01.log", Header, CommanderEvent("Kailoren"), LoadGame("Kailoren"),
            """{ "timestamp":"2026-10-23T19:00:00Z", "event":"Music", "MusicTrack":"MainMenu" }""",
            CommanderEvent("AltCmdr"), LoadGame("AltCmdr"),
            """{ "timestamp":"2026-10-23T19:30:00Z", "event":"FSDJump", "StarSystem":"Sol", "StarPos":[0,0,0] }""");
        // A later session that never got past the menu has no login in it.
        Journal(two, "Journal.2026-10-24T090000.01.log", Header);
        // A file whose modified time is newest but whose name is oldest: the name decides.
        var stale = Journal(two, "Journal.2026-10-01T100000.01.log", Header, CommanderEvent("Stale"));
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(5));
        Equal("the last login in the newest journal that has one", "AltCmdr", new JournalReader(two).GetLatestCommander());

        var parts = Path.Combine(root, "parts");
        Journal(parts, "Journal.2026-10-24T090000.01.log", Header, LoadGame("PartOne"));
        Journal(parts, "Journal.2026-10-24T090000.02.log", Header);
        Equal("a second part with no login falls back to the first part", "PartOne", new JournalReader(parts).GetLatestCommander());

        var oldNames = Path.Combine(root, "old names");
        Journal(oldNames, "Journal.200101100000.01.log", Header, CommanderEvent("Ancient"));
        Journal(oldNames, "Journal.2026-10-24T090000.01.log", Header, LoadGame("Modern"));
        Equal("old and new file names sort together", "Modern", new JournalReader(oldNames).GetLatestCommander());

        var onlyLoadGame = Path.Combine(root, "load game only");
        Journal(onlyLoadGame, "Journal.2026-10-24T090000.01.log", Header, LoadGame("FromLoadGame"));
        Equal("LoadGame alone is enough", "FromLoadGame", new JournalReader(onlyLoadGame).GetLatestCommander());

        var junk = Path.Combine(root, "junk");
        Journal(junk, "Journal.2026-10-24T090000.01.log", Header, CommanderEvent("Good"),
            """{ "timestamp":"2026-10-24T09:00:00Z", "event":"Commander", "Name":""",
            """{ "event":"Commander", "Name": 42 }""",
            """not json at all, mentions Commander""",
            """{ "event":"Friends", "Name":"Commander Friend", "Status":"Online" }""",
            """{ "event":"Commander" }""");
        Equal("broken and unrelated lines are skipped", "Good", new JournalReader(junk).GetLatestCommander());

        var hostile = Path.Combine(root, "hostile");
        Journal(hostile, "Journal.2026-10-24T090000.01.log", Header, CommanderEvent("Kai\\u202Eloren\\u0007"));
        Equal("a name with invisible characters is cleaned", "Kailoren", new JournalReader(hostile).GetLatestCommander());

        var none = Path.Combine(root, "no logins");
        Journal(none, "Journal.2026-10-24T090000.01.log", Header);
        Equal("journals with no login give nothing", "", new JournalReader(none).GetLatestCommander());
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);
        Equal("no journals give nothing", "", new JournalReader(empty).GetLatestCommander());
        Equal("a folder that does not exist gives nothing", "", new JournalReader(Path.Combine(root, "missing")).GetLatestCommander());
        Equal("no folder at all gives nothing", "", new JournalReader(null).GetLatestCommander());
        Equal("a relative folder gives nothing", "", new JournalReader("journals").GetLatestCommander());
        Equal("the location reader still works beside it", "Sol", new JournalReader(two).GetCurrentLocation()?.System);
    }
}
