using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using FleetView.Models;
using FleetView.Services;

namespace FleetView.ViewModels;

/// <summary>One labelled line of a card or the breakdown: "Invite" and what it takes.</summary>
public sealed record EngineerLine(string Label, string Text);

/// <summary>What the Show box can narrow the cards to.</summary>
public enum EngineerShow
{
    All,
    Unlocked,
    NotUnlocked,
    Bubble,
    Colonia,
}

/// <summary>One entry of the Show box: the words shown and the filter behind them.</summary>
public sealed record EngineerShowOption(string Label, EngineerShow Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// One engineer's card, and everything the breakdown panel shows for them. The facts from the data
/// file never change; the state and the distance change in place on a live update, so a card keeps
/// its object (and the tab its scroll position and any open breakdown) while the journal grows.
/// </summary>
public sealed class EngineerCard : ObservableObject
{
    /// <param name="unlockOrder">The chain this engineer is in, as
    /// <see cref="EngineerText.UnlockOrder"/> writes it; empty for one in no chain.</param>
    public EngineerCard(Engineer engineer, string unlockOrder = "")
    {
        Engineer = engineer;
        UnlockOrder = unlockOrder;
        Update(null, null);
    }

    /// <summary>"Domino Green > Kit Fowler > Yarden Bond": who leads to whom, for the breakdown.</summary>
    public string UnlockOrder { get; }

    /// <summary>The data file's entry, for anything not repeated below.</summary>
    public Engineer Engineer { get; }

    public string Name => Engineer.Name;

    /// <summary>"Bubble" or "Colonia", for the chip on the right.</summary>
    public string Region => Engineer.Region;

    public string System => Engineer.System;

    public string Settlement => Engineer.Settlement;

    /// <summary>"Nevermore Terrace": the line under the system.</summary>
    public string PlaceText => EngineerText.Place(Engineer);

    /// <summary>"On-foot engineer, the Bubble", under the name in the breakdown.</summary>
    public string KindText => Engineer.IsColonia ? "On-foot engineer, Colonia" : "On-foot engineer, the Bubble";

    public IReadOnlyList<string> SuitModules => Engineer.SuitModules;

    public IReadOnlyList<string> WeaponModules => Engineer.WeaponModules;

    public bool HasSuitModules => Engineer.SuitModules.Count > 0;

    public bool HasWeaponModules => Engineer.WeaponModules.Count > 0;

    /// <summary>"3 suit modules, 2 weapon modules."</summary>
    public string ModulesText => EngineerText.Modules(Engineer);

    private EngineerState _state;
    public EngineerState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsUnlocked));
                OnPropertyChanged(nameof(ShowPrerequisites));
            }
        }
    }

    /// <summary>True once unlocked: the name turns to the accent colour, the prerequisites go and
    /// the referral moves to the foot of the card.</summary>
    public bool IsUnlocked => State == EngineerState.Unlocked;

    private int? _rank;
    /// <summary>The grade, if the journal ever gives one for this engineer (it does not for
    /// on-foot engineers today).</summary>
    public int? Rank
    {
        get => _rank;
        private set => SetProperty(ref _rank, value);
    }

    private string _stateText = "";
    /// <summary>"Unlocked", "Known", "Invited", "Not met" (or "Barred"), with the grade when there
    /// is one.</summary>
    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    private double? _distanceLy;
    /// <summary>Light years from the commander, unrounded; null when either end is unknown.</summary>
    public double? DistanceLy
    {
        get => _distanceLy;
        private set
        {
            if (SetProperty(ref _distanceLy, value))
            {
                OnPropertyChanged(nameof(HasDistance));
                OnPropertyChanged(nameof(DistanceLabel));
            }
        }
    }

    public bool HasDistance => DistanceLy != null;

    private string _distanceText = "";
    /// <summary>"128 ly", or empty when the distance is not known.</summary>
    public string DistanceText
    {
        get => _distanceText;
        private set
        {
            if (SetProperty(ref _distanceText, value)) OnPropertyChanged(nameof(DistanceLabel));
        }
    }

    /// <summary>What the card shows on the right of the system: "128 ly", or "distance unknown".</summary>
    public string DistanceLabel => HasDistance ? DistanceText : "distance unknown";

    private string _copyNote = "";
    /// <summary>"Copied" for a moment after the system was copied from this card or its breakdown,
    /// or why it could not be; empty the rest of the time.</summary>
    public string CopyNote
    {
        get => _copyNote;
        internal set
        {
            if (SetProperty(ref _copyNote, value)) OnPropertyChanged(nameof(HasCopyNote));
        }
    }

    public bool HasCopyNote => CopyNote.Length > 0;

    private string _distanceFromYouText = "";
    /// <summary>"126 ly from you", or "distance unknown", for the breakdown.</summary>
    public string DistanceFromYouText
    {
        get => _distanceFromYouText;
        private set => SetProperty(ref _distanceFromYouText, value);
    }

    private IReadOnlyList<EngineerLine> _prerequisites = [];
    /// <summary>
    /// The steps still to do before this engineer is unlocked, under "Unlock prerequisites": Find
    /// while not met, Invite until invited, and the referral task. Empty once unlocked.
    /// </summary>
    public IReadOnlyList<EngineerLine> Prerequisites
    {
        get => _prerequisites;
        private set => SetProperty(ref _prerequisites, value);
    }

    public bool ShowPrerequisites => !IsUnlocked && Prerequisites.Count > 0;

    private string _referralFooter = "";
    /// <summary>"Referral: Provide 15 Settlement Defence Plans to learn about Wellington Beck.",
    /// at the foot of an unlocked card; empty otherwise, or when this engineer refers nobody.</summary>
    public string ReferralFooter
    {
        get => _referralFooter;
        private set => SetProperty(ref _referralFooter, value);
    }

    private IReadOnlyList<EngineerLine> _breakdownLines = [];
    /// <summary>Everything about this engineer for the breakdown panel: progress, find, invite,
    /// referral, who they refer you to, and where they are.</summary>
    public IReadOnlyList<EngineerLine> BreakdownLines
    {
        get => _breakdownLines;
        private set => SetProperty(ref _breakdownLines, value);
    }

    /// <summary>Takes this engineer's latest standing and distance.</summary>
    internal void Update(EngineerStanding? standing, double? distanceLy)
    {
        State = standing?.State ?? EngineerState.NotMet;
        Rank = standing?.Rank;
        StateText = EngineerText.State(State, Rank);
        DistanceLy = distanceLy;
        DistanceText = EngineerText.Distance(distanceLy);
        DistanceFromYouText = distanceLy == null ? "distance unknown" : DistanceText + " from you";

        string referral = EngineerText.Referral(Engineer);
        var steps = new List<EngineerLine>();
        if (State != EngineerState.Unlocked)
        {
            if (State == EngineerState.NotMet && Engineer.Find.Length > 0)
                steps.Add(new("Find", Engineer.Find));
            if (State is EngineerState.NotMet or EngineerState.Known && Engineer.Invite.Length > 0)
                steps.Add(new("Invite", Engineer.Invite));
            if (referral.Length > 0)
                steps.Add(new("Referral", referral));
        }
        if (!SameLines(steps, Prerequisites)) Prerequisites = steps;
        OnPropertyChanged(nameof(ShowPrerequisites));
        ReferralFooter = State == EngineerState.Unlocked && referral.Length > 0 ? "Referral: " + referral : "";

        var lines = new List<EngineerLine> { new("Your progress", StateText) };
        if (Engineer.Find.Length > 0) lines.Add(new("Find", Engineer.Find));
        if (Engineer.Invite.Length > 0) lines.Add(new("Invite", Engineer.Invite));
        if (referral.Length > 0) lines.Add(new("Referral", referral));
        if (Engineer.RefersTo.Count > 0) lines.Add(new("Refers you to", EngineerText.Join(Engineer.RefersTo)));
        if (UnlockOrder.Length > 0) lines.Add(new("Unlock order", UnlockOrder));
        lines.Add(new("Location", EngineerText.Location(Engineer)));
        lines.Add(new("Distance", distanceLy == null ? "Unknown" : DistanceText));
        if (!SameLines(lines, BreakdownLines)) BreakdownLines = lines;
    }

    private static bool SameLines(IReadOnlyList<EngineerLine> a, IReadOnlyList<EngineerLine> b) => a.SequenceEqual(b);
}

