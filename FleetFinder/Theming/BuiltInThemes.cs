using System.Text.RegularExpressions;
using System.Windows.Media;
using FleetView.Services;

namespace FleetView.Theming;

/// <summary>
/// One of the themes FleetFinder comes with: a fixed id, its name, every role's colour, and the
/// label of the group it starts or belongs to on the strip ("Built in", "Powerplay", or empty for
/// a group marked by a line alone).
/// </summary>
public sealed record BuiltInTheme(string Id, string Name, IReadOnlyDictionary<string, Color> Colours, string Section = "");

/// <summary>
/// The themes that ship inside FleetFinder. They show on the Themes strip after the user's own,
/// with "FleetFinder" as their author, and can be applied and shared like any other but never
/// deleted. They are not files in <c>Data\themes</c>, so nothing a user does to that folder can
/// break one. FleetFinder Orange (the shipped colours) comes first, then the twelve Powerplay
/// leaders in their own group, then Guardian Cyan, Synthwave, Windows 98, Windows XP and Green
/// Screen (D4 and D5, Ali 2026-10-24).
/// </summary>
/// <remarks>
/// <para>
/// Most are written as a small palette of base colours (window, panel, text, accent and so on)
/// that <see cref="Spread"/> turns into all 93 roles, the same way Ali's own palette is built,
/// followed by the handful of roles where a theme wants something else. The two light ones
/// (Windows 98 and XP) override a lot more, since a light theme puts its text on very different
/// backgrounds. The Powerplay themes all share one near-black page and differ only in the power's
/// colour (<see cref="Power"/>), sampled from Ali's grid of the leaders.
/// </para>
/// <para>
/// This file and <see cref="ThemeRoles"/> are the only C# that may name a colour (the self-test's
/// scan allows exactly these two). Colours are written <c>#RRGGBB</c>, or <c>#AARRGGBB</c> for the
/// five roles with alpha. Anything wrong with a definition (an unreadable colour, alpha on a role
/// that has none, an override that changes nothing, a role with no colour) leaves that one theme
/// out and is listed in <see cref="Problems"/>, which the self-test requires to be empty; the app
/// never fails to open over it. The self-test also pins every theme's colours by share code and
/// checks the text in each one can be read on what it is drawn over.
/// </para>
/// </remarks>
public static partial class BuiltInThemes
{
    /// <summary>The author line on every built-in theme's card.</summary>
    public const string Author = "FleetFinder";

    /// <summary>The label over the twelve Powerplay themes on the strip.</summary>
    public const string PowerplaySection = "Powerplay";

    /// <summary>
    /// The base colours of a theme. Each one is a kind of colour rather than a role: one "edge"
    /// colour is every divider, border, scrollbar and secondary button, one "accent" is every
    /// highlight, heading and primary button, and so on. <see cref="Spread"/> says which role takes
    /// which. The last four default to the see-through blacks Ali's palette uses, which suit any
    /// dark theme.
    /// </summary>
    /// <param name="Window">Behind everything: the window, title bar, text boxes, the barter totals box.</param>
    /// <param name="Panel">Panels, grids, the selected tab, tooltips, the status bar, dialogs.</param>
    /// <param name="Alternate">Every other grid row, and a tab under the mouse.</param>
    /// <param name="Line">Grid lines and headers, a window button under the mouse, the progress track.</param>
    /// <param name="Edge">Dividers and borders, secondary and plus/minus buttons, the scrollbar.</param>
    /// <param name="EdgeHover">A plus or minus button under the mouse.</param>
    /// <param name="Disabled">A button that cannot be pressed.</param>
    /// <param name="Text">Main, grid, text box and secondary button text.</param>
    /// <param name="Soft">Tab text, grid headers, the status line: a step quieter than the text.</param>
    /// <param name="Muted">Hints and captions, the app name, window buttons, unknown values, greyed text.</param>
    /// <param name="Accent">Borders of panels, headings, counts, the primary button, highlights.</param>
    /// <param name="OnAccent">Text on the accent: the primary button, the banner, the close button's icon.</param>
    /// <param name="Wash">The right-click menu's item under the mouse, see-through.</param>
    public sealed record Palette(
        string Window, string Panel, string Alternate, string Line, string Edge, string EdgeHover, string Disabled,
        string Text, string Soft, string Muted, string Accent, string OnAccent, string Link, string LinkHover,
        string Good, string Bad, string Selection, string Menu, string MenuDisabled, string Wash,
        string Dim = "#CC000000", string FaintWash = "#0A000000", string FaintEdge = "#21000000", string Shadow = "#71000000");

