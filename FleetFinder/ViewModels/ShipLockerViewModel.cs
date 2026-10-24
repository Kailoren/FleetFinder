using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using FleetView.Models;
using FleetView.Services;

namespace FleetView.ViewModels;

/// <summary>One row of a Ship Locker card. The count changes in place on a live update, so the
/// grid keeps its scroll position and only the number moves.</summary>
public sealed class LockerRow : ObservableObject
{
    public LockerRow(string name, int count)
    {
        Name = name;
        _count = count;
    }

    public string Name { get; }

    private int _count;
    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }
}

/// <summary>One of the four cards: Goods, Assets, Data or Consumables.</summary>
public sealed class LockerCard : ObservableObject
{
    public LockerCard(LockerGroup group)
    {
        Group = group;
    }

    public LockerGroup Group { get; }

    public string Title => Group.ToString();

    /// <summary>The rows the filter lets through, sorted by name.</summary>
    public ObservableCollection<LockerRow> Rows { get; } = new();

    private string _heldText = "";
    /// <summary>"475 held, 15 kinds", for the rows showing.</summary>
    public string HeldText
    {
        get => _heldText;
        set => SetProperty(ref _heldText, value);
    }

    private string _emptyText = "";
    /// <summary>What the card says when it has no rows; empty while it has some.</summary>
    public string EmptyText
    {
        get => _emptyText;
        set
        {
            if (SetProperty(ref _emptyText, value)) OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public bool IsEmpty => EmptyText.Length > 0;

    /// <summary>
    /// Brings <see cref="Rows"/> to <paramref name="target"/> (sorted the same way) with the fewest
    /// changes: rows that stay keep their object and just take the new count, so a live update does
    /// not reset the grid's scroll position or its selection.
    /// </summary>
    internal void SyncRows(IReadOnlyList<LockerItem> target)
    {
        var order = ShipLockerContentsReader.NameOrder;
        int i = 0;
        foreach (var item in target)
        {
            while (i < Rows.Count && order.Compare(Rows[i].Name, item.Name) < 0)
                Rows.RemoveAt(i);
            if (i < Rows.Count && Rows[i].Name == item.Name)
                Rows[i].Count = item.Count;
            else
                Rows.Insert(i, new LockerRow(item.Name, item.Count));
            i++;
        }
        while (Rows.Count > i) Rows.RemoveAt(Rows.Count - 1);
    }
}

/// <summary>
/// The Ship Locker tab: the latest read of the locker, the filter, and the words around them.
/// </summary>
/// <remarks>
/// Reading goes to the thread pool, because it reads journal files and the locker file can be
/// mid-write, which the read waits out with short sleeps. One read runs at a time; a refresh asked
/// for while one is running is remembered and run straight after, so a burst of rewrites from the
/// game ends with the latest one shown and never queues up more than one read behind it.
/// </remarks>
public sealed class ShipLockerViewModel : ObservableObject
{
    private readonly Func<ShipLockerContents> _read;
    private readonly Action<Action> _post;
    private bool _reading;
    private bool _again;

    /// <param name="read">One read of the locker; called on a thread-pool thread.</param>
    /// <param name="post">
    /// How a finished read gets back to the UI thread. Null means the application's dispatcher, or
    /// straight away when there is no application (the self-test).
    /// </param>
    public ShipLockerViewModel(Func<ShipLockerContents> read, Action<Action>? post = null)
    {
        _read = read;
        _post = post ?? PostToDispatcher;
        foreach (var group in Enum.GetValues<LockerGroup>())
            Cards.Add(new LockerCard(group));
        Show(null);
    }

    public ObservableCollection<LockerCard> Cards { get; } = new();

    /// <summary>What is on screen now; null before the first read.</summary>
    public ShipLockerContents? Contents { get; private set; }

    private string _commanderText = "";
    /// <summary>"CMDR Kailoren", or empty when no journal names one.</summary>
    public string CommanderText
    {
        get => _commanderText;
        private set => SetProperty(ref _commanderText, value);
    }

    private string _asOfText = "";
    /// <summary>"Ship locker as of 17 Oct 21:13", after the commander.</summary>
    public string AsOfText
    {
        get => _asOfText;
        private set => SetProperty(ref _asOfText, value);
    }

    private string _summary = "";
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    private string _filterText = "";
    /// <summary>Narrows every card to the items whose name contains it, ignoring case.</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value ?? "")) ShowCards();
        }
    }

    /// <summary>True while a read is running.</summary>
    public bool IsReading => _reading;

