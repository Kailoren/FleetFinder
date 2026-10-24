using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FleetView;
using FleetView.Settings;
using FleetView.Theming;

// The D4 sections (2026-10-24): the themes FleetFinder comes with. Their definitions first (nine,
// every role a usable colour, broken definitions caught, each theme pinned by its share code so a
// change to one has to be made here as well), then whether the text in each can be read on what it
// is drawn over (WCAG contrast ratios), then their cards on the shelf (after the user's own, never
// deleted, pictures kept in a cache named by their colours) and on the real page, built in the main
// window and never shown. Every folder is a temporary one and every clipboard a pretend one.
internal sealed partial class SelfTest
{
    /// <summary>
    /// The built-in themes in strip order, each pinned by its share code (name, no commander, all
    /// 93 colours). Changing a theme's colours or name on purpose means pasting its new code here,
    /// which the failing check prints; changing one by accident fails, naming the roles that moved.
    /// </summary>
    private static readonly (string Id, string Name, string Code)[] PinnedBuiltIns =
    [
        ("federation-blue", "Federation Blue",
            "FFTheme1HbdkTZ7CTUgRGF5dg8n8ctJG6ppetR55mkm3D6YuMvZpFoLZSkYc4HsyPtsvi65ua8XQMCQer12CpjC1RoCiEiCak3Az4YBuJtPNE28rxVKWxwHtLXrSZFLYmCPBnBf3vpoTWw57eTFw9UqRrcEGMgYkymDAfcMgPZYiMt2TxiSqyumwTmbkopFLJEdVFNfQ157XeWf"),
        ("imperial-purple", "Imperial Purple",
            "FFTheme1HcP2sCS891qqDVt8DVjiQABpossy35qaBqG8tgg1G1zZCcS1ggYGRiUoQ5HfmR1PgZEYB3xKCXQGPL4mCDprzjPdvKWnKjxxyVeMouhQz15Fejfos6fhjwmigNDZBdVPgGGNt2o7yfxwwC7zsMYTvMWZ2sncrbhxtLvUVnZVXvNygFFJotc6znkaBwAD6qRZtSkaSFG"),
        ("alliance-green", "Alliance Green",
            "FFTheme14WYPyRAmomstN4vkAKwmPkew8pwhrNHbmppCs2GoNiBjm3MGQWVLT3UivaghPUdSPBdms4TKfg6K12FKtkohoikothMiiTkqK7MJ778v2gHHdFrAN3mqTA6c8Q2GSBetV49xe2jxh1bF8R5veTikdRqgy7qgPMbY3HxgJrfEdYtgHzdsA9JKvDtgAa2ZMQnrq2BKMd"),
        ("guardian-cyan", "Guardian Cyan",
            "FFTheme1jxTjLYVGRt5VMvMgffkwRtbg495siPog9zgaiHavQJgmmnHoS982bZQKUdxdoysB4WZrk9vGPSFsdKM5JMbfRd25hTSu4VA62gedeCJat3pTgyBKh5w5L2yzxU1iG4uBGQEXWT4LYng4HfWcSquAEwH5XGQquNpwjZ2SFGR3shQsVUDeKQ8cdJsggGZdAkTaEtPf"),
        ("synthwave", "Synthwave",
            "FFTheme15cQGCnT2XqUs42sLGRuBHinao1m5YKWQb2isiqp8AnjmuaBsnHCNY7WNGa16LghZ34xJbqFDG3mXPhumZHtzGiibmFvWm4983r2VDeT3bbsQ55n4ScNHLWYNbvPfKVygaMi3ZxGWaku16JXp33aJdT9TPyn9B367eDQ3PTEGe51wtgodiuczgHADC742mkD"),
        ("windows-98", "Windows 98",
            "FFTheme1GDmwqNgSF2JNWmS9JNN7pJBjzQe5bTUoCSCLNYK5bn6waZ9bSnLdwc2qDZjKFihYmwJTzTPiQ4V6o3ztVdWZKTrHcMKZZ34oRwANG3JV6QKNcjdS5cj1tiEj2Sa7x13qdLMfuJmnjFrH2X2gv4onCQCWU4tJnjv8orngWEVJK3qgLtXCcTAYxgifzcXR"),
        ("windows-xp", "Windows XP",
            "FFTheme12pX8SvHcBnCAjdSut1x7qEd4GxgGWV8HnqxRnhfCgPAMqK9gqFRYqnUJGYYhtiuv3FcdCsRuz3iAqVqZrZ1Ddewzxd46vzxRyTHjSEkwbBgnHb4CadDUweDim9oV7KizNMCTL5YATJTUtNokg4cmNeSRBTNNh9sRy1dXfHgc1tzDyFcegPcKX4A7Fj9wXB5feGyYw1a9ASBA8cjnAmoVZXhLKxitNdKhnqpJh9VwN2cQVXEnaLk3BpsCoeyECGwm8jgiSJAaq"),
        ("green-screen", "Green Screen",
            "FFTheme1PNBwAF3LBRBnEPaKBWabD7G9ubj9vfYjLyrXUUvfDQk679Vt1ivWeLBoZk9WcHtRpvMS9yvKHiMienxo73CLzd8wBSYXvHiwutMzWfczQhfk3U53UVvus4dZQdFHV2dJFrkRjJaPx1P9F8XRKVbkY43WX3JhQjskfy"),
        ("classic", "FleetFinder classic",
            "FFTheme136CswR1Sybr9mQeVRYgTpvvN9UfVjw5u6dUZKadWNVVDPkrQxuQ9B5FbyVcjdifoN8cvVsEQcYjkKJyBzWPYBxYHBtMbrV9S8ifXpzBFuTJ2JFzd8PbxfDYAcv7z7umQBBKT7so8jtinhWuzYzKEKocFrK1ZEUioWovzmEtZJbngZj3x6DJ66mbG7LQtiVid2BkHg8tLNah5vzYkQckPNUhyPrL2UNT5o127uhkZNmbWxk6muFQ4QY"),
    ];

