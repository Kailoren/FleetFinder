using System.IO;
using FleetView.Models;
using FleetView.Services;
using FleetView.ViewModels;

/// <summary>A coordinate source that answers from a dictionary and counts what it is asked, so no
/// test ever reaches EDSM.</summary>
internal sealed class FakeCoords(Dictionary<string, SystemCoords> known, bool fail = false) : ICoordinateSource
{
    public List<string> Asked { get; } = new();

    public Task<IReadOnlyDictionary<string, SystemCoords>> GetCoordsAsync(IEnumerable<string> systemNames, CancellationToken ct = default)
    {
        var names = systemNames.ToList();
        lock (Asked) Asked.AddRange(names);
        if (fail) throw new HttpRequestExceptionStandIn();
        IReadOnlyDictionary<string, SystemCoords> found = names
            .Where(n => known.ContainsKey(n))
            .ToDictionary(n => ShipLockerReader.Normalize(n), n => known[n]);
        return Task.FromResult(found);
    }

    private sealed class HttpRequestExceptionStandIn : Exception;
}

// T2 (2026-10-24): the Engineers tab's data and logic, no UI. The shipped engineers.json pinned
// against Ali's list (T2b), the loader on damaged files, EngineerProgress per commander on
// journals made up here in temp folders (never the game's own), coordinates from a fake source,
// the view model's order, search and Show filter, and the journal growth probe.
internal sealed partial class SelfTest
{
    // ---- fixtures -------------------------------------------------------------------------------

    /// <summary>A login-time EngineerProgress. Entries are "Name|Progress" or "Name|Progress|Rank".</summary>
    private static string FullProgress(string time, params string[] entries) =>
        $$"""{ "timestamp":"2026-10-17T{{time}}Z", "event":"EngineerProgress", "Engineers":[ {{string.Join(", ", entries.Select(ProgressFields).Select(f => "{ " + f + " }"))}} ] }""";

    /// <summary>A single-engineer EngineerProgress, "Name|Progress" or "Name|Progress|Rank"; an
    /// empty Progress leaves the field out, as a grade change does.</summary>
    private static string SingleProgress(string time, string entry) =>
        $$"""{ "timestamp":"2026-10-17T{{time}}Z", "event":"EngineerProgress", {{ProgressFields(entry)}} }""";

    private static string ProgressFields(string entry)
    {
        var p = entry.Split('|');
        var fields = new List<string> { $"\"Engineer\":\"{p[0]}\"", "\"EngineerID\":400001" };
        if (p.Length > 1 && p[1].Length > 0) fields.Add($"\"Progress\":\"{p[1]}\"");
        if (p.Length > 2) fields.Add($"\"Rank\":{p[2]}, \"RankProgress\":0");
        return string.Join(", ", fields);
    }

    private static string JumpTo(string time, string system, double x, double y, double z) =>
        FormattableString.Invariant($$"""{ "timestamp":"2026-10-17T{{time}}Z", "event":"FSDJump", "StarSystem":"{{system}}", "StarPos":[{{x}},{{y}},{{z}}] }""");

    private static EngineerProgressSnapshot ReadProgress(string folder) =>
        new EngineerProgressReader(new JournalReader(folder)).Read();

    private static string StateOf(EngineerProgressSnapshot s, string name) =>
        s.For(name) is { } st ? EngineerText.State(st.State, st.Rank) : "Not met";

    /// <summary>A small engineers file: four engineers, one in Colonia, one whose system the
    /// test readings never have coordinates for.</summary>
    private const string SmallEngineersJson = """
        { "engineers": [
          { "name":"Domino Green", "region":"Bubble", "system":"Orishis", "settlement":"The Jackrabbit",
            "find":"Common knowledge.", "invite":"Travel at least 100 light years in shuttles.",
            "referral":"Provide 5 doses of Push.", "refersTo":["Kit Fowler"],
            "suitModules":["Enhanced Tracking","Extra Backpack Capacity"], "weaponModules":["Stability","Greater Range"] },
          { "name":"Kit Fowler", "region":"Bubble", "system":"Capoya", "settlement":"The Last Call",
            "find":"From Domino Green.", "invite":"Sell 10 Opinion polls to bartenders.",
            "referral":"", "refersTo":[], "suitModules":["Faster Shield Regen"], "weaponModules":["Magazine Size"] },
          { "name":"Yi Shen", "region":"Colonia", "system":"Einheriar", "settlement":"Eidolon Hold",
            "find":"From Baltanos, Eleanor Bresa and Rosa Dayette.",
            "invite":"Complete referral tasks.", "referral":"", "refersTo":[], "suitModules":["Night Vision"], "weaponModules":["Audio Masking"] },
          { "name":"Ada Nocoords", "region":"Bubble", "system":"Nowhere Known", "settlement":"Hut",
            "find":"Common knowledge.", "invite":"Wait.", "referral":"", "refersTo":[], "suitModules":[], "weaponModules":["Scope"] }
        ] }
        """;