/// <summary>One module in the breakdown: its name, and what applying it takes when FleetFinder's
/// own modification list has it ("5 Weapon Inventory, 3 Memory Chip"; empty when it does not).</summary>
public sealed record EngineerModuleLine(string Name, string Materials)
{
    public bool HasMaterials => Materials.Length > 0;
}

/// <summary>
/// What the breakdown panel shows for one engineer: the card (which stays current while the
/// journal grows) and each module the engineer does, with its materials looked up by name in the
/// modification list the Modifications tab uses. A module the list does not know shows its name
/// alone, so a data file with other module names still works.
/// </summary>
public sealed class EngineerBreakdown
{
    /// <param name="modificationName">The modification list's name for a module the data file
    /// names otherwise; the module's own name when null.</param>
    public EngineerBreakdown(EngineerCard card, IReadOnlyList<Modification> modifications,
        Func<string, string>? modificationName = null)
    {
        Card = card;
        modificationName ??= m => m;
        SuitModules = card.SuitModules.Select(m => Line(m, modificationName(m), "Suit", modifications)).ToList();
        WeaponModules = card.WeaponModules.Select(m => Line(m, modificationName(m), "Weapon", modifications)).ToList();
    }

    public EngineerCard Card { get; }

    public IReadOnlyList<EngineerModuleLine> SuitModules { get; }