    /// <summary>The 17 roles where the original look differs from Ali's palette, as TASKS.md's
    /// Roles table gives them ("was"). FleetFinder classic must be exactly the defaults with these.</summary>
    private static readonly (string Key, string Hex)[] OriginalLook =
    [
        ("CloseButtonHover", "#E81123"), ("CloseButtonHoverGlyph", "#FFFFFF"), ("InputHoverBorder", "#7EB4EA"),
        ("InputFocusBorder", "#569DE5"), ("TextSelection", "#0078D4"), ("ToolTipBackground", "#F1F2F7"),
        ("ToolTipBorder", "#767676"), ("ToolTipText", "#575757"), ("MenuBackground", "#F5F5F5"),
        ("MenuBorder", "#959595"), ("MenuText", "#000000"), ("MenuDisabledText", "#707070"),
        ("MenuHighlight", "#3D26A0DA"), ("MenuHighlightBorder", "#26A0DA"), ("MenuGutter", "#F1F1F1"),
        ("MenuGutterLine", "#E2E3E3"), ("MenuGutterLineHighlight", "#FFFFFF"),
    ];

    // ---- the definitions ---------------------------------------------------------------------------

    private void BuiltInThemeTests()
    {
        Equal("no built-in theme has a problem in its definition", "", string.Join(" | ", BuiltInThemes.Problems));
        var all = BuiltInThemes.All;
        Equal("there are nine built-in themes, in strip order", string.Join("|", PinnedBuiltIns.Select(p => p.Name)),
            string.Join("|", all.Select(t => t.Name)));
        Equal("with their ids", string.Join("|", PinnedBuiltIns.Select(p => p.Id)), string.Join("|", all.Select(t => t.Id)));
        Check("ids are short lower-case words", all.All(t => Regex.IsMatch(t.Id, "^[a-z0-9]+(-[a-z0-9]+)*$")));
        Equal("names are unique, whatever their case", all.Count, all.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Check("names are as the theme text rules keep them", all.All(t => ThemeText.CleanName(t.Name) == t.Name));
        Check("no em or en dash in any name", !all.Any(t => t.Name.Contains('\u2014') || t.Name.Contains('\u2013')));
        Check("and no Microsoft in any name (colours and names only, no logos)", !all.Any(t => t.Name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)));
        Check("BuiltInThemes.Find finds each by id", all.All(t => ReferenceEquals(BuiltInThemes.Find(t.Id), t)) && BuiltInThemes.Find("nope") == null);

        foreach (var theme in all)
        {
            var keys = theme.Colours.Keys.ToHashSet(StringComparer.Ordinal);
            var missing = ThemeRoles.All.Where(r => !keys.Contains(r.Key)).Select(r => r.Key).ToList();
            var extra = keys.Where(k => ThemeRoles.Find(k) == null).ToList();
            Check($"{theme.Name}: has a colour for every role and nothing else ({theme.Colours.Count})", missing.Count == 0 && extra.Count == 0,
                "missing " + string.Join(", ", missing) + "; not roles " + string.Join(", ", extra));
            var badAlpha = ThemeRoles.All.Where(r => theme.Colours.TryGetValue(r.Key, out var c)
                && (r.HasAlpha ? c.A is 0 or 255 : c.A != 255)).Select(r => r.Key).ToList();
            Check($"{theme.Name}: opaque where a role has no alpha, part see-through where it has", badAlpha.Count == 0, string.Join(", ", badAlpha));
            Check($"{theme.Name}: is not just the shipped defaults", !SameColours(theme.Colours, ThemeRoles.Defaults()));
        }
        Equal("no two built-in themes are the same colours", all.Count,
            all.Select(t => string.Join(",", ThemeRoles.All.Select(r => t.Colours[r.Key].ToString()))).Distinct().Count());

        // FleetFinder classic: the original look, decoded from the D1 code, must be the defaults
        // with the 17 colours Ali changed put back.
        var original = ThemeRoles.Defaults();
        foreach (var (key, hex) in OriginalLook) original[key] = Hex(hex);
        var classic = BuiltInThemes.Find("classic");
        var moved = classic == null ? ["no classic theme"] : ThemeRoles.All.Where(r => classic.Colours[r.Key] != original[r.Key]).Select(r => r.Key).ToList();
        Check("FleetFinder classic is the original look exactly: the defaults with the 17 original colours", moved.Count == 0, string.Join(", ", moved));

        // Pinned: each theme still encodes to the code written above.
        foreach (var (id, name, code) in PinnedBuiltIns)
        {
            if (BuiltInThemes.Find(id) is not { } theme)
            {
                Check($"{name} is pinned", false, "no such theme");
                continue;
            }
            var now = ThemeCode.Encode(theme.Name, "", theme.Colours);
            var pinned = ThemeCode.Decode(code);
            var changed = pinned.Ok ? ThemeRoles.All.Where(r => pinned.Theme!.Colours[r.Key] != theme.Colours[r.Key]).Select(r => r.Key).ToList() : [];
            string detail = !pinned.Ok ? $"the pin does not read ({pinned.Problem})"
                : pinned.Theme!.Name != theme.Name ? $"renamed from {pinned.Theme.Name}"
                : $"changed: {string.Join(", ", changed)}";
            Check($"{name} is pinned: its colours and name are as this test says", now == code, $"{detail}; if that is meant, pin it as {now}");
        }

        BrokenDefinitionTests();
    }