    /// <summary>One theme as written below: its palette and the roles it sets itself, or (for
    /// FleetFinder Orange) the shipped defaults as they are. <paramref name="Section"/> starts a
    /// group on the strip.</summary>
    private sealed record Definition(string Id, string Name, Palette? Palette, (string Key, string Hex)[] Overrides,
                                     bool IsDefaults = false, string Section = "");

    /// <summary>In the order the strip shows them.</summary>
    private static readonly Definition[] Definitions =
    [
        // The shipped colours, Ali's palette, so they are one Apply away after trying another theme.
        new("fleetfinder-orange", "FleetFinder Orange", null, [], IsDefaults: true, Section: "Built in"),

        // The twelve Powerplay leaders, in the order of Ali's grid.
        Power("jerome-archer", "Jerome Archer", "#F009F0", alternate: "#1D161D", linkHover: "#F778F7"),
        Power("nakato-kaine", "Nakato Kaine", "#82F80F", alternate: "#191D16", linkHover: "#BAFB7B", good: "#8FD8FF"),
        Power("zemina-torval", "Zemina Torval", "#0341F0", alternate: "#15171D", linkHover: "#9BB5FE", onColour: "#FFFFFF", text: "#4A79FD", state: "#2059FC"),
        Power("yuri-grom", "Yuri Grom", "#F64606", alternate: "#1D1716", linkHover: "#FC9E7C", text: "#F94E10", bad: "#FF8AC8"),
        Power("pranav-antal", "Pranav Antal", "#FBF019", alternate: "#1D1D16", linkHover: "#FDF780"),
        Power("li-yong-rui", "Li Yong-Rui", "#11F662", alternate: "#161D18", linkHover: "#7CFAA9", good: "#8FD8FF"),
        Power("felicia-winters", "Felicia Winters", "#F68712", alternate: "#1D1916", linkHover: "#FABD7D"),
        Power("edmund-mahon", "Edmund Mahon", "#09720B", alternate: "#161916", linkHover: "#79C77B", onColour: "#FFFFFF", text: "#0C9A0F", state: "#0A7E0C", good: "#8FD8FF"),
        Power("denton-patreus", "Denton Patreus", "#0CC3C7", alternate: "#161B1B", linkHover: "#79DEE0"),
        Power("archon-delaine", "Archon Delaine", "#FF0100", alternate: "#1D1515", linkHover: "#FF8E8D", text: "#FF3130", bad: "#FF8AC8"),
        Power("aisling-duval", "Aisling Duval", "#009AFA", alternate: "#151A1D", linkHover: "#73C7FC"),
        Power("arissa-lavigny-duval", "Arissa Lavigny-Duval", "#8001FB", alternate: "#19151D", linkHover: "#D0A0FE", onColour: "#FFFFFF", text: "#A952FE", state: "#9222FE"),

        // Charcoal grey with the cyan glow of Guardian ruins.
        new("guardian-cyan", "Guardian Cyan", new Palette(
            Window: "#0A0C0E", Panel: "#121619", Alternate: "#1A1F24", Line: "#242A31", Edge: "#36414A",
            EdgeHover: "#47545F", Disabled: "#262D34", Text: "#E6F3F5", Soft: "#A8C3C8", Muted: "#85A1A7",
            Accent: "#3FE0E8", OnAccent: "#051214", Link: "#9BD8FF", LinkHover: "#C9EBFF", Good: "#7CF29A",
            Bad: "#FF7B8A", Selection: "#1FA6AE", Menu: "#060809", MenuDisabled: "#5D727A", Wash: "#403FE0E8"),
            []),

        // Deep purple with hot pink for borders, buttons and counts, cyan for headings, links and
        // anything ticked, and a mint green for docking Yes.
        new("synthwave", "Synthwave", new Palette(
            Window: "#11041E", Panel: "#1B0A2D", Alternate: "#26103D", Line: "#34174F", Edge: "#4F2475",
            EdgeHover: "#652F94", Disabled: "#3A1A58", Text: "#FCF0FF", Soft: "#E2BFF5", Muted: "#B98FD6",
            Accent: "#FF2E97", OnAccent: "#1A0526", Link: "#2DE2E6", LinkHover: "#9AF4F6", Good: "#72F1B8",
            Bad: "#FF6B6B", Selection: "#C2187A", Menu: "#0C0316", MenuDisabled: "#86609F", Wash: "#40FF2E97"),
            [("PanelText", "#2DE2E6"), ("GroupHeaderText", "#2DE2E6"), ("SelectedTabText", "#2DE2E6"),
             ("HaveCount", "#2DE2E6"), ("DialogTitle", "#2DE2E6"), ("SummaryText", "#2DE2E6"),
             ("BarterResult", "#2DE2E6"), ("MenuText", "#FCF0FF"), ("TitleBarText", "#E2BFF5"),
             ("CheckboxTicked", "#2DE2E6"), ("ProgressFill", "#2DE2E6")]),

        // The teal desktop, silver panels with black text, a navy title bar, white grids and text
        // boxes, the pale yellow tooltip, silver menus with a navy highlight. Navy stands in for
        // the selection wherever the app shows one (the row outline, selected text, the menu).
        // Text on the teal is white, as black on it is a shade too dark to read well, and the
        // buttons are a lighter silver than the panels, there being no bevel to show them by.
        new("windows-98", "Windows 98", new Palette(
            Window: "#008080", Panel: "#C0C0C0", Alternate: "#FFFFFF", Line: "#C0C0C0", Edge: "#808080",
            EdgeHover: "#DFDFDF", Disabled: "#D4D4D4", Text: "#000000", Soft: "#000000", Muted: "#444444",
            Accent: "#000080", OnAccent: "#FFFFFF", Link: "#0000FF", LinkHover: "#800080", Good: "#008000",
            Bad: "#C00000", Selection: "#000080", Menu: "#C0C0C0", MenuDisabled: "#5C5C5C", Wash: "#40000080",
            Dim: "#55000000", FaintWash: "#10000080", FaintEdge: "#40000080", Shadow: "#40000000"),
            [("TitleBarBackground", "#000080"), ("TitleBarText", "#FFFFFF"), ("TitleBarVersion", "#C0C0C0"),
             ("CaptionButtonGlyph", "#FFFFFF"), ("CaptionButtonHoverGlyph", "#000000"), ("CloseButtonHover", "#C0C0C0"),
             ("CloseButtonHoverGlyph", "#000000"), ("BannerBackground", "#FFFFE1"), ("BannerText", "#000000"),
             ("BannerButtonBackground", "#C0C0C0"), ("TabText", "#FFFFFF"),
             ("TabHoverBackground", "#006A6A"), ("SelectedTabOutline", "#FFFFFF"), ("SelectedTabText", "#000000"),
             ("PanelBorder", "#808080"), ("PanelText", "#000000"), ("TotalsBoxBackground", "#FFFFFF"),
             ("GridBackground", "#FFFFFF"), ("GridRow", "#FFFFFF"), ("UnknownValue", "#6E6E6E"),
             ("ButtonBackground", "#DFDFDF"), ("ButtonText", "#000000"), ("ButtonDisabledText", "#666666"),
             ("SecondaryButtonBackground", "#DFDFDF"), ("StepperBackground", "#C0C0C0"),
             ("InputBackground", "#FFFFFF"), ("InputHoverBorder", "#000000"), ("CheckboxBackground", "#FFFFFF"),
             ("CheckboxHoverBorder", "#000000"), ("ToolTipBackground", "#FFFFE1"), ("ToolTipBorder", "#000000"),
             ("MenuBorder", "#808080"), ("MenuText", "#000000"),
             ("MenuGutterLine", "#808080"), ("MenuGutterLineHighlight", "#FFFFFF"), ("ProgressTrack", "#FFFFFF"),
             ("StatusDetailText", "#404040")]),

        // Luna: the blue title bar with white text, beige panels, white grids and text boxes with
        // XP's blue-grey border, the blue selection, pale yellow tooltips, the red-orange close
        // button, XP's green for the main button and progress bar, and the orange glow it put on
        // anything under the mouse (and along the top of the chosen tab).
        new("windows-xp", "Windows XP", new Palette(
            Window: "#3A6BD6", Panel: "#ECE9D8", Alternate: "#FFFFFF", Line: "#EBEADB", Edge: "#ACA899",
            EdgeHover: "#FBE3A3", Disabled: "#F5F4EA", Text: "#000000", Soft: "#000000", Muted: "#5A5A5A",
            Accent: "#0046D5", OnAccent: "#FFFFFF", Link: "#0066CC", LinkHover: "#003399", Good: "#2B802B",
            Bad: "#C1272D", Selection: "#316AC5", Menu: "#FFFFFF", MenuDisabled: "#8A8775", Wash: "#50316AC5",
            Dim: "#55000000", FaintWash: "#18316AC5", FaintEdge: "#60316AC5", Shadow: "#60000000"),
            [("TitleBarBackground", "#0054E3"), ("TitleBarText", "#FFFFFF"), ("TitleBarVersion", "#D6E2FA"),
             ("CaptionButtonGlyph", "#FFFFFF"), ("CaptionButtonHover", "#3C81F3"),
             ("CaptionButtonHoverGlyph", "#FFFFFF"), ("CloseButtonHover", "#E0441C"),
             ("BannerBackground", "#FFFFE1"), ("BannerText", "#000000"),
             ("BannerButtonBackground", "#2B802B"), ("BannerButtonText", "#FFFFFF"), ("TabText", "#FFFFFF"),
             ("TabHoverBackground", "#2556C9"), ("SelectedTabOutline", "#FFC83C"), ("SelectedTabText", "#000000"),
             ("PanelBorder", "#7F9DB9"), ("TotalsBoxBackground", "#FFFFFF"), ("GridBackground", "#FFFFFF"),
             ("GridBorder", "#7F9DB9"), ("GridRow", "#FFFFFF"), ("GridHeaderLines", "#C7C5B2"),
             ("RowOutline", "#316AC5"), ("UnknownValue", "#6D6D6D"), ("ButtonBackground", "#2B802B"),
             ("ButtonDisabledText", "#8A8775"), ("SecondaryButtonBackground", "#FFFFFF"),
             ("StepperBackground", "#ECE9D8"), ("InputBackground", "#FFFFFF"), ("InputBorder", "#7F9DB9"),
             ("InputHoverBorder", "#F8B636"), ("InputFocusBorder", "#316AC5"), ("CheckboxBackground", "#FFFFFF"),
             ("CheckboxBorder", "#1C5180"), ("CheckboxHoverBorder", "#F8B636"), ("CheckboxTicked", "#21A121"),
             ("ToolTipBackground", "#FFFFE1"), ("ToolTipBorder", "#000000"),
             ("MenuBorder", "#ACA899"), ("MenuText", "#000000"), ("MenuHighlightBorder", "#316AC5"),
             ("MenuGutter", "#F4F3EE"), ("MenuGutterLine", "#D0D0BF"), ("MenuGutterLineHighlight", "#FFFFFF"),
             ("ScrollbarThumb", "#B5C9F5"), ("ScrollbarThumbHover", "#7B9DE8"), ("ProgressTrack", "#FFFFFF"),
             ("ProgressFill", "#21A121"), ("StatusDetailText", "#4D4D4D"), ("DialogBorder", "#0054E3")]),

        // A monochrome terminal: phosphor green on black, in a few strengths. Buttons and the
        // banner are drawn in reverse, black on bright green. Docking Yes and No are both green,
        // as everything is; the words still say which.
        new("green-screen", "Green Screen", new Palette(
            Window: "#000000", Panel: "#020902", Alternate: "#061806", Line: "#0A2A0A", Edge: "#145A14",
            EdgeHover: "#1A6E1A", Disabled: "#0B2E0B", Text: "#4DFF4D", Soft: "#33D633", Muted: "#22B022",
            Accent: "#66FF66", OnAccent: "#000000", Link: "#99FF99", LinkHover: "#CCFFCC", Good: "#99FF99",
            Bad: "#33CC33", Selection: "#33FF33", Menu: "#000000", MenuDisabled: "#1A8A1A", Wash: "#4033FF33"),
            []),
    ];