    public IReadOnlyList<EngineerModuleLine> WeaponModules { get; }

    public bool HasSuitModules => SuitModules.Count > 0;

    public bool HasWeaponModules => WeaponModules.Count > 0;

    /// <summary>The module's materials, found under <paramref name="listName"/>, preferring an
    /// entry of the same kind when a suit and a weapon modification share a name. The line keeps
    /// the module's own name.</summary>
    internal static EngineerModuleLine Line(string module, string listName, string kind, IReadOnlyList<Modification> modifications)
    {
        var named = modifications.Where(m => string.Equals(m.Name.Trim(), listName.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var mod = named.FirstOrDefault(m => string.Equals(m.Kind, kind, StringComparison.OrdinalIgnoreCase)) ?? named.FirstOrDefault();
        string materials = mod == null ? "" : string.Join(", ",
            mod.Requirements.Where(r => r.Amount > 0 && r.Commodity.Length > 0)
                .Select(r => $"{r.Amount.ToString(CultureInfo.InvariantCulture)} {r.Commodity}"));
        return new EngineerModuleLine(module, materials);
    }
}

/// <summary>
/// The Engineers tab: a card per on-foot engineer, nearest first, narrowed by the search box and
/// the Show box, with the commander's standing with each and the distance to them.
/// </summary>
/// <remarks>
/// Reads run on the thread pool, one at a time, and a refresh asked for during a read makes one
/// more read after it, the same as the Ship Locker tab. The main view model asks for a read when
/// the inventory refreshes and whenever the newest journal grows.
/// </remarks>
public sealed class EngineersViewModel : ObservableObject
{
    private readonly Func<EngineerReading> _read;
    private readonly Action<Action> _post;
    private readonly List<EngineerCard> _all;
    private bool _reading;
    private bool _again;

    /// <param name="catalog">The engineers from the data file, and what went wrong loading it.</param>
    /// <param name="read">One read; called on a thread-pool thread.</param>
    /// <param name="post">How a finished read gets back to the UI thread. Null means the
    /// application's dispatcher, or straight away when there is no application (the self-test).</param>
    /// <param name="modifications">The modification list, for each module's materials in the
    /// breakdown. Null means none, and the breakdown lists module names alone.</param>
    /// <param name="after">Runs an action once after a delay, for taking the copy note away.
    /// Null means a dispatcher timer, or never when there is no application (the self-test
    /// passes its own).</param>
    public EngineersViewModel(EngineerCatalog catalog, Func<EngineerReading> read, Action<Action>? post = null,
        IReadOnlyList<Modification>? modifications = null, Action<TimeSpan, Action>? after = null)
    {
        _read = read;
        _post = post ?? PostToDispatcher;
        _modifications = modifications ?? [];
        _after = after ?? AfterOnDispatcher;
        CatalogProblem = catalog.Problem;
        _catalog = catalog;
        _all = catalog.Engineers.Select(e => new EngineerCard(e, EngineerText.UnlockOrder(e, catalog.Engineers))).ToList();
        OpenBreakdownCommand = new RelayCommand<EngineerCard>(card =>
        {
            if (card != null) SelectedEngineer = card;
        });
        CloseBreakdownCommand = new RelayCommand(() => SelectedEngineer = null);
        CopySystemCommand = new RelayCommand<EngineerCard>(card => CopySystem(card));
        ShowCards();
    }

    private readonly IReadOnlyList<Modification> _modifications;
    private readonly EngineerCatalog _catalog;
    private readonly Action<TimeSpan, Action> _after;
    private EngineerCard? _noted;
    private int _noteToken;

    /// <summary>The clipboard the system names go to. The self-test and the screenshot harness
    /// swap in a pretend one, so the real clipboard is never touched by them.</summary>
    public IClipboardText ClipboardText { get; set; } = SystemClipboardText.Instance;

    /// <summary>The note shown beside a system that was copied.</summary>
    public const string CopiedNote = "Copied";

    /// <summary>The note shown when another program was holding the clipboard.</summary>
    public const string BusyNote = "Clipboard busy, try again";

    /// <summary>How long the copy note shows.</summary>
    public static TimeSpan CopyNoteTime { get; } = TimeSpan.FromSeconds(1.5);

    /// <summary>A name was clicked: opens that engineer's breakdown.</summary>
    public RelayCommand<EngineerCard> OpenBreakdownCommand { get; }

    /// <summary>The breakdown's Close button.</summary>
    public RelayCommand CloseBreakdownCommand { get; }

    /// <summary>A system was clicked, on a card or in the breakdown: copies it.</summary>
    public RelayCommand<EngineerCard> CopySystemCommand { get; }

    /// <summary>
    /// Puts the card's system on the clipboard and shows a short note beside it ("Copied", or that
    /// the clipboard was busy) for <see cref="CopyNoteTime"/>. A second copy takes the first
    /// note away at once, and the first note's timer then leaves the second alone. Returns the
    /// note, or empty when there was nothing to copy.
    /// </summary>
    public string CopySystem(EngineerCard? card)
    {
        if (card == null || card.System.Trim().Length == 0) return "";
        string note = ClipboardText.SetText(card.System.Trim()) ? CopiedNote : BusyNote;
        if (_noted != null && !ReferenceEquals(_noted, card)) _noted.CopyNote = "";
        card.CopyNote = note;
        _noted = card;
        int token = ++_noteToken;
        _after(CopyNoteTime, () =>
        {
            if (token != _noteToken || _noted == null) return;
            _noted.CopyNote = "";
            _noted = null;
        });
        return note;
    }

    /// <summary>Escape on the tab: closes the breakdown if it is open. True when it was.</summary>
    public bool CloseOnEscape()
    {
        if (SelectedEngineer == null) return false;
        SelectedEngineer = null;
        return true;
    }

    private EngineerBreakdown? _breakdown;
    /// <summary>What the breakdown panel shows; null while it is shut.</summary>
    public EngineerBreakdown? Breakdown
    {
        get => _breakdown;
        private set
        {
            if (SetProperty(ref _breakdown, value)) OnPropertyChanged(nameof(IsBreakdownOpen));
        }
    }

    public bool IsBreakdownOpen => Breakdown != null;

    /// <summary>The cards the search and Show box let through, nearest first.</summary>
    public ObservableCollection<EngineerCard> Cards { get; } = new();

    /// <summary>Every engineer from the data file, whatever the filter.</summary>
    public IReadOnlyList<EngineerCard> AllCards => _all;

    /// <summary>The Show box's entries, in order.</summary>
    public IReadOnlyList<EngineerShowOption> ShowOptions { get; } =
    [
        new("All engineers", EngineerShow.All),
        new("Unlocked", EngineerShow.Unlocked),
        new("Not unlocked yet", EngineerShow.NotUnlocked),
        new("Bubble", EngineerShow.Bubble),
        new("Colonia", EngineerShow.Colonia),
    ];

    /// <summary>What went wrong reading engineers.json, in a sentence; empty when it was fine.</summary>
    public string CatalogProblem { get; }

    /// <summary>The latest read shown; null before the first.</summary>
    public EngineerReading? Reading { get; private set; }

    private string _searchText = "";
    /// <summary>Narrows the cards to engineers whose name, system, settlement or any module
    /// contains it, ignoring case and the spaces round it.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? "")) ShowCards();
        }
    }