    /// <summary>Hands <see cref="BuiltInThemes.Expand"/> definitions with one thing wrong each and
    /// checks every one is caught, naming the role.</summary>
    private void BrokenDefinitionTests()
    {
        var good = new BuiltInThemes.Palette(
            Window: "#101010", Panel: "#202020", Alternate: "#2A2A2A", Line: "#303030", Edge: "#505050", EdgeHover: "#606060",
            Disabled: "#404040", Text: "#F0F0F0", Soft: "#C0C0C0", Muted: "#A0A0A0", Accent: "#FFAA00", OnAccent: "#000000",
            Link: "#88CCFF", LinkHover: "#AADDFF", Good: "#66EE99", Bad: "#FF7777", Selection: "#3366CC", Menu: "#000000",
            MenuDisabled: "#707070", Wash: "#40FFAA00");
        var problems = new List<string>();
        var colours = BuiltInThemes.Expand("Test", good, [("PanelText", "#FFFFFF")], problems);
        Check("a good palette has no problems", problems.Count == 0, string.Join(" | ", problems));
        Equal("and gives every role a colour", ThemeRoles.All.Count, colours.Count);
        Equal("an override takes over its role", Hex("#FFFFFF"), colours["PanelText"]);
        Equal("and the palette fills the rest", Hex("#FFAA00"), colours["PanelBorder"]);
        Equal("see-through roles keep the alpha written", (byte)0x40, colours["MenuHighlight"].A);

        foreach (var (what, palette, overrides, expected) in new (string, BuiltInThemes.Palette, (string, string)[], string)[]
                 {
                     ("a colour one digit short", good with { Panel = "#20202" }, [], "PanelBackground is \"#20202\""),
                     ("a colour with no #", good with { Text = "F0F0F0" }, [], "MainText is \"F0F0F0\""),
                     ("a colour in lower case", good with { Accent = "#ffaa00" }, [], "PanelBorder is \"#ffaa00\""),
                     ("a colour that is not hex", good with { Muted = "#A0A0AG" }, [], "MutedText is \"#A0A0AG\""),
                     ("a short #rgb colour", good with { Line = "#333" }, [], "GridLines is \"#333\""),
                     ("alpha on a role that has none", good, [("GridRow", "#80202020")], "GridRow is \"#80202020\""),
                     ("no alpha on a role that has it", good with { Wash = "#FFAA00" }, [], "MenuHighlight is \"#FFAA00\""),
                     ("a see-through role made solid", good with { Dim = "#FF000000" }, [], "OverlayDim is \"#FF000000\", which is not see-through"),
                     ("a see-through role made invisible", good with { Shadow = "#00000000" }, [], "MenuShadow is \"#00000000\", which is not see-through"),
                     ("an override of something that is not a role", good, [("GridRows", "#FFFFFF")], "GridRows is not a role"),
                     ("an override given twice", good, [("PanelText", "#FFFFFF"), ("PanelText", "#EEEEEE")], "PanelText is set twice"),
                     ("an override that changes nothing", good, [("PanelBorder", "#FFAA00")], "PanelBorder is set to #FFAA00, which its palette gives it already"),
                 })
        {
            var found = new List<string>();
            BuiltInThemes.Expand("Test", palette, overrides, found);
            Check($"caught: {what}", found.Any(p => p.StartsWith("Test: ", StringComparison.Ordinal) && p.Contains(expected, StringComparison.Ordinal)),
                found.Count == 0 ? "nothing reported" : string.Join(" | ", found));
        }
    }

    // ---- contrast --------------------------------------------------------------------------------

    /// <summary>Body text, which WCAG wants at 4.5 to 1 against what is behind it.</summary>
    private const double BodyText = 4.5;

