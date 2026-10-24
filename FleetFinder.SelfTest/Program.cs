using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using FleetView;
using FleetView.Controls;
using FleetView.Services;
using FleetView.Theming;

// Self-test for FleetFinder's theme layer (the colour changer, 2026-10): the colour maths in both
// directions and its text forms, theme.json loading and saving, the brushes the theme service
// hands out, a scan of MainWindow.xaml that fails if a colour literal comes back or a
// DynamicResource names something that is not a role, the colour picker (its state on its own,
// then the real control driven through its public methods), the Settings draft (save, revert,
// reset, the picker's edits), and the Settings page and its preview. Then the shipped defaults,
// and the logic behind saved and shared themes (ThemeTests.cs): names cleaned for display, the
// share code both ways and every way a paste can be wrong, the theme library on disk, and the
// commander name read from the journal. Then hover copy and paste and click to find (FindTests.cs),
// and the saved themes on the page: cards, Save as theme, Import code (ShelfTests.cs). Prints one
// PASS or FAIL line per check and a final SUMMARY line; exit code 1 if anything failed.
//
// Runs on an STA thread because WPF brushes are dispatcher objects. It builds the main window's
// object tree to check the Settings page in it, but never shows it, so nothing appears on screen.
var suite = new SelfTest();
var thread = new Thread(suite.Run);
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
Console.WriteLine($"SUMMARY: {suite.Passed} passed, {suite.Failed} failed");
return suite.Failed == 0 ? 0 : 1;

