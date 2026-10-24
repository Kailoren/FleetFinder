using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using FleetView;
using FleetView.Controls;
using FleetView.Services;
using FleetView.Settings;
using FleetView.Theming;

// The D2 sections (2026-10-24): copying a colour from one row of the Settings list to another
// with Ctrl+C and Ctrl+V over the rows (C7), and clicking the preview to find a colour's rows
// (C9). The rules on their own first, then the tags in the preview's XAML, then the real page.
// Nothing here touches the real clipboard: every copy and paste goes to a pretend one.
internal sealed partial class SelfTest
{
    /// <summary>A clipboard that is only a string, and can be made busy.</summary>
    private sealed class FakeClipboard : IClipboardText
    {
        public string? Text;
        public bool Busy;
        public string? GetText() => Busy ? null : Text;

        public bool SetText(string text)
        {
            if (Busy) return false;
            Text = text;
            return true;
        }
    }

    // ---- hover copy and paste ---------------------------------------------------------------------

    private void HoverClipboardTests()
    {
        HoverAction Act(Key key, ModifierKeys mods, bool showing = true, bool row = true, bool overlay = false, bool typing = false) =>
            HoverClipboard.ActionFor(key, mods, showing, row, overlay, typing);

        Equal("Ctrl+C over a row copies", HoverAction.Copy, Act(Key.C, ModifierKeys.Control));
        Equal("Ctrl+V over a row pastes", HoverAction.Paste, Act(Key.V, ModifierKeys.Control));
        Equal("C on its own is left alone", HoverAction.None, Act(Key.C, ModifierKeys.None));
        Equal("Ctrl+Shift+C is left alone", HoverAction.None, Act(Key.C, ModifierKeys.Control | ModifierKeys.Shift));
        Equal("Ctrl+Alt+V is left alone", HoverAction.None, Act(Key.V, ModifierKeys.Control | ModifierKeys.Alt));
        Equal("Ctrl+X is left alone", HoverAction.None, Act(Key.X, ModifierKeys.Control));
        Equal("not with Settings hidden", HoverAction.None, Act(Key.C, ModifierKeys.Control, showing: false));
        Equal("not with no row under the mouse", HoverAction.None, Act(Key.V, ModifierKeys.Control, row: false));
        Equal("not with the picker open", HoverAction.None, Act(Key.V, ModifierKeys.Control, overlay: true));
        Equal("not while typing in a text box, which copies text as before", HoverAction.None,
            Act(Key.C, ModifierKeys.Control, typing: true));

        Equal("copied text is #RRGGBB in capitals", "#FF7500", HoverClipboard.Format(Hex("#ff7500")));
        Equal("and never carries alpha", "#000000", HoverClipboard.Format(Hex("#CC000000")));

        // Paste takes exactly what the picker's HEX field takes.
        string?[] samples =
        [
            "#ff7500", "FF7500", "  #fa3  ", "fa3\r\n", "#80FF7500", "#FF7500\n", "#000", "#FFFFFF",
            "", "   ", null, "orange", "#12345", "#1234567", "#GG7500", "rgb(255, 117, 0)", "255, 117, 0",
            "#ff7500ff00", "# ff7500", "ff 75 00", "#\u0660\u0661\u0662\u0663\u0664\u0665",
        ];
        var disagree = new List<string>();
        foreach (var text in samples)
        {
            var field = new ColourPickerState();
            field.SetColour(Color.FromRgb(1, 2, 3));
            bool fieldTakes = field.Commit(ColourPickerState.Field.Hex, text);
            bool pasteTakes = HoverClipboard.TryRead(text, out var pasted);
            if (fieldTakes != pasteTakes
                || (pasteTakes && field.OpaqueColour != Color.FromRgb(pasted.R, pasted.G, pasted.B)))
                disagree.Add(text ?? "null");
        }
        Check($"paste and the HEX field agree on all {samples.Length} samples", disagree.Count == 0, string.Join(" | ", disagree));

        Equal("a pasted colour keeps the target's alpha", Hex("#CC123456"), HoverClipboard.KeepAlpha(Hex("#123456"), Hex("#CC000000")));
        Equal("and an opaque target stays opaque, whatever alpha came in", Hex("#FF123456"),
            HoverClipboard.KeepAlpha(Hex("#40123456"), Hex("#FF000000")));

        // Through a draft, with a pretend clipboard.
        var saved = ThemeRoles.Defaults();
        var draft = new ThemeDraft(saved, Path.Combine(_dir, "hover", "theme.json"), _ => { });
        var clip = new FakeClipboard { Text = "untouched" };
        var hover = new HoverClipboard(draft, clip);
        Equal("copy says what it copied", "Copied #FF7500", hover.Copy("PanelBorder"));
        Equal("and puts it on the clipboard", "#FF7500", clip.Text);
        Check("copying changes nothing", !draft.IsDirty);
        Equal("paste says so", HoverClipboard.PastedNote, hover.Paste("GridRow"));
        Equal("and the row has the colour", Hex("#FF7500"), draft["GridRow"]);
        Check("which is an unsaved change", draft.IsDirty);
        draft.Revert();
        Equal("Revert undoes a paste", saved["GridRow"], draft["GridRow"]);

        clip.Text = "  #123456\r\n";
        hover.Paste("OverlayDim");
        Equal("pasting onto the dimming keeps it see-through", Hex("#CC123456"), draft["OverlayDim"]);
        clip.Text = "#80ABCDEF";
        hover.Paste("PanelBackground");
        Equal("eight digits onto an opaque role paste opaque", Hex("#FFABCDEF"), draft["PanelBackground"]);
        draft.Revert();

        foreach (var junk in new[] { null, "", "orange", "Copied #FF7500" })
        {
            clip.Text = junk;
            Equal($"\"{junk ?? "null"}\" on the clipboard pastes nothing", HoverClipboard.NothingToPasteNote, hover.Paste("GridRow"));
        }
        Check("and leaves the draft alone", !draft.IsDirty);
        clip.Busy = true;
        Equal("a busy clipboard says so on copy", HoverClipboard.ClipboardBusyNote, hover.Copy("GridRow"));
        Equal("and a paste from it finds nothing", HoverClipboard.NothingToPasteNote, hover.Paste("GridRow"));
    }

