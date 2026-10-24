using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Xml.Linq;
using FleetView;
using FleetView.Controls;
using FleetView.Models;
using FleetView.Services;
using FleetView.Theming;
using FleetView.ViewModels;

/// <summary>Stands in for MainViewModel in the Engineers tab test: the tab binds to Engineers.</summary>
public sealed class EngineersHost(EngineersViewModel engineers)
{
    public EngineersViewModel Engineers { get; } = engineers;
}

// T3 (2026-10-24): the Engineers tab's own logic (the card columns, the copy note, the
// breakdown and its module materials, the empty-state words) and the tab in the real window,
// built and laid out but never shown. The engineers here come from a small file written in a temp
// folder, with no coordinates or bodies in it: distances come in through a reading's looked-up
// coordinates, so the tests hold whatever the data file carries.
internal sealed partial class SelfTest
{
    private const string TabEngineersJson = """
        { "engineers": [
          { "name":"Domino Green", "region":"Bubble", "system":"Orishis", "settlement":"The Jackrabbit",
            "find":"Common knowledge.", "invite":"Travel at least 100 light years in shuttles.",
            "referral":"Provide 5 doses of Push.", "refersTo":["Kit Fowler"],
            "suitModules":["Extra Backpack Capacity","Made Up Module"], "weaponModules":["Stability"] },
          { "name":"Kit Fowler", "region":"Bubble", "system":"Capoya", "settlement":"The Last Call",
            "find":"From Domino Green.", "invite":"Sell 5 Opinion Polls to bartenders.",
            "referral":"Provide 5 Surveillance Equipment.", "refersTo":["Yarden Bond"],
            "suitModules":["Faster Shield Regen"], "weaponModules":["Magazine Size"] },
          { "name":"Yarden Bond", "region":"Bubble", "system":"Bayan", "settlement":"Salamander Bank",
            "find":"From Kit Fowler.", "invite":"Sell 5 Smear Campaign Plans to bartenders.",
            "referral":"", "refersTo":[], "suitModules":["Quieter Footsteps"], "weaponModules":["Audio Masking"] },
          { "name":"Yi Shen", "region":"Colonia", "system":"Einheriar", "settlement":"Eidolon Hold",
            "find":"From Baltanos, Eleanor Bresa and Rosa Dayette.", "invite":"Complete referral tasks.",
            "referral":"", "refersTo":[], "suitModules":["Night Vision"], "weaponModules":["Headshot Damage"] },
          { "name":"Ada Faraway", "region":"Bubble", "system":"Nowhere Known", "settlement":"Hut",
            "find":"Common knowledge.", "invite":"Wait.", "referral":"", "refersTo":[], "suitModules":[], "weaponModules":["Scope"] }
        ] }
        """;