    private EngineerShow _show = EngineerShow.All;
    public EngineerShow Show
    {
        get => _show;
        set
        {
            if (SetProperty(ref _show, value))
            {
                OnPropertyChanged(nameof(SelectedShowOption));
                ShowCards();
            }
        }
    }

    /// <summary>The Show box's selected entry, for a ComboBox bound to <see cref="ShowOptions"/>.</summary>
    public EngineerShowOption SelectedShowOption
    {
        get => ShowOptions.First(o => o.Value == Show);
        set
        {
            if (value != null) Show = value.Value;
        }
    }

    private EngineerCard? _selectedEngineer;
    /// <summary>The engineer whose breakdown is open; null when it is closed. The card object
    /// lasts across live updates, so the panel stays open and current while the journal grows.</summary>
    public EngineerCard? SelectedEngineer
    {
        get => _selectedEngineer;
        set
        {
            if (SetProperty(ref _selectedEngineer, value))
                Breakdown = value == null ? null : new EngineerBreakdown(value, _modifications, _catalog.ModificationNameFor);
        }
    }

    private string _commanderText = "";
    /// <summary>"CMDR Kailoren", or empty when no journal names one.</summary>
    public string CommanderText
    {
        get => _commanderText;
        private set => SetProperty(ref _commanderText, value);
    }