    /// <summary>
    /// A Powerplay theme: the near-black page of Ali's grid (panels <c>#0E0E0E</c>, a darker
    /// window, every other row with the faintest touch of the power's hue), light grey text, and
    /// the power's colour exactly wherever it is a fill or a line: the accent, panel borders, the
    /// tab and row outlines, the banner, the main button, ticks, the progress bar, the text box and
    /// dialog borders. Where the colour is text (headings, counts, links) and too dark to read on
    /// the page, <paramref name="text"/> is the same hue made just light enough (4.5 to 1);
    /// <paramref name="state"/> does the same at 3 to 1 for the large headings, the window button
    /// icon under the mouse, ticks, the row outline and the progress bar. Both were worked out by
    /// raising the colour's lightness in small steps until the self-test's contrast pairs pass.
    /// <paramref name="onColour"/> is black or white, whichever reads on the colour.
    /// </summary>
    private static Definition Power(string id, string name, string colour, string alternate, string linkHover,
                                    string onColour = "#000000", string? text = null, string? state = null,
                                    string good = "#6EE7A0", string bad = "#F87171")
    {
        var palette = new Palette(
            Window: "#090909", Panel: "#0E0E0E", Alternate: alternate, Line: "#202020", Edge: "#3A3A3A",
            EdgeHover: "#4C4C4C", Disabled: "#242424", Text: "#EDEDED", Soft: "#C2C2C2", Muted: "#9C9C9C",
            Accent: colour, OnAccent: onColour, Link: text ?? colour, LinkHover: linkHover, Good: good, Bad: bad,
            Selection: colour, Menu: "#060606", MenuDisabled: "#707070", Wash: "#40" + colour[1..]);
        var overrides = new List<(string, string)>();
        if (text != null)
            foreach (var key in new[] { "PanelText", "NeededCount", "HaveCount", "SummaryText", "UnmatchedText", "BarterPoints", "SelectedTabText", "MenuText" })
                overrides.Add((key, text));
        if (state != null)
            foreach (var key in new[] { "GroupHeaderText", "DialogTitle", "BarterResult", "CaptionButtonHoverGlyph", "CheckboxTicked", "RowOutline", "ProgressFill" })
                overrides.Add((key, state));
        return new Definition(id, name, palette, [.. overrides], Section: PowerplaySection);
    }