    /// <summary>Headings, large text, icons and things that show a state (a tick, an outline, the
    /// progress fill): 3 to 1. Greyed text is held to this too; WCAG leaves it out, but it should
    /// still read.</summary>
    private const double LargeText = 3.0;

    /// <summary>One piece of text (or icon) and what it is drawn over in the app.</summary>
    private sealed record ContrastPair(string Text, string Over, double Minimum, Func<IReadOnlyDictionary<string, Color>, Color> Background);

    private static ContrastPair On(string text, string background, double minimum) => new(text, background, minimum, c => c[background]);

    /// <summary>
    /// Every place text sits on a colour in the app, from reading MainWindow.xaml, SettingsPage.xaml
    /// and the card and dialog templates. The theme cards and the barter totals box share the totals
    /// box colours; the menu's item under the mouse is its see-through highlight over the menu, and
    /// selected text in a text box is the selection colour at 40% over the box.
    /// </summary>
    private static readonly ContrastPair[] ContrastPairs =
    [
        // Body text.
        On("MainText", "PanelBackground", BodyText),          // Settings list labels
        On("MainText", "DialogBackground", BodyText),         // the dialogs' messages, the picker
        On("MainText", "TotalsBoxBackground", BodyText),      // a theme card's name
        On("MutedText", "PanelBackground", BodyText),         // captions and hints
        On("MutedText", "TotalsBoxBackground", BodyText),     // barter captions, a card's author
        On("MutedText", "DialogBackground", BodyText),        // dialog hints
        On("MutedText", "GridBackground", BodyText),          // "No fleet carriers are selling..."
        On("TitleBarText", "TitleBarBackground", BodyText),
        On("TitleBarVersion", "TitleBarBackground", BodyText),
        On("BannerText", "BannerBackground", BodyText),       // and its Dismiss button
        On("BannerButtonText", "BannerButtonBackground", BodyText),
        On("TabText", "WindowBackground", BodyText),
        On("TabText", "TabHoverBackground", BodyText),
        On("SelectedTabText", "SelectedTabBackground", BodyText),
        On("PanelText", "PanelBackground", BodyText),
        On("PanelText", "TotalsBoxBackground", BodyText),     // the barter totals
        On("GridText", "GridRow", BodyText),
        On("GridText", "GridAlternateRow", BodyText),
        On("GridHeaderText", "GridHeaderBackground", BodyText),
        On("NeededCount", "GridRow", BodyText),
        On("NeededCount", "GridAlternateRow", BodyText),
        On("NeededCount", "InputBackground", BodyText),       // the Needed cell while it is edited
        On("HaveCount", "GridRow", BodyText),
        On("HaveCount", "GridAlternateRow", BodyText),
        On("GoodValue", "GridRow", BodyText),
        On("GoodValue", "GridAlternateRow", BodyText),
        On("BadValue", "GridRow", BodyText),
        On("BadValue", "GridAlternateRow", BodyText),
        On("UnknownValue", "GridRow", BodyText),
        On("UnknownValue", "GridAlternateRow", BodyText),
        On("LinkText", "GridRow", BodyText),
        On("LinkText", "GridAlternateRow", BodyText),
        On("LinkHoverText", "GridRow", BodyText),
        On("LinkHoverText", "GridAlternateRow", BodyText),
        On("SummaryText", "PanelBackground", BodyText),
        On("UnmatchedText", "GridRow", BodyText),
        On("UnmatchedText", "GridAlternateRow", BodyText),
        On("BarterPoints", "GridRow", BodyText),
        On("BarterPoints", "GridAlternateRow", BodyText),
        On("ButtonText", "ButtonBackground", BodyText),
        On("SecondaryButtonText", "SecondaryButtonBackground", BodyText),
        On("StepperText", "StepperBackground", BodyText),
        On("StepperText", "StepperHover", BodyText),
        On("InputText", "InputBackground", BodyText),
        new("InputText", "TextSelection at 40% over InputBackground", BodyText, c => Blend(c["TextSelection"], c["InputBackground"], 0.4)),
        On("ToolTipText", "ToolTipBackground", BodyText),
        On("MenuText", "MenuBackground", BodyText),
        new("MenuText", "MenuHighlight over MenuBackground", BodyText, c => Blend(c["MenuHighlight"], c["MenuBackground"], c["MenuHighlight"].A / 255.0)),
        On("StatusText", "StatusBarBackground", BodyText),
        On("StatusDetailText", "StatusBarBackground", BodyText),

        // Headings, large text, icons and state.
        On("GroupHeaderText", "GridBackground", LargeText),
        On("GroupHeaderText", "PanelBackground", LargeText),  // the Settings list's group headings
        On("DialogTitle", "DialogBackground", LargeText),
        On("BarterResult", "TotalsBoxBackground", LargeText), // 26 px bold
        On("CaptionButtonGlyph", "TitleBarBackground", LargeText),
        On("CaptionButtonGlyph", "WindowBackground", LargeText), // the cog
        On("SelectedTabText", "WindowBackground", LargeText), // the cog while Settings is open
        On("CaptionButtonHoverGlyph", "CaptionButtonHover", LargeText),
        On("CloseButtonHoverGlyph", "CloseButtonHover", LargeText),
        On("CheckboxTicked", "GridRow", LargeText),
        On("CheckboxTicked", "GridAlternateRow", LargeText),
        On("RowOutline", "GridRow", LargeText),
        On("ProgressFill", "ProgressTrack", LargeText),

        // Greyed out.
        On("ButtonDisabledText", "ButtonDisabledBackground", LargeText),
        On("MenuDisabledText", "MenuBackground", LargeText),
        new("MenuDisabledText", "MenuDisabledHighlight over MenuBackground", LargeText,
            c => Blend(c["MenuDisabledHighlight"], c["MenuBackground"], c["MenuDisabledHighlight"].A / 255.0)),
    ];