    private EngineerCatalog SmallEngineers()
    {
        var folder = Path.Combine(_dir, "engineer data");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "small.json");
        File.WriteAllText(path, SmallEngineersJson);
        return EngineerLoader.Load(path);
    }

    private static string Order(EngineersViewModel vm) => string.Join(",", vm.Cards.Select(c => c.Name));

    // ---- the shipped data -----------------------------------------------------------------------

    /// <summary>
    /// The thirteen on-foot engineers as Ali's list has them (2026-10-24): name, system and
    /// region, pinned so an edit to the file shows up here.
    /// </summary>
    private static readonly (string Name, string System, string Region)[] ShippedEngineers =
    [
        ("Baltanos", "Deriso", "Colonia"),
        ("Domino Green", "Orishis", "Bubble"),
        ("Eleanor Bresa", "Desy", "Colonia"),
        ("Hero Ferrari", "Siris", "Bubble"),
        ("Jude Navarro", "Aurai", "Bubble"),
        ("Kit Fowler", "Capoya", "Bubble"),
        ("Oden Geiger", "Candiaei", "Bubble"),
        ("Rosa Dayette", "Kojeara", "Colonia"),
        ("Terra Velasquez", "Shou Xing", "Bubble"),
        ("Uma Laszlo", "Xuane", "Bubble"),
        ("Wellington Beck", "Jolapa", "Bubble"),
        ("Yarden Bond", "Bayan", "Bubble"),
        ("Yi Shen", "Einheriar", "Colonia"),
    ];

    private void EngineerDataTests()
    {
        var catalog = EngineerLoader.Load();
        Equal("the shipped engineers.json loads without a problem", "", catalog.Problem);
        var all = catalog.Engineers;
        Equal("thirteen on-foot engineers", 13, all.Count);
        Equal("nine in the Bubble", 9, all.Count(e => e.Region == "Bubble"));
        Equal("four in Colonia", 4, all.Count(e => e.Region == "Colonia"));
        Equal("each with the system Ali's list gives",
            string.Join("; ", ShippedEngineers.Select(e => $"{e.Name} {e.System} {e.Region}")),
            string.Join("; ", all.OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => $"{e.Name} {e.System} {e.Region}")));
        Check("every engineer has a settlement and a way to be found and unlocked",
            all.All(e => e.Settlement.Length > 0 && e.Find.Length > 0 && e.Invite.Length > 0));
        var raw = File.ReadAllText(EngineerLoader.DefaultPath);
        Check("the file carries no coordinates and no bodies (distances are looked up)",
            !raw.Contains("\"x\"") && !raw.Contains("\"body\""));
        Equal("Ali's numbers: Kit Fowler's unlock is 5 Opinion Polls", "Sell 5 Opinion Polls to bartenders.",
            all.Single(e => e.Name == "Kit Fowler").Invite);
        Equal("and Hero Ferrari's referral is 5 Settlement Defence Plans", "Provide 5 Settlement Defence Plans.",
            all.Single(e => e.Name == "Hero Ferrari").Referral);
        Equal("Yi Shen comes from all three Colonia engineers", "From Baltanos, Eleanor Bresa and Rosa Dayette",
            all.Single(e => e.Name == "Yi Shen").Find);

        var mods = ModificationLoader.Load();
        var suit = mods.Where(m => m.Kind == "Suit").Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var weapon = mods.Where(m => m.Kind == "Weapon").Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        string ListName(string m) => catalog.ModificationNameFor(m);
        Check("every suit module is a suit modification FleetFinder knows, spelt the same or mapped",
            all.All(e => e.SuitModules.All(m => suit.Contains(ListName(m)))),
            string.Join(", ", all.SelectMany(e => e.SuitModules).Where(m => !suit.Contains(ListName(m))).Distinct()));
        Check("every weapon module is a weapon modification FleetFinder knows, spelt the same or mapped",
            all.All(e => e.WeaponModules.All(m => weapon.Contains(ListName(m)))),
            string.Join(", ", all.SelectMany(e => e.WeaponModules).Where(m => !weapon.Contains(ListName(m))).Distinct()));
        Equal("the game's Improved Hip Fire Accuracy is the list's Higher Accuracy", "Higher Accuracy",
            catalog.ModificationNameFor("Improved Hip Fire Accuracy"));
        Check("and the cards show the game's wording", all.Single(e => e.Name == "Yarden Bond").WeaponModules.Contains("Improved Hip Fire Accuracy"));
        Check("every engineer does at least one weapon module", all.All(e => e.WeaponModules.Count > 0));
        Check("every suit and weapon modification has an engineer who does it",
            suit.All(m => all.Any(e => e.SuitModules.Select(ListName).Contains(m)))
            && weapon.All(m => all.Any(e => e.WeaponModules.Select(ListName).Contains(m))));
        Equal("Hero Ferrari's modules, as Ali's list has them",
            "Increased Sprint Duration,Improved Jump Assist,Increased Air Reserves|Faster Handling,Noise Suppressor",
            all.Where(e => e.Name == "Hero Ferrari").Select(e => string.Join(",", e.SuitModules) + "|" + string.Join(",", e.WeaponModules)).Single());

        var names = all.Select(e => e.Name).ToHashSet();
        Check("every referral leads to an engineer in the file", all.All(e => e.RefersTo.All(names.Contains)));
        Check("an engineer has a referral task exactly when they refer someone", all.All(e => (e.Referral.Length > 0) == (e.RefersTo.Count > 0)));
        Equal("the referral chains",
            "Baltanos>Yi Shen,Domino Green>Kit Fowler,Eleanor Bresa>Yi Shen,Hero Ferrari>Wellington Beck,Jude Navarro>Terra Velasquez,Kit Fowler>Yarden Bond,Rosa Dayette>Yi Shen,Terra Velasquez>Oden Geiger,Wellington Beck>Uma Laszlo",
            string.Join(",", all.Where(e => e.RefersTo.Count > 0).OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => e.Name + ">" + string.Join("+", e.RefersTo))));
        Check("everyone not common knowledge is found through someone who refers them",
            all.Where(e => e.Find.StartsWith("From ")).All(e => all.Any(r => r.RefersTo.Contains(e.Name))));
        var texts = all.SelectMany(e => new[] { e.Name, e.System, e.Settlement, e.Find, e.Invite, e.Referral }
            .Concat(e.SuitModules).Concat(e.WeaponModules)).ToList();
        Check("no em or en dashes in the data", texts.All(t => !t.Contains('\u2014') && !t.Contains('\u2013')));

        Equal("the unlock order of the first Bubble chain", "Hero Ferrari > Wellington Beck > Uma Laszlo",
            EngineerText.UnlockOrder(all.Single(e => e.Name == "Wellington Beck"), all));
        Equal("the second, from its end", "Jude Navarro > Terra Velasquez > Oden Geiger",
            EngineerText.UnlockOrder(all.Single(e => e.Name == "Oden Geiger"), all));
        Equal("the third, from its start", "Domino Green > Kit Fowler > Yarden Bond",
            EngineerText.UnlockOrder(all.Single(e => e.Name == "Domino Green"), all));
        Equal("Colonia's, from any of the three", "Baltanos, Eleanor Bresa and Rosa Dayette > Yi Shen",
            EngineerText.UnlockOrder(all.Single(e => e.Name == "Rosa Dayette"), all));
        Equal("and from Yi Shen", "Baltanos, Eleanor Bresa and Rosa Dayette > Yi Shen",
            EngineerText.UnlockOrder(all.Single(e => e.Name == "Yi Shen"), all));
        Equal("an engineer in no chain has none", "",
            EngineerText.UnlockOrder(new Engineer { Name = "Alone" }, [.. all, new Engineer { Name = "Alone" }]));

        // Ali's rule: the engineer data names no other app, anywhere in the repo's own files.
        var other = "EDD" + "iscovery";
        var mentions = Directory.EnumerateFiles(RepoFile("FleetFinder"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(RepoFile("FleetFinder.SelfTest"), "*.cs"))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && Path.GetExtension(f) is ".cs" or ".xaml" or ".json" or ".md" or ".csproj" or ".txt")
            .Where(f => File.ReadAllText(f).Contains(other, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName).ToList();
        Check("no file names the app the placeholder data came from", mentions.Count == 0, string.Join(", ", mentions));
        Check("and no notice for it ships", !File.Exists(Path.Combine(RepoFile("FleetFinder"), "Data", "NOTICE-engineers.txt")));
        var readme = File.ReadAllText(RepoFile("README.md"));
        Check("nor does the README credit it for the engineers", !readme.Contains("NOTICE-engineers") && !readme.Contains("engineer data"));
    }

    // ---- the loader on bad files ----------------------------------------------------------------

    private EngineerCatalog LoadEngineersText(string name, string json)
    {
        var folder = Path.Combine(_dir, "engineer data");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, json);
        return EngineerLoader.Load(path);
    }

    private const string GoodEntry = """{ "name":"Kit Fowler", "region":"Bubble", "system":"Capoya" }""";

    private void EngineerLoaderTests()
    {
        var missing = EngineerLoader.Load(Path.Combine(_dir, "engineer data", "not there.json"));
        Equal("a missing file gives no engineers", 0, missing.Engineers.Count);
        Check("and says it is missing", missing.Problem.Contains("missing"));

        var notJson = LoadEngineersText("damaged.json", "{ \"engineers\": [ { \"name\": \"Kit Fowl");
        Equal("a damaged file gives no engineers", 0, notJson.Engineers.Count);
        Check("and says it is damaged", notJson.Problem.Contains("damaged"));
        Equal("a field of the wrong kind is damage too", 0, LoadEngineersText("wrong type.json", """{ "engineers": [ { "name": 5 } ] }""").Engineers.Count);
        Check("an empty file says so", LoadEngineersText("empty.json", "").Problem.Length > 0);
        Check("a file with no list says it has no engineers", LoadEngineersText("no list.json", "{}").Problem.Contains("no engineers"));
        Check("a bare list is not the file's shape", LoadEngineersText("bare list.json", "[" + GoodEntry + "]").Engineers.Count == 0);
        Check("an empty list says so", LoadEngineersText("empty list.json", """{ "engineers": [] }""").Problem.Contains("no engineers"));

        var partly = LoadEngineersText("partly bad.json", "{ \"engineers\": [ " + string.Join(", ",
            GoodEntry,
            """{ "region":"Bubble", "system":"Nameless" }""",
            """{ "name":"No System", "region":"Bubble" }""",
            """{ "name":"Wrong Region", "region":"Sol", "system":"Sol" }""",
            """{ "name":"KIT FOWLER", "region":"Bubble", "system":"Again" }""",
            "null",
            """{ "name":"Two Coords", "region":"colonia", "system":"Desy", "x":1, "y":2, "refersTo":["Kit Fowler","Nobody"], "suitModules":null, "weaponModules":["Scope","scope",""] }""",
            """{ "name":"Old Fields", "region":"Bubble", "system":"Far", "body":"5 C", "x":1e400, "y":0, "z":0 }""") + " ], " +
            """ "modificationNames": { "Improved Hip Fire Accuracy": "Higher Accuracy", "": "Nothing", "Blank": "  " } }""");
        Equal("good entries load and bad ones are left out", "Kit Fowler,Two Coords,Old Fields", string.Join(",", partly.Engineers.Select(e => e.Name)));
        Check("and the problem says how many were left out", partly.Problem.Contains("5 entries were left out"), partly.Problem);
        Check("and that a referral to someone missing was dropped", partly.Problem.Contains("1 referral"), partly.Problem);
        var two = partly.Engineers[1];
        Equal("a region in any case is read", "Colonia", two.Region);
        Equal("a referral to someone in the file stays", "Kit Fowler", string.Join(",", two.RefersTo));
        Equal("a missing module list is an empty one", 0, two.SuitModules.Count);
        Equal("repeated and blank module names are dropped", "Scope", string.Join(",", two.WeaponModules));
        Equal("coordinates and a body in an older file are ignored, not a problem", "Far", partly.Engineers[2].System);
        Equal("a module name map is read", "Higher Accuracy", partly.ModificationNameFor("improved hip fire accuracy"));
        Equal("pairs with a blank side are left out", "Blank|1", partly.ModificationNameFor("Blank") + "|" + partly.ModificationNames!.Count);
        Equal("a module the map does not have keeps its name", "Scope", partly.ModificationNameFor("Scope"));
        Equal("a file with no map maps nothing", "Scope", missing.ModificationNameFor("Scope"));

        var hostile = LoadEngineersText("hostile.json", """{ "engineers": [ { "name":"Kit‮ Fowler​", "region":"Bubble", "system":"Cap\u0007oya" } ] }""");
        Equal("invisible characters are cleaned out of names", "Kit Fowler|Capoya", hostile.Engineers[0].Name + "|" + hostile.Engineers[0].System);

        var huge = Path.Combine(_dir, "engineer data", "huge.json");
        File.WriteAllText(huge, "{ \"engineers\": [] , \"pad\": \"" + new string('x', 2 * 1024 * 1024) + "\" }");
        Check("a file far too big is not read", EngineerLoader.Load(huge).Problem.Contains("bigger"));

        // The view model on a damaged file: no cards, and the problem is what the tab says.
        var vm = new EngineersViewModel(notJson, () => throw new InvalidOperationException("not used"));
        Equal("a damaged file shows no cards", 0, vm.Cards.Count);
        Equal("and the tab says what is wrong", notJson.Problem, vm.EmptyText);
        Equal("and the summary says there is nothing", "No engineer data to show.", vm.Summary);
    }

    // ---- EngineerProgress per commander ---------------------------------------------------------

    private void EngineerProgressTests()
    {
        var root = Path.Combine(_dir, "engineer progress");

        // The usual session: the full list at login, then single updates as the commander plays.
        var usual = Path.Combine(root, "full then singles");
        Journal(usual, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"),
            LoadGameAt("20:00:01", "Kailoren", "F1"),
            FullProgress("20:00:02", "Domino Green|Known", "Felicity Farseer|Unlocked|5", "Hero Ferrari|Unlocked"),
            SingleProgress("20:10:00", "Domino Green|Invited"),
            SingleProgress("20:20:00", "Domino Green|Unlocked"),
            SingleProgress("20:30:00", "Kit Fowler|Acquainted"),
            SingleProgress("20:40:00", "Felicity Farseer||5"));
        var s = ReadProgress(usual);
        Equal("for the commander of the latest login", "Kailoren", s.Commander);
        Check("progress was found", s.HasProgress);
        Equal("single updates land on the full list in order", "Unlocked", StateOf(s, "Domino Green"));
        Equal("Acquainted is Known", "Known", StateOf(s, "Kit Fowler"));
        Equal("an engineer only the full list names keeps that", "Unlocked", StateOf(s, "Hero Ferrari"));
        Equal("an engineer never mentioned is Not met", "Not met", StateOf(s, "Yarden Bond"));
        Equal("a grade change without a Progress keeps the state and the grade", "Unlocked, grade 5", StateOf(s, "Felicity Farseer"));
        Equal("the time is the latest event's", At("20:20:00"), s.For("Domino Green")!.Utc);
        Equal("names match ignoring case", "Unlocked", StateOf(s, "domino green"));

        // A later login list is the whole truth again.
        var relogin = Path.Combine(root, "later full list");
        Journal(relogin, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"),
            FullProgress("20:00:02", "Domino Green|Unlocked"),
            SingleProgress("20:10:00", "Jude Navarro|Invited"),
            Login("21:00:00", "Kailoren", "F1"),
            FullProgress("21:00:02", "Domino Green|Unlocked", "Kit Fowler|Known"));
        s = ReadProgress(relogin);
        Equal("a later full list replaces what came before", "Unlocked|Known|Not met",
            $"{StateOf(s, "Domino Green")}|{StateOf(s, "Kit Fowler")}|{StateOf(s, "Jude Navarro")}");

        // Ali's two commanders, switching on the same journal and across journals.
        var two = Path.Combine(root, "two commanders");
        Journal(two, "Journal.2026-10-16T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"),
            FullProgress("20:00:02", "Domino Green|Unlocked", "Hero Ferrari|Unlocked"),
            Login("21:00:00", "AltCmdr", "F2"),
            FullProgress("21:00:02", "Hero Ferrari|Invited"),
            SingleProgress("21:30:00", "Kit Fowler|Known"));
        Journal(two, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"));
        s = ReadProgress(two);
        Equal("the latest login's commander is the one shown", "Kailoren", s.Commander);
        Equal("with that commander's own progress, not the other's later events", "Unlocked|Unlocked|Not met",
            $"{StateOf(s, "Domino Green")}|{StateOf(s, "Hero Ferrari")}|{StateOf(s, "Kit Fowler")}");
        Journal(two, "Journal.2026-10-17T220000.01.log", Header, Login("22:00:00", "AltCmdr", "F2"));
        s = ReadProgress(two);
        Equal("logging in as the other commander shows theirs", "AltCmdr|Not met|Invited|Known",
            $"{s.Commander}|{StateOf(s, "Domino Green")}|{StateOf(s, "Hero Ferrari")}|{StateOf(s, "Kit Fowler")}");

        // Two commanders with one name are told apart by their Frontier IDs.
        var sameName = Path.Combine(root, "same name");
        Journal(sameName, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"),
            FullProgress("20:00:02", "Domino Green|Unlocked"), Login("21:00:00", "Kailoren", "F9"));
        s = ReadProgress(sameName);
        Check("the same name with another FID is another commander, with no progress yet", !s.HasProgress && s.Standings.Count == 0);
        Equal("so it is Not met", "Not met", StateOf(s, "Domino Green"));

        // A long session the game split into a second part with no login in it.
        var parts = Path.Combine(root, "two parts");
        Journal(parts, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"),
            FullProgress("20:00:02", "Domino Green|Invited"));
        Journal(parts, "Journal.2026-10-17T200000.02.log", Header, SingleProgress("23:00:00", "Domino Green|Unlocked"));
        Equal("a second part with no login keeps the first part's commander", "Unlocked", StateOf(ReadProgress(parts), "Domino Green"));

        // Names the app does not know, words the game might add, broken lines.
        var junk = Path.Combine(root, "junk");
        Journal(junk, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"),
            FullProgress("20:00:02", "Domino Green|Known"),
            """{ "timestamp":"2026-10-17T20:00:03Z", "event":"EngineerProgress", "Engineers":[ {"Engineer":""",
            """not json but says "EngineerProgress" """,
            """{ "event":"EngineerProgress", "Engineer":"Domino Green", "Progress":"Barred" }""",
            """{ "timestamp":"2026-10-17T20:00:04Z", "event":"EngineerProgress", "Engineers":"all of them" }""",
            """{ "timestamp":"2026-10-17T20:00:05Z", "event":"EngineerProgress", "Engineers":[ 5, null, {"Progress":"Unlocked"}, {"Engineer":"Hero Ferrari","Progress":"Invited","Rank":99}, {"Engineer":"Nobody Real","Progress":"Unlocked"}, {"Engineer":"Kit Fowler","Progress":"Befriended"} ] }""",
            SingleProgress("20:00:06", "Domino Green|Unlocked"),
            SingleProgress("20:00:07", "|Unlocked"));
        s = ReadProgress(junk);
        Equal("broken, timeless and malformed events are skipped, the rest read", "Unlocked", StateOf(s, "Domino Green"));
        Equal("an entry with a grade past any real one keeps its state without the grade", "Invited", StateOf(s, "Hero Ferrari"));
        Equal("a name the app does not know is kept and harms nothing", "Unlocked", StateOf(s, "Nobody Real"));
        Equal("a progress word the app does not know leaves the engineer Not met", "Not met", StateOf(s, "Kit Fowler"));

        var none = ReadProgress(Path.Combine(root, "no journals"));
        Check("no journals give no commander and no progress", none.Commander == "" && !none.HasProgress);
        var noLogin = Path.Combine(root, "no login");
        Journal(noLogin, "Journal.2026-10-17T200000.01.log", Header, FullProgress("20:00:02", "Domino Green|Known"));
        s = ReadProgress(noLogin);
        Equal("with no login anywhere there is one commander to credit it to", "|Known", s.Commander + "|" + StateOf(s, "Domino Green"));

        // The same reader kept while the journal grows, as the app keeps it.
        var live = Path.Combine(root, "live");
        var liveJournal = Journal(live, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"),
            FullProgress("20:00:02", "Domino Green|Known"));
        var reader = new EngineerProgressReader(new JournalReader(live));
        Equal("first read", "Known", StateOf(reader.Read(), "Domino Green"));
        File.AppendAllText(liveJournal, SingleProgress("20:05:00", "Domino Green|Invited") + "\r\n");
        Equal("a single update appended is read", "Invited", StateOf(reader.Read(), "Domino Green"));
        File.AppendAllText(liveJournal, Login("20:10:00", "AltCmdr", "F2") + "\r\n");
        s = reader.Read();
        Equal("another commander logging in on the same journal starts from nothing", "AltCmdr|Not met", s.Commander + "|" + StateOf(s, "Domino Green"));
    }

    // ---- reads, positions and coordinates -------------------------------------------------------

    private void EngineerStatusTests()
    {
        var catalog = SmallEngineers();
        Equal("the small test file loads", "|4", catalog.Problem + "|" + catalog.Engineers.Count);
        var folder = Path.Combine(_dir, "engineer status");
        Journal(folder, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"),
            FullProgress("20:00:02", "Domino Green|Unlocked"),
            JumpTo("20:05:00", "Somewhere", 0, 0, 0));

        var fake = new FakeCoords(new() { ["Nowhere Known"] = new SystemCoords(0, 0, 7), ["Orishis"] = new SystemCoords(3, 4, 12) });
        var clock = new DateTime(2026, 10, 17, 20, 0, 0, DateTimeKind.Utc);
        var source = new EngineerStatusSource(new JournalReader(folder), catalog.Engineers, fake, () => clock);
        int found = 0;
        source.CoordinatesFound += () => Interlocked.Increment(ref found);
        var r = source.Read();
        Equal("the position is the journal's latest", "Somewhere", r.Location?.System);
        Check("the first read does not wait for the lookup", r.LookedUp.Count == 0 && r.Progress.HasProgress);
        Check("which finishes in the background", source.Lookup.Wait(5000));
        Equal("every engineer's system is looked up", "Capoya,Einheriar,Nowhere Known,Orishis",
            string.Join(",", fake.Asked.Order(StringComparer.Ordinal)));
        Equal("and says when it found some", 1, found);
        r = source.Read();
        Check("the next read has the coordinates",
            r.LookedUp.TryGetValue(ShipLockerReader.Normalize("Nowhere Known"), out var c) && c.Z == 7 && r.LookedUp.Count == 2);
        source.Lookup.Wait(5000);
        Equal("a system found or just asked about is not asked again", 4, fake.Asked.Count);
        clock += EngineerStatusSource.RetryAfter;
        source.Read();
        source.Lookup.Wait(5000);
        Equal("after the retry wait only the ones not found are asked again", "Capoya,Einheriar",
            string.Join(",", fake.Asked.Skip(4).Order(StringComparer.Ordinal)));
        Equal("and finding nothing new says nothing", 1, found);

        // A slow lookup: reads go on without it, and only one runs at a time.
        using var release = new ManualResetEventSlim(false);
        var slow = new SlowCoords(release, new() { ["Capoya"] = new SystemCoords(0, 0, 50) });
        var slowSource = new EngineerStatusSource(new JournalReader(folder), catalog.Engineers, slow, () => clock);
        var started = DateTime.UtcNow;
        r = slowSource.Read();
        slowSource.Read();
        Check($"reads come back while the lookup is still out ({(DateTime.UtcNow - started).TotalMilliseconds:F0} ms)",
            !slowSource.Lookup.IsCompleted && r.LookedUp.Count == 0);
        release.Set();
        slowSource.Lookup.Wait(5000);
        Equal("one lookup at a time", 1, slow.Calls);
        Check("and its answer shows in the next read", slowSource.Read().LookedUp.ContainsKey(ShipLockerReader.Normalize("Capoya")));

        var failing = new FakeCoords([], fail: true);
        var failSource = new EngineerStatusSource(new JournalReader(folder), catalog.Engineers, failing, () => clock);
        r = failSource.Read();
        Check("a lookup that fails leaves the coordinates unknown, without throwing",
            failSource.Lookup.Wait(5000) && failSource.Read().LookedUp.Count == 0 && r.Progress.HasProgress);
        failSource.Lookup.Wait(5000);
        Equal("and is not asked again straight away", 4, failing.Asked.Count);
        clock += EngineerStatusSource.RetryAfter;
        failSource.Read();
        failSource.Lookup.Wait(5000);
        Equal("but is after the retry wait", 8, failing.Asked.Count);

        r = new EngineerStatusSource(new JournalReader(folder), catalog.Engineers, null).Read();
        Check("with no coordinate source nothing is looked up", r.LookedUp.Count == 0);

        var noJump = Path.Combine(_dir, "engineer status no position");
        Journal(noJump, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"));
        r = new EngineerStatusSource(new JournalReader(noJump), catalog.Engineers, null).Read();
        Check("a journal with no position gives none", r.Location == null);

        var shipped = EngineerLoader.Load();
        var asked = new FakeCoords([]);
        var shippedSource = new EngineerStatusSource(new JournalReader(folder), shipped.Engineers, asked);
        shippedSource.Read();
        shippedSource.Lookup.Wait(5000);
        Equal("with the shipped file all thirteen systems are looked up", 13, asked.Asked.Distinct().Count());
    }

    /// <summary>A coordinate source that holds its answer until released, and counts calls.</summary>
    private sealed class SlowCoords(ManualResetEventSlim release, Dictionary<string, SystemCoords> known) : ICoordinateSource
    {
        public int Calls;

        public async Task<IReadOnlyDictionary<string, SystemCoords>> GetCoordsAsync(IEnumerable<string> systemNames, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            var names = systemNames.ToList();
            await Task.Run(() => release.Wait(5000), ct);
            return names.Where(known.ContainsKey).ToDictionary(n => ShipLockerReader.Normalize(n), n => known[n]);
        }
    }

    // ---- the view model -------------------------------------------------------------------------

    /// <summary>A reading as the status source gives it once the lookup has answered for the small
    /// file's systems, all but Nowhere Known.</summary>
    private static EngineerReading Reading(PlayerLocation? at, string commander, params (string Name, EngineerState State)[] standings)
    {
        var map = standings.ToDictionary(s => s.Name, s => new EngineerStanding(s.State, null, null, DateTime.UtcNow),
            StringComparer.OrdinalIgnoreCase);
        return new EngineerReading(new EngineerProgressSnapshot(commander, map, standings.Length > 0), at,
            new Dictionary<string, SystemCoords>
            {
                [ShipLockerReader.Normalize("Orishis")] = new SystemCoords(3, 4, 12),
                [ShipLockerReader.Normalize("Capoya")] = new SystemCoords(0, 0, 50),
                [ShipLockerReader.Normalize("Einheriar")] = new SystemCoords(-9557.8125, -880.1875, 19801.5625),
            });
    }

    private void EngineerViewModelTests()
    {
        var catalog = SmallEngineers();
        var vm = new EngineersViewModel(catalog, () => throw new InvalidOperationException("not used"));
        Equal("before a read, every card shows, by name", "Ada Nocoords,Domino Green,Kit Fowler,Yi Shen", Order(vm));
        Check("and the summary says it is reading", vm.Summary.EndsWith("Reading the journal..."), vm.Summary);
        Equal("the Show box's options", "All engineers,Unlocked,Not unlocked yet,Bubble,Colonia", string.Join(",", vm.ShowOptions));
        Equal("with All engineers chosen", "All engineers", vm.SelectedShowOption.Label);

        var sol = new PlayerLocation("Sol", 0, 0, 0);
        vm.Accept(Reading(sol, "Kailoren", ("Domino Green", EngineerState.Unlocked), ("Kit Fowler", EngineerState.Known)));
        Equal("nearest first, unknown distance last", "Domino Green,Kit Fowler,Yi Shen,Ada Nocoords", Order(vm));
        var domino = vm.Cards[0];
        var kit = vm.Cards[1];
        var yi = vm.Cards[2];
        var ada = vm.Cards[3];
        Equal("a distance from the commander's position", "13 ly", domino.DistanceText);
        Equal("thousands get a separator", "22,005 ly", yi.DistanceText);
        Check("no coordinates means no distance, not a wrong one", ada.DistanceLy == null && ada.DistanceText == "" && !ada.HasDistance);
        Equal("and the breakdown says so", "distance unknown", ada.DistanceFromYouText);
        Equal("the header names the commander", "CMDR Kailoren", vm.CommanderText);
        Equal("and where distances are from", "Distances from Sol", vm.DistancesFromText);
        Equal("the summary counts", "4 on-foot engineers, 1 unlocked.", vm.Summary);

        Equal("states", "Unlocked|Known|Not met", $"{domino.StateText}|{kit.StateText}|{yi.StateText}");
        Check("an unlocked card shows no prerequisites", domino.IsUnlocked && !domino.ShowPrerequisites && domino.Prerequisites.Count == 0);
        Equal("and has its referral at the foot", "Referral: Provide 5 doses of Push to learn about Kit Fowler.", domino.ReferralFooter);
        Equal("a known engineer still needs the invite", "Invite: Sell 10 Opinion polls to bartenders.",
            string.Join(" ", kit.Prerequisites.Select(l => $"{l.Label}: {l.Text}")));
        Equal("one not met needs finding and inviting",
            "Find: From Baltanos, Eleanor Bresa and Rosa Dayette.|Invite: Complete referral tasks.",
            string.Join("|", yi.Prerequisites.Select(l => $"{l.Label}: {l.Text}")));
        Check("and shows them", yi.ShowPrerequisites && yi.ReferralFooter == "");
        Equal("the place line is the settlement", "The Jackrabbit", domino.PlaceText);
        Equal("for each engineer", "Hut", ada.PlaceText);
        Equal("the kind line", "On-foot engineer, Colonia", yi.KindText);
        Equal("the breakdown has everything",
            "Your progress: Unlocked|Find: Common knowledge.|Invite: Travel at least 100 light years in shuttles.|Referral: Provide 5 doses of Push to learn about Kit Fowler.|Unlock order: Domino Green > Kit Fowler",
            string.Join("|", domino.BreakdownLines.Select(l => $"{l.Label}: {l.Text}")));
        Equal("and counts the modules", "2 suit modules, 2 weapon modules.", domino.ModulesText);
        Equal("one of each reads as one", "1 suit module, 1 weapon module.", kit.ModulesText);

        // Search: name, system, settlement and modules, ignoring case and spaces.
        vm.SearchText = "kit";
        Equal("search by name", "Kit Fowler", Order(vm));
        vm.SearchText = "  EINHERIAR ";
        Equal("search by system, ignoring case and spaces round it", "Yi Shen", Order(vm));
        vm.SearchText = "night vision";
        Equal("search by suit module", "Yi Shen", Order(vm));
        vm.SearchText = "scope";
        Equal("search by weapon module", "Ada Nocoords", Order(vm));
        vm.SearchText = "jackrabbit";
        Equal("search by settlement", "Domino Green", Order(vm));
        vm.SearchText = "zzz";
        Equal("nothing matching shows nothing", 0, vm.Cards.Count);
        Equal("and says so", "No engineer matches \"zzz\". Clear the search to see them all.", vm.EmptyText);
        Check("and counts as empty", vm.IsEmpty);
        Equal("the summary says what shows", "4 on-foot engineers, 1 unlocked. Showing 0 of 4.", vm.Summary);
        vm.SearchText = "";
        Check("clearing it brings them all back", vm.Cards.Count == 4 && !vm.IsEmpty);

        // Show.
        vm.Show = EngineerShow.Unlocked;
        Equal("Show Unlocked", "Domino Green", Order(vm));
        Equal("which the box shows", "Unlocked", vm.SelectedShowOption.Label);
        vm.Show = EngineerShow.NotUnlocked;
        Equal("Show Not unlocked yet", "Kit Fowler,Yi Shen,Ada Nocoords", Order(vm));
        vm.Show = EngineerShow.Bubble;
        Equal("Show Bubble", "Domino Green,Kit Fowler,Ada Nocoords", Order(vm));
        vm.SelectedShowOption = vm.ShowOptions[4];
        Equal("Show Colonia, chosen through the box", "Yi Shen", Order(vm));
        vm.SearchText = "kit";
        Equal("search and Show together", 0, vm.Cards.Count);
        vm.Show = EngineerShow.All;
        Equal("both apply", "Kit Fowler", Order(vm));
        vm.SearchText = "";

        // A live update: the commander jumps to Colonia and Kit Fowler is unlocked.
        vm.SelectedEngineer = kit;
        vm.Accept(Reading(new PlayerLocation("Einheriar", -9557.8125, -880.1875, 19801.5625), "Kailoren",
            ("Domino Green", EngineerState.Unlocked), ("Kit Fowler", EngineerState.Unlocked), ("Yi Shen", EngineerState.Invited)));
        Equal("the order follows the new position", "Yi Shen,Kit Fowler,Domino Green,Ada Nocoords", Order(vm));
        Equal("in the same system is 0 ly", "0 ly", yi.DistanceText);
        Check("cards are the same objects, moved rather than rebuilt", ReferenceEquals(vm.Cards[0], yi) && ReferenceEquals(vm.Cards[1], kit));
        Check("an open breakdown stays open on the same card", ReferenceEquals(vm.SelectedEngineer, kit) && kit.StateText == "Unlocked");
        Equal("an invited engineer shows no find or invite", 0, yi.Prerequisites.Count);
        Check("so no prerequisites box", !yi.ShowPrerequisites);
        Equal("the summary moves on", "4 on-foot engineers, 2 unlocked.", vm.Summary);

        // No position: every distance unknown, sorted by name.
        vm.Accept(Reading(null, "Kailoren", ("Domino Green", EngineerState.Unlocked)));
        Equal("with no position, all by name", "Ada Nocoords,Domino Green,Kit Fowler,Yi Shen", Order(vm));
        Check("and no distances", vm.Cards.All(c => c.DistanceLy == null));
        Equal("the header says why", "No position in the journal yet, so no distances", vm.DistancesFromText);
        Equal("an engineer the journal dropped goes back to Not met", "Not met", kit.StateText);

        // No progress yet for this commander.
        vm.Accept(Reading(sol, "Kailoren"));
        Check("no progress says when the game writes it", vm.Summary.Contains("no engineer progress for CMDR Kailoren yet"), vm.Summary);

        // A coordinate from the lookup is used when the file has none.
        var looked = new EngineerReading(EngineerProgressSnapshot.None, sol,
            new Dictionary<string, SystemCoords> { [ShipLockerReader.Normalize("Nowhere Known")] = new SystemCoords(0, 0, 7) });
        vm.Accept(looked);
        Equal("a looked-up coordinate gives a distance", "7 ly", ada.DistanceText);
        Equal("and sorts by it", "Ada Nocoords,Domino Green,Kit Fowler,Yi Shen", Order(vm));

        // The real file through the view model: every engineer has a distance from Sol.
        var real = new EngineersViewModel(EngineerLoader.Load(), () => throw new InvalidOperationException("not used"));
        real.Accept(new EngineerReading(EngineerProgressSnapshot.None, sol, new Dictionary<string, SystemCoords>()));
        Check("with the shipped file and nothing looked up yet, no distances", real.Cards.Count == 13 && real.Cards.All(c => !c.HasDistance));
        var pretend = real.AllCards.Select((c, i) => (c.System, Coords: c.Region == "Colonia" ? new SystemCoords(0, 0, 22_000 + i) : new SystemCoords(0, 0, 100 + i)))
            .ToDictionary(p => ShipLockerReader.Normalize(p.System), p => p.Coords);
        real.Accept(new EngineerReading(EngineerProgressSnapshot.None, sol, pretend));
        Check("once looked up every engineer has a distance", real.Cards.All(c => c.HasDistance));
        Check("and Colonia's four come last from Sol", real.Cards.TakeLast(4).All(c => c.Region == "Colonia"));
        var words = real.AllCards.SelectMany(c => c.BreakdownLines.Select(l => l.Text).Concat(c.Prerequisites.Select(l => l.Text)))
            .Append(real.Summary).Append(real.DistancesFromText).ToList();
        Check("no em or en dashes in anything the tab says", words.All(t => !t.Contains('\u2014') && !t.Contains('\u2013')));

        Equal("rounding of half a light year", "2 ly", EngineerText.Distance(1.5));
        Equal("the join of two names", "A and B", EngineerText.Join(["A", "B"]));

        // Reads run off the calling thread, one at a time; asks during a read make one more.
        int reads = 0;
        int callerThread = Environment.CurrentManagedThreadId;
        int readThread = -1;
        using var gate = new ManualResetEventSlim(false);
        using var done = new ManualResetEventSlim(false);
        var queued = new EngineersViewModel(catalog, () =>
        {
            readThread = Environment.CurrentManagedThreadId;
            int n = Interlocked.Increment(ref reads);
            if (n == 1) gate.Wait(5000);
            return Reading(sol, "Kailoren", ("Kit Fowler", n == 1 ? EngineerState.Known : EngineerState.Unlocked));
        });
        queued.Refreshed += () => { if (Volatile.Read(ref reads) >= 2 && !queued.IsReading) done.Set(); };
        queued.Refresh();
        queued.Refresh();
        queued.Refresh();
        gate.Set();
        Check("the reads finish", done.Wait(5000));
        Equal("three asks during one read make two reads", 2, reads);
        Check("reading happens off the calling thread", readThread != -1 && readThread != callerThread);
        Equal("and the last read is what shows", "Unlocked", queued.AllCards.Single(c => c.Name == "Kit Fowler").StateText);

        var throwing = new EngineersViewModel(catalog, () => throw new IOException("pretend bug"));
        using var thrown = new ManualResetEventSlim(false);
        throwing.Refreshed += thrown.Set;
        throwing.Refresh();
        Check("a read that throws is caught and the tab carries on", thrown.Wait(5000) && throwing.Reading == null && throwing.Cards.Count == 4);
    }

    // ---- the journal growth probe ---------------------------------------------------------------

    private void JournalProbeTests()
    {
        var folder = Path.Combine(_dir, "journal probe");
        var first = Journal(folder, "Journal.2026-10-17T200000.01.log", Header, Login("20:00:00", "Kailoren", "F1"));
        var clock = new DateTime(2026, 10, 17, 20, 0, 0, DateTimeKind.Utc);
        var probe = new JournalGrowthProbe(new JournalReader(folder), () => clock);
        Check("the first look only takes a baseline", !probe.Changed());
        Check("nothing written, nothing changed", !probe.Changed());
        File.AppendAllText(first, JumpTo("20:05:00", "Sol", 0, 0, 0) + "\r\n");
        Check("a line appended is a change", probe.Changed());
        Check("seen once", !probe.Changed());
        Journal(folder, "Journal.2026-10-17T220000.01.log", Header);
        Check("a new journal is not looked for every second", !probe.Changed());
        clock += JournalGrowthProbe.RelistEvery;
        Check("but is found once the relist wait is up", probe.Changed());

        var empty = new JournalGrowthProbe(new JournalReader(Path.Combine(_dir, "journal probe none")), () => clock);
        Check("no journals, no change", !empty.Changed() && !empty.Changed());
        Journal(Path.Combine(_dir, "journal probe none"), "Journal.2026-10-17T200000.01.log", Header);
        Check("the first journal to appear is a change", empty.Changed());
    }
}