    // ---- click to find ----------------------------------------------------------------------------

    private static RoleRegion Region(double x, double y, double w, double h, params string[] roles) =>
        new(new Rect(x, y, w, h), roles);

    private void RoleFinderTests()
    {
        var panel = Region(0, 0, 100, 100, "PanelBackground", "PanelBorder");
        var text = Region(10, 10, 50, 20, "PanelText");
        var line = Region(10, 50, 80, 1, "Divider");
        var over = Region(70, 60, 30, 30, "OverlayDim");
        var regions = new List<RoleRegion> { panel, text, line, over };

        Check("a click on the text finds the text, not its panel", ReferenceEquals(RoleFinder.At(regions, new Point(20, 15)), text));
        Check("beside it, the panel", ReferenceEquals(RoleFinder.At(regions, new Point(80, 20)), panel));
        Check("off everything, nothing", RoleFinder.At(regions, new Point(150, 20)) == null);
        Check("where two overlap, the one painted later", ReferenceEquals(RoleFinder.At(regions, new Point(80, 70)), over));
        Check("a one-pixel line answers two pixels either side", ReferenceEquals(RoleFinder.At(regions, new Point(40, 52.4)), line)
            && ReferenceEquals(RoleFinder.At(regions, new Point(40, 48.6)), line));
        Check("but not three", ReferenceEquals(RoleFinder.At(regions, new Point(40, 53.6)), panel));
        Equal("a thin region's target is the minimum size", RoleFinder.MinHitSize, RoleFinder.HitRect(line.Bounds).Height);

        for (int i = 0; i < regions.Count; i++)
        {
            var p = RoleFinder.ExposedPoint(regions, i);
            Check($"region {i} has a point that finds it", p is { } q && ReferenceEquals(RoleFinder.At(regions, q), regions[i]), $"{p}");
        }
        var hidden = new List<RoleRegion> { text, panel };
        Check("a region painted over completely has no such point", RoleFinder.ExposedPoint(hidden, 0) == null);
        var ring = new List<RoleRegion> { panel, Region(0, 0, 100, 40), Region(0, 60, 100, 40), Region(0, 40, 45, 20), Region(55, 40, 45, 20) };
        Check("a hole in the middle is found", RoleFinder.ExposedPoint(ring, 0) is { } hole
            && ReferenceEquals(RoleFinder.At(ring, hole), panel) && new Rect(45, 40, 10, 20).Contains(hole), $"{RoleFinder.ExposedPoint(ring, 0)}");

        // Scrolling the list to the found rows: viewport 500 of a 3000 list, margin 60.
        Near("rows in view with room to spare stay put", 300, RoleFinder.ScrollTarget(300, 500, 3000, 500, 560, 60));
        Near("rows below are centred", 1780, RoleFinder.ScrollTarget(0, 500, 3000, 2000, 2060, 60));
        Near("rows above are centred", 380, RoleFinder.ScrollTarget(2000, 500, 3000, 600, 660, 60));
        Near("rows just inside the edge are moved off it, to the middle", 535, RoleFinder.ScrollTarget(300, 500, 3000, 780, 790, 60));
        Near("a run too tall to fit starts a margin below the top", 940, RoleFinder.ScrollTarget(0, 500, 3000, 1000, 1700, 60));
        Near("never above the top", 0, RoleFinder.ScrollTarget(900, 500, 3000, 10, 40, 60));
        Near("never past the end", 2500, RoleFinder.ScrollTarget(0, 500, 3000, 2960, 2990, 60));
        Near("the margin is at most a quarter of a short viewport", 950, RoleFinder.ScrollTarget(0, 200, 3000, 1000, 1120, 60));
    }