    /// <summary>Roles that are drawn as text or as an icon somewhere, so each must be in a pair.</summary>
    private static IEnumerable<string> TextRoles() => ThemeRoles.All.Select(r => r.Key)
        .Where(k => k.EndsWith("Text", StringComparison.Ordinal)).Concat(
        [
            "TitleBarVersion", "NeededCount", "HaveCount", "GoodValue", "BadValue", "UnknownValue", "BarterResult", "BarterPoints",
            "DialogTitle", "CaptionButtonGlyph", "CaptionButtonHoverGlyph", "CloseButtonHoverGlyph", "CheckboxTicked",
        ]);

    /// <summary>WCAG 2 relative luminance of an sRGB colour (alpha ignored).</summary>
    private static double Luminance(Color c)
    {
        static double Linear(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    /// <summary>WCAG contrast ratio, from 1 (the same) to 21 (black on white).</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>A colour laid over another at some opacity, as it shows on screen.</summary>
    private static Color Blend(Color top, Color under, double opacity) => Color.FromRgb(
        (byte)Math.Round(top.R * opacity + under.R * (1 - opacity)),
        (byte)Math.Round(top.G * opacity + under.G * (1 - opacity)),
        (byte)Math.Round(top.B * opacity + under.B * (1 - opacity)));

    /// <summary>The pairs a palette fails, as "GridText on GridRow 1.00 (needs 4.5)", ratios
    /// rounded down so a near miss never prints as the minimum.</summary>
    private static List<string> ContrastFailures(IReadOnlyDictionary<string, Color> colours) => ContrastPairs
        .Select(p => (p, ratio: ContrastRatio(colours[p.Text], p.Background(colours))))
        .Where(x => x.ratio < x.p.Minimum)
        .Select(x => $"{x.p.Text} on {x.p.Over} {Math.Floor(x.ratio * 100) / 100:F2} (needs {x.p.Minimum:0.0})")
        .ToList();

    private void ContrastTests()
    {
        // The sums themselves, against WCAG's own figures.
        Near("black on white is 21 to 1", 21, ContrastRatio(Colors.Black, Colors.White), 1e-9);
        Near("a colour on itself is 1 to 1", 1, ContrastRatio(Hex("#FF7500"), Hex("#FF7500")), 1e-9);
        Near("#767676 on white is the classic 4.54", 4.54, ContrastRatio(Hex("#767676"), Colors.White), 0.005);
        Near("#777777 on white just misses at 4.48", 4.48, ContrastRatio(Hex("#777777"), Colors.White), 0.005);
        Equal("a 40% blend is worked out on the colours", Hex("#6666FF"), Blend(Hex("#0000FF"), Colors.White, 0.6));

        var uncovered = TextRoles().Where(k => !ContrastPairs.Any(p => p.Text == k)).ToList();
        Check($"every role drawn as text or an icon is checked against what it is drawn over ({ContrastPairs.Length} pairs)",
            uncovered.Count == 0, "not in a pair: " + string.Join(", ", uncovered));
        Check("every pair names real roles", ContrastPairs.All(p => ThemeRoles.Find(p.Text) != null
            && (ThemeRoles.Find(p.Over) != null || p.Over.Contains(" over ", StringComparison.Ordinal))));

        foreach (var theme in BuiltInThemes.All)
        {
            var fails = ContrastFailures(theme.Colours);
            if (theme.Id == "classic")
            {
                // The original look kept exactly, so it is reported like the defaults below
                // rather than changed to pass.
                Console.WriteLine($"   (reported, not counted) {theme.Name}, the original look: {ContrastPairs.Length - fails.Count} of {ContrastPairs.Length} pairs pass"
                                  + (fails.Count == 0 ? "" : "; short: " + string.Join("; ", fails)));
                continue;
            }
            Check($"{theme.Name}: every text can be read on what it is drawn over", fails.Count == 0, string.Join("; ", fails));
        }

        // Ali's palette, the shipped defaults: reported only, never changed here.
        var defaults = ContrastFailures(ThemeRoles.Defaults());
        Console.WriteLine($"   (reported, not counted) the shipped defaults, Ali's palette: {ContrastPairs.Length - defaults.Count} of {ContrastPairs.Length} pairs pass"
                          + (defaults.Count == 0 ? "" : "; short: " + string.Join("; ", defaults)));
    }

    // ---- the cards on the shelf -----------------------------------------------------------------------

    private void BuiltInShelfTests()
    {
        var folder = Path.Combine(_dir, "builtin-shelf");
        WriteTheme(folder, "0000000000000001", "Mine", "Kailoren", "2026-10-20T10:00:00Z", Tinted(Teal));
        var library = new ThemeLibrary(folder);
        var (draft, themeJson, applied) = NewDraft("builtindraft");
        var clip = new FakeClipboard { Text = "untouched" };
        var shelf = new ThemeShelf(library, draft, clip);
        shelf.Reload();

        Equal("the user's own cards are only theirs", "Mine", string.Join("|", shelf.Cards.Select(c => c.Name)));
        Equal("the built-in ones are cards of their own, in order", string.Join("|", BuiltInThemes.All.Select(t => t.Name)),
            string.Join("|", shelf.BuiltIns.Select(c => c.Name)));
        Check("each marked built in, the user's not", shelf.BuiltIns.All(c => c.IsBuiltIn) && !shelf.Cards[0].IsBuiltIn);
        Check("by FleetFinder", shelf.BuiltIns.All(c => c.Author == "FleetFinder"));
        Equal("with a credit line that says so", "Windows 98 - built into FleetFinder", shelf.BuiltIns.First(c => c.Name == "Windows 98").Credit);
        Check("in their own colours", shelf.BuiltIns.All(c => SameColours(c.Theme.Colours, BuiltInThemes.All.First(t => t.Name == c.Name).Colours)));
        Check("their ids are never library ids", shelf.BuiltIns.All(c => c.Id.StartsWith(ThemeShelf.BuiltInIdPrefix, StringComparison.Ordinal)
            && !ThemeLibrary.IsValidId(c.Id)));

        // Apply and Copy share code work as on any card.
        var w98 = shelf.BuiltIns.First(c => c.Name == "Windows 98");
        shelf.Apply(w98);
        Check("Apply puts a built-in theme in the draft", SameColours(draft.Colours, w98.Theme.Colours) && draft.IsDirty);
        Check("and nothing else", applied.Count == 0 && !File.Exists(themeJson));
        draft.Revert();
        Equal("Copy share code works", ThemeShelf.CopiedNote, shelf.CopyCode(w98));
        var shared = ThemeCode.Decode(clip.Text);
        Check("and the code is the theme, credited to nobody", shared.Ok && shared.Theme!.Name == "Windows 98" && shared.Theme.Commander == ""
            && SameColours(shared.Theme.Colours, w98.Theme.Colours));

        // Never deleted, never saved over.
        shelf.AskDelete(w98);
        Check("a built-in card is never asked about deleting", !w98.IsConfirmingDelete && shelf.Confirming == null);
        Check("nor deleted", !shelf.Delete(w98) && shelf.BuiltIns.Contains(w98) && shelf.BuiltIns.Count == BuiltInThemes.All.Count);
        Check("nor found as a theme to replace by Save as theme", shelf.Find("windows 98") == null);

        // Importing a built-in's code: already had.
        Check("the shelf has a built-in theme's own code", shelf.Has(shared.Theme!) && shelf.HasBuiltIn(shared.Theme!));
        Check("but not the same colours under another name", !shelf.Has(shared.Theme! with { Name = "My 98" }));
        Check("and the user's own theme is not a built-in one", !shelf.HasBuiltIn(new SharedTheme("Mine", "Kailoren", Tinted(Teal))));
        var import = new ThemeImport(shelf);
        import.Read(clip.Text);
        Check("Import code with a built-in's code says FleetFinder has it", import.State == ImportState.AlreadyHave
            && import.Message == ThemeImport.BuiltInMessage && !import.CanAdd && import.Add(null) == null);
        Equal("and nothing is added", 1, library.List().Count);

        // Pictures: drawn once, kept in built-in\, named by the colours.
        Equal("every built-in picture is missing to begin with, after the user's", "Mine|" + string.Join("|", shelf.BuiltIns.Select(c => c.Name)),
            string.Join("|", shelf.MissingSnapshots.Select(c => c.Name)));
        var png = Png(160, 160, Teal);
        Check("a drawn built-in picture is kept", shelf.StoreSnapshot(w98, png));
        var name = ThemeShelf.BuiltInSnapshotName(w98.Theme.Colours, 160);
        Check($"in built-in\\, named by its colours and width ({name})", Regex.IsMatch(name, "^[0-9a-f]{16}-160\\.png$")
            && File.ReadAllBytes(Path.Combine(folder, "built-in", name)).SequenceEqual(png));
        Check("shown at once", w98.Snapshot != null);
        Equal("the library does not mistake it for a theme", 1, library.List().Count);
        Check("the same colours give the same name", ThemeShelf.BuiltInSnapshotName(new Dictionary<string, Color>(w98.Theme.Colours), 160) == name);
        var nudged = new Dictionary<string, Color>(w98.Theme.Colours) { ["GridText"] = Hex("#010101") };
        Check("one colour changed gives another, so a changed theme is drawn again", ThemeShelf.BuiltInSnapshotName(nudged, 160)[..16] != name[..16]);
        Check("and another screen scale another file", ThemeShelf.BuiltInSnapshotName(w98.Theme.Colours, 200) == name[..16] + "-200.png");

        shelf.Reload();
        Check("a reload keeps a built-in picture already shown", ReferenceEquals(shelf.BuiltIns.First(c => c.Name == "Windows 98"), w98) && w98.Snapshot != null);
        var next = new ThemeShelf(library, draft, clip);
        next.Reload();
        var w98Again = next.BuiltIns.First(c => c.Name == "Windows 98");
        Check("read back from the cache next time", w98Again.Snapshot is BitmapSource { PixelWidth: 160 });
        Check("and no longer missing", !next.MissingSnapshots.Contains(w98Again)
            && next.MissingSnapshots.Count(c => c.IsBuiltIn) == BuiltInThemes.All.Count - 1);
        Check("the cached picture was read whole: nothing holds it open", Unlocked(Path.Combine(folder, "built-in", name)));
        var scaled = new ThemeShelf(library, draft, clip) { DpiScale = 1.25 };
        scaled.Reload();
        Check("at another screen scale it is looked for at that width, and drawn again", scaled.BuiltIns.First(c => c.Name == "Windows 98").Snapshot == null);

        // Tidying: an old picture (another build's or a changed theme's) goes, anything else stays.
        var cache = Path.Combine(folder, "built-in");
        File.WriteAllBytes(Path.Combine(cache, "0123456789abcdef-160.png"), png);
        File.WriteAllText(Path.Combine(cache, "notes.txt"), "mine");
        File.WriteAllBytes(Path.Combine(cache, "mine.png"), png);
        shelf.Reload();
        var synth = shelf.BuiltIns.First(c => c.Name == "Synthwave");
        Check("another picture kept", shelf.StoreSnapshot(synth, png));
        Check("the old one is tidied away", !File.Exists(Path.Combine(cache, "0123456789abcdef-160.png")));
        Check("files named any other way are left alone", File.Exists(Path.Combine(cache, "notes.txt")) && File.Exists(Path.Combine(cache, "mine.png")));
        Check("and the current ones stay", File.Exists(Path.Combine(cache, name)) && File.Exists(Path.Combine(cache, ThemeShelf.BuiltInSnapshotName(synth.Theme.Colours, 160))));
        Equal("no temporary file is left behind", 0, Directory.GetFiles(cache, "*.tmp").Length);

        // A cached file that is not a picture is drawn again.
        File.WriteAllBytes(Path.Combine(cache, name), [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        var broken = new ThemeShelf(library, draft, clip);
        broken.Reload();
        Check("a broken cached picture counts as missing", broken.BuiltIns.First(c => c.Name == "Windows 98").Snapshot == null);

        // A cache that cannot be written: the picture shows all the same.
        var stuckFolder = Path.Combine(_dir, "builtin-stuck");
        Directory.CreateDirectory(stuckFolder);
        File.WriteAllText(Path.Combine(stuckFolder, "built-in"), "a file where the folder would go");
        var stuck = new ThemeShelf(new ThemeLibrary(stuckFolder), draft, clip);
        stuck.Reload();
        var stuckCard = stuck.BuiltIns[0];
        Check("a cache that cannot be written says so", !stuck.StoreSnapshot(stuckCard, png));
        Check("and the card shows its picture anyway", stuckCard.Snapshot != null);
    }

    // ---- the page ------------------------------------------------------------------------------------------

    private void SettingsBuiltInTests()
    {
        ThemeService.Apply(ThemeRoles.Defaults());
        var window = new MainWindow();
        var page = (SettingsPage)window.FindName("SettingsView");
        var clip = new FakeClipboard { Text = "untouched" };
        page.ClipboardText = clip;
        var folder = Path.Combine(_dir, "builtin-page");
        WriteTheme(folder, "0000000000000001", "Orange", "Kailoren", "2026-10-20T10:00:00Z", ThemeRoles.Defaults());
        WriteTheme(folder, "0000000000000002", "Teal", "", "2026-10-21T10:00:00Z", Tinted(Teal));
        page.ThemeLibrary = new ThemeLibrary(folder);
        Layout(page);

        var user = page.ThemeCards;
        var builtIn = page.BuiltInThemeCards;
        Equal("the strip has the user's two and the nine built in", "2 9", $"{user.Count} {builtIn.Count}");
        double Left(FrameworkElement e) => e.TransformToAncestor(page).Transform(new Point(0, 0)).X;
        var lastUser = page.CardElementOf(user[^1])!;
        var divider = (FrameworkElement)page.FindName("BuiltInDivider");
        var firstBuiltIn = page.CardElementOf(builtIn[0])!;
        Check("the built-in ones come after the user's, past a divider", Left(lastUser) < Left(divider) && Left(divider) < Left(firstBuiltIn));
        Check("which says Built in", divider.Visibility == Visibility.Visible && Descendants<TextBlock>(divider).Any(t => t.Text == "Built in"));
        Check("and the built-in cards keep strip order", builtIn.Select(c => Left(page.CardElementOf(c)!)).Zip(builtIn.Skip(1).Select(c => Left(page.CardElementOf(c)!)))
            .All(p => p.First < p.Second));
        Check("with no empty tile when the user has themes", ((FrameworkElement)page.FindName("ThemesEmptyTile")).Visibility == Visibility.Collapsed);

        // The same card: picture, name, author, Apply.
        var fed = page.CardElementOf(builtIn[0])!;
        var face = Descendants<StackPanel>(fed).First(p => p.Name == "CardFace");
        Equal("a built-in card is a picture, its name, FleetFinder and Apply", "1|Federation Blue|FleetFinder|Apply",
            Descendants<Image>(fed).Count() + "|" + string.Join("|", face.Children.OfType<FrameworkElement>()
                .Select(e => e switch { Button b => b.Content as string, TextBlock t => t.Text, _ => e.GetType().Name })));
        Equal("its right-click menu has Copy share code and no Delete", "Copy share code",
            string.Join("|", fed.ContextMenu?.Items.OfType<MenuItem>().Select(i => i.Header as string) ?? []));
        Check("and it has no delete question to show", !Descendants<StackPanel>(fed).Any(p => p.Name == "CardConfirm"));
        Equal("the user's card menu still has Delete", "Copy share code|Delete",
            string.Join("|", page.CardElementOf(user[0])!.ContextMenu?.Items.OfType<MenuItem>().Select(i => i.Header as string) ?? []));
        var tile = (Border)fed;
        Check("cards are drawn in the barter totals box colours", ReferenceEquals(tile.Background, window.TryFindResource("TotalsBoxBackground"))
            && ReferenceEquals(tile.BorderBrush, window.TryFindResource("TotalsBoxBorder")));

        // Apply, from the page.
        var xp = builtIn.First(c => c.Name == "Windows XP");
        page.ApplyTheme(xp);
        Equal("Apply on Windows XP puts it in the preview", xp.Theme.Colours["TitleBarBackground"],
            ((SolidColorBrush)page.PreviewControl.Resources["TitleBarBackground"]).Color);
        Equal("with Unsaved changes", "Unsaved changes", ((TextBlock)page.FindName("FootNote")).Text);
        Equal("and the app as it was", ThemeRoles.Find("TitleBarBackground")!.Default, ThemeService.Current["TitleBarBackground"]);
        page.Draft.Revert();
        Equal("Copy share code from a built-in card", ThemeShelf.CopiedNote, page.CopyShareCode(xp));
        Equal("puts its code on the (pretend) clipboard", "Windows XP", ThemeCode.Decode(clip.Text).Theme?.Name);

        // Pictures, drawn from each theme's own colours and kept for next time.
        Equal("the page draws the user's two and the nine built-in pictures", 2 + BuiltInThemes.All.Count, page.DrawMissingSnapshots());
        var cache = Path.Combine(folder, "built-in");
        double dpi = VisualTreeHelper.GetDpi(page).DpiScaleX;
        int width = (int)Math.Round(ThemeSnapshot.CardWidth * dpi);
        Check($"each kept in built-in\\ under its own name ({Directory.GetFiles(cache, "*.png").Length} files)",
            builtIn.All(c => File.Exists(Path.Combine(cache, ThemeShelf.BuiltInSnapshotName(c.Theme.Colours, width)))));
        foreach (var card in builtIn)
        {
            if (card.Snapshot is not BitmapSource picture)
            {
                Check($"{card.Name}: has a picture", false);
                continue;
            }
            var px = new byte[4];
            new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(picture.PixelWidth / 2, 1, 1, 1), px, 4, 0);
            var top = Color.FromRgb(px[2], px[1], px[0]);
            var want = card.Theme.Colours["TitleBarBackground"];
            Check($"{card.Name}: its picture is drawn in its own colours (title bar {top}, theme {want})",
                Math.Abs(top.R - want.R) + Math.Abs(top.G - want.G) + Math.Abs(top.B - want.B) < 24);
        }
        Equal("drawing them leaves the real preview in the draft's colours", page.Draft["TitleBarBackground"],
            ((SolidColorBrush)page.PreviewControl.Resources["TitleBarBackground"]).Color);

        Equal("the real clipboard was never used", typeof(FakeClipboard), page.ClipboardText.GetType());

        // Next time FleetFinder starts: a new window and page, the same folder.
        var again = (SettingsPage)new MainWindow().FindName("SettingsView");
        again.ClipboardText = new FakeClipboard();
        again.ThemeLibrary = new ThemeLibrary(folder);
        Check("next time the built-in pictures are read from the cache", again.BuiltInThemeCards.All(c => c.Snapshot != null));
        Equal("so nothing is drawn", 0, again.DrawMissingSnapshots());
    }
}