    private EngineerCatalog TabEngineers()
    {
        var folder = Path.Combine(_dir, "engineer tab data");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "engineers.json");
        File.WriteAllText(path, TabEngineersJson);
        return EngineerLoader.Load(path);
    }

    /// <summary>A reading from Sol with every system but Nowhere Known looked up, so the order is
    /// Domino Green, Kit Fowler, Yarden Bond, Yi Shen, then Ada Faraway with no distance.</summary>
    private static EngineerReading TabReading(PlayerLocation? at, params (string Name, EngineerState State)[] standings)
    {
        var map = standings.ToDictionary(s => s.Name, s => new EngineerStanding(s.State, null, null, DateTime.UtcNow),
            StringComparer.OrdinalIgnoreCase);
        var coords = new Dictionary<string, SystemCoords>
        {
            [ShipLockerReader.Normalize("Orishis")] = new SystemCoords(0, 0, 13),
            [ShipLockerReader.Normalize("Capoya")] = new SystemCoords(0, 0, 50),
            [ShipLockerReader.Normalize("Bayan")] = new SystemCoords(0, 0, 177),
            [ShipLockerReader.Normalize("Einheriar")] = new SystemCoords(-9557.8125, -880.1875, 19801.5625),
        };
        return new EngineerReading(new EngineerProgressSnapshot(at == null && standings.Length == 0 ? "" : "Kailoren", map, standings.Length > 0),
            at, coords);
    }

    private static readonly PlayerLocation Sol = new("Sol", 0, 0, 0);

    private static IReadOnlyList<Modification> TabModifications() =>
    [
        new() { Name = "Extra Backpack Capacity", Kind = "Suit", Requirements = [new() { Commodity = "Weapon Inventory", Amount = 5 }, new() { Commodity = "Memory Chip", Amount = 3 }] },
        new() { Name = "Stability", Kind = "Suit", Requirements = [new() { Commodity = "Wrong Kind", Amount = 1 }] },
        new() { Name = "Stability", Kind = "Weapon", Requirements = [new() { Commodity = "Weapon Schematic", Amount = 1 }, new() { Commodity = "Nothing Needed", Amount = 0 }] },
    ];

    // ---- logic ----------------------------------------------------------------------------------

    private void EngineerTabLogicTests()
    {
        // The card columns.
        Equal("four columns at Ali's 1720 window", 4, CardRowsPanel.ColumnsFor(1686, 380));
        Equal("two on the smallest window", 2, CardRowsPanel.ColumnsFor(926, 380));
        Equal("never fewer than one", 1, CardRowsPanel.ColumnsFor(120, 380));
        Equal("and one when the width is unbounded", 1, CardRowsPanel.ColumnsFor(double.PositiveInfinity, 380));

        var pending = new List<Action>();
        var vm = new EngineersViewModel(TabEngineers(), () => throw new InvalidOperationException("not used"),
            modifications: TabModifications(), after: (_, action) => pending.Add(action));
        vm.Accept(TabReading(Sol, ("Domino Green", EngineerState.Unlocked), ("Kit Fowler", EngineerState.Invited)));
        Equal("nearest first, the one with no distance last", "Domino Green,Kit Fowler,Yarden Bond,Yi Shen,Ada Faraway",
            string.Join(",", vm.Cards.Select(c => c.Name)));
        var domino = vm.Cards[0];
        var kit = vm.Cards[1];
        var ada = vm.Cards[4];
        Equal("a card shows its distance on the right of the system", "13 ly", domino.DistanceLabel);
        Equal("or says the distance is unknown", "distance unknown", ada.DistanceLabel);

        // Copying a system.
        var clipboard = new FakeClipboard();
        vm.ClipboardText = clipboard;
        Equal("copying a system says so", EngineersViewModel.CopiedNote, vm.CopySystem(kit));
        Equal("and the system is on the clipboard", "Capoya", clipboard.Text);
        Check("the note shows on that card", kit.HasCopyNote && kit.CopyNote == "Copied");
        Equal("and goes after a moment", 1, pending.Count);
        vm.CopySystemCommand.Execute(domino);
        Equal("the command copies the card it is given", "Orishis", clipboard.Text);
        Check("a second copy takes the first note away at once", !kit.HasCopyNote && domino.HasCopyNote);
        pending[0]();
        Check("and the first note's timer leaves the second alone", domino.CopyNote == "Copied");
        pending[1]();
        Check("the second note's own timer takes it away", !domino.HasCopyNote);
        clipboard.Busy = true;
        Equal("a busy clipboard says so, in plain words", "Clipboard busy, try again", vm.CopySystem(kit));
        Equal("and on the card", "Clipboard busy, try again", kit.CopyNote);
        clipboard.Busy = false;
        clipboard.Text = "untouched";
        Equal("nothing to copy without a card", "", vm.CopySystem(null));
        Equal("and the clipboard is left alone", "untouched", clipboard.Text);

        // The breakdown.
        Check("the breakdown starts shut", !vm.IsBreakdownOpen && vm.Breakdown == null);
        Check("Escape with it shut does nothing", !vm.CloseOnEscape());
        vm.OpenBreakdownCommand.Execute(domino);
        Check("clicking a name opens that engineer's breakdown", vm.IsBreakdownOpen && ReferenceEquals(vm.Breakdown?.Card, domino));
        var b = vm.Breakdown!;
        Equal("every suit module, with what it takes from the modification list",
            "Extra Backpack Capacity: 5 Weapon Inventory, 3 Memory Chip|Made Up Module: ",
            string.Join("|", b.SuitModules.Select(m => $"{m.Name}: {m.Materials}")));
        Check("a module the list does not know shows its name alone", !b.SuitModules[1].HasMaterials && b.SuitModules[0].HasMaterials);
        Equal("a weapon module takes the weapon entry of a shared name, leaving out nothing needed",
            "Stability: 1 Weapon Schematic", string.Join("|", b.WeaponModules.Select(m => $"{m.Name}: {m.Materials}")));
        Check("both lists show", b.HasSuitModules && b.HasWeaponModules);
        Check("Ada Faraway's breakdown has no suit list", !new EngineerBreakdown(ada, TabModifications()).HasSuitModules);
        Equal("with no modification list, names alone", "Extra Backpack Capacity|Made Up Module",
            string.Join("|", new EngineerBreakdown(domino, []).SuitModules.Where(m => !m.HasMaterials).Select(m => m.Name)));
        vm.Accept(TabReading(new PlayerLocation("Bayan", 0, 0, 177), ("Domino Green", EngineerState.Unlocked), ("Kit Fowler", EngineerState.Unlocked)));
        Check("a live update keeps the breakdown open on the same engineer", vm.IsBreakdownOpen && ReferenceEquals(vm.Breakdown, b));
        Check("Escape shuts it", vm.CloseOnEscape() && !vm.IsBreakdownOpen && vm.SelectedEngineer == null);
        vm.SelectedEngineer = kit;
        vm.CloseBreakdownCommand.Execute(null);
        Check("and so does Close", !vm.IsBreakdownOpen);
        vm.OpenBreakdownCommand.Execute(null);
        Check("a click with no card opens nothing", !vm.IsBreakdownOpen);

        // The words for an empty tab.
        vm.SearchText = "zzz";
        Equal("a search that finds nothing says what to do", "No engineer matches \"zzz\". Clear the search to see them all.", vm.EmptyText);
        vm.Show = EngineerShow.Colonia;
        Equal("with a Show filter too it names both", "No engineer in \"Colonia\" matches \"zzz\". Clear the search or choose All engineers.", vm.EmptyText);
        vm.SearchText = "";
        vm.Show = EngineerShow.Unlocked;
        vm.Accept(TabReading(Sol, ("Kit Fowler", EngineerState.Known)));
        Equal("a Show filter alone that leaves nothing", "No engineer to show in \"Unlocked\". Choose All engineers to see them all.", vm.EmptyText);
        vm.Show = EngineerShow.All;
        Check("All engineers brings them back", !vm.IsEmpty && vm.Cards.Count == 5);

        // No journal at all: no commander, no position, no progress.
        vm.Accept(TabReading(null));
        Check("with no journal the summary says why every card is Not met",
            vm.Summary.Contains("No journal to read yet, so every engineer shows as not met."), vm.Summary);
        Check("and every card says the distance is unknown", vm.Cards.All(c => c.DistanceLabel == "distance unknown"));
        // The shipped file and modification list: a module the game words differently from the
        // list still finds its materials, and keeps the game's words.
        var shipped = new EngineersViewModel(EngineerLoader.Load(), () => throw new InvalidOperationException("not used"),
            modifications: ModificationLoader.Load());
        shipped.SelectedEngineer = shipped.AllCards.Single(c => c.Name == "Yarden Bond");
        var hip = shipped.Breakdown!.WeaponModules.Single(m => m.Name == "Improved Hip Fire Accuracy");
        Check("Improved Hip Fire Accuracy shows Higher Accuracy's materials", hip.HasMaterials, hip.Materials);
        Check("every shipped module finds its materials",
            shipped.AllCards.All(c => new EngineerBreakdown(c, ModificationLoader.Load(), EngineerLoader.Load().ModificationNameFor)
                is var b && b.SuitModules.Concat(b.WeaponModules).All(m => m.HasMaterials)));
        Equal("the breakdown shows the unlock order", "Domino Green > Kit Fowler > Yarden Bond",
            shipped.Breakdown.Card.BreakdownLines.Single(l => l.Label == "Unlock order").Text);

        var words = new[] { vm.Summary, vm.EmptyText, EngineerText.NoMatch("a", "b"), EngineerText.NoMatch("a", null),
            EngineerText.NoMatch("", "b"), EngineersViewModel.BusyNote };
        Check("no em or en dashes in the new words", words.All(t => !t.Contains('\u2014') && !t.Contains('\u2013')));
    }

    // ---- the tab --------------------------------------------------------------------------------

    private void EngineerTabTests()
    {
        var doc = XDocument.Load(RepoFile("FleetFinder", "MainWindow.xaml"));
        var tabXaml = doc.Descendants().Single(e => e.Name.LocalName == "TabItem" && e.Attribute("Header")?.Value == "Engineers");
        var rootXaml = tabXaml.Elements().First();
        Equal("Escape is looked at on the whole tab", "EngineersTab_PreviewKeyDown", rootXaml.Attribute("PreviewKeyDown")?.Value);
        Check("the tab's words have no em or en dashes",
            !tabXaml.ToString().Contains('\u2014') && !tabXaml.ToString().Contains('\u2013'));

        ThemeService.Apply(ThemeRoles.Defaults());
        var pending = new List<Action>();
        var vm = new EngineersViewModel(TabEngineers(), () => throw new InvalidOperationException("not used"),
            modifications: TabModifications(), after: (_, action) => pending.Add(action));
        var clipboard = new FakeClipboard();
        vm.ClipboardText = clipboard;
        vm.Accept(TabReading(Sol, ("Domino Green", EngineerState.Unlocked), ("Kit Fowler", EngineerState.Invited)));

        var window = new MainWindow { DataContext = new EngineersHost(vm) };
        // No application here, so the role brushes go in the window itself for the tab to find.
        window.Resources.MergedDictionaries.Add(ThemeService.BuildDictionary(ThemeRoles.Defaults()));
        var tabs = (TabControl)window.FindName("MainTabs");
        Equal("Engineers sits between Ship Locker and Import", "Find Carriers,Modifications,Barter,Ship Locker,Engineers,Import",
            string.Join(",", tabs.Items.OfType<TabItem>().Where(t => t.Visibility == Visibility.Visible).Select(t => t.Header)));
        var page = (FleetView.Settings.SettingsPage)window.FindName("SettingsView");
        Equal("and the Settings preview's strip matches the window's", "Find Carriers,Modifications,Barter,Ship Locker,Engineers,Import",
            string.Join(",", Logical<TabItem>(page.PreviewControl).Select(t => t.Header)));
        var tab = tabs.Items.OfType<TabItem>().Single(t => (t.Header as string) == "Engineers");
        var root = (Grid)tab.Content;
        Lay(root, 1692, 1306);
        Check("its content binds to the view model", ReferenceEquals(root.DataContext, vm));

        var cards = (ItemsControl)window.FindName("EngineerCards");
        var panel = VisualChildren<CardRowsPanel>(cards).Single();
        Equal("four cards across at Ali's size", 4, panel.Columns);
        var borders = CardBordersOf(cards);
        Equal("a card per engineer", 5, borders.Count);
        Check($"each about a quarter of the page ({borders[0].ActualWidth:F0} wide)", borders[0].ActualWidth > 380 && borders[0].ActualWidth < 1692 / 3.0);
        Check("a row's cards are one height", borders.Take(4).All(x => Math.Abs(x.ActualHeight - borders[0].ActualHeight) < 0.5));
        Check("and the next row starts under them", Math.Abs(borders[4].TranslatePoint(new Point(), borders[0]).Y - borders[0].ActualHeight - 6) < 1);
        Check("a short row stays short", borders[4].ActualHeight < borders[0].ActualHeight);

        string Texts(FrameworkElement card) => string.Join("|", VisualChildren<TextBlock>(card)
            .Where(t => IsShown(t, card)).Select(TextOf).Where(t => t.Length > 0));
        var dominoText = Texts(borders[0]);
        var kitText = Texts(borders[1]);
        Check("a card reads name, chip, state, system, distance, place and modules",
            dominoText.StartsWith("Bubble|Domino Green|Unlocked|13 ly|Orishis|The Jackrabbit|Suit Modules|Extra Backpack Capacity|Made Up Module|Weapon Modules|Stability|"), dominoText);
        Check("an unlocked card ends with its referral", dominoText.EndsWith("Referral: Provide 5 doses of Push to learn about Kit Fowler."), dominoText);
        Check("and shows no prerequisites", !dominoText.Contains("Unlock Prerequisites"));
        Check("one not yet unlocked shows what is left, in plain words",
            kitText.Contains("Unlock Prerequisites|Referral: Provide 5 Surveillance Equipment to learn about Yarden Bond.|Suit Modules"), kitText);
        Check("and no referral line at its foot", !kitText.EndsWith("Yarden Bond."), kitText);
        Check("a card with no distance says so", Texts(borders[4]).Contains("distance unknown"));

        var nameButtons = borders.Select(x => VisualChildren<Button>(x).First()).ToList();
        Check("an unlocked name is in the panel heading colour", ReferenceEquals(nameButtons[0].Foreground, window.FindResource("PanelText")));
        Check("others in the main text colour", ReferenceEquals(nameButtons[1].Foreground, window.FindResource("MainText")));
        var chip = VisualChildren<Border>(borders[0]).First(x => VisualChildren<TextBlock>(x).Any(t => t.Text == "Bubble"));
        Check("the region chip is a dark tile like the barter totals box",
            ReferenceEquals(chip.Background, window.FindResource("TotalsBoxBackground")) && ReferenceEquals(chip.BorderBrush, window.FindResource("TotalsBoxBorder")));

        // Search and Show through the real boxes.
        var search = (TextBox)window.FindName("EngineersSearch");
        var show = (ComboBox)window.FindName("EngineersShow");
        var empty = (TextBlock)window.FindName("EngineersEmpty");
        search.Text = "night";
        Lay(root, 1692, 1306);
        Equal("typing in Search narrows the cards", 1, CardBordersOf(cards).Count);
        search.Text = "zzz";
        Lay(root, 1692, 1306);
        Check("and nothing found says so on the tab", empty.Visibility == Visibility.Visible && empty.Text.StartsWith("No engineer matches \"zzz\""), empty.Text);
        search.Text = "";
        Equal("the Show box lists the options", "All engineers,Unlocked,Not unlocked yet,Bubble,Colonia", string.Join(",", show.Items.Cast<object>()));
        show.SelectedIndex = 4;
        Lay(root, 1692, 1306);
        Check("choosing Colonia in it filters the cards", vm.Show == EngineerShow.Colonia && CardBordersOf(cards).Count == 1);
        Check("the closed box draws in the text box colours", VisualChildren<Border>(show).Any(x => ReferenceEquals(x.Background, window.FindResource("InputBackground"))));
        show.SelectedIndex = 0;
        Lay(root, 1692, 1306);
        Check("empty note hidden again", empty.Visibility == Visibility.Collapsed);
        borders = CardBordersOf(cards);

        // Copy a system from a card.
        var systemButton = VisualChildren<Button>(borders[1]).First(x => x.Content as string == "Capoya");
        Click(systemButton);
        Lay(root, 1692, 1306);
        Equal("clicking a system copies it", "Capoya", clipboard.Text);
        Check("and the card shows Copied beside it", Texts(borders[1]).Contains("Capoya|Copied"), Texts(borders[1]));

        // The breakdown.
        var layer = (Border)window.FindName("EngineerBreakdownLayer");
        Check("the breakdown is shut to start", layer.Visibility == Visibility.Collapsed);
        nameButtons = CardBordersOf(cards).Select(x => VisualChildren<Button>(x).First()).ToList();
        Click(nameButtons[0]);
        Lay(root, 1692, 1306);
        Check("clicking a name opens it over the tab", layer.Visibility == Visibility.Visible && ReferenceEquals(vm.SelectedEngineer, vm.Cards[0]));
        Check("over the whole tab, dimmed", Grid.GetRowSpan(layer) == 3 && ReferenceEquals(layer.Background, window.FindResource("OverlayDim")));
        var breakdownCard = (Border)window.FindName("EngineerBreakdownCard");
        var breakdownText = string.Join("|", VisualChildren<TextBlock>(breakdownCard).Where(t => IsShown(t, breakdownCard)).Select(TextOf).Where(t => t.Length > 0));
        Check("it has the name, kind, system and distance", breakdownText.StartsWith("Close|Domino Green|On-foot engineer, the Bubble|Orishis|13 ly from you|The Jackrabbit|"), breakdownText);
        Check("the requirements", breakdownText.Contains("Your progress|Unlocked|Find|Common knowledge.|Invite|Travel at least 100 light years in shuttles.|Referral|"), breakdownText);
        Check("and every module with what it takes", breakdownText.Contains("Suit Modules|Extra Backpack Capacity|5 Weapon Inventory, 3 Memory Chip|Made Up Module|Weapon Modules|Stability|1 Weapon Schematic"), breakdownText);

        // Live: an update moves cards and changes states, and the breakdown and scroll stay put.
        var scroll = (ScrollViewer)window.FindName("EngineersScroll");
        Lay(root, 1692, 420);
        scroll.ScrollToVerticalOffset(120);
        Lay(root, 1692, 420);
        double offset = scroll.VerticalOffset;
        var openCard = vm.SelectedEngineer;
        vm.Accept(TabReading(new PlayerLocation("Bayan", 0, 0, 177), ("Domino Green", EngineerState.Unlocked), ("Kit Fowler", EngineerState.Unlocked)));
        Lay(root, 1692, 420);
        Check($"a live update keeps the scroll position ({offset:F0}, now {scroll.VerticalOffset:F0})", offset > 0 && Math.Abs(scroll.VerticalOffset - offset) < 0.5);
        Check("and the open breakdown", layer.Visibility == Visibility.Visible && ReferenceEquals(vm.SelectedEngineer, openCard));
        Equal("with the cards in their new order", "Yarden Bond,Kit Fowler,Domino Green,Yi Shen,Ada Faraway", string.Join(",", vm.Cards.Select(c => c.Name)));
        Lay(root, 1692, 1306);

        // Click outside, click inside, Escape and Close.
        breakdownCard.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        Check("a click on the breakdown itself leaves it open", vm.IsBreakdownOpen);
        layer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        Check("a click on the dimmed tab round it shuts it", !vm.IsBreakdownOpen);
        vm.SelectedEngineer = vm.Cards[0];
        using (var source = new HwndSource(new HwndSourceParameters("FleetFinder self-test") { WindowStyle = 0, Width = 1, Height = 1 }))
        {
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            search.RaiseEvent(key);
            Check("Escape on the tab shuts it", !vm.IsBreakdownOpen && key.Handled);
            var again = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            search.RaiseEvent(again);
            Check("and Escape with it shut is left for others", !again.Handled);
        }
        vm.SelectedEngineer = vm.Cards[0];
        Lay(root, 1692, 1306);
        Click((Button)window.FindName("EngineerBreakdownClose"));
        Check("and so does Close", !vm.IsBreakdownOpen);

        // The smallest window.
        Lay(root, 932, 300);
        Equal("two cards across on the smallest window", 2, panel.Columns);
    }

    /// <summary>Clicks a button the way a mouse click ends: its OnClick, which raises Click and runs
    /// its command. Raising the Click event alone would leave the command out.</summary>
    private static void Click(ButtonBase button) =>
        typeof(ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(button, null);

    private static void Lay(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        Pump();
        root.UpdateLayout();
    }

    private static List<FrameworkElement> CardBordersOf(ItemsControl cards) =>
        Enumerable.Range(0, cards.Items.Count)
            .Select(i => cards.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement)
            .Where(c => c != null)
            .Select(c => (FrameworkElement)VisualChildren<Border>(c!).First())
            .ToList();

    /// <summary>A text block's words, from its runs when it has them.</summary>
    private static string TextOf(TextBlock t) =>
        t.Text.Length > 0 ? t.Text : string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));

    /// <summary>Whether an element and everything above it up to <paramref name="top"/> is set to
    /// show; IsVisible is false throughout a window that was never shown.</summary>
    private static bool IsShown(FrameworkElement element, FrameworkElement top)
    {
        for (DependencyObject? at = element; at != null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is UIElement { Visibility: not Visibility.Visible }) return false;
            if (ReferenceEquals(at, top)) return true;
        }
        return true;
    }
}
