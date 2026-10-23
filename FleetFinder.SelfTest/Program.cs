using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using FleetView.Services;
using FleetView.Theming;

// Self-test for FleetFinder's theme layer (the colour changer, 2026-10): the colour maths in both
// directions and its text forms, theme.json loading and saving, the brushes the theme service
// hands out, and a scan of MainWindow.xaml that fails if a colour literal comes back or a
// DynamicResource names something that is not a role. Prints one PASS or FAIL line per check and a
// final SUMMARY line; exit code 1 if anything failed.
//
// Runs on an STA thread because WPF brushes are dispatcher objects. It never creates a window.
var suite = new SelfTest();
var thread = new Thread(suite.Run);
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
Console.WriteLine($"SUMMARY: {suite.Passed} passed, {suite.Failed} failed");
return suite.Failed == 0 ? 0 : 1;

internal sealed class SelfTest
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
        Equal("only the overlay dim has alpha", "OverlayDim", string.Join(",", all.Where(r => r.HasAlpha).Select(r => r.Key)));
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

        var unknown = used.Keys.Where(k => ThemeRoles.Find(k) is null).ToList();
        Check("every DynamicResource key is a role", unknown.Count == 0, string.Join(", ", unknown));
        var unused = ThemeRoles.All.Where(r => !used.ContainsKey(r.Key)).Select(r => r.Key).ToList();
        Check("every role is used in the XAML", unused.Count == 0, string.Join(", ", unused));
        Console.WriteLine($"   ({used.Values.Sum()} DynamicResource uses across {used.Count} roles)");

        var raw = File.ReadAllText(path);
        Check("no StaticResource left for the old Panel, Accent or Muted brushes",
            !Regex.IsMatch(raw, @"StaticResource\s+(Panel|Accent|Muted)\}"));
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
}
