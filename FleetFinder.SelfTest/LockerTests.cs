using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Xml.Linq;
using FleetView;
using FleetView.Models;
using FleetView.Services;
using FleetView.Theming;
using FleetView.ViewModels;

/// <summary>Gives the main window something with a ShipLocker property to bind its tab to, without
/// building the whole app's view model.</summary>
public sealed class LockerHost(ShipLockerViewModel shipLocker)
{
    public ShipLockerViewModel ShipLocker { get; } = shipLocker;
}

// T1 (2026-10-24): the Ship Locker tab. The reader on journals and ShipLocker.json files made up
// here in temp folders (never the game's own), the sentences the tab shows, its view model, and
// the tab itself in the real window, built and laid out but never shown.
internal sealed partial class SelfTest
{
    // ---- fixtures -------------------------------------------------------------------------------

    private static string Login(string time, string name, string fid = "") =>
        $$"""{ "timestamp":"2026-10-17T{{time}}Z", "event":"Commander"{{(fid.Length > 0 ? $", \"FID\":\"{fid}\"" : "")}}, "Name":"{{name}}" }""";

    private static string LoadGameAt(string time, string name, string fid = "") =>
        $$"""{ "timestamp":"2026-10-17T{{time}}Z", "event":"LoadGame"{{(fid.Length > 0 ? $", \"FID\":\"{fid}\"" : "")}}, "Commander":"{{name}}", "Odyssey":true }""";

    /// <summary>A ShipLocker event with no list, as the game writes after every change.</summary>
    private static string EmptyLockerEvent(string time) =>
        $$"""{ "timestamp":"2026-10-17T{{time}}Z", "event":"ShipLocker" }""";

    /// <summary>A ShipLocker event or file body. Each group is "symbol|localised|count" entries;
    /// an empty localised part leaves Name_Localised out.</summary>
    private static string LockerJson(string time, string[]? goods = null, string[]? assets = null,
        string[]? data = null, string[]? consumables = null)
    {
        static string Group(string[]? entries) => "[" + string.Join(",", (entries ?? []).Select(e =>
        {
            var p = e.Split('|');
            var local = p[1].Length > 0 ? $", \"Name_Localised\":\"{p[1]}\"" : "";
            return $"{{ \"Name\":\"{p[0]}\"{local}, \"OwnerID\":0, \"Count\":{p[2]} }}";
        })) + "]";
        return $"{{ \"timestamp\":\"2026-10-17T{time}Z\", \"event\":\"ShipLocker\", \"Items\":{Group(goods)}, " +
               $"\"Components\":{Group(assets)}, \"Consumables\":{Group(consumables)}, \"Data\":{Group(data)} }}";
    }

    private ShipLockerContents ReadLocker(string folder, IEnumerable<FleetView.Models.Component>? catalog = null) =>
        new ShipLockerContentsReader(new JournalReader(folder), Path.Combine(folder, "ShipLocker.json"), catalog ?? SmallCatalog).Read();

    private static readonly FleetView.Models.Component[] SmallCatalog =
    [
        new() { Key = "chemicalcatalyst", Name = "Chemical Catalyst", Category = "Assets" },
        new() { Key = "opinionpoll", Name = "Opinion Poll", Category = "Data" },
        new() { Key = "gmeds", Name = "G-Meds", Category = "Goods" },
    ];

    private static string Names(ShipLockerContents c, LockerGroup g) =>
        string.Join(",", c[g].Items.Select(i => $"{i.Name} {i.Count}"));