internal sealed partial class SelfTest
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fleetfinder selftest " + Guid.NewGuid().ToString("N")[..8]);

    private static readonly Color Reference = Color.FromRgb(0xFC, 0xBA, 0x03);

    public void Run()
    {
        Directory.CreateDirectory(_dir);
        try
        {
            Section("role registry", RoleTests);
            Section("hex", HexTests);
            Section("display formats", FormatTests);
            Section("parsing", ParseTests);
            Section("round trips", RoundTripTests);
            Section("edge colours", EdgeTests);
            Section("theme.json", StoreTests);
            Section("theme service", ServiceTests);
            Section("MainWindow.xaml", XamlTests);
            Section("no colours in the C#", CodeTests);
            Section("colour picker state", PickerStateTests);
            Section("colour picker control", PickerControlTests);
            Section("ColourPicker.xaml", PickerXamlTests);
            Section("theme draft", DraftTests);
            Section("Settings XAML", SettingsXamlTests);
            Section("Settings page and preview", SettingsPageTests);
            Section("shipped defaults and labels", DefaultsTests);
            Section("theme text", ThemeTextTests);
            Section("share code", ShareCodeTests);
            Section("saved theme library", LibraryTests);
            Section("commander from the journal", CommanderTests);
            Section("hover copy and paste", HoverClipboardTests);
            Section("click to find", RoleFinderTests);
            Section("preview tags in the XAML", PreviewTagXamlTests);
            Section("Settings find and copy", SettingsFindAndCopyTests);
            Section("theme draft takes a whole theme", DraftUseThemeTests);
            Section("theme library saves over a theme", LibraryReplaceTests);
            Section("theme cards", ShelfTests);
            Section("save as theme", SaveThemeFormTests);
            Section("import a theme code", ImportTests);
            Section("Settings themes", SettingsThemesTests);
        }
        finally
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void Section(string name, Action body)
    {
        Console.WriteLine($"-- {name}");
        try { body(); }
        catch (Exception ex) { Check($"{name}: ran without throwing", false, ex.ToString()); }
    }

    private void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine(ok || detail.Length == 0 ? $"{(ok ? "PASS" : "FAIL")}: {name}" : $"FAIL: {name} ({detail})");
        if (ok) Passed++; else Failed++;
    }

    private void Equal<T>(string name, T expected, T actual) =>
        Check(name, EqualityComparer<T>.Default.Equals(expected, actual), $"expected {expected}, got {actual}");

    private void Near(string name, double expected, double actual, double tolerance = 1e-9) =>
        Check(name, Math.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}");

    private static Color Hex(string text) =>
        ColourMaths.TryParseHex(text, out var c) ? c : throw new ArgumentException("bad test hex " + text);

    // ---- roles ----------------------------------------------------------------------------------

    private void RoleTests()
    {
        var all = ThemeRoles.All;
        Check($"there are roles ({all.Count})", all.Count > 0);
        Equal("role keys are unique", all.Count, all.Select(r => r.Key).Distinct(StringComparer.Ordinal).Count());
        Equal("role labels are unique", all.Count, all.Select(r => r.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Check("role keys are plain identifiers", all.All(r => Regex.IsMatch(r.Key, "^[A-Z][A-Za-z]+$")),
            string.Join(", ", all.Where(r => !Regex.IsMatch(r.Key, "^[A-Z][A-Za-z]+$")).Select(r => r.Key)));
        Check("every role has a group and a label", all.All(r => r.Group.Length > 0 && r.Label.Length > 0));
        Check("each group's roles sit together", ThemeRoles.Groups.All(g =>
        {
            var idx = all.Select((r, i) => (r, i)).Where(x => x.r.Group == g).Select(x => x.i).ToList();
            return idx.Last() - idx.First() == idx.Count - 1;
        }));
        // The overlay dim, and the menu's see-through highlight washes and shadow, which were
        // see-through in WPF's own menu. Anything else with alpha is a mistake.
        Equal("the roles with alpha are exactly these",
            "MenuHighlight,MenuDisabledHighlight,MenuDisabledHighlightBorder,MenuShadow,OverlayDim",
            string.Join(",", all.Where(r => r.HasAlpha).Select(r => r.Key)));
        Equal("overlay dim keeps the original #CC000000", Color.FromArgb(0xCC, 0, 0, 0), ThemeRoles.Find("OverlayDim")!.Default);
        Check("Find is exact and case-sensitive", ThemeRoles.Find("PanelBackground") != null && ThemeRoles.Find("panelbackground") == null);
        Check("every SystemColors alias follows a real role", ThemeRoles.SystemAliases.All(a => ThemeRoles.Find(a.RoleKey) != null));
        Equal("Defaults() covers every role", all.Count, ThemeRoles.Defaults().Count);
    }

    // ---- hex ------------------------------------------------------------------------------------

    private void HexTests()
    {
        foreach (var text in new[] { "#fcba03", "fcba03", "#FCBA03", "  #fcba03  ", "#FcBa03" })
        {
            Check($"hex \"{text}\" reads as #fcba03",
                ColourMaths.TryParseHex(text, out var c, out bool alpha) && c == Reference && !alpha);
        }
        Check("#rgb doubles each digit (#fa3 is #ffaa33)", ColourMaths.TryParseHex("#fa3", out var s) && s == Color.FromRgb(0xFF, 0xAA, 0x33));
        Check("rgb without # works too", ColourMaths.TryParseHex("fa3", out var s2) && s2 == Color.FromRgb(0xFF, 0xAA, 0x33));
        Check("#aarrggbb carries alpha", ColourMaths.TryParseHex("#CC000000", out var a, out bool hadAlpha)
            && a == Color.FromArgb(0xCC, 0, 0, 0) && hadAlpha);
        Check("six digits are opaque", ColourMaths.TryParseHex("#120E08", out var o) && o.A == 255);

        foreach (var bad in new[] { null, "", " ", "#", "#1", "#12", "#1234", "#12345", "#1234567", "#123456789",
                     "#gggggg", "# fcba03", "0x123456", "#fcba03ff00", "##fcba03", "red", "#12 456" })
        {
            Check($"hex {(bad is null ? "null" : $"\"{bad}\"")} is refused", !ColourMaths.TryParseHex(bad, out _));
        }

        Equal("display hex is lower case without alpha", "#fcba03", ColourMaths.FormatHex(Color.FromArgb(0x80, 0xFC, 0xBA, 0x03)));
        Equal("storage hex is #RRGGBB", "#120E08", ColourMaths.ToStorageHex(Color.FromRgb(0x12, 0x0E, 0x08), false));
        Equal("storage hex with alpha is #AARRGGBB", "#CC000000", ColourMaths.ToStorageHex(Color.FromArgb(0xCC, 0, 0, 0), true));
    }

    // ---- display formats ------------------------------------------------------------------------

    /// <summary>The reference picker the design came from shows exactly these strings for #fcba03.</summary>
    private void FormatTests()
    {
        Equal("HEX", "#fcba03", ColourMaths.FormatHex(Reference));
        Equal("RGB", "252, 186, 3", ColourMaths.FormatRgb(Reference));
        Equal("CMYK", "0%, 26%, 99%, 1%", ColourMaths.FormatCmyk(ColourMaths.ToCmyk(Reference)));
        Equal("HSV", "44°, 99%, 99%", ColourMaths.FormatHsv(ColourMaths.ToHsv(Reference)));
        Equal("HSL", "44°, 98%, 50%", ColourMaths.FormatHsl(ColourMaths.ToHsl(Reference)));
        Equal("black CMYK", "0%, 0%, 0%, 100%", ColourMaths.FormatCmyk(ColourMaths.ToCmyk(Colors.Black)));
        Equal("white HSL", "0°, 0%, 100%", ColourMaths.FormatHsl(ColourMaths.ToHsl(Colors.White)));
        // #ff0001 has a hue of 359.76, which rounds to 360; the field shows that as 0.
        Equal("a hue that rounds to 360 shows as 0°", "0°, 100%, 100%", ColourMaths.FormatHsv(ColourMaths.ToHsv(Color.FromRgb(0xFF, 0, 1))));
    }

    // ---- parsing --------------------------------------------------------------------------------

    private void ParseTests()
    {
        Check("RGB parses its own display", ColourMaths.TryParseRgb("252, 186, 3", out var rgb) && rgb == Reference);
        Check("RGB with spaces only", ColourMaths.TryParseRgb("252 186 3", out var rgb2) && rgb2 == Reference);
        Check("RGB with no spaces", ColourMaths.TryParseRgb("252,186,3", out var rgb3) && rgb3 == Reference);

        foreach (var text in new[] { "44°, 99%, 99%", "44, 99, 99", "44° 99% 99%", "44 °, 99 %, 99 %", "44;99;99" })
        {
            Check($"HSV \"{text}\"", ColourMaths.TryParseHsv(text, out var h) && h == new Hsv(44, 0.99, 0.99), $"{h}");
        }
        Check("HSL with signs", ColourMaths.TryParseHsl("44°, 98%, 50%", out var l) && l == new Hsl(44, 0.98, 0.5), $"{l}");
        Check("HSL without signs", ColourMaths.TryParseHsl("44,98,50", out var l2) && l2 == new Hsl(44, 0.98, 0.5));
        Check("CMYK with signs", ColourMaths.TryParseCmyk("0%, 26%, 99%, 1%", out var k) && k == new Cmyk(0, 0.26, 0.99, 0.01), $"{k}");
        Check("CMYK without signs", ColourMaths.TryParseCmyk("0 26 99 1", out var k2) && k2 == new Cmyk(0, 0.26, 0.99, 0.01));
        Check("decimals are accepted in HSV", ColourMaths.TryParseHsv("44.5, 99.5, 0.5", out var hd) && hd == new Hsv(44.5, 0.995, 0.005));

        foreach (var bad in new[] { "256, 0, 0", "-1, 0, 0", "1.5, 2, 3", "1, 2", "1, 2, 3, 4", "a, b, c", "", null, "1,,2", "0x10, 0, 0" })
            Check($"RGB {(bad is null ? "null" : $"\"{bad}\"")} is refused", !ColourMaths.TryParseRgb(bad, out _));
        foreach (var bad in new[] { "361, 50, 50", "-1, 50, 50", "10, 101, 0", "10, 50, -1", "10, 50", "NaN, 50, 50",
                     "Infinity, 1, 1", "1e2, 0, 0", "10, 50, 50, 50", "ten, 50, 50" })
        {
            Check($"HSV \"{bad}\" is refused", !ColourMaths.TryParseHsv(bad, out _));
            Check($"HSL \"{bad}\" is refused", !ColourMaths.TryParseHsl(bad, out _));
        }
        foreach (var bad in new[] { "0, 0, 0", "0, 0, 0, 101", "0, 0, -1, 0", "0, 0, 0, 0, 0", "x, 0, 0, 0" })
            Check($"CMYK \"{bad}\" is refused", !ColourMaths.TryParseCmyk(bad, out _));
    }

    // ---- round trips ----------------------------------------------------------------------------

    /// <summary>
    /// Every one of the 16,777,216 opaque colours out to HSV, HSL and CMYK and back, exactly. The
    /// display text is rounded, so separately: typing back what a field shows lands within a few
    /// steps of where it started.
    /// </summary>
    private void RoundTripTests()
    {
        long hsvMiss = 0, hslMiss = 0, cmykMiss = 0;
        Color? firstHsv = null, firstHsl = null, firstCmyk = null;
        for (int r = 0; r < 256; r++)
        for (int g = 0; g < 256; g++)
        for (int b = 0; b < 256; b++)
        {
            var c = Color.FromRgb((byte)r, (byte)g, (byte)b);
            if (ColourMaths.FromHsv(ColourMaths.ToHsv(c)) != c) { hsvMiss++; firstHsv ??= c; }
            if (ColourMaths.FromHsl(ColourMaths.ToHsl(c)) != c) { hslMiss++; firstHsl ??= c; }
            if (ColourMaths.FromCmyk(ColourMaths.ToCmyk(c)) != c) { cmykMiss++; firstCmyk ??= c; }
        }
        Check("every colour survives RGB > HSV > RGB", hsvMiss == 0, $"{hsvMiss} differ, first {firstHsv}");
        Check("every colour survives RGB > HSL > RGB", hslMiss == 0, $"{hslMiss} differ, first {firstHsl}");
        Check("every colour survives RGB > CMYK > RGB", cmykMiss == 0, $"{cmykMiss} differ, first {firstCmyk}");

        int worstHsv = 0, worstHsl = 0, worstCmyk = 0;
        for (int r = 0; r < 256; r += 5)
        for (int g = 0; g < 256; g += 5)
        for (int b = 0; b < 256; b += 5)
        {
            var c = Color.FromRgb((byte)r, (byte)g, (byte)b);
            ColourMaths.TryParseHsv(ColourMaths.FormatHsv(ColourMaths.ToHsv(c)), out var hsv);
            ColourMaths.TryParseHsl(ColourMaths.FormatHsl(ColourMaths.ToHsl(c)), out var hsl);
            ColourMaths.TryParseCmyk(ColourMaths.FormatCmyk(ColourMaths.ToCmyk(c)), out var cmyk);
            worstHsv = Math.Max(worstHsv, Distance(c, ColourMaths.FromHsv(hsv)));
            worstHsl = Math.Max(worstHsl, Distance(c, ColourMaths.FromHsl(hsl)));
            worstCmyk = Math.Max(worstCmyk, Distance(c, ColourMaths.FromCmyk(cmyk)));
        }
        Check($"HSV text re-entered lands within 4 per channel (worst {worstHsv})", worstHsv <= 4);
        Check($"HSL text re-entered lands within 4 per channel (worst {worstHsl})", worstHsl <= 4);
        Check($"CMYK text re-entered lands within 4 per channel (worst {worstCmyk})", worstCmyk <= 4);

        Check("RGB text round trips exactly", ColourMaths.TryParseRgb(ColourMaths.FormatRgb(Reference), out var back) && back == Reference);
        Check("hex text round trips exactly", ColourMaths.TryParseHex(ColourMaths.FormatHex(Reference), out var back2) && back2 == Reference);
    }

    private static int Distance(Color a, Color b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    // ---- edge colours ---------------------------------------------------------------------------

    private void EdgeTests()
    {
        Equal("black HSV", new Hsv(0, 0, 0), ColourMaths.ToHsv(Colors.Black));
        Equal("black HSL", new Hsl(0, 0, 0), ColourMaths.ToHsl(Colors.Black));
        Equal("black CMYK is all K", new Cmyk(0, 0, 0, 1), ColourMaths.ToCmyk(Colors.Black));
        Equal("white HSV", new Hsv(0, 0, 1), ColourMaths.ToHsv(Colors.White));
        Equal("white HSL", new Hsl(0, 0, 1), ColourMaths.ToHsl(Colors.White));
        Equal("white CMYK is all zero", new Cmyk(0, 0, 0, 0), ColourMaths.ToCmyk(Colors.White));

        Equal("any hue at value 0 is black", Colors.Black, ColourMaths.FromHsv(new Hsv(123, 0.7, 0)));
        Equal("any hue at lightness 0 is black", Colors.Black, ColourMaths.FromHsl(new Hsl(200, 1, 0)));
        Equal("any hue at lightness 1 is white", Colors.White, ColourMaths.FromHsl(new Hsl(200, 1, 1)));
        Equal("CMYK with full K is black whatever else", Colors.Black, ColourMaths.FromCmyk(new Cmyk(0.3, 0.6, 0.9, 1)));

        bool greysOk = true;
        for (int v = 0; v < 256; v++)
        {
            var grey = Color.FromRgb((byte)v, (byte)v, (byte)v);
            var hsv = ColourMaths.ToHsv(grey);
            var hsl = ColourMaths.ToHsl(grey);
            var cmyk = ColourMaths.ToCmyk(grey);
            greysOk &= hsv.H == 0 && hsv.S == 0 && hsl.H == 0 && hsl.S == 0 && cmyk.C == 0 && cmyk.M == 0 && cmyk.Y == 0;
            // Saturation 0 makes hue irrelevant: the picker keeps its own hue, and this is why it can.
            greysOk &= ColourMaths.FromHsv(new Hsv(250, 0, hsv.V)) == grey && ColourMaths.FromHsl(new Hsl(77, 0, hsl.L)) == grey;
        }
        Check("all 256 greys have no hue or saturation, and come back from any hue", greysOk);

        var red = Color.FromRgb(255, 0, 0);
        Equal("hue 360 is red", red, ColourMaths.FromHsv(new Hsv(360, 1, 1)));
        Equal("hue 0 is red", red, ColourMaths.FromHsv(new Hsv(0, 1, 1)));
        Equal("hue 720 folds to red", red, ColourMaths.FromHsv(new Hsv(720, 1, 1)));
        Equal("hue -120 folds to blue", Color.FromRgb(0, 0, 255), ColourMaths.FromHsv(new Hsv(-120, 1, 1)));
        Equal("HSL hue 360 is red", red, ColourMaths.FromHsl(new Hsl(360, 1, 0.5)));
        Check("parsing 360° gives hue 0", ColourMaths.TryParseHsv("360°, 100%, 100%", out var h360) && h360.H == 0
            && ColourMaths.FromHsv(h360) == red);
        Equal("pure yellow sits at 60°", 60.0, ColourMaths.ToHsv(Color.FromRgb(255, 255, 0)).H);
        Equal("pure cyan sits at 180°", 180.0, ColourMaths.ToHsl(Color.FromRgb(0, 255, 255)).H);
        Equal("pure magenta sits at 300°", 300.0, ColourMaths.ToHsv(Color.FromRgb(255, 0, 255)).H);
        Equal("out-of-range HSV is clamped, not wrapped", red, ColourMaths.FromHsv(new Hsv(0, 2, 2)));
        Equal("NaN saturation reads as grey", Color.FromRgb(255, 255, 255), ColourMaths.FromHsv(new Hsv(10, double.NaN, 1)));
        Equal("colour maths returns opaque colours", (byte)255, ColourMaths.FromHsl(new Hsl(10, 0.5, 0.5)).A);
    }

    // ---- theme.json ------------------------------------------------------------------------------

    private string TempFile(string name, string? content = null)
    {
        var path = Path.Combine(_dir, name);
        if (content != null) File.WriteAllText(path, content);
        return path;
    }

    private static int LogCount(string text)
    {
        try
        {
            return File.Exists(DiagnosticLog.FilePath)
                ? Regex.Matches(File.ReadAllText(DiagnosticLog.FilePath), Regex.Escape(text)).Count
                : 0;
        }
        catch (IOException) { return -1; }
    }

    private void StoreTests()
    {
        var defaults = ThemeRoles.Defaults();
        bool SameAs(Dictionary<string, Color> a, IReadOnlyDictionary<string, Color> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

        Check("a missing file means all defaults", SameAs(ThemeStore.Load(TempFile("missing.json")), defaults));

        // Save then load.
        var colours = ThemeRoles.Defaults();
        colours["PanelBackground"] = Color.FromRgb(0x12, 0x34, 0x56);
        colours["OverlayDim"] = Color.FromArgb(0x80, 0x11, 0x22, 0x33);
        var nested = Path.Combine(_dir, "does", "not", "exist", "theme.json");
        Check("save creates the folder and reports success", ThemeStore.Save(nested, colours) && File.Exists(nested));
        Check("what was saved loads back the same", SameAs(ThemeStore.Load(nested), colours));
        using (var doc = JsonDocument.Parse(File.ReadAllText(nested)))
        {
            var root = doc.RootElement;
            Equal("saved file says version 1", 1, root.GetProperty("version").GetInt32());
            var map = root.GetProperty("colours");
            Equal("saved file lists every role", ThemeRoles.All.Count, map.EnumerateObject().Count());
            Equal("a plain role is written #RRGGBB", "#123456", map.GetProperty("PanelBackground").GetString());
            Equal("the alpha role is written #AARRGGBB", "#80112233", map.GetProperty("OverlayDim").GetString());
        }

        // Unknown and missing keys.
        var unknown = TempFile("unknown.json", """{ "version": 1, "colours": { "NotARole": "#FFFFFF", "PanelBackground": "#010203" } }""");
        var loaded = ThemeStore.Load(unknown);
        Equal("a known key is applied", Color.FromRgb(1, 2, 3), loaded["PanelBackground"]);
        Check("an unknown key is ignored", !loaded.ContainsKey("NotARole"));
        Equal("every role is still present", ThemeRoles.All.Count, loaded.Count);
        Equal("a role the file leaves out gets its default", defaults["GridRow"], loaded["GridRow"]);

        // Malformed values: default for that role, noted once.
        int before = LogCount("Theme colour for PanelBackground is not a usable colour");
        var malformed = TempFile("malformed.json",
            """{ "version": 1, "colours": { "PanelBackground": "#zzz", "GridRow": 42, "GridText": "#12345", "MainText": "#abc" } }""");
        loaded = ThemeStore.Load(malformed);
        Equal("a non-hex value gets the default", defaults["PanelBackground"], loaded["PanelBackground"]);
        Equal("a number instead of a string gets the default", defaults["GridRow"], loaded["GridRow"]);
        Equal("a five-digit hex gets the default", defaults["GridText"], loaded["GridText"]);
        Equal("a good #rgb beside them still applies", Color.FromRgb(0xAA, 0xBB, 0xCC), loaded["MainText"]);
        Equal("the malformed value is logged", before + 1, LogCount("Theme colour for PanelBackground is not a usable colour"));
        ThemeStore.Load(malformed);
        Equal("and logged once, not again on the next load", before + 1, LogCount("Theme colour for PanelBackground is not a usable colour"));

        // Alpha.
        loaded = ThemeStore.Load(TempFile("alpha1.json", """{ "colours": { "OverlayDim": "#112233" } }"""));
        Equal("six digits for the alpha role keep its alpha", Color.FromArgb(0xCC, 0x11, 0x22, 0x33), loaded["OverlayDim"]);
        loaded = ThemeStore.Load(TempFile("alpha2.json", """{ "colours": { "OverlayDim": "#40112233" } }"""));
        Equal("eight digits for the alpha role are taken as written", Color.FromArgb(0x40, 0x11, 0x22, 0x33), loaded["OverlayDim"]);
        loaded = ThemeStore.Load(TempFile("alpha3.json", """{ "colours": { "PanelBackground": "#80112233" } }"""));
        Equal("alpha on a role without it is refused", defaults["PanelBackground"], loaded["PanelBackground"]);
        loaded = ThemeStore.Load(TempFile("alpha4.json", """{ "colours": { "PanelBackground": "#FF112233" } }"""));
        Equal("fully opaque eight digits on a plain role are fine", Color.FromRgb(0x11, 0x22, 0x33), loaded["PanelBackground"]);

        // Broken files: all defaults, never a throw.
        foreach (var (name, content) in new[]
                 {
                     ("broken.json", "{ \"colours\": { \"PanelBackground\": \"#010203\" "),
                     ("array.json", "[1, 2, 3]"),
                     ("nocolours.json", "{ \"version\": 1 }"),
                     ("colourslist.json", "{ \"colours\": [\"#010203\"] }"),
                     ("empty.json", ""),
                     ("binary.json", "\u0000\u0001\u0002"),
                 })
        {
            Check($"{name} loads as all defaults", SameAs(ThemeStore.Load(TempFile(name, content)), defaults));
        }
        var huge = TempFile("huge.json", "{ \"colours\": { \"PanelBackground\": \"#010203\" }, \"pad\": \"" + new string('x', 70_000) + "\" }");
        Check("an oversize file is refused unread", SameAs(ThemeStore.Load(huge), defaults));
        Equal("a file with no version is still read", Color.FromRgb(1, 2, 3),
            ThemeStore.Load(TempFile("noversion.json", """{ "colours": { "PanelBackground": "#010203" } }"""))["PanelBackground"]);

        // A save that cannot happen says so and does not throw.
        var folder = Path.Combine(_dir, "a folder");
        Directory.CreateDirectory(folder);
        Check("saving over a folder fails quietly", !ThemeStore.Save(folder, defaults));
        Check("ThemeStore.FilePath is Data\\theme.json beside the exe",
            ThemeStore.FilePath == Path.Combine(AppContext.BaseDirectory, "Data", "theme.json"));
    }

    // ---- theme service ---------------------------------------------------------------------------

    private void ServiceTests()
    {
        var defaults = ThemeRoles.Defaults();
        var d = ThemeService.BuildDictionary(defaults);
        bool allThere = ThemeRoles.All.All(r => d[r.Key] is SolidColorBrush b && b.Color == r.Default && b.IsFrozen);
        Check("BuildDictionary has a frozen brush per role in its default colour", allThere);
        Equal("and nothing else besides the SystemColors aliases", ThemeRoles.All.Count + ThemeRoles.SystemAliases.Count, d.Count);
        Check("selected-row text follows Grid text", ReferenceEquals(d[SystemColors.HighlightTextBrushKey], d["GridText"])
            && ReferenceEquals(d[SystemColors.InactiveSelectionHighlightTextBrushKey], d["GridText"]));

        var old = (SolidColorBrush)d["PanelBackground"];
        ThemeService.SetBrush(d, "PanelBackground", Colors.Red);
        var replaced = (SolidColorBrush)d["PanelBackground"];
        Check("SetBrush puts a new brush in", !ReferenceEquals(old, replaced) && replaced.Color == Colors.Red && replaced.IsFrozen);
        Equal("and leaves the old brush as it was", defaults["PanelBackground"], old.Color);
        ThemeService.SetBrush(d, "PanelBackground", Colors.Red);
        Check("setting the same colour again keeps the same brush", ReferenceEquals(replaced, d["PanelBackground"]));

        ThemeService.SetBrush(d, "GridText", Colors.Lime);
        Check("changing Grid text moves both SystemColors aliases with it",
            ((SolidColorBrush)d[SystemColors.HighlightTextBrushKey]).Color == Colors.Lime
            && ReferenceEquals(d[SystemColors.InactiveSelectionHighlightTextBrushKey], d["GridText"]));

        var partial = new Dictionary<string, Color> { ["GridRow"] = Colors.Blue, ["NotARole"] = Colors.Pink };
        var p = ThemeService.BuildDictionary(partial);
        Equal("a partial map fills the rest with defaults", defaults["PanelBackground"], ((SolidColorBrush)p["PanelBackground"]).Color);
        Equal("and applies what it has", Colors.Blue, ((SolidColorBrush)p["GridRow"]).Color);
        Check("and ignores keys that are not roles", !p.Contains("NotARole"));

        ThemeService.Apply(partial);
        Check("Apply with no Application running only records the colours",
            Application.Current == null && ThemeService.Current["GridRow"] == Colors.Blue
            && ThemeService.Current.Count == ThemeRoles.All.Count);
        ThemeService.Apply(defaults);
    }

    // ---- MainWindow.xaml -------------------------------------------------------------------------

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FleetFinder", "MainWindow.xaml")))
            dir = dir.Parent;
        if (dir == null) throw new FileNotFoundException("FleetFinder\\MainWindow.xaml not found above " + AppContext.BaseDirectory);
        return Path.Combine([dir.FullName, .. parts]);
    }

    /// <summary>Resource keys the XAML may look up that are not colours: the text box menu.</summary>
    private static readonly HashSet<string> NonColourKeys = new(StringComparer.Ordinal) { "TextBoxMenu" };

    private static readonly Regex HexLiteral = new(@"#[0-9A-Fa-f]{3,8}\b");
    private static readonly Regex DynamicKey = new(@"\{DynamicResource\s+([^}\s]+)\s*\}");

    /// <summary>Property names that take a brush or a colour in this XAML.</summary>
    private static bool IsColourProperty(string name)
    {
        var last = name[(name.LastIndexOf('.') + 1)..];
        return last is "Background" or "Foreground" or "Fill" or "Stroke" or "Color" or "OpacityMask"
               || last.EndsWith("Brush", StringComparison.Ordinal)
               || last.EndsWith("Background", StringComparison.Ordinal)
               || last.EndsWith("Foreground", StringComparison.Ordinal);
    }

    /// <summary>What a colour property may hold: a role, Transparent, or a binding to another
    /// property that is itself themed.</summary>
    private static bool IsAllowedColourValue(string value) =>
        value == "Transparent"
        || Regex.IsMatch(value, @"^\{DynamicResource\s+[A-Za-z]+\s*\}$")
        || value.StartsWith("{Binding", StringComparison.Ordinal)
        || value.StartsWith("{TemplateBinding", StringComparison.Ordinal);

    private void XamlTests()
    {
        var path = RepoFile("FleetFinder", "MainWindow.xaml");
        var doc = XDocument.Load(path, LoadOptions.SetLineInfo);
        var problems = new List<string>();
        var used = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var el in doc.Descendants())
        {
            int line = ((System.Xml.IXmlLineInfo)el).LineNumber;
            string elName = el.Name.LocalName;

            foreach (var attr in el.Attributes())
            {
                string name = attr.Name.LocalName, value = attr.Value;
                if (HexLiteral.IsMatch(value))
                    problems.Add($"line {line}: {elName} {name}=\"{value}\" is a hex colour");
                foreach (Match m in DynamicKey.Matches(value))
                    used[m.Groups[1].Value] = used.GetValueOrDefault(m.Groups[1].Value) + 1;
                if (IsColourProperty(name) && !IsAllowedColourValue(value))
                    problems.Add($"line {line}: {elName} {name}=\"{value}\" is not a role, Transparent or a binding");
            }

            // <Setter Property="Foreground" Value="..."/>: the property is named in one attribute
            // and the colour sits in another.
            if (elName == "Setter" && el.Attribute("Property")?.Value is { } prop && IsColourProperty(prop)
                && el.Attribute("Value")?.Value is { } setterValue && !IsAllowedColourValue(setterValue))
            {
                problems.Add($"line {line}: Setter {prop} Value=\"{setterValue}\" is not a role, Transparent or a binding");
            }
            if (elName == "Setter" && el.Attribute("Property")?.Value is { } p2 && IsColourProperty(p2) && el.Attribute("Value") == null)
                problems.Add($"line {line}: Setter {p2} has its value as an element; use a role");

            // Brush objects written out in full. The only ones allowed are the SystemColors
            // overrides that make selection invisible, which are Transparent.
            if (elName is "SolidColorBrush" && el.Attribute("Color")?.Value != "Transparent")
                problems.Add($"line {line}: a SolidColorBrush that is not Transparent");
            if (elName is "LinearGradientBrush" or "RadialGradientBrush" or "GradientStop" or "Color")
                problems.Add($"line {line}: {elName} written out in the XAML");
        }

        Check("no colour literals outside the roles", problems.Count == 0, string.Join("; ", problems));

        var unknown = used.Keys.Where(k => ThemeRoles.Find(k) is null && !NonColourKeys.Contains(k)).ToList();
        Check("every DynamicResource key is a role (or the text box menu)", unknown.Count == 0, string.Join(", ", unknown));

        // Text boxes take their selection colour and right-click menu from the window's text box
        // style; the menu is one per text box (x:Shared).
        var textBoxStyle = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Style"
            && e.Attribute("TargetType")?.Value == "TextBox" && e.Attribute(Xaml + "Key") == null
            && e.Parent?.Name.LocalName == "Window.Resources");
        string? Setter(XElement? style, string property) => style?.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Setter" && e.Attribute("Property")?.Value == property)?.Attribute("Value")?.Value;
        Equal("text boxes select in the TextSelection role", "{DynamicResource TextSelection}", Setter(textBoxStyle, "SelectionBrush"));
        Equal("text boxes get the themed right-click menu", "{DynamicResource TextBoxMenu}", Setter(textBoxStyle, "ContextMenu"));
        var menu = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ContextMenu" && e.Attribute(Xaml + "Key")?.Value == "TextBoxMenu");
        Check("the text box menu exists and is not shared", menu?.Attribute(Xaml + "Shared")?.Value == "False");
        Equal("it has Cut, Copy and Paste, the items WPF's own menu has",
            "Cut,Copy,Paste",
            string.Join(",", menu?.Elements().Where(e => e.Name.LocalName == "MenuItem")
                .Select(e => e.Attribute("Command")?.Value.Replace("ApplicationCommands.", "")) ?? []));
        // Without this the items are never aimed at the text box and open greyed out.
        Equal("and the handler that aims them at the right-clicked box", "TextBoxMenu_Opened", menu?.Attribute("Opened")?.Value);
        var unused = ThemeRoles.All.Where(r => !used.ContainsKey(r.Key)).Select(r => r.Key).ToList();
        Check("every role is used in the XAML", unused.Count == 0, string.Join(", ", unused));
        Console.WriteLine($"   ({used.Values.Sum()} DynamicResource uses across {used.Count} roles)");

        var raw = File.ReadAllText(path);
        Check("no StaticResource left for the old Panel, Accent or Muted brushes",
            !Regex.IsMatch(raw, @"StaticResource\s+(Panel|Accent|Muted)\}"));

        // The Needed cell editor's border never showed in a colour of its own: the editor has the
        // keyboard while open and the focus trigger drew over it. It is the focus role now, and
        // the role that did nothing is gone.
        var editorBorder = doc.Descendants().Where(e => e.Name.LocalName == "DataGridTextColumn.EditingElementStyle")
            .SelectMany(e => e.Descendants()).FirstOrDefault(e => e.Name.LocalName == "Setter" && e.Attribute("Property")?.Value == "BorderBrush");
        Equal("the Needed cell editor's border is the typing role", "{DynamicResource InputFocusBorder}", editorBorder?.Attribute("Value")?.Value);
        Check("the old cell editor role is gone", ThemeRoles.Find("CellEditorBorder") == null && !raw.Contains("CellEditorBorder"));
    }

    /// <summary>
    /// The roles' defaults are the only colours the C# may name. Comments are skipped, so the
    /// examples in doc comments do not count; string literals and the usual constructors do.
    /// </summary>
    private void CodeTests()
    {
        var root = RepoFile("FleetFinder");
        var allowed = Path.Combine(root, "Theming", "ThemeRoles.cs");
        var literal = new Regex(@"""#[0-9A-Fa-f]{3,8}""|\bColors\.[A-Z]|Color\.From(Rgb|Argb|ScRgb)\(\s*(0x[0-9A-Fa-f]+|\d)|\bBrushes\.[A-Z]");
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            if (rel.StartsWith("bin" + Path.DirectorySeparatorChar) || rel.StartsWith("obj" + Path.DirectorySeparatorChar)) continue;
            if (string.Equals(file, allowed, StringComparison.OrdinalIgnoreCase)) continue;
            int n = 0;
            foreach (var rawLine in File.ReadLines(file))
            {
                n++;
                int comment = rawLine.IndexOf("//", StringComparison.Ordinal);
                var code = comment >= 0 ? rawLine[..comment] : rawLine;
                if (literal.IsMatch(code)) hits.Add($"{rel}:{n}");
            }
        }
        Check("no colour literals in the C# outside ThemeRoles.cs", hits.Count == 0, string.Join(", ", hits));
    }

    // ---- colour picker state ----------------------------------------------------------------------

    private static readonly Color Orange = Color.FromRgb(0xFF, 0x75, 0x00);
    private static readonly Color Black = Color.FromRgb(0, 0, 0);
    private static Color Grey(byte v) => Color.FromRgb(v, v, v);

    private static (double H, double S, double V) Hsv3(ColourPickerState s) => (s.Hue, s.Saturation, s.Value);

    /// <summary>
    /// The picker's state on its own. The headline cases are the classic picker bug: dragging to
    /// black or the grey edge and back, and typing a grey, must not lose the hue.
    /// </summary>
    private void PickerStateTests()
    {
        const ColourPickerState.Field Hex = ColourPickerState.Field.Hex, Rgb = ColourPickerState.Field.Rgb,
            Cmyk = ColourPickerState.Field.Cmyk, Hsv = ColourPickerState.Field.Hsv, Hsl = ColourPickerState.Field.Hsl;

        var s = new ColourPickerState();
        Equal("a new picker is opaque black", Black, s.Colour);

        // The reference picker's own numbers for its own colour.
        s.SetColour(Reference);
        Equal("reference HEX field", "#fcba03", s.Text(Hex));
        Equal("reference RGB field", "252, 186, 3", s.Text(Rgb));
        Equal("reference CMYK field", "0%, 26%, 99%, 1%", s.Text(Cmyk));
        Equal("reference HSV field", "44°, 99%, 99%", s.Text(Hsv));
        Equal("reference HSL field", "44°, 98%, 50%", s.Text(Hsl));
        Near("reference square handle across (saturation 249/252)", 249.0 / 252, s.SquareX);
        Near("reference square handle down (1 - 252/255)", 1 - 252.0 / 255, s.SquareY);
        Near("reference strip handle (hue 44.1 of 360)", (183.0 / 249 * 60) / 360, s.StripX);
        Equal("the pure hue for the square's corner", Color.FromRgb(0xFF, 0xBB, 0x00), s.PureHue);

        // Drag to black and back.
        s.SetColour(Orange);
        var (hue, sat, _) = Hsv3(s);
        Near("orange's hue is 27.5", 117.0 / 255 * 60, hue);
        s.SetSquare(s.SquareX, 1);
        Equal("square dragged to the bottom is black", Black, s.Colour);
        Equal("black keeps the hue", hue, s.Hue);
        Equal("black keeps the saturation", sat, s.Saturation);
        Equal("black shows the kept hue and saturation in HSV", "28°, 100%, 0%", s.Text(Hsv));
        s.SetSquare(s.SquareX, 0);
        Equal("dragged back up it is exactly orange again", Orange, s.Colour);

        // Drag to the grey edge and back.
        s.SetSquare(0, 0.5);
        var edge = s.Colour;
        Check("the left edge is grey", edge.R == edge.G && edge.G == edge.B, edge.ToString());
        Equal("the grey edge keeps the hue", hue, s.Hue);
        s.SetSquare(1, 0);
        Equal("dragged back to the corner it is orange again", Orange, s.Colour);

        // Typing greys, black and white.
        Check("HEX takes #808080", s.Commit(Hex, "#808080"));
        Equal("the grey typed is the grey picked", Grey(128), s.Colour);
        Equal("typing a grey into HEX does not snap the hue to red", hue, s.Hue);
        Equal("a grey's saturation is 0, so its handle is on the left edge", 0.0, s.SquareX);
        Equal("HSV shows the strip's hue for a grey", "28°, 0%, 50%", s.Text(Hsv));
        Equal("and so does HSL", "28°, 0%, 50%", s.Text(Hsl));
        Check("HEX takes #000", s.Commit(Hex, "#000"));
        Equal("typing black keeps the hue", hue, s.Hue);
        Check("HEX takes #fff", s.Commit(Hex, "#fff"));
        Equal("typing white keeps the hue", hue, s.Hue);
        Check("RGB takes a grey", s.Commit(Rgb, "77, 77, 77"));
        Equal("a grey typed into RGB keeps the hue", hue, s.Hue);
        Check("CMYK takes a grey", s.Commit(Cmyk, "0%, 0%, 0%, 50%"));
        Equal("a grey typed into CMYK keeps the hue", hue, s.Hue);
        s.SetSquare(1, 0);
        Equal("after all of that the corner is still orange", Orange, s.Colour);
        s.SetColour(Grey(60));
        Equal("a grey from the host keeps the hue as well", hue, s.Hue);

        // The host echoing the picker's own colour back (a two-way binding does this on every
        // drag step) must not re-derive anything from the rounded bytes.
        s.SetStrip(123.4 / 360);
        s.SetSquare(0.37, 0.42);
        var before = Hsv3(s);
        s.SetColour(s.Colour);
        Equal("its own colour echoed back moves nothing", before, Hsv3(s));
        s.SetSquare(0.37, 1);
        before = Hsv3(s);
        s.SetColour(s.Colour);
        Equal("nor when that colour is black", before, Hsv3(s));

        // Tabbing through the fields commits each one unchanged; that must not nudge anything.
        s.SetStrip(201.7 / 360);
        s.SetSquare(0.613, 0.287);
        before = Hsv3(s);
        foreach (var f in Enum.GetValues<ColourPickerState.Field>())
            Check($"{f} field committed as shown is accepted", s.Commit(f, s.Text(f)));
        Equal("and changes nothing at all", before, Hsv3(s));

        // HSV and HSL say what hue they want, even for a grey.
        Check("HSV takes 200, 0, 50", s.Commit(Hsv, "200, 0, 50"));
        Equal("a typed HSV grey is that grey", Grey(128), s.Colour);
        Equal("and moves the strip to the typed hue", 200.0, s.Hue);
        Check("HSL takes 300°, 0%, 25%", s.Commit(Hsl, "300°, 0%, 25%"));
        Equal("a typed HSL grey is that grey", Grey(64), s.Colour);
        Equal("and moves the strip to its hue", 300.0, s.Hue);
        s.SetSquare(0.4, 0.5);
        double keptSat = s.Saturation;
        Check("HSL takes lightness 0", s.Commit(Hsl, "120, 50, 0"));
        Equal("HSL lightness 0 is black", Black, s.Colour);
        Equal("with the typed hue", 120.0, s.Hue);
        Equal("and the square's own saturation", keptSat, s.Saturation);
        Check("HSL takes the reference text", s.Commit(Hsl, "44°, 98%, 50%"));
        Check("which lands within 4 of the reference", Distance(s.Colour, Reference) <= 4, s.Colour.ToString());

        // Bad input is refused and changes nothing.
        s.SetColour(Orange);
        foreach (var (f, bad) in new (ColourPickerState.Field, string?)[]
                 {
                     (Hex, "zz"), (Hex, ""), (Hex, "#12345"), (Rgb, "300, 0, 0"), (Rgb, "1, 2"), (Cmyk, "1, 2, 3"),
                     (Hsv, "abc"), (Hsv, "400, 0, 0"), (Hsl, "10, 200, 50"), (Hsl, null),
                 })
        {
            Check($"{f} \"{bad ?? "null"}\" is refused", !s.Commit(f, bad));
        }
        Equal("and the colour is untouched", Orange, s.Colour);
        Equal("and so is the hue", hue, s.Hue);

        // Alpha comes in, goes out, and is never edited.
        s.SetColour(Color.FromArgb(0xCC, 0x11, 0x22, 0x33));
        Equal("alpha from the host is kept", (byte)0xCC, s.Alpha);
        s.SetSquare(0.5, 0.5);
        Equal("a drag keeps it", (byte)0xCC, s.Colour.A);
        Check("HEX takes eight digits", s.Commit(Hex, "#80ff0000"));
        Equal("eight-digit HEX: the colour is taken, the alpha is not", Color.FromArgb(0xCC, 0xFF, 0, 0), s.Colour);
        Equal("HEX never shows alpha", "#ff0000", s.Text(Hex));
        s.SetColour(Color.FromArgb(0x40, 0xFF, 0, 0));
        Equal("the same colour with a new alpha takes the alpha", (byte)0x40, s.Colour.A);

        // The ends of the strip and the edges of the square.
        s.SetColour(Orange);
        s.SetStrip(1);
        Equal("the right end of the strip is red", Color.FromRgb(0xFF, 0, 0), s.Colour);
        Equal("and its handle stays at the right end", 1.0, s.StripX);
        Equal("shown as 0°", "0°, 100%, 100%", s.Text(Hsv));
        s.SetStrip(-0.5);
        Equal("left of the strip is 0", 0.0, s.Hue);
        s.SetStrip(2);
        Equal("right of the strip is 360", 360.0, s.Hue);
        s.SetStrip(double.NaN);
        Equal("NaN on the strip is 0, not NaN", 0.0, s.Hue);
        s.SetSquare(-1, 2);
        Check("below left of the square pins to its bottom left corner", s.Saturation == 0 && s.Value == 0);
        s.SetSquare(5, -3);
        Check("above right pins to the top right corner", s.Saturation == 1 && s.Value == 1);

        s.SetStrip(240.0 / 360);
        s.SetSquare(0, 0);
        Equal("top left of the square is white", Color.FromRgb(255, 255, 255), s.Colour);
        s.SetSquare(1, 0);
        Equal("top right is the pure hue", Color.FromRgb(0, 0, 255), s.Colour);
        s.SetSquare(0.3, 1);
        Equal("the bottom is black", Black, s.Colour);
    }

    // ---- colour picker control ------------------------------------------------------------------

    private sealed class ColourSource : INotifyPropertyChanged
    {
        private Color _colour;
        public event PropertyChangedEventHandler? PropertyChanged;
        public Color Colour
        {
            get => _colour;
            set { _colour = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Colour))); }
        }
    }

    private static T Named<T>(FrameworkElement scope, string name) where T : class =>
        scope.FindName(name) as T ?? throw new InvalidOperationException($"no {typeof(T).Name} named {name}");

    /// <summary>What leaving a field does, without a window to move focus in.</summary>
    private static void Leave(TextBox box) =>
        box.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, box, null)
            { RoutedEvent = Keyboard.LostKeyboardFocusEvent });

    /// <summary>
    /// The real control, laid out at 600 wide with no window, driven through the same methods its
    /// mouse handlers call.
    /// </summary>
    private void PickerControlTests()
    {
        var picker = new ColourPicker();
        picker.Measure(new Size(600, double.PositiveInfinity));
        picker.Arrange(new Rect(picker.DesiredSize));
        picker.UpdateLayout();
        var square = Named<FrameworkElement>(picker, "Square");
        var strip = Named<FrameworkElement>(picker, "Strip");
        Check($"it lays out ({square.ActualWidth:F0}x{square.ActualHeight:F0} square, {strip.ActualWidth:F0} strip)",
            square.ActualWidth > 100 && square.ActualHeight > 100 && strip.ActualWidth > 100);
        Equal("an unbound picker starts opaque", (byte)255, picker.SelectedColor.A);

        var source = new ColourSource { Colour = Orange };
        picker.SetBinding(ColourPicker.SelectedColorProperty, new Binding(nameof(ColourSource.Colour)) { Source = source });
        var hex = Named<TextBox>(picker, "HexBox");
        Equal("a bound colour reaches the picker", Orange, picker.SelectedColor);
        Equal("and its HEX field", "#ff7500", hex.Text);
        double orangeHue = picker.Hue;

        int raised = 0;
        picker.SelectedColorChanged += (_, _) => raised++;
        picker.PickSquareAt(new Point(square.ActualWidth / 2, square.ActualHeight / 2));
        var firstStep = source.Colour;
        Check("pressing on the square changes the bound source at once", firstStep != Orange && firstStep == picker.SelectedColor,
            firstStep.ToString());
        picker.PickSquareAt(new Point(square.ActualWidth / 4, square.ActualHeight / 3));
        Check("and so does each drag step", source.Colour != firstStep && source.Colour == picker.SelectedColor);
        Equal("SelectedColorChanged was raised for each", 2, raised);

        picker.PickSquareAt(new Point(square.ActualWidth / 4, square.ActualHeight * 3));
        Equal("dragging past the bottom pins to black", Black, source.Colour);
        Equal("and keeps the hue", orangeHue, picker.Hue);
        picker.PickSquareAt(new Point(square.ActualWidth * 2, -40));
        Equal("dragging out past the top right is orange again", Orange, source.Colour);

        var squareHandle = Named<FrameworkElement>(picker, "SquareHandle");
        var stripHandle = Named<FrameworkElement>(picker, "StripHandle");
        Near("the square handle is centred on the top right corner",
            square.ActualWidth, Canvas.GetLeft(squareHandle) + squareHandle.Width / 2, 1e-6);
        Near("and on the top edge", 0, Canvas.GetTop(squareHandle) + squareHandle.Height / 2, 1e-6);
        Near("the strip handle is at orange's hue",
            orangeHue / 360 * strip.ActualWidth, Canvas.GetLeft(stripHandle) + stripHandle.Width / 2, 1e-6);
        Equal("the swatch shows the colour",
            Orange, (Named<Border>(picker, "Swatch").Background as SolidColorBrush)?.Color ?? default);

        picker.PickHueAt(new Point(strip.ActualWidth * 300 / 360, 5));
        Equal("the strip at 300° gives magenta", Color.FromRgb(0xFF, 0, 0xFF), source.Colour);

        // Typing.
        hex.Text = "#808080";
        Leave(hex);
        Equal("a grey typed into HEX reaches the source when the field is left", Grey(128), source.Colour);
        Near("and the strip stays at 300°", 300, picker.Hue);
        Near("its handle with it", 300.0 / 360 * strip.ActualWidth, Canvas.GetLeft(stripHandle) + stripHandle.Width / 2, 1e-6);
        var rgb = Named<TextBox>(picker, "RgbBox");
        rgb.Text = "999, 0, 0";
        Leave(rgb);
        Equal("bad RGB puts the field back", "128, 128, 128", rgb.Text);
        Equal("and changes nothing", Grey(128), source.Colour);
        var hsv = Named<TextBox>(picker, "HsvBox");
        hsv.Text = "200 50 100";
        Leave(hsv);
        var expected = ColourMaths.FromHsv(new Hsv(200, 0.5, 1));
        Equal("HSV typed reaches the source", expected, source.Colour);
        Equal("and tidies itself into the display form", "200°, 50%, 100%", hsv.Text);
        Equal("and every other field follows", ColourMaths.FormatHex(expected), hex.Text);
        Equal("CMYK too", ColourMaths.FormatCmyk(ColourMaths.ToCmyk(expected)), Named<TextBox>(picker, "CmykBox").Text);

        // Typed but not entered: the host applies it before taking the colour.
        var hsl = Named<TextBox>(picker, "HslBox");
        hsl.Text = "60, 100, 50";
        Equal("text typed and not yet entered changes nothing yet", expected, source.Colour);
        picker.CommitPendingEdits();
        Equal("CommitPendingEdits applies it", Color.FromRgb(0xFF, 0xFF, 0), source.Colour);
        hsl.Text = "nonsense";
        picker.CommitPendingEdits();
        Equal("and puts back what it cannot read", "60°, 100%, 50%", hsl.Text);
        Equal("leaving the colour alone", Color.FromRgb(0xFF, 0xFF, 0), source.Colour);

        // Alpha in, alpha out.
        source.Colour = Color.FromArgb(0xCC, 0, 0, 0);
        picker.PickSquareAt(new Point(square.ActualWidth / 2, 10));
        Equal("a drag on a colour with alpha hands the alpha back", (byte)0xCC, source.Colour.A);
        Equal("the fields never show it", 7, hex.Text.Length);

        // The host setting a grey keeps the strip where it was.
        picker.PickHueAt(new Point(strip.ActualWidth / 3, 5));
        source.Colour = Grey(90);
        Near("a grey from the host keeps the strip at 120°", 120, picker.Hue);
    }

    // ---- ColourPicker.xaml ---------------------------------------------------------------------

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// The picker's own XAML: its themed parts use known roles, and its only literal colours are
    /// the colour model's (gradient stops) and the handles' white ring and shadow.
    /// </summary>
    private void PickerXamlTests()
    {
        var doc = XDocument.Load(RepoFile("FleetFinder", "Controls", "ColourPicker.xaml"), LoadOptions.SetLineInfo);
        var problems = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        int allowed = 0;

        foreach (var el in doc.Descendants())
        {
            int line = ((System.Xml.IXmlLineInfo)el).LineNumber;
            string elName = el.Name.LocalName;
            bool colourModel = elName is "GradientStop" or "DropShadowEffect"
                || (elName == "Ellipse" && (el.Attribute(Xaml + "Name")?.Value.EndsWith("Handle", StringComparison.Ordinal) ?? false));

            foreach (var attr in el.Attributes())
            {
                foreach (Match m in DynamicKey.Matches(attr.Value))
                    used.Add(m.Groups[1].Value);
                bool literal = HexLiteral.IsMatch(attr.Value)
                    || (IsColourProperty(attr.Name.LocalName) && !IsAllowedColourValue(attr.Value));
                if (!literal) continue;
                if (colourModel) allowed++;
                else problems.Add($"line {line}: {elName} {attr.Name.LocalName}=\"{attr.Value}\"");
            }

            if (elName == "Setter" && el.Attribute("Property")?.Value is { } prop && IsColourProperty(prop)
                && el.Attribute("Value")?.Value is { } value && !IsAllowedColourValue(value))
            {
                problems.Add($"line {line}: Setter {prop} Value=\"{value}\"");
            }
            if (elName == "SolidColorBrush")
                problems.Add($"line {line}: a SolidColorBrush written out");
        }

        Check($"literal colours only in the gradients and handles ({allowed} there)", problems.Count == 0 && allowed > 0,
            string.Join("; ", problems));
        var unknown = used.Where(k => ThemeRoles.Find(k) is null && !NonColourKeys.Contains(k)).ToList();
        Check("every DynamicResource key in the picker is a role (or the text box menu)", unknown.Count == 0, string.Join(", ", unknown));
        Check("its fields use the selection role and the host's text box menu",
            used.Contains("TextSelection") && used.Contains("TextBoxMenu"));
        Check("its fields use the text box hover and focus roles",
            used.Contains("InputHoverBorder") && used.Contains("InputFocusBorder"));
    }

    // ---- theme draft ----------------------------------------------------------------------------

    private static bool SameColours(IReadOnlyDictionary<string, Color> a, IReadOnlyDictionary<string, Color> b) =>
        ThemeRoles.All.All(r => a.TryGetValue(r.Key, out var x) && b.TryGetValue(r.Key, out var y) && x == y);

    /// <summary>
    /// The Settings page's draft on its own: dirty tracking, Save, Revert, Reset to defaults and
    /// the picker's edit calls. Writes only into this run's temp folder.
    /// </summary>
    private void DraftTests()
    {
        var defaults = ThemeRoles.Defaults();
        var path = Path.Combine(_dir, "draft", "Data", "theme.json");
        var saved = ThemeRoles.Defaults();
        saved["PanelBackground"] = Color.FromRgb(0x10, 0x20, 0x30);
        var applied = new List<IReadOnlyDictionary<string, Color>>();
        var draft = new ThemeDraft(saved, path, c => applied.Add(c));
        var changes = new List<string?>();
        var raised = new List<string?>();
        draft.ColourChanged += k => changes.Add(k);
        draft.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Check("a new draft holds the saved colours", SameColours(draft.Colours, saved));
        Check("and is not dirty", !draft.IsDirty);
        Check("saved colours that are not the shipped ones are not default", !draft.IsDefault);
        Check("nothing is written before Save", !File.Exists(path));

        // Changing one role.
        var teal = Color.FromRgb(0x00, 0x80, 0x80);
        Check("Set says it changed something", draft.Set("GridRow", teal));
        Equal("the draft has the new colour", teal, draft["GridRow"]);
        Check("the draft is dirty", draft.IsDirty);
        Equal("the saved colours are untouched", saved["GridRow"], draft.Saved["GridRow"]);
        Equal("ColourChanged names the role", "GridRow", changes.LastOrDefault());
        Equal("IsDirty was announced once", 1, raised.Count(n => n == nameof(ThemeDraft.IsDirty)));
        int before = changes.Count;
        Check("the same colour again is not a change", !draft.Set("GridRow", teal) && changes.Count == before);
        draft.Set("GridRow", saved["GridRow"]);
        Check("setting it back to the saved colour clears dirty", !draft.IsDirty);
        Equal("and that was announced too", 2, raised.Count(n => n == nameof(ThemeDraft.IsDirty)));

        // Alpha.
        draft.Set("GridText", Color.FromArgb(0x80, 0x11, 0x22, 0x33));
        Equal("a role without alpha is kept opaque", Color.FromRgb(0x11, 0x22, 0x33), draft["GridText"]);
        draft.Set("OverlayDim", Color.FromArgb(0x40, 0x11, 0x22, 0x33));
        Equal("a role with alpha keeps the alpha it is given", Color.FromArgb(0x40, 0x11, 0x22, 0x33), draft["OverlayDim"]);
        bool threw = false;
        try { draft.Set("NotARole", teal); } catch (ArgumentException) { threw = true; }
        Check("an unknown role is refused", threw);

        // Revert.
        changes.Clear();
        draft.Revert();
        Check("Revert goes back to the saved colours", SameColours(draft.Colours, saved));
        Check("and is not dirty", !draft.IsDirty);
        Check("and says everything may have changed", changes.SequenceEqual(new string?[] { null }));
        changes.Clear();
        draft.Revert();
        Check("Revert with nothing to undo says nothing", changes.Count == 0);

        // Reset to defaults: the draft only.
        draft.ResetToDefaults();
        Check("Reset to defaults makes the draft the shipped palette", SameColours(draft.Colours, defaults) && draft.IsDefault);
        Check("which differs from what is saved", draft.IsDirty);
        Check("and is neither applied nor written", applied.Count == 0 && !File.Exists(path));

        // Save.
        draft.Set("ButtonBackground", teal);
        raised.Clear();
        Check("Save reports the file was written", draft.Save());
        Equal("Save put the colours on the app once", 1, applied.Count);
        Check("the colours it applied are the draft", SameColours(applied[0], draft.Colours));
        Check("theme.json was written where it was told, folder and all", File.Exists(path));
        var loaded = ThemeStore.Load(path);
        Check("the file loads back as the draft", SameColours(loaded, draft.Colours));
        Check("what is saved is now the draft", SameColours(draft.Saved, draft.Colours));
        Check("so nothing is unsaved", !draft.IsDirty && raised.Contains(nameof(ThemeDraft.IsDirty)));
        draft.Set("ButtonBackground", Color.FromRgb(1, 2, 3));
        draft.Revert();
        Equal("Revert after a save goes back to that save, not the start", teal, draft["ButtonBackground"]);

        // Reset to defaults then Save: the file is the shipped palette.
        draft.ResetToDefaults();
        draft.Save();
        Check("after Reset and Save the file is all defaults", SameColours(ThemeStore.Load(path), defaults));
        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            Equal("and still lists every role", ThemeRoles.All.Count, doc.RootElement.GetProperty("colours").EnumerateObject().Count());

        // An unsaved change does not outlive the draft: the next start reads the file.
        draft.Set("PanelBackground", teal);
        var nextStart = new ThemeDraft(ThemeStore.Load(path), path, _ => { });
        Check("a draft made from the file has the saved colours, not the unsaved one",
            nextStart["PanelBackground"] == defaults["PanelBackground"] && !nextStart.IsDirty);
        draft.Revert();

        // The picker's edits: a drag, then Cancel.
        draft.Set("GridLines", teal);
        draft.BeginEdit("PanelBackground");
        Equal("the picker's role is remembered", "PanelBackground", draft.EditingKey);
        foreach (var step in new[] { Color.FromRgb(9, 9, 9), Color.FromRgb(40, 40, 90), Color.FromRgb(80, 20, 200) })
            draft.Set("PanelBackground", step);
        draft.CancelEdit();
        Equal("Cancel puts the role back as it was when the picker opened", defaults["PanelBackground"], draft["PanelBackground"]);
        Equal("and leaves other changes alone", teal, draft["GridLines"]);
        Check("and closes the edit", draft.EditingKey == null);
        draft.CancelEdit();
        Equal("Cancel with no picker open does nothing", teal, draft["GridLines"]);

        // OK (or a click outside) keeps it.
        draft.BeginEdit("PanelBackground");
        draft.Set("PanelBackground", Color.FromRgb(80, 20, 200));
        draft.EndEdit();
        draft.CancelEdit();
        Equal("OK keeps the colour, and a Cancel afterwards cannot undo it", Color.FromRgb(80, 20, 200), draft["PanelBackground"]);

        // Opening the picker on a second role keeps the first one's edit.
        draft.BeginEdit("GridRow");
        draft.Set("GridRow", Color.FromRgb(5, 5, 5));
        draft.BeginEdit("GridText");
        draft.Set("GridText", Color.FromRgb(6, 6, 6));
        draft.CancelEdit();
        Check("a second edit cancelled restores only its own role",
            draft["GridRow"] == Color.FromRgb(5, 5, 5) && draft["GridText"] == defaults["GridText"]);

        // Revert or Save part way through an edit ends it.
        draft.BeginEdit("GridRow");
        draft.Revert();
        draft.Set("GridRow", Color.FromRgb(7, 7, 7));
        draft.CancelEdit();
        Equal("Revert ends an open edit, so a later Cancel touches nothing", Color.FromRgb(7, 7, 7), draft["GridRow"]);
        draft.BeginEdit("GridRow");
        draft.Set("GridRow", Color.FromRgb(8, 8, 8));
        draft.Save();
        draft.CancelEdit();
        Equal("Save keeps an open edit", Color.FromRgb(8, 8, 8), draft["GridRow"]);

        // A save that cannot be written: the colours are on the app all the same.
        var folder = Path.Combine(_dir, "draft", "a folder");
        Directory.CreateDirectory(folder);
        applied.Clear();
        var stuck = new ThemeDraft(defaults, folder, c => applied.Add(c));
        stuck.Set("GridRow", teal);
        Check("a save that cannot write says so", !stuck.Save());
        Check("but still applied the colours", applied.Count == 1 && applied[0]["GridRow"] == teal);
        Check("and counts them as saved", !stuck.IsDirty);

        Check("the app's own draft starts from what the app shows", SameColours(ThemeDraft.ForApp().Colours, ThemeService.Current));
    }

    // ---- Settings XAML --------------------------------------------------------------------------

    /// <summary>Keyed styles and the cog drawing, which the Settings files take from the window.</summary>
    private static readonly HashSet<string> WindowKeys = new(StringComparer.Ordinal)
        { "CaptionButton", "CloseCaptionButton", "LinkButton", "StepperButton", "CogGlyph" };

    /// <summary>
    /// The Settings page and its preview follow the same rules as the window: no colour literals,
    /// every DynamicResource a role, or one of the window's own styles they borrow.
    /// </summary>
    private void SettingsXamlTests()
    {
        var windowDoc = XDocument.Load(RepoFile("FleetFinder", "MainWindow.xaml"));
        var windowKeys = windowDoc.Descendants().Select(e => e.Attribute(Xaml + "Key")?.Value).OfType<string>().ToHashSet();
        Check("the styles they borrow are in the window", WindowKeys.All(windowKeys.Contains),
            string.Join(", ", WindowKeys.Where(k => !windowKeys.Contains(k))));

        foreach (var file in new[] { "SettingsPage.xaml", "ThemePreview.xaml" })
        {
            var doc = XDocument.Load(RepoFile("FleetFinder", "Settings", file), LoadOptions.SetLineInfo);
            var problems = new List<string>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in doc.Descendants())
            {
                int line = ((System.Xml.IXmlLineInfo)el).LineNumber;
                string elName = el.Name.LocalName;
                foreach (var attr in el.Attributes())
                {
                    foreach (Match m in DynamicKey.Matches(attr.Value)) used.Add(m.Groups[1].Value);
                    if (HexLiteral.IsMatch(attr.Value)
                        || (IsColourProperty(attr.Name.LocalName) && !IsAllowedColourValue(attr.Value)))
                    {
                        problems.Add($"line {line}: {elName} {attr.Name.LocalName}=\"{attr.Value}\"");
                    }
                }
                if (elName == "Setter" && el.Attribute("Property")?.Value is { } prop && IsColourProperty(prop)
                    && el.Attribute("Value")?.Value is { } value && !IsAllowedColourValue(value))
                {
                    problems.Add($"line {line}: Setter {prop} Value=\"{value}\"");
                }
                if (elName is "SolidColorBrush" or "LinearGradientBrush" or "RadialGradientBrush" or "GradientStop" or "Color")
                    problems.Add($"line {line}: {elName} written out");
            }
            Check($"{file}: no colour literals", problems.Count == 0, string.Join("; ", problems));
            var unknown = used.Where(k => ThemeRoles.Find(k) is null && !WindowKeys.Contains(k)).ToList();
            Check($"{file}: every DynamicResource is a role or a window style", unknown.Count == 0, string.Join(", ", unknown));
            // StaticResource with a type key would find WPF's own light style here, not the window's,
            // because these files are read before they are put in the window. The DataGridCell one
            // is allowed: the window has no DataGridCell style either, so both find the same one.
            var typeStatics = Regex.Matches(File.ReadAllText(RepoFile("FleetFinder", "Settings", file)),
                    @"StaticResource\s+\{x:Type\s+(\w+)\}").Select(m => m.Groups[1].Value)
                .Where(t => t != "DataGridCell").Distinct().ToList();
            Check($"{file}: borrows no window style by type", typeStatics.Count == 0, string.Join(", ", typeStatics));
        }
    }

    // ---- Settings page and preview ------------------------------------------------------------

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static readonly Dictionary<Type, DependencyProperty[]> BrushProperties = [];

    /// <summary>Every brush-typed dependency property a type has, its own and inherited.</summary>
    private static DependencyProperty[] BrushPropertiesOf(Type type)
    {
        if (BrushProperties.TryGetValue(type, out var known)) return known;
        var found = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                                   | System.Reflection.BindingFlags.FlattenHierarchy)
            .Where(f => f.FieldType == typeof(DependencyProperty))
            .Select(f => (DependencyProperty)f.GetValue(null)!)
            .Where(dp => typeof(Brush).IsAssignableFrom(dp.PropertyType))
            .Append(TextElement.ForegroundProperty)
            .Distinct()
            .ToArray();
        return BrushProperties[type] = found;
    }

    /// <summary>
    /// Every brush object any element under <paramref name="root"/> is drawing with right now: its
    /// backgrounds, borders, text, fills and strokes, from styles, templates, triggers or set
    /// directly. Compared by reference, which tells two roles of the same colour apart.
    /// </summary>
    private static HashSet<object> BrushesIn(DependencyObject root)
    {
        var found = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(DependencyObject d)
        {
            if (d is UIElement { Visibility: not Visibility.Visible }) return;
            foreach (var dp in BrushPropertiesOf(d.GetType()))
                if (d.GetValue(dp) is Brush b) found.Add(b);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return found;
    }

    /// <summary>
    /// The Settings page inside the real main window (built, never shown): the cog and the hidden
    /// tab, the colour list, and the preview, which has to draw with every role.
    /// </summary>
    private void SettingsPageTests()
    {
        ThemeService.Apply(ThemeRoles.Defaults());
        var window = new MainWindow();
        var tabs = (TabControl)window.FindName("MainTabs");
        var tab = (TabItem)window.FindName("SettingsTab");
        var page = (FleetView.Settings.SettingsPage)window.FindName("SettingsView");
        var cog = (Button)window.FindName("SettingsCogButton");

        Equal("the four tabs are the ones there always were", "Find Carriers,Modifications,Barter,Import",
            string.Join(",", tabs.Items.OfType<TabItem>().Where(t => t.Visibility == Visibility.Visible).Select(t => t.Header)));
        Check("Settings is a tab with no header in the strip", tab.Visibility == Visibility.Collapsed && tabs.Items.Contains(tab));
        Check("after the others, so they keep their places", tabs.Items.IndexOf(tab) == tabs.Items.Count - 1);
        Check("Settings holds the page", ReferenceEquals(tab.Content, page));
        Equal("the cog's tooltip", "Settings", cog.ToolTip as string);
        Equal("and its name for screen readers", "Settings", System.Windows.Automation.AutomationProperties.GetName(cog));
        cog.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check("clicking the cog opens Settings", ReferenceEquals(tabs.SelectedItem, tab));
        tabs.SelectedIndex = 1;
        Check("and choosing a tab leaves it", !tab.IsSelected);
        tabs.SelectedIndex = 0;

        // The list.
        var rows = page.Groups.SelectMany(g => g.Rows).ToList();
        Equal("the list has every role, in order", string.Join(",", ThemeRoles.All.Select(r => r.Key)), string.Join(",", rows.Select(r => r.Key)));
        Equal("under its group headings, in order", string.Join(",", ThemeRoles.Groups), string.Join(",", page.Groups.Select(g => g.Name)));
        Equal("text selection says how it shows", "Shows at 40% over the box", rows.Single(r => r.Key == "TextSelection").Note);
        Equal("a see-through role says how see-through", "Shows at 80% over what is behind it", rows.Single(r => r.Key == "OverlayDim").Note);
        Check("other roles have no note", rows.Where(r => !r.Role.HasAlpha && r.Key != "TextSelection").All(r => r.Note == null));
        Check("labels say 'under the mouse' and 'while typing', not hover and focus",
            ThemeRoles.All.All(r => !Regex.IsMatch(r.Label, "hover|focus", RegexOptions.IgnoreCase)));
        Equal("the hex beside a box is written as the picker writes it", "#120e08", rows.Single(r => r.Key == "PanelBackground").Hex);
        Check("the page starts with nothing unsaved", !page.Draft.IsDirty);

        // The preview, laid out at about the size it gets in Ali's window.
        page.Measure(new Size(1692, 1306));
        page.Arrange(new Rect(0, 0, 1692, 1306));
        page.UpdateLayout();
        Pump();
        page.UpdateLayout();
        var preview = page.PreviewControl;
        Check($"the preview lays out ({preview.ActualWidth:F0}x{preview.ActualHeight:F0})", preview.ActualWidth > 500 && preview.ActualHeight > 500);
        object Draft(string key) => preview.Resources[key];
        Equal("the preview has a brush of its own for each role", ThemeRoles.All.Count,
            ThemeRoles.All.Select(r => Draft(r.Key)).Distinct(ReferenceEqualityComparer.Instance).Count());
        var used = BrushesIn(preview);
        var missing = ThemeRoles.All.Where(r => !used.Contains(Draft(r.Key))).Select(r => r.Key).ToList();
        Check($"every role is drawn somewhere in the preview ({ThemeRoles.All.Count - missing.Count} of {ThemeRoles.All.Count})",
            missing.Count == 0, "never drawn: " + string.Join(", ", missing));

        // Inherited colours resolve where they are set, so the preview has to set the window's
        // own ones again inside its scope, or it shows the app's.
        var root = (Border)preview.FindName("PreviewRoot");
        Check("the preview sets the window background itself", ReferenceEquals(root.Background, Draft("WindowBackground")));
        Check("and the main text colour", ReferenceEquals(TextElement.GetForeground(root), Draft("MainText")));
        var tabContent = Logical<TabItem>(preview).First(t => t.IsSelected).Content as FrameworkElement;
        Check("and gives its tab content the panel text colour",
            tabContent != null && ReferenceEquals(TextElement.GetForeground(tabContent), Draft("PanelText")));

        // A draft change moves the preview and the list, not the app.
        var odd = Color.FromRgb(0x12, 0xAB, 0x34);
        page.Draft.Set("PanelBackground", odd);
        Equal("a draft colour reaches the preview", odd, (Draft("PanelBackground") as SolidColorBrush)?.Color ?? default);
        Equal("and its row's box", odd, (rows.Single(r => r.Key == "PanelBackground").Swatch as SolidColorBrush)?.Color ?? default);
        Equal("and its row's hex", "#12ab34", rows.Single(r => r.Key == "PanelBackground").Hex);
        Equal("but not the app", ThemeRoles.Find("PanelBackground")!.Default, ThemeService.Current["PanelBackground"]);
        Check("the page now has something unsaved", page.Draft.IsDirty);
        page.Draft.Revert();
        Equal("Revert puts the preview back", ThemeRoles.Find("PanelBackground")!.Default,
            (Draft("PanelBackground") as SolidColorBrush)?.Color ?? default);

        // The picker keeps the app's colours: it must not sit inside the preview.
        DependencyObject? up = page.PickerControl;
        bool inside = false;
        while (up != null)
        {
            inside |= ReferenceEquals(up, preview);
            up = LogicalTreeHelper.GetParent(up) ?? VisualTreeHelper.GetParent(up);
        }
        Check("the picker is outside the preview's colours", !inside);
    }

    private static IEnumerable<T> Logical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var deeper in Logical<T>(child)) yield return deeper;
        }
    }
}