    /// <summary>
    /// Which base colour each role takes. Every role is here once, in the order of
    /// <see cref="ThemeRoles.All"/> (the compiler refuses a key listed twice, and a role left out
    /// is a <see cref="Problems"/> entry). This is Ali's palette read as a pattern: his window
    /// background is also the title bar, the text boxes and the totals box; his orange is every
    /// border, heading, count and button; and so on.
    /// </summary>
    private static string? Spread(string key, Palette p) => key switch
    {
        "WindowBackground" => p.Window,
        "MainText" => p.Text,
        "MutedText" => p.Muted,

        "TitleBarBackground" => p.Window,
        "TitleBarText" => p.Muted,
        "TitleBarVersion" => p.Muted,
        "CaptionButtonGlyph" => p.Muted,
        "CaptionButtonHover" => p.Line,
        "CaptionButtonHoverGlyph" => p.Accent,
        "CloseButtonHover" => p.Accent,
        "CloseButtonHoverGlyph" => p.OnAccent,

        "BannerBackground" => p.Accent,
        "BannerText" => p.OnAccent,
        "BannerButtonBackground" => p.Window,
        "BannerButtonText" => p.Text,

        "TabText" => p.Soft,
        "TabHoverBackground" => p.Alternate,
        "SelectedTabBackground" => p.Panel,
        "SelectedTabOutline" => p.Accent,
        "SelectedTabText" => p.Accent,

        "PanelBackground" => p.Panel,
        "PanelBorder" => p.Accent,
        "PanelText" => p.Accent,
        "Divider" => p.Edge,
        "TotalsBoxBackground" => p.Window,
        "TotalsBoxBorder" => p.Edge,

        "GridBackground" => p.Panel,
        "GridBorder" => p.Edge,
        "GridRow" => p.Panel,
        "GridAlternateRow" => p.Alternate,
        "GridText" => p.Text,
        "GridLines" => p.Line,
        "GridHeaderBackground" => p.Line,
        "GridHeaderText" => p.Soft,
        "GridHeaderLines" => p.Edge,
        "GroupHeaderText" => p.Accent,
        "RowOutline" => p.Accent,

        "NeededCount" => p.Accent,
        "HaveCount" => p.Accent,
        "GoodValue" => p.Good,
        "BadValue" => p.Bad,
        "UnknownValue" => p.Muted,
        "LinkText" => p.Link,
        "LinkHoverText" => p.LinkHover,
        "SummaryText" => p.Accent,
        "UnmatchedText" => p.Accent,
        "BarterResult" => p.Accent,
        "BarterPoints" => p.Accent,

        "ButtonBackground" => p.Accent,
        "ButtonText" => p.OnAccent,
        "ButtonDisabledBackground" => p.Disabled,
        "ButtonDisabledText" => p.Muted,
        "SecondaryButtonBackground" => p.Edge,
        "SecondaryButtonText" => p.Text,
        "StepperBackground" => p.Edge,
        "StepperText" => p.Text,
        "StepperHover" => p.EdgeHover,

        "InputBackground" => p.Window,
        "InputText" => p.Text,
        "InputBorder" => p.Edge,
        "InputHoverBorder" => p.Accent,
        "InputFocusBorder" => p.Accent,
        "CheckboxBackground" => p.Panel,
        "CheckboxBorder" => p.Edge,
        "CheckboxHoverBorder" => p.Accent,
        "CheckboxTicked" => p.Accent,
        "TextSelection" => p.Selection,

        "ToolTipBackground" => p.Panel,
        "ToolTipBorder" => p.Accent,
        "ToolTipText" => p.Text,
        "MenuBackground" => p.Menu,
        "MenuBorder" => p.Accent,
        "MenuText" => p.Accent,
        "MenuDisabledText" => p.MenuDisabled,
        "MenuHighlight" => p.Wash,
        "MenuHighlightBorder" => p.Accent,
        "MenuDisabledHighlight" => p.FaintWash,
        "MenuDisabledHighlightBorder" => p.FaintEdge,
        "MenuGutter" => p.Menu,
        "MenuGutterLine" => p.Accent,
        "MenuGutterLineHighlight" => p.Accent,
        "MenuShadow" => p.Shadow,

        "ScrollbarThumb" => p.Edge,
        "ScrollbarThumbHover" => p.Accent,
        "ProgressTrack" => p.Line,
        "ProgressFill" => p.Accent,

        "StatusBarBackground" => p.Panel,
        "StatusText" => p.Soft,
        "StatusDetailText" => p.Muted,

        "OverlayDim" => p.Dim,
        "DialogBackground" => p.Panel,
        "DialogBorder" => p.Accent,
        "DialogTitle" => p.Accent,
        _ => null,
    };