    private string _distancesFromText = "";
    /// <summary>"Distances from LHS 1387", or why there are none.</summary>
    public string DistancesFromText
    {
        get => _distancesFromText;
        private set => SetProperty(ref _distancesFromText, value);
    }

    private string _summary = "";
    /// <summary>"13 on-foot engineers, 3 unlocked.", plus what the filter shows.</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    private string _emptyText = "";
    /// <summary>What the tab says when no card shows; empty while some do.</summary>
    public string EmptyText
    {
        get => _emptyText;
        private set
        {
            if (SetProperty(ref _emptyText, value)) OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => EmptyText.Length > 0;

    /// <summary>True while a read is running.</summary>
    public bool IsReading => _reading;

    /// <summary>Raised on the UI thread each time a read has been shown.</summary>
    public event Action? Refreshed;

    /// <summary>Starts a read, or marks one to follow the read already running.</summary>
    public void Refresh()
    {
        if (_reading)
        {
            _again = true;
            return;
        }
        _reading = true;
        Task.Run(() =>
        {
            EngineerReading? result = null;
            try
            {
                result = _read();
            }
            catch (Exception ex)
            {
                // The readers handle everything on disk and on the network themselves, so this is
                // a bug; logged, and the tab keeps what it had.
                DiagnosticLog.Crash(ex);
            }
            _post(() =>
            {
                _reading = false;
                if (result != null) Accept(result);
                if (_again)
                {
                    _again = false;
                    Refresh();
                }
                Refreshed?.Invoke();
            });
        });
    }

    /// <summary>Takes a finished read: every card's state and distance, then the order and filter.</summary>
    public void Accept(EngineerReading reading)
    {
        Reading = reading;
        var here = reading.Location;
        foreach (var card in _all)
        {
            SystemCoords? coords = reading.LookedUp.TryGetValue(ShipLockerReader.Normalize(card.System), out var c) ? c : null;
            double? distance = here != null && coords is SystemCoords at ? EngineerText.Distance(here, at) : null;
            card.Update(reading.Progress.For(card.Name), distance);
        }

        CommanderText = reading.Progress.Commander.Length > 0 ? "CMDR " + reading.Progress.Commander : "";
        DistancesFromText = here == null
            ? "No position in the journal yet, so no distances"
            : here.System.Length > 0 ? "Distances from " + here.System : "Distances from your last position";
        ShowCards();
    }

    private void ShowCards()
    {
        string search = SearchText.Trim();
        var target = _all
            .Where(c => Passes(c, Show) && Matches(c, search))
            .OrderBy(c => c.DistanceLy == null ? 1 : 0)
            .ThenBy(c => c.DistanceLy ?? 0)
            .ThenBy(c => c.Name, ShipLockerContentsReader.NameOrder)
            .ToList();
        Sync(target);

        Summary = EngineerText.Summary(_all.Count, _all.Count(c => c.IsUnlocked), Reading,
            search.Length > 0 || Show != EngineerShow.All ? target.Count : null);
        EmptyText = target.Count > 0 ? ""
            : _all.Count == 0 ? (CatalogProblem.Length > 0 ? CatalogProblem : "No engineer data.")
            : EngineerText.NoMatch(search, Show == EngineerShow.All ? null : SelectedShowOption.Label);
    }

    /// <summary>Brings <see cref="Cards"/> to <paramref name="target"/> with moves rather than a
    /// rebuild, so the cards on screen keep their objects.</summary>
    private void Sync(List<EngineerCard> target)
    {
        for (int i = 0; i < target.Count; i++)
        {
            if (i < Cards.Count && ReferenceEquals(Cards[i], target[i])) continue;
            int at = Cards.IndexOf(target[i]);
            if (at >= 0) Cards.Move(at, i);
            else Cards.Insert(i, target[i]);
        }
        while (Cards.Count > target.Count) Cards.RemoveAt(Cards.Count - 1);
    }

    internal static bool Passes(EngineerCard card, EngineerShow show) => show switch
    {
        EngineerShow.Unlocked => card.IsUnlocked,
        EngineerShow.NotUnlocked => !card.IsUnlocked,
        EngineerShow.Bubble => !card.Engineer.IsColonia,
        EngineerShow.Colonia => card.Engineer.IsColonia,
        _ => true,
    };

    internal static bool Matches(EngineerCard card, string search)
    {
        if (search.Length == 0) return true;
        bool Has(string text) => text.Contains(search, StringComparison.OrdinalIgnoreCase);
        return Has(card.Name) || Has(card.System) || Has(card.Settlement)
               || card.SuitModules.Any(Has) || card.WeaponModules.Any(Has);
    }

    private static void PostToDispatcher(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) action();
        else dispatcher.BeginInvoke(action);
    }

    private static void AfterOnDispatcher(TimeSpan delay, Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }
}

/// <summary>The Engineers tab's words and sums, kept apart so the self-test can read them.</summary>
public static class EngineerText
{
    private static readonly CultureInfo Numbers = CultureInfo.InvariantCulture;

