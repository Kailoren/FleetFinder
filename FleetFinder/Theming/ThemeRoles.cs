using System.Windows;
using System.Windows.Media;

namespace FleetView.Theming;

/// <summary>
/// One kind of element the user can recolour: its resource key, the heading it sits under in
/// Settings, the label beside its colour box, and the colour the app ships with.
/// </summary>
public sealed record ThemeRole(string Key, string Group, string Label, Color Default)
{
    /// <summary>
    /// Whether the role carries transparency. Only the overlay dim does today. The picker edits
    /// the colour and leaves this alpha alone, and theme.json writes it as <c>#AARRGGBB</c>.
    /// </summary>
    public bool HasAlpha => Default.A != 255;
}

/// <summary>
/// Every colour in the app, as a list of roles. MainWindow.xaml refers to each one with
/// <c>{DynamicResource Key}</c>, and <see cref="ThemeService"/> puts a brush under every key.
/// </summary>
/// <remarks>
/// <para>
/// A role is a kind of element, not a hex value. A grid header and a divider were both
/// <c>#3A2410</c> in the original XAML, but they are different things and become two roles that
/// happen to start out the same, so changing one does not drag the other along. Two places share
/// a role only when they are the same kind of thing: every panel background is one role, every
/// orange "still needed" count is one role.
/// </para>
/// <para>
/// The defaults are exactly the colours the XAML had before this existed, which is what lets the
/// change be checked by pixel diff. This file is the only place in the C# that names a colour.
/// The order here is the order Settings lists them in, group by group.
/// </para>
/// </remarks>
public static class ThemeRoles
{
    private const string Window = "Window";
    private const string TitleBar = "Title bar";
    private const string Banner = "Update banner";
    private const string Tabs = "Tabs";
    private const string Panels = "Panels";
    private const string Grids = "Grids";
    private const string Values = "Values";
    private const string Buttons = "Buttons";
    private const string Inputs = "Inputs";
    private const string Scrolling = "Scrollbars and progress";
    private const string StatusBar = "Status bar";
    private const string Dialog = "Dialog";