    private static readonly (IReadOnlyList<BuiltInTheme> Themes, IReadOnlyList<string> Problems) Built = Build();

    /// <summary>The built-in themes, in the order the strip shows them. A theme whose definition
    /// has a problem is left out.</summary>
    public static IReadOnlyList<BuiltInTheme> All => Built.Themes;

    /// <summary>What is wrong with the definitions, one line each. Empty, or the self-test fails.</summary>
    public static IReadOnlyList<string> Problems => Built.Problems;

    /// <summary>The built-in theme with this id, or null.</summary>
    public static BuiltInTheme? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    [GeneratedRegex("^#[0-9A-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex Opaque();

    [GeneratedRegex("^#[0-9A-F]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex WithAlpha();

    private static (IReadOnlyList<BuiltInTheme>, IReadOnlyList<string>) Build()
    {
        var themes = new List<BuiltInTheme>();
        var problems = new List<string>();
        foreach (var definition in Definitions)
        {
            var found = new List<string>();
            var colours = definition.IsDefaults ? ThemeRoles.Defaults()
                : definition.Palette is { } palette ? Expand(definition.Name, palette, definition.Overrides, found)
                : [];
            if (!definition.IsDefaults && definition.Palette is null)
                found.Add($"{definition.Name}: has neither a palette nor the shipped defaults.");
            if (found.Count == 0)
                themes.Add(new BuiltInTheme(definition.Id, definition.Name, colours, definition.Section));
            problems.AddRange(found);
        }
        foreach (var problem in problems)
            DiagnosticLog.Note("Built-in theme left out: " + problem);
        return (themes, problems);
    }

    /// <summary>
    /// A palette and its overrides as every role's colour, the way each built-in theme is made.
    /// Whatever is wrong is added to <paramref name="problems"/>, one line each, naming the theme:
    /// a role the palette gives no colour, a colour not written exactly <c>#RRGGBB</c> (or
    /// <c>#AARRGGBB</c>, part see-through, for a role with alpha), an override of something that
    /// is not a role, set twice, or set to what the palette already gives it. Public so the
    /// self-test can hand it broken definitions and see each one caught.
    /// </summary>
    public static Dictionary<string, Color> Expand(string name, Palette palette, IReadOnlyList<(string Key, string Hex)> overrides,
                                                   ICollection<string> problems)
    {
        var colours = new Dictionary<string, Color>(StringComparer.Ordinal);
        foreach (var role in ThemeRoles.All)
        {
            if (Spread(role.Key, palette) is not { } hex)
                problems.Add($"{name}: no base colour for {role.Key}.");
            else if (Read(name, role, hex, problems) is { } colour)
                colours[role.Key] = colour;
        }

        var overridden = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, hex) in overrides)
        {
            if (ThemeRoles.Find(key) is not { } role)
            {
                problems.Add($"{name}: {key} is not a role.");
                continue;
            }
            if (!overridden.Add(key))
            {
                problems.Add($"{name}: {key} is set twice.");
                continue;
            }
            if (Read(name, role, hex, problems) is not { } colour) continue;
            if (colours.TryGetValue(key, out var spread) && spread == colour)
                problems.Add($"{name}: {key} is set to {hex}, which its palette gives it already.");
            colours[key] = colour;
        }
        return colours;
    }

    /// <summary>One colour as written, or null (and a problem) if it is not exactly
    /// <c>#RRGGBB</c>, or <c>#AARRGGBB</c> with some transparency for a role that has alpha.</summary>
    private static Color? Read(string name, ThemeRole role, string hex, ICollection<string> problems)
    {
        bool shapeOk = role.HasAlpha ? WithAlpha().IsMatch(hex) : Opaque().IsMatch(hex);
        if (!shapeOk || !ColourMaths.TryParseHex(hex, out var colour))
        {
            problems.Add($"{name}: {role.Key} is \"{hex}\", which is not {(role.HasAlpha ? "#AARRGGBB" : "#RRGGBB")}.");
            return null;
        }
        // A see-through role fully clear or fully solid could not be put right in the picker,
        // which keeps a role's alpha as it is.
        if (role.HasAlpha && (colour.A == 0 || colour.A == 255))
        {
            problems.Add($"{name}: {role.Key} is \"{hex}\", which is not see-through.");
            return null;
        }
        return colour;
    }
}