    // ---- the preview's tags -----------------------------------------------------------------------

    private static readonly XNamespace SettingsNs = "clr-namespace:FleetView.Settings";

    /// <summary>Every tag written in ThemePreview.xaml, on an element or in a style, names a real role.</summary>
    private void PreviewTagXamlTests()
    {
        var doc = XDocument.Load(RepoFile("FleetFinder", "Settings", "ThemePreview.xaml"), LoadOptions.SetLineInfo);
        var tags = new List<(int Line, string Value)>();
        foreach (var el in doc.Descendants())
        {
            int line = ((System.Xml.IXmlLineInfo)el).LineNumber;
            if (el.Attribute(SettingsNs + "PreviewTag.Roles") is { } attr) tags.Add((line, attr.Value));
            if (el.Name.LocalName == "Setter" && el.Attribute("Property")?.Value == "settings:PreviewTag.Roles")
                tags.Add((line, el.Attribute("Value")?.Value ?? ""));
        }
        Check($"the preview's XAML carries tags ({tags.Count})", tags.Count > 100);
        var bad = tags.SelectMany(t => PreviewTag.Split(t.Value).Where(k => ThemeRoles.Find(k) is null).Select(k => $"line {t.Line}: {k}"))
            .ToList();
        Check("every tag written in the XAML is a real role", bad.Count == 0, string.Join(", ", bad));
        Check("and no tag is empty", tags.All(t => PreviewTag.Split(t.Value).Count > 0));
        Check("no tag names a role twice", tags.All(t => PreviewTag.Split(t.Value).Distinct().Count() == PreviewTag.Split(t.Value).Count));
    }

    // ---- the real page ----------------------------------------------------------------------------