    public static IReadOnlyList<ThemeRole> All { get; } =
    [
        Role("WindowBackground", Window, "Window background", "#0A0705"),
        // The window's own text colour. Most text sets its own, so in practice this is the
        // resume-search dialog's message and anything added later that does not.
        Role("MainText", Window, "Main text", "#EAE4DA"),
        // Small grey-brown helper text: field captions, hints, "no results", the location line.
        Role("MutedText", Window, "Muted text", "#9C7A55"),

        Role("TitleBarBackground", TitleBar, "Title bar background", "#0A0705"),
        Role("TitleBarText", TitleBar, "App name", "#9C7A55"),
        Role("TitleBarVersion", TitleBar, "Version number", "#5A3A15"),
        Role("CaptionButtonGlyph", TitleBar, "Window buttons", "#9C7A55"),
        Role("CaptionButtonHover", TitleBar, "Window button hover", "#3A2410"),
        Role("CaptionButtonHoverGlyph", TitleBar, "Window button hover icon", "#FF7500"),
        Role("CloseButtonHover", TitleBar, "Close button hover", "#E81123"),
        Role("CloseButtonHoverGlyph", TitleBar, "Close button hover icon", "#FFFFFF"),

        Role("BannerBackground", Banner, "Banner background", "#FF7500"),
        Role("BannerText", Banner, "Banner text", "#0A0705"),
        Role("BannerButtonBackground", Banner, "Download button", "#0A0705"),
        Role("BannerButtonText", Banner, "Download button text", "#EAE4DA"),

        Role("TabText", Tabs, "Tab text", "#B89470"),
        Role("TabHoverBackground", Tabs, "Tab hover", "#241809"),
        Role("SelectedTabBackground", Tabs, "Selected tab background", "#120E08"),
        Role("SelectedTabOutline", Tabs, "Selected tab outline", "#FF7500"),
        Role("SelectedTabText", Tabs, "Selected tab text", "#FF7500"),

        Role("PanelBackground", Panels, "Panel background", "#120E08"),
        Role("PanelBorder", Panels, "Panel border", "#FF7500"),
        // Headings and plain values inside a tab's panels. These used to inherit the selected
        // tab's text colour through the TabItem, so they were orange only because the tab label
        // was; each tab's content now sets this role itself, keeping the two apart.
        Role("PanelText", Panels, "Panel headings and text", "#FF7500"),
        Role("Divider", Panels, "Dividers", "#5A3A15"),
        Role("TotalsBoxBackground", Panels, "Barter totals box background", "#0A0705"),
        Role("TotalsBoxBorder", Panels, "Barter totals box border", "#5A3A15"),

        Role("GridBackground", Grids, "Grid background", "#120E08"),
        Role("GridBorder", Grids, "Grid border", "#5A3A15"),
        Role("GridRow", Grids, "Grid row", "#120E08"),
        Role("GridAlternateRow", Grids, "Grid alternate row", "#241809"),
        Role("GridText", Grids, "Grid text", "#EAE4DA"),
        Role("GridLines", Grids, "Grid lines", "#3A2410"),
        Role("GridHeaderBackground", Grids, "Grid header background", "#3A2410"),
        Role("GridHeaderText", Grids, "Grid header text", "#B89470"),
        Role("GridHeaderLines", Grids, "Grid header lines", "#5A3A15"),
        Role("GroupHeaderText", Grids, "Group headings", "#FF7500"),
        // The clicked row in a results grid and the row the search box jumped to share one look
        // on purpose (MainWindow.xaml says so beside the search highlight).
        Role("RowOutline", Grids, "Selected row outline", "#FF7500"),

        Role("NeededCount", Values, "Needed count", "#FF7500"),
        Role("HaveCount", Values, "Have count (Where to Sell)", "#FF7500"),
        Role("GoodValue", Values, "Good value (docking Yes)", "#6EE7A0"),
        Role("BadValue", Values, "Bad value (docking No)", "#F87171"),
        Role("UnknownValue", Values, "Unknown value", "#9C7A55"),
        Role("LinkText", Values, "System name link", "#FFB066"),
        Role("LinkHoverText", Values, "System name link hover", "#FFCE9E"),
        Role("SummaryText", Values, "Modification summary", "#FF7500"),
        Role("UnmatchedText", Values, "Unmatched import item", "#FF7500"),
        Role("BarterResult", Values, "Barter units you could acquire", "#FF7500"),
        Role("BarterPoints", Values, "Barter points", "#FF7500"),

        Role("ButtonBackground", Buttons, "Button background", "#FF7500"),
        Role("ButtonText", Buttons, "Button text", "#000000"),
        Role("ButtonDisabledBackground", Buttons, "Disabled button background", "#4A3316"),
        Role("ButtonDisabledText", Buttons, "Disabled button text", "#9C7A55"),
        Role("SecondaryButtonBackground", Buttons, "Secondary button background", "#5A3A15"),
        Role("SecondaryButtonText", Buttons, "Secondary button text", "#EAE4DA"),
        Role("StepperBackground", Buttons, "Plus and minus buttons", "#5A3A15"),
        Role("StepperText", Buttons, "Plus and minus button text", "#EAE4DA"),
        Role("StepperHover", Buttons, "Plus and minus button hover", "#6B4A1E"),

        Role("InputBackground", Inputs, "Text box background", "#0A0705"),
        Role("InputText", Inputs, "Text box text", "#EAE4DA"),
        Role("InputBorder", Inputs, "Text box border", "#5A3A15"),
        Role("CellEditorBorder", Inputs, "Needed cell editor border", "#FF7500"),
        Role("CheckboxBackground", Inputs, "Checkbox background", "#120E08"),
        Role("CheckboxBorder", Inputs, "Checkbox border", "#5A3A15"),
        Role("CheckboxHoverBorder", Inputs, "Checkbox hover border", "#FF7500"),
        Role("CheckboxTicked", Inputs, "Ticked checkbox", "#FF7500"),

        Role("ScrollbarThumb", Scrolling, "Scrollbar", "#5A3A15"),
        Role("ScrollbarThumbHover", Scrolling, "Scrollbar hover", "#FF7500"),
        Role("ProgressTrack", Scrolling, "Progress bar track", "#3A2410"),
        Role("ProgressFill", Scrolling, "Progress bar", "#FF7500"),

        Role("StatusBarBackground", StatusBar, "Status bar background", "#120E08"),
        Role("StatusText", StatusBar, "Status text", "#B89470"),
        Role("StatusDetailText", StatusBar, "Inventory status", "#9C7A55"),

        // The one role with alpha: it dims the tabs behind the resume-search dialog.
        Role("OverlayDim", Dialog, "Overlay dim", "#CC000000"),
        Role("DialogBackground", Dialog, "Dialog background", "#120E08"),
        Role("DialogBorder", Dialog, "Dialog border", "#FF7500"),
        Role("DialogTitle", Dialog, "Dialog title", "#FF7500"),
    ];

    /// <summary>The group headings, in the order Settings shows them.</summary>
    public static IReadOnlyList<string> Groups { get; } = All.Select(r => r.Group).Distinct().ToList();

    private static readonly Dictionary<string, ThemeRole> ByKey = All.ToDictionary(r => r.Key, StringComparer.Ordinal);

    /// <summary>The role with this exact key, or null. Keys are case-sensitive, as resource keys are.</summary>
    public static ThemeRole? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>A fresh map of every role to its shipped colour.</summary>
    public static Dictionary<string, Color> Defaults() =>
        All.ToDictionary(r => r.Key, r => r.Default, StringComparer.Ordinal);

    /// <summary>
    /// Framework resource keys that must carry a role's brush as well as the role's own key.
    /// </summary>
    /// <remarks>
    /// The DataGrid's built-in cell style colours a selected cell's text from these two
    /// SystemColors keys. MainWindow.xaml used to override them with a literal, so that clicking a
    /// row (which only outlines it) left its text looking like every other row's. They now follow
    /// Grid text, from the same dictionary as everything else, which also means the Settings
    /// preview gets them in its own dictionary without doing anything special. The three
    /// Transparent overrides beside them stay in the XAML, as Transparent is not a colour.
    /// </remarks>
    public static IReadOnlyList<(object ResourceKey, string RoleKey)> SystemAliases { get; } =
    [
        (SystemColors.HighlightTextBrushKey, "GridText"),
        (SystemColors.InactiveSelectionHighlightTextBrushKey, "GridText"),
    ];

    private static ThemeRole Role(string key, string group, string label, string hex) =>
        ColourMaths.TryParseHex(hex, out var colour)
            ? new ThemeRole(key, group, label, colour)
            : throw new InvalidOperationException($"Theme role {key} has an unreadable default {hex}.");
}