    public static string State(EngineerState state, int? rank)
    {
        string text = state switch
        {
            EngineerState.Unlocked => "Unlocked",
            EngineerState.Invited => "Invited",
            EngineerState.Known => "Known",
            EngineerState.Barred => "Barred",
            _ => "Not met",
        };
        return rank is int r && r > 0 ? $"{text}, grade {r.ToString(Numbers)}" : text;
    }

    /// <summary>Straight-line light years between two points.</summary>
    public static double Distance(PlayerLocation from, SystemCoords to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>"128 ly", "22,000 ly"; empty when unknown.</summary>
    public static string Distance(double? ly) =>
        ly is double d ? Math.Round(d, MidpointRounding.AwayFromZero).ToString("N0", Numbers) + " ly" : "";

    /// <summary>"Nevermore Terrace": the settlement the engineer works from.</summary>
    public static string Place(Engineer e) => e.Settlement;

    /// <summary>"Nevermore Terrace, Siris (the Bubble)".</summary>
    public static string Location(Engineer e)
    {
        string place = Place(e);
        string where = e.IsColonia ? "Colonia" : "the Bubble";
        return (place.Length > 0 ? place + ", " : "") + $"{e.System} ({where})";
    }

    /// <summary>"Provide 5 doses of Push to learn about Kit Fowler." Empty when the engineer refers
    /// nobody; the bare task when the file names no one to learn about.</summary>
    public static string Referral(Engineer e)
    {
        if (e.Referral.Length == 0) return "";
        if (e.RefersTo.Count == 0) return e.Referral;
        return e.Referral.TrimEnd('.', ' ') + " to learn about " + Join(e.RefersTo) + ".";
    }

    /// <summary>
    /// The unlock chain an engineer is in, from the first engineer to the last, with the
    /// engineers who refer the same one joined: "Hero Ferrari > Wellington Beck > Uma Laszlo",
    /// "Baltanos, Eleanor Bresa and Rosa Dayette > Yi Shen". Worked out from who refers whom in the
    /// data file, so it follows the file. Empty for an engineer nobody refers and who refers nobody.
    /// </summary>
    public static string UnlockOrder(Engineer engineer, IReadOnlyList<Engineer> all)
    {
        const string Arrow = " > ";
        var byName = all.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // On to the end of the chain: each step is everyone the step before it refers.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { engineer.Name };
        List<string> last = [engineer.Name];
        while (true)
        {
            var next = last.SelectMany(n => byName.TryGetValue(n, out var e) ? e.RefersTo : [])
                .Where(seen.Add).ToList();
            if (next.Count == 0) break;
            last = next;
        }

        // Then back from the end to the start: each step is everyone who refers someone in the
        // step after it, so an engineer reached from three shows all three.
        var steps = new List<List<string>> { last };
        var placed = new HashSet<string>(last, StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var referrers = all.Where(e => !placed.Contains(e.Name)
                                           && e.RefersTo.Any(r => steps[0].Contains(r, StringComparer.OrdinalIgnoreCase)))
                .Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (referrers.Count == 0) break;
            referrers.ForEach(n => placed.Add(n));
            steps.Insert(0, referrers);
        }

        return steps.Count < 2 ? "" : string.Join(Arrow, steps.Select(Join));
    }

    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string Join(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>
    /// What the tab says when the search, the Show box or both leave no card: "No engineer matches
    /// "zzz"." with what to do about it.
    /// </summary>
    /// <param name="search">The search, trimmed; empty when there is none.</param>
    /// <param name="show">The Show box's label, or null for All engineers.</param>
    public static string NoMatch(string search, string? show) => (search.Length > 0, show) switch
    {
        (true, null) => $"No engineer matches \"{search}\". Clear the search to see them all.",
        (true, _) => $"No engineer in \"{show}\" matches \"{search}\". Clear the search or choose All engineers.",
        (false, not null) => $"No engineer to show in \"{show}\". Choose All engineers to see them all.",
        _ => "No engineer to show.",
    };

    /// <summary>"3 suit modules, 2 weapon modules."</summary>
    public static string Modules(Engineer e) =>
        $"{Count(e.SuitModules.Count, "suit module")}, {Count(e.WeaponModules.Count, "weapon module")}.";

    private static string Count(int n, string word) => $"{n.ToString(Numbers)} {word}{(n == 1 ? "" : "s")}";

    /// <summary>
    /// The line under the search box: how many engineers and how many unlocked, what the filter
    /// shows, and a word when the journal has not said anything about engineers yet.
    /// </summary>
    /// <param name="shown">How many cards the filter lets through, or null with no filter.</param>
    public static string Summary(int total, int unlocked, EngineerReading? reading, int? shown)
    {
        if (total == 0) return "No engineer data to show.";
        string line = $"{Count(total, "on-foot engineer")}, {unlocked.ToString(Numbers)} unlocked.";
        if (shown is int s) line += $" Showing {s.ToString(Numbers)} of {total.ToString(Numbers)}.";
        if (reading == null)
            line += " Reading the journal...";
        else if (!reading.Progress.HasProgress && reading.Progress.Commander.Length == 0 && reading.Location == null)
            line += " No journal to read yet, so every engineer shows as not met. Play once and they fill in.";
        else if (!reading.Progress.HasProgress)
            line += reading.Progress.Commander.Length > 0
                ? $" The journal has no engineer progress for CMDR {reading.Progress.Commander} yet; the game writes it at login."
                : " No engineer progress in the journal yet; the game writes it at login.";
        return line;
    }
}