    /// <summary>
    /// The Settings page in the real main window, built and laid out at about the size it has in
    /// Ali's 1720x1440 window, never shown: the preview's tags, clicking it, the outline, copy and
    /// paste over the rows, and the labels on one line each.
    /// </summary>
    private void SettingsFindAndCopyTests()
    {
        ThemeService.Apply(ThemeRoles.Defaults());
        var window = new MainWindow();
        var page = (SettingsPage)window.FindName("SettingsView");
        var clip = new FakeClipboard { Text = "untouched" };
        page.ClipboardText = clip;
        page.Measure(new Size(1692, 1306));
        page.Arrange(new Rect(0, 0, 1692, 1306));
        page.UpdateLayout();
        Pump();
        page.UpdateLayout();
        var preview = page.PreviewControl;
        object Draft(string key) => preview.Resources[key];
        var rows = page.Groups.SelectMany(g => g.Rows).ToList();
        var scroll = (ScrollViewer)page.FindName("ListScroll");
        var list = (ItemsControl)page.FindName("ColourList");

        // The tags as laid out: the ones in the XAML and the ones PreviewTag gives the parts WPF
        // builds itself (grid headers and rows, check boxes, the thumb, the progress bar's fill).
        var regions = page.PreviewRegions().ToList();
        Check($"the preview has tagged elements ({regions.Count})", regions.Count > 150);
        var unknown = regions.SelectMany(r => r.Roles).Where(k => ThemeRoles.Find(k) is null).Distinct().ToList();
        Check("every tag in the laid-out preview is a real role", unknown.Count == 0, string.Join(", ", unknown));
        Check("the grids' own parts are tagged too", regions.Any(r => r.Source is System.Windows.Controls.Primitives.DataGridColumnHeader)
            && regions.Any(r => r.Source is DataGridRow) && regions.Any(r => r.Source is CheckBox));

        var reachable = new Dictionary<string, Point>(StringComparer.Ordinal);
        for (int i = 0; i < regions.Count; i++)
            if (RoleFinder.ExposedPoint(regions, i) is { } p && ReferenceEquals(RoleFinder.At(regions, p), regions[i]))
                foreach (var key in regions[i].Roles)
                    reachable.TryAdd(key, p);
        var unreachable = ThemeRoles.All.Where(r => !reachable.ContainsKey(r.Key)).Select(r => r.Key).ToList();
        Check($"every role can be found by clicking the preview ({ThemeRoles.All.Count - unreachable.Count} of {ThemeRoles.All.Count})",
            unreachable.Count == 0, "no click finds: " + string.Join(", ", unreachable));

        // A tag has to say what the element really draws with.
        var wrong = regions.Where(r => r.Source is DependencyObject d && !r.Roles.Any(k => BrushesIn(d).Contains(Draft(k))))
            .Select(r => $"{r.Source!.GetType().Name} [{string.Join(" ", r.Roles)}]").Distinct().ToList();
        Check("every tagged element draws with one of its tags", wrong.Count == 0, string.Join("; ", wrong.Take(12)));
        var texts = regions.Where(r => r.Source is TextBlock).ToList();
        var wrongText = texts.Where(r => r.Roles.Count != 1 || !ReferenceEquals(((TextBlock)r.Source!).Foreground, Draft(r.Roles[0])))
            .Select(r => $"\"{((TextBlock)r.Source!).Text}\" [{string.Join(" ", r.Roles)}]").Distinct().ToList();
        Check($"every tagged text carries exactly its own text colour ({texts.Count} texts)", wrongText.Count == 0,
            string.Join("; ", wrongText.Take(12)));
        var cellKinds = new[] { "GoodValue", "BadValue", "UnknownValue", "NeededCount", "HaveCount", "GridText" };
        Check("grid cells are tagged by the cells' own rules (Yes, No, Unknown, counts, plain text)",
            cellKinds.All(k => texts.Any(r => r.Roles[0] == k)),
            string.Join(", ", cellKinds.Where(k => !texts.Any(r => r.Roles[0] == k))));

        // Click the tooltip sample: its two rows light up, far down the list, and come into view.
        RoleRegion One(params string[] roles) => regions.First(r => r.Roles.SequenceEqual(roles));
        Point Spot(RoleRegion r) => RoleFinder.ExposedPoint(regions, regions.FindIndex(x => ReferenceEquals(x, r)))!.Value;
        scroll.ScrollToTop();
        page.UpdateLayout();
        var tooltip = One("ToolTipBackground", "ToolTipBorder");
        var foundRoles = page.FindInPreview(Spot(tooltip));
        Equal("clicking the tooltip sample finds its background and border", "ToolTipBackground,ToolTipBorder", string.Join(",", foundRoles));
        Equal("and lights up exactly those rows", "ToolTipBackground,ToolTipBorder",
            string.Join(",", rows.Where(r => r.IsFound).Select(r => r.Key)));
        var firstRow = RowElement(list, "ToolTipBackground");
        double top = firstRow.TransformToAncestor(list).Transform(new Point(0, 0)).Y - scroll.VerticalOffset;
        Check($"the list scrolled down to them ({scroll.VerticalOffset:F0})", scroll.VerticalOffset > 500);
        Check($"with room above and below ({top:F0} from the top of {scroll.ViewportHeight:F0})",
            top >= 59 && top + 2 * firstRow.ActualHeight <= scroll.ViewportHeight - 59);
        double kept = scroll.VerticalOffset;
        var text = One("ToolTipText");
        Equal("clicking the tooltip's text finds the text colour alone", "ToolTipText", string.Join(",", page.FindInPreview(Spot(text))));
        Check("and the rows beside it, already in view, do not move the list", Math.Abs(scroll.VerticalOffset - kept) < 0.5);
        Equal("a click off the preview finds nothing", 0, page.FindInPreview(new Point(-20, -20)).Count);
        Check("and clears the rows", rows.All(r => !r.IsFound));

        Equal("a click on the window background finds it", "WindowBackground", string.Join(",", page.FindInPreview(reachable["WindowBackground"])));
        Equal("on the menu's icon strip, the strip and its lines",
            "MenuGutter,MenuGutterLine,MenuGutterLineHighlight", string.Join(",", page.FindInPreview(Spot(One("MenuGutter", "MenuGutterLine", "MenuGutterLineHighlight")))));
        Equal("on a grid header, the header's colours", "GridHeaderBackground,GridHeaderText,GridHeaderLines",
            string.Join(",", page.FindInPreview(Spot(regions.First(r => r.Source is System.Windows.Controls.Primitives.DataGridColumnHeader { Column: not null })))));
        var gridRows = regions.Where(r => r.Source is DataGridRow).ToList();
        var misread = gridRows.Where(r => r.Roles.Contains("GridAlternateRow")
                != ReferenceEquals(((DataGridRow)r.Source!).Background, Draft("GridAlternateRow")))
            .Select(r => $"{((DataGridRow)r.Source!).GetIndex()} [{string.Join(" ", r.Roles)}]").ToList();
        Check($"every grid row is tagged with the background it really has ({gridRows.Count} rows)",
            gridRows.Count > 10 && misread.Count == 0, string.Join(", ", misread));
        var selected = gridRows.First(r => r.Source is DataGridRow { IsSelected: true });
        Equal("on the selected row, its outline and colours",
            "RowOutline," + (ReferenceEquals(((DataGridRow)selected.Source!).Background, Draft("GridAlternateRow")) ? "GridAlternateRow" : "GridRow")
            + ",GridLines",
            string.Join(",", page.FindInPreview(Spot(selected))));

        // The picker opening clears the found rows.
        page.FindInPreview(Spot(tooltip));
        var box = Descendants<Button>(RowElement(list, "PanelBackground")).First();
        box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check("the picker opens on a box", page.IsPickerOpen);
        Check("and the found rows go out", rows.All(r => !r.IsFound));
        ((Button)page.FindName("PickerCancelButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check("Cancel closes it", !page.IsPickerOpen);

        // The outline over what a click would pick.
        var outline = (System.Windows.Shapes.Rectangle)page.FindName("HoverOutline");
        var layer = (Canvas)page.FindName("OutlineLayer");
        var outlined = page.OutlineInPreview(Spot(tooltip));
        Check("the mouse over the tooltip sample outlines it", ReferenceEquals(outlined?.Source, tooltip.Source) && outline.Visibility == Visibility.Visible);
        var shown = preview.TransformToVisual(layer).TransformBounds(tooltip.Bounds);
        var drawn = new Rect(Canvas.GetLeft(outline), Canvas.GetTop(outline), outline.Width, outline.Height);
        Check($"just outside it all round ({drawn} round {shown})",
            drawn.Contains(shown) && drawn.Left >= shown.Left - 3 && drawn.Right <= shown.Right + 3);
        Check("on whole pixels", drawn.Left % 1 == 0 && drawn.Top % 1 == 0 && drawn.Width % 1 == 0 && drawn.Height % 1 == 0);
        page.OutlineInPreview(null);
        Check("and the mouse leaving takes it away", outline.Visibility != Visibility.Visible);

        // Copy and paste over the rows, through the page.
        Check("the keys do nothing while the page is not showing", !page.HandleHoverKey(Key.C, ModifierKeys.Control, null)
            && clip.Text == "untouched");
        page.HoverRow("PanelBorder");
        Check("Ctrl+C over a row", page.CopyOrPaste(HoverAction.Copy));
        Equal("copies its colour as text", "#FF7500", clip.Text);
        Equal("and says so beside it", "Copied #FF7500", page.NoteShowing);
        page.HoverRow("GridRow");
        page.CopyOrPaste(HoverAction.Paste);
        Equal("Ctrl+V over another pastes it into the draft", Hex("#FF7500"), page.Draft["GridRow"]);
        Equal("the preview shows it", Hex("#FF7500"), (Draft("GridRow") as SolidColorBrush)?.Color ?? default);
        Equal("so does the row's box", Hex("#FF7500"), (rows.Single(r => r.Key == "GridRow").Swatch as SolidColorBrush)?.Color ?? default);
        Equal("the note says Pasted", "Pasted", page.NoteShowing);
        Equal("and the foot says Unsaved changes", "Unsaved changes", ((TextBlock)page.FindName("FootNote")).Text);
        Equal("but the app is as it was", ThemeRoles.Find("GridRow")!.Default, ThemeService.Current["GridRow"]);
        page.Draft.Revert();
        Equal("Revert puts it back", ThemeRoles.Find("GridRow")!.Default, page.Draft["GridRow"]);
        clip.Text = "nonsense";
        page.CopyOrPaste(HoverAction.Paste);
        Equal("with no colour on the clipboard the note says so", HoverClipboard.NothingToPasteNote, page.NoteShowing);
        page.HoverRow(null);
        Check("with no row under the mouse nothing happens", !page.CopyOrPaste(HoverAction.Copy) && clip.Text == "nonsense");

        // Every label on one line at Ali's size.
        var labels = rows.Select(r => Descendants<TextBlock>(RowElement(list, r.Key)).First(t => t.Text == r.Label)).ToList();
        double line = labels.Min(t => t.ActualHeight);
        var wrapped = labels.Where(t => t.ActualHeight > line + 0.5).Select(t => t.Text).ToList();
        Check($"every label fits on one line ({line:F1} high)", line is > 10 and < 25 && wrapped.Count == 0, string.Join(", ", wrapped));
        var intro = Descendants<TextBlock>(page).FirstOrDefault(t => t.Text.StartsWith("Click a colour to change it", StringComparison.Ordinal));
        Check("the intro says how to copy a colour across",
            intro?.Text.EndsWith("Hover a colour and press Ctrl+C, then Ctrl+V on another to copy it across.", StringComparison.Ordinal) == true);
        Check("no em dash on the page", !Descendants<TextBlock>(page).Any(t => t.Text.Contains('\u2014')));
        Equal("the real clipboard was never used", typeof(FakeClipboard), page.ClipboardText.GetType());
    }

    private static FrameworkElement RowElement(ItemsControl list, string key) =>
        Descendants<Border>(list).First(b => b.Name == "RowHit" && (b.DataContext as ColourRow)?.Key == key);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }
}