    private static DateTime At(string time) =>
        DateTime.SpecifyKind(DateTime.Parse("2026-10-17T" + time, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    // ---- the reader -----------------------------------------------------------------------------

    private void LockerReaderTests()
    {
        var root = Path.Combine(_dir, "ship locker");

        // The game's usual pattern: a full list at login, then empty events while the file is
        // rewritten. The file is newer, so it is what shows.
        var usual = Path.Combine(root, "empty event, full file");
        Journal(usual, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"),
            LoadGameAt("20:00:01", "Kailoren", "F1"),
            LockerJson("20:00:05", assets: ["graphene|Graphene|1"]),
            EmptyLockerEvent("21:13:00"));
        File.WriteAllText(Path.Combine(usual, "ShipLocker.json"), LockerJson("21:13:00",
            goods: ["weaponschematic|Weapon Schematic|39", "biochemicalagent|Biochemical Agent|32"],
            assets: ["graphene|Graphene|91", "graphene|Graphene|9", "phneutraliser|pH Neutraliser|20", "rdx|RDX|40", "opticallens|Optical Lens|23"],
            data: ["airqualityreports|Air Quality Reports|4"],
            consumables: ["healthpack|Medkit|93", "energycell|Energy Cell|69"]));
        var c = ReadLocker(usual);
        Equal("an empty event falls back to the file", LockerSource.File, c.Source);
        Equal("and it is read", LockerState.Ready, c.State);
        Equal("for the commander of the latest login", "Kailoren", c.Commander);
        Equal("as of the file's own timestamp", At("21:13:00"), c.AsOfUtc);
        Equal("the groups come in the tab's order", "Goods,Assets,Data,Consumables", string.Join(",", c.Sections.Select(s => s.Group)));
        Equal("Items are Goods", "Biochemical Agent 32,Weapon Schematic 39", Names(c, LockerGroup.Goods));
        Equal("Components are Assets, two stacks of one kind added together, sorted ignoring case",
            "Graphene 100,Optical Lens 23,pH Neutraliser 20,RDX 40", Names(c, LockerGroup.Assets));
        Equal("Data", "Air Quality Reports 4", Names(c, LockerGroup.Data));
        Equal("Consumables by the game's own names", "Energy Cell 69,Medkit 93", Names(c, LockerGroup.Consumables));
        Equal("items held", 39 + 32 + 100 + 20 + 40 + 23 + 4 + 93 + 69, c.ItemsHeld);
        Equal("kinds", 9, c.Kinds);
        Equal("per group held", 183, c[LockerGroup.Assets].Held);

        // A full event and no file at all.
        var eventOnly = Path.Combine(root, "event only");
        Journal(eventOnly, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"),
            LockerJson("20:00:05", data: ["$opinionpoll_name;||5"]));
        c = ReadLocker(eventOnly);
        Equal("a full event with no file is read from the journal", LockerSource.JournalEvent, c.Source);
        Equal("and named from the catalog when the game gives no name", "Opinion Poll 5", Names(c, LockerGroup.Data));

        // A full event newer than the file wins.
        var newerEvent = Path.Combine(root, "newer event");
        Journal(newerEvent, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"),
            LockerJson("20:30:00", assets: ["motor|Motor|15"]));
        File.WriteAllText(Path.Combine(newerEvent, "ShipLocker.json"), LockerJson("20:00:05", assets: ["motor|Motor|2"]));
        c = ReadLocker(newerEvent);
        Equal("a full event newer than the file wins", "Motor 15", Names(c, LockerGroup.Assets));

        // Both empty: an empty event and a file that stays at nothing (the game caught truncating it).
        var bothEmpty = Path.Combine(root, "both empty");
        Journal(bothEmpty, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"), EmptyLockerEvent("20:00:05"));
        File.WriteAllText(Path.Combine(bothEmpty, "ShipLocker.json"), "");
        c = ReadLocker(bothEmpty);
        Equal("an empty event and an empty file is unreadable, not an empty locker", LockerState.Unreadable, c.State);
        Equal("with nothing in it", 0, c.Kinds);
        Equal("still naming the commander", "Kailoren", c.Commander);
        File.WriteAllText(Path.Combine(bothEmpty, "ShipLocker.json"), LockerJson("20:00:05"));
        c = ReadLocker(bothEmpty);
        Equal("a file with four empty lists is an empty locker", LockerState.Ready, c.State);
        Equal("of no kinds", 0, c.Kinds);
        Check("and all four groups are there, empty", c.Sections.Count == 4 && c.Sections.All(s => s.Items.Count == 0));

        var nothing = Path.Combine(root, "nothing");
        Directory.CreateDirectory(nothing);
        Equal("no journal and no file is no locker", LockerState.NoLocker, ReadLocker(nothing).State);
        var noFile = Path.Combine(root, "no file");
        Journal(noFile, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"), EmptyLockerEvent("20:00:05"));
        c = ReadLocker(noFile);
        Equal("an empty event and no file is no locker", LockerState.NoLocker, c.State);
        Equal("for the commander who logged in", "Kailoren", c.Commander);
        var noJournal = Path.Combine(root, "no journal");
        Directory.CreateDirectory(noJournal);
        File.WriteAllText(Path.Combine(noJournal, "ShipLocker.json"), LockerJson("20:00:05", assets: ["motor|Motor|3"]));
        c = ReadLocker(noJournal);
        Equal("a file and no journal is still read", "Motor 3", Names(c, LockerGroup.Assets));
        Equal("credited to nobody", "", c.Commander);

        // Unknown items, missing and odd names, odd counts.
        var odd = Path.Combine(root, "odd items");
        Directory.CreateDirectory(odd);
        File.WriteAllText(Path.Combine(odd, "ShipLocker.json"),
            """
            { "timestamp":"2026-10-17T20:00:00Z", "event":"ShipLocker",
              "Items":[ { "Name":"$mysterything_name;", "Count":2 },
                        { "Name":"some_new_widget", "Name_Localised":"$some_new_widget_name;", "Count":1 },
                        { "Name_Localised":"Only A Local Name", "Count":4 },
                        { "Count":9 },
                        { "Name":"stringcount", "Count":"5" },
                        { "Name":"fraction", "Count":2.5 },
                        { "Name":"negative", "Count":-1 },
                        { "Name":"zero", "Count":0 },
                        { "Name":"huge", "Count":99999999999 },
                        "not an object",
                        { "Name":"chemicalcatalyst", "Count":3 },
                        { "Name":"gmeds", "Name_Localised":"", "Count":6 },
                        { "Name":"cleaned", "Name_Localised":"Hidden\u202EName\u200B", "Count":1 } ],
              "Components":"not a list",
              "Data":[] }
            """);
        c = ReadLocker(odd);
        Equal("odd entries are skipped and the rest kept", LockerState.Ready, c.State);
        Equal("unknown symbols are tidied into a name, never shown raw",
            "Chemical Catalyst 3,G-Meds 6,HiddenName 1,Mysterything 2,Only A Local Name 4,Some new widget 1", Names(c, LockerGroup.Goods));
        Check("no name anywhere has a $ or ; in it", c.Sections.SelectMany(s => s.Items).All(i => !i.Name.Contains('$') && !i.Name.Contains(';')));
        Equal("a group that is not a list is just empty", 0, c[LockerGroup.Assets].Kinds);
        Equal("and a missing group too", 0, c[LockerGroup.Consumables].Kinds);

        // Every catalog item, given only its symbol, comes out under its catalog name.
        var catalog = CatalogLoader.Load();
        var names = ShipLockerContentsReader.CatalogNameMap(catalog);
        var wrong = catalog.Where(k => ShipLockerContentsReader.DisplayName("$" + k.Key + "_name;", null, names) != k.Name).Select(k => k.Key).ToList();
        Check($"all {catalog.Count} catalog items are named from the catalog when the game sends only a symbol", wrong.Count == 0, string.Join(", ", wrong));
        Equal("the game's own name wins over the catalog's", "Insight Entertainment Suite",
            ShipLockerContentsReader.DisplayName("insightentertainmentsuite", "Insight Entertainment Suite", names));
        Equal("nothing at all is no name", "", ShipLockerContentsReader.DisplayName(null, null, names));
        Equal("a symbol made only of the wrapping is no name", "", ShipLockerContentsReader.DisplayName("$_name;", "", names));

        // Two commanders. Alpha played earlier and left a full event; Beta logged in last.
        var two = Path.Combine(root, "two commanders");
        Journal(two, "Journal.2026-10-17T180000.01.log", Header, Login("18:00:00", "Alpha", "F1"),
            LockerJson("18:00:05", assets: ["graphene|Graphene|7"]));
        Journal(two, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Beta", "F2"), EmptyLockerEvent("20:00:05"));
        var twoFile = Path.Combine(two, "ShipLocker.json");
        File.WriteAllText(twoFile, LockerJson("20:00:05", assets: ["motor|Motor|2"]));
        c = ReadLocker(two);
        Equal("the file written after Beta's login is Beta's", "Beta|Motor 2", c.Commander + "|" + Names(c, LockerGroup.Assets));
        File.WriteAllText(twoFile, LockerJson("19:00:00", assets: ["graphene|Graphene|8"]));
        c = ReadLocker(two);
        Equal("a file written while Alpha played is not shown as Beta's", LockerState.OtherCommander, c.State);
        Equal("it says whose it is", "Alpha", c.OtherCommander);
        Equal("and whose it is not", "Beta", c.Commander);
        Equal("and Alpha's journal list is never used for Beta", 0, c.Kinds);
        // Beta had an older session of their own with a full list: that is Beta's latest known locker.
        Journal(two, "Journal.2026-10-17T170000.01.log", Header, Login("17:00:00", "Beta", "F2"),
            LockerJson("17:00:05", assets: ["epoxyadhesive|Epoxy Adhesive|4"]));
        c = ReadLocker(two);
        Equal("Beta's own older list beats Alpha's newer file", "Ready|JournalEvent|Epoxy Adhesive 4",
            $"{c.State}|{c.Source}|{Names(c, LockerGroup.Assets)}");

        // Switching commander inside one journal, the way logging out to the menu does.
        var switching = Path.Combine(root, "switch in one journal");
        Journal(switching, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Alpha", "F1"),
            LockerJson("20:00:05", assets: ["graphene|Graphene|7"]),
            LoadGameAt("20:30:00", "Beta", "F2"), LockerJson("20:30:05", assets: ["motor|Motor|9"]));
        c = ReadLocker(switching);
        Equal("the last login in the newest journal is the one that counts", "Beta|Motor 9", c.Commander + "|" + Names(c, LockerGroup.Assets));
        // Same name, different Frontier IDs: told apart by the ID.
        var sameName = Path.Combine(root, "same name");
        Journal(sameName, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kai", "F1"),
            LockerJson("20:00:05", assets: ["graphene|Graphene|7"]), Login("20:30:00", "Kai", "F2"), EmptyLockerEvent("20:30:05"));
        Equal("two commanders with one name are told apart by Frontier ID", LockerState.NoLocker, ReadLocker(sameName).State);

        // A file being rewritten while it is read: held open by the game, then let go with the new list.
        var busy = Path.Combine(root, "rewritten mid-read");
        Journal(busy, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"), EmptyLockerEvent("20:10:00"));
        var busyFile = Path.Combine(busy, "ShipLocker.json");
        File.WriteAllText(busyFile, LockerJson("20:00:05", assets: ["motor|Motor|1"]));
        var held = new FileStream(busyFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var writer = Task.Run(async () =>
        {
            await Task.Delay(250);
            var bytes = Encoding.UTF8.GetBytes(LockerJson("20:10:00", assets: ["motor|Motor|12"]));
            held.SetLength(0);
            held.Write(bytes);
            held.Dispose();
        });
        c = ReadLocker(busy);
        writer.Wait();
        Equal("a file held open by the game is waited for and read once it is let go", "Ready|Motor 12",
            $"{c.State}|{Names(c, LockerGroup.Assets)}");
        // And one caught truncated, empty for a moment before the list goes back in.
        File.WriteAllText(busyFile, "");
        var refill = Task.Run(async () =>
        {
            await Task.Delay(200);
            File.WriteAllText(busyFile, LockerJson("20:10:00", assets: ["motor|Motor|13"]));
        });
        c = ReadLocker(busy);
        refill.Wait();
        Equal("a file caught empty mid-rewrite is read once it is written", "Ready|Motor 13", $"{c.State}|{Names(c, LockerGroup.Assets)}");

        // The same reader kept across rewrites, as the app keeps it: new events and a new file show.
        var live = Path.Combine(root, "live");
        var liveJournal = Journal(live, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"),
            LockerJson("20:00:05", assets: ["motor|Motor|1"]));
        var reader = new ShipLockerContentsReader(new JournalReader(live), Path.Combine(live, "ShipLocker.json"), SmallCatalog);
        Equal("first read", "Motor 1", Names(reader.Read(), LockerGroup.Assets));
        File.AppendAllText(liveJournal, EmptyLockerEvent("20:05:00") + "\r\n");
        File.WriteAllText(Path.Combine(live, "ShipLocker.json"), LockerJson("20:05:00", assets: ["motor|Motor|2"]));
        Equal("after the game rewrites the file", "Motor 2", Names(reader.Read(), LockerGroup.Assets));
        File.AppendAllText(liveJournal, Login("20:10:00", "Alt", "F9") + "\r\n" + LockerJson("20:10:05", assets: ["rdx|RDX|5"]) + "\r\n");
        c = reader.Read();
        Equal("after another commander logs in on the same journal", "Alt|RDX 5", c.Commander + "|" + Names(c, LockerGroup.Assets));

        // Broken and oversized journal lines are skipped, the read goes on.
        var junk = Path.Combine(root, "junk lines");
        Journal(junk, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"),
            """{ "timestamp":"2026-10-17T20:00:03Z", "event":"ShipLocker", "Items":[ {"Name":""",
            """not json, mentions "ShipLocker" and "Commander" """,
            """{ "event":"ShipLocker", "Items":[] }""",
            "{ \"timestamp\":\"2026-10-17T20:00:04Z\", \"event\":\"ShipLocker\", \"Items\":[], \"Pad\":\"" + new string('x', 2 * 1024 * 1024) + "\" }",
            LockerJson("20:00:02", goods: ["gmeds|G-Meds|4"]));
        c = ReadLocker(junk);
        Equal("broken, timeless and oversized lines are skipped", "Ready|G-Meds 4", $"{c.State}|{Names(c, LockerGroup.Goods)}");
    }

    // ---- the words ------------------------------------------------------------------------------

    private void LockerTextTests()
    {
        var local = new DateTime(2026, 10, 17, 21, 13, 0, DateTimeKind.Local);
        var utc = local.ToUniversalTime();
        Equal("the time reads like the design", "17 Oct 21:13", LockerText.Time(utc));
        Equal("held and kinds", "475 held, 15 kinds", LockerText.Held(475, 15));
        Equal("thousands get a separator", "1,000 held, 32 kinds", LockerText.Held(1000, 32));
        Equal("one is one kind", "1 held, 1 kind", LockerText.Held(1, 1));

        // A locker with the design's totals: 2,451 items in 92 kinds.
        var items = Enumerable.Range(0, 92).Select(i => new LockerItem($"Item {i:D2}", i < 91 ? 26 : 2451 - 26 * 91)).ToList();
        var big = new ShipLockerContents(LockerState.Ready, "Kailoren", utc, LockerSource.File,
        [
            new LockerSection(LockerGroup.Goods, items.Take(15).ToList()),
            new LockerSection(LockerGroup.Assets, items.Skip(15).Take(32).ToList()),
            new LockerSection(LockerGroup.Data, items.Skip(47).Take(39).ToList()),
            new LockerSection(LockerGroup.Consumables, items.Skip(86).ToList()),
        ]);
        Equal("the summary reads like the design", "2,451 items, 92 kinds, as of 17 Oct 21:13. The ship locker has no cap per item.",
            LockerText.Summary(big, null));
        Equal("with a filter it says how many it shows",
            "2,451 items, 92 kinds, as of 17 Oct 21:13. The ship locker has no cap per item. The filter shows 3 of 92 kinds.",
            LockerText.Summary(big, 3));
        var one = new ShipLockerContents(LockerState.Ready, "", utc, LockerSource.File,
            [.. ShipLockerContents.EmptySections.Where(s => s.Group != LockerGroup.Goods), new LockerSection(LockerGroup.Goods, [new("Insight", 1)])]);
        Equal("one item, one kind", "1 item, 1 kind, as of 17 Oct 21:13. The ship locker has no cap per item.", LockerText.Summary(one, null));
        var empty = new ShipLockerContents(LockerState.Ready, "Kailoren", utc, LockerSource.File, ShipLockerContents.EmptySections);
        Equal("an empty locker says so", "The ship locker is empty, as of 17 Oct 21:13.", LockerText.Summary(empty, null));

        var states = new[]
        {
            LockerText.Summary(null, null),
            LockerText.Summary(new(LockerState.NoLocker, "", null, LockerSource.None, ShipLockerContents.EmptySections), null),
            LockerText.Summary(new(LockerState.OtherCommander, "Beta", null, LockerSource.None, ShipLockerContents.EmptySections, "Alpha"), null),
            LockerText.Summary(new(LockerState.Unreadable, "Kailoren", null, LockerSource.None, ShipLockerContents.EmptySections), null),
            LockerText.Summary(new(LockerState.NoLocker, "Kailoren", null, LockerSource.None, ShipLockerContents.EmptySections), null),
        };
        Check("the missing locker says where it looks", states[1].Contains("ShipLocker.json") && states[1].Contains("Saved Games"));
        Equal("the other commander's locker names both",
            "Nothing to show for CMDR Beta yet: the game last wrote the ship locker for CMDR Alpha. It fills in when the game next writes it for CMDR Beta, at login or when the locker changes.",
            states[2]);
        Check("an unreadable file says it will try again", states[3].Contains("again"));
        Equal("with a commander but no file it says when it will appear",
            "No ship locker for CMDR Kailoren yet. The game writes ShipLocker.json when you log in to Odyssey, so it shows here after your next login.",
            states[4]);
        Check("no em or en dashes in any of it", states.Append(LockerText.Summary(big, 3)).All(s => !s.Contains('\u2014') && !s.Contains('\u2013')));
    }

    // ---- the view model -------------------------------------------------------------------------

    private static ShipLockerContents Contents(DateTime utc, params (LockerGroup Group, string Name, int Count)[] items) =>
        new(LockerState.Ready, "Kailoren", utc, LockerSource.File,
            Enum.GetValues<LockerGroup>().Select(g => new LockerSection(g,
                items.Where(i => i.Group == g).Select(i => new LockerItem(i.Name, i.Count))
                    .OrderBy(i => i.Name, ShipLockerContentsReader.NameOrder).ToList())).ToList());

    private static string Rows(LockerCard card) => string.Join(",", card.Rows.Select(r => $"{r.Name} {r.Count}"));

    private void LockerViewModelTests()
    {
        var utc = new DateTime(2026, 10, 17, 20, 0, 0, DateTimeKind.Utc);
        var vm = new ShipLockerViewModel(() => throw new InvalidOperationException("not used"));
        Equal("four cards in the tab's order", "Goods,Assets,Data,Consumables", string.Join(",", vm.Cards.Select(c => c.Title)));
        Equal("before the first read it says it is reading", "Reading the ship locker...", vm.Summary);

        var first = Contents(utc,
            (LockerGroup.Goods, "Biochemical Agent", 32), (LockerGroup.Goods, "Chemical Patents", 1),
            (LockerGroup.Assets, "Chemical Catalyst", 5), (LockerGroup.Assets, "Graphene", 91), (LockerGroup.Assets, "Motor", 15),
            (LockerGroup.Data, "Chemical Formulae", 1), (LockerGroup.Data, "Cat Media", 5),
            (LockerGroup.Consumables, "Medkit", 93));
        vm.Accept(first);
        var goods = vm.Cards[0];
        var assets = vm.Cards[1];
        var consumables = vm.Cards[3];
        Equal("a card lists its group sorted", "Chemical Catalyst 5,Graphene 91,Motor 15", Rows(assets));
        Equal("and says what it holds", "111 held, 3 kinds", assets.HeldText);
        Equal("the header names the commander", "CMDR Kailoren", vm.CommanderText);
        Check("and the time", vm.AsOfText.Contains("Ship locker as of " + LockerText.Time(utc)));
        Check("a full card is not empty", !assets.IsEmpty);

        vm.FilterText = "chem";
        Equal("the filter narrows Goods", "Biochemical Agent 32,Chemical Patents 1", Rows(goods));
        Equal("and Assets", "Chemical Catalyst 5", Rows(assets));
        Equal("and Data", "Chemical Formulae 1", Rows(vm.Cards[2]));
        Equal("and a card with no match says so", "Nothing here matches the filter.", consumables.EmptyText);
        Check("and counts as empty", consumables.IsEmpty);
        Equal("a narrowed card counts what it shows", "5 held, 1 kind", assets.HeldText);
        Check("the summary says how many the filter shows", vm.Summary.EndsWith("The filter shows 4 of 8 kinds."));
        vm.FilterText = "  CHEM ";
        Equal("ignoring case and spaces round it", "Chemical Catalyst 5", Rows(assets));
        vm.FilterText = "";
        Equal("clearing the filter brings everything back", "Chemical Catalyst 5,Graphene 91,Motor 15", Rows(assets));
        Equal("Consumables too", "", consumables.EmptyText);

        // A live update: Graphene changes, Epoxy arrives, Motor goes.
        var keptGraphene = assets.Rows.First(r => r.Name == "Graphene");
        vm.Accept(Contents(utc.AddMinutes(5),
            (LockerGroup.Goods, "Biochemical Agent", 32), (LockerGroup.Goods, "Chemical Patents", 1),
            (LockerGroup.Assets, "Chemical Catalyst", 5), (LockerGroup.Assets, "Epoxy Adhesive", 3), (LockerGroup.Assets, "Graphene", 80),
            (LockerGroup.Data, "Chemical Formulae", 1), (LockerGroup.Data, "Cat Media", 5),
            (LockerGroup.Consumables, "Medkit", 93)));
        Equal("a live update changes counts, adds and removes rows", "Chemical Catalyst 5,Epoxy Adhesive 3,Graphene 80", Rows(assets));
        Check("a row that stays is the same row, so the grid keeps its place", ReferenceEquals(keptGraphene, assets.Rows[2]));
        Check("the time moves on", vm.AsOfText.Contains(LockerText.Time(utc.AddMinutes(5))));

        vm.Accept(new ShipLockerContents(LockerState.Unreadable, "Kailoren", null, LockerSource.None, ShipLockerContents.EmptySections));
        Equal("a read that fails does not wipe what was showing", "Chemical Catalyst 5,Epoxy Adhesive 3,Graphene 80", Rows(assets));

        vm.Accept(new ShipLockerContents(LockerState.Ready, "Kailoren", utc, LockerSource.File, ShipLockerContents.EmptySections));
        Equal("an empty locker empties every card", 0, vm.Cards.Sum(c => c.Rows.Count));
        Equal("which say so", "Nothing held.", assets.EmptyText);
        Equal("and the summary too", "The ship locker is empty, as of " + LockerText.Time(utc) + ".", vm.Summary);

        var none = new ShipLockerViewModel(() => throw new InvalidOperationException());
        none.Accept(new ShipLockerContents(LockerState.NoLocker, "", null, LockerSource.None, ShipLockerContents.EmptySections));
        Equal("with no locker the header says so", "No ship locker found", none.AsOfText);
        Equal("with no commander, no CMDR", "", none.CommanderText);
        Equal("the cards say there is nothing yet", "Nothing to show yet.", none.Cards[0].EmptyText);
        Equal("and show no totals", "", none.Cards[0].HeldText);
        none.Accept(new ShipLockerContents(LockerState.Unreadable, "", null, LockerSource.None, ShipLockerContents.EmptySections));
        Equal("an unreadable file with nothing showing says so", "Ship locker could not be read", none.AsOfText);

        // Reads run off the calling thread, one at a time; asks during a read make one more read.
        int reads = 0;
        int callerThread = Environment.CurrentManagedThreadId;
        int readThread = -1;
        using var gate = new ManualResetEventSlim(false);
        using var done = new ManualResetEventSlim(false);
        var queued = new ShipLockerViewModel(() =>
        {
            readThread = Environment.CurrentManagedThreadId;
            int n = Interlocked.Increment(ref reads);
            if (n == 1) gate.Wait(5000);
            return Contents(utc.AddMinutes(n), (LockerGroup.Assets, "Motor", n));
        });
        queued.Refreshed += () => { if (Volatile.Read(ref reads) >= 2 && !queued.IsReading) done.Set(); };
        queued.Refresh();
        queued.Refresh();
        queued.Refresh();
        queued.Refresh();
        gate.Set();
        Check("the reads finish", done.Wait(5000));
        Equal("four asks during one read make two reads, not four", 2, reads);
        Check("reading happens off the calling thread", readThread != -1 && readThread != callerThread);
        Equal("and the last read is what shows", "Motor 2", Rows(queued.Cards[1]));

        var throwing = new ShipLockerViewModel(() => throw new IOException("pretend bug"));
        using var thrown = new ManualResetEventSlim(false);
        throwing.Refreshed += thrown.Set;
        throwing.Refresh();
        Check("a read that throws is caught and the tab carries on", thrown.Wait(5000) && throwing.Contents == null);
    }

    // ---- the tab --------------------------------------------------------------------------------

    private void LockerTabTests()
    {
        var doc = XDocument.Load(RepoFile("FleetFinder", "MainWindow.xaml"));
        var tabsInXaml = doc.Descendants().Where(e => e.Name.LocalName == "TabItem" && e.Attribute(Xaml + "Name")?.Value != "SettingsTab"
                                                      && e.Ancestors().Any(a => a.Attribute(Xaml + "Name")?.Value == "MainTabs")).ToList();
        var noPanelText = tabsInXaml.Where(t => t.Elements().FirstOrDefault()?.Attribute(XName.Get("TextElement.Foreground"))?.Value
                                                != "{DynamicResource PanelText}").Select(t => t.Attribute("Header")?.Value).ToList();
        Check("every tab's root sets the panel text colour, Ship Locker included", noPanelText.Count == 0, string.Join(", ", noPanelText));

        ThemeService.Apply(ThemeRoles.Defaults());
        var utc = new DateTime(2026, 10, 17, 20, 0, 0, DateTimeKind.Utc);
        var vm = new ShipLockerViewModel(() => throw new InvalidOperationException("not used"));
        var many = Enumerable.Range(1, 60).SelectMany(i => new[]
        {
            (LockerGroup.Goods, $"Good {i:D2}", i), (LockerGroup.Assets, $"Asset {i:D2}", i),
            (LockerGroup.Data, $"Data {i:D2}", i),
        }).Append((LockerGroup.Consumables, "Medkit", 93)).Append((LockerGroup.Assets, "Chemical Catalyst", 5)).ToArray();
        vm.Accept(Contents(utc, many));

        var window = new MainWindow { DataContext = new LockerHost(vm) };
        var tabs = (TabControl)window.FindName("MainTabs");
        Equal("Ship Locker sits between Barter and Engineers", "Find Carriers,Modifications,Barter,Ship Locker,Engineers,Import",
            string.Join(",", tabs.Items.OfType<TabItem>().Where(t => t.Visibility == Visibility.Visible).Select(t => t.Header)));
        var tab = tabs.Items.OfType<TabItem>().Single(t => (t.Header as string) == "Ship Locker");
        var root = (Grid)tab.Content;

        root.Measure(new Size(1692, 1306));
        root.Arrange(new Rect(0, 0, 1692, 1306));
        root.UpdateLayout();
        Pump();
        root.UpdateLayout();
        Check("its content binds to the view model", ReferenceEquals(root.DataContext, vm));
        var cards = (ItemsControl)window.FindName("ShipLockerCards");
        var borders = Enumerable.Range(0, cards.Items.Count)
            .Select(i => (FrameworkElement)cards.ItemContainerGenerator.ContainerFromIndex(i))
            .Select(c => VisualChildren<Border>(c).First()).ToList();
        Equal("four cards", 4, borders.Count);
        Check($"of equal width ({string.Join(", ", borders.Select(b => b.ActualWidth.ToString("F1")))})",
            borders.All(b => Math.Abs(b.ActualWidth - borders[0].ActualWidth) < 1 && b.ActualWidth > 300));
        Check($"filling the height left under the top panel ({borders[0].ActualHeight:F0} of 1306)", borders.All(b => b.ActualHeight > 1100));
        var grids = borders.Select(b => VisualChildren<DataGrid>(b).Single()).ToList();
        Equal("each with an Item and a Count column", "Item,Count|Item,Count|Item,Count|Item,Count",
            string.Join("|", grids.Select(g => string.Join(",", g.Columns.Select(c => c.Header)))));
        Equal("showing its group's rows", "60,61,60,1", string.Join(",", grids.Select(g => g.Items.Count)));
        Check("a long card scrolls inside itself", VisualChildren<ScrollViewer>(grids[1]).First().ScrollableHeight > 0);
        Equal("the count column is right-aligned", HorizontalAlignment.Right,
            ((DataGridTextColumn)grids[0].Columns[1]).ElementStyle.Setters.OfType<Setter>()
                .Where(s => s.Property == FrameworkElement.HorizontalAlignmentProperty).Select(s => (HorizontalAlignment)s.Value).FirstOrDefault());

        var filter = (TextBox)window.FindName("ShipLockerFilter");
        filter.Text = "chem";
        root.UpdateLayout();
        Pump();
        Equal("typing in Filter narrows every card", "0,1,0,0", string.Join(",", grids.Select(g => g.Items.Count)));
        var emptyNotes = borders.Select(b => VisualChildren<TextBlock>(b).Where(t => t.Visibility == Visibility.Visible && t.Text == "Nothing here matches the filter.").Count()).ToList();
        Equal("and the cards with nothing say so", "1,0,1,1", string.Join(",", emptyNotes));
        filter.Text = "";
        Pump();
        Equal("clearing it brings them back", "60,61,60,1", string.Join(",", grids.Select(g => g.Items.Count)));
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var deeper in VisualChildren<T>(child)) yield return deeper;
        }
    }
}