    /// <summary>Raised on the UI thread each time a read has been shown; the self-test and the
    /// harness wait on it.</summary>
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
            ShipLockerContents? result = null;
            try
            {
                result = _read();
            }
            catch (Exception ex)
            {
                // The reader handles everything on disk itself, so this is a bug; logged, and the
                // tab keeps what it had rather than going blank.
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

    /// <summary>
    /// Takes a finished read. A file that could not be read this time does not wipe a locker
    /// already showing: the game is most likely mid-write, and the next rewrite brings a new read.
    /// </summary>
    public void Accept(ShipLockerContents contents)
    {
        if (contents.State == LockerState.Unreadable && Contents?.State == LockerState.Ready)
            return;
        Show(contents);
    }

    private void Show(ShipLockerContents? contents)
    {
        Contents = contents;
        CommanderText = contents?.Commander is { Length: > 0 } cmdr ? "CMDR " + cmdr : "";
        string asOf = contents?.State switch
        {
            LockerState.Ready => "Ship locker as of " + LockerText.Time(contents.AsOfUtc),
            LockerState.NoLocker => "No ship locker found",
            LockerState.OtherCommander => "Ship locker not written for this commander yet",
            LockerState.Unreadable => "Ship locker could not be read",
            _ => "Reading the ship locker",
        };
        AsOfText = CommanderText.Length > 0 ? "   " + asOf : asOf;
        ShowCards();
    }

    private void ShowCards()
    {
        var contents = Contents;
        string filter = FilterText.Trim();
        int shownKinds = 0;
        foreach (var card in Cards)
        {
            var items = contents?.State == LockerState.Ready ? contents[card.Group].Items : [];
            var visible = filter.Length == 0
                ? items
                : items.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            card.SyncRows(visible);
            shownKinds += visible.Count;
            card.HeldText = contents?.State == LockerState.Ready
                ? LockerText.Held(visible.Sum(i => (long)i.Count), visible.Count)
                : "";
            card.EmptyText = visible.Count > 0 ? ""
                : contents?.State != LockerState.Ready ? "Nothing to show yet."
                : items.Count == 0 ? "Nothing held."
                : "Nothing here matches the filter.";
        }
        Summary = LockerText.Summary(contents, filter.Length > 0 ? shownKinds : null);
    }

    private static void PostToDispatcher(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) action();
        else dispatcher.BeginInvoke(action);
    }
}

/// <summary>The Ship Locker tab's sentences, kept apart so the self-test can read them.</summary>
public static class LockerText
{
    private static readonly CultureInfo Numbers = CultureInfo.InvariantCulture;

    /// <summary>"17 Oct 21:13", in the PC's own time zone.</summary>
    public static string Time(DateTime? utc) =>
        utc is DateTime t
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture)
            : "an unknown time";

    /// <summary>"475 held, 15 kinds".</summary>
    public static string Held(long held, int kinds) =>
        $"{held.ToString("N0", Numbers)} held, {Count(kinds, "kind")}";

    private static string Count(long n, string word) => $"{n.ToString("N0", Numbers)} {word}{(n == 1 ? "" : "s")}";

    /// <summary>
    /// The line under the filter: totals and the time for a locker that was read, otherwise what
    /// was found instead and what will change it, in plain words.
    /// </summary>
    /// <param name="filteredKinds">How many kinds the filter lets through, or null with no filter.</param>
    public static string Summary(ShipLockerContents? contents, int? filteredKinds)
    {
        switch (contents?.State)
        {
            case null:
                return "Reading the ship locker...";
            case LockerState.NoLocker when contents.Commander.Length > 0:
                return $"No ship locker for CMDR {contents.Commander} yet. The game writes ShipLocker.json when you log in " +
                       "to Odyssey, so it shows here after your next login.";
            case LockerState.NoLocker:
                return "No ship locker yet. FleetFinder reads ShipLocker.json and the journal from the game's Saved Games folder, " +
                       "and both appear once Elite Dangerous Odyssey has been played on this Windows account.";
            case LockerState.OtherCommander:
                string who = contents.Commander.Length > 0 ? "CMDR " + contents.Commander : "this commander";
                string other = contents.OtherCommander == "another commander" ? "another commander" : "CMDR " + contents.OtherCommander;
                return $"Nothing to show for {who} yet: the game last wrote the ship locker for {other}. " +
                       $"It fills in when the game next writes it for {who}, at login or when the locker changes.";
            case LockerState.Unreadable:
                return "ShipLocker.json is there but could not be read just now. FleetFinder reads it again the next time the game writes it.";
        }

        string when = Time(contents.AsOfUtc);
        if (contents.Kinds == 0)
            return $"The ship locker is empty, as of {when}.";
        string line = $"{Count(contents.ItemsHeld, "item")}, {Count(contents.Kinds, "kind")}, as of {when}. " +
                      "The ship locker has no cap per item.";
        if (filteredKinds is int shown)
            line += $" The filter shows {shown.ToString("N0", Numbers)} of {Count(contents.Kinds, "kind")}.";
        return line;
    }
}
