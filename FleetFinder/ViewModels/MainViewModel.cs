using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using FleetView.Models;
using FleetView.Services;
using Component = FleetView.Models.Component;

namespace FleetView.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IReadOnlyList<Component> _catalog;
    private readonly IReadOnlyList<Modification> _modCatalog;
    private readonly ShipLockerReader _locker;
    private readonly ICarrierMarketSource _market;
    private readonly JournalReader _journal;
    private readonly ICoordinateSource? _coords;
    private readonly HashSet<string> _tradeableNorm;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _pollTimer;
    private DateTime? _lastSeenWriteUtc;

    public ObservableCollection<ComponentRow> Components { get; } = new();
    public ICollectionView ComponentsView { get; }

    public ObservableCollection<ModificationRow> Modifications { get; } = new();
    public ICollectionView ModificationsView { get; }

    /// <summary>Rows parsed from a pasted/loaded EDOMH wishlist export (Import tab preview).</summary>
    public ObservableCollection<ImportPreviewRow> ImportPreview { get; } = new();

    /// <summary>Requirements of the clicked modification (top pane), scaled by its quantity.</summary>
    public ObservableCollection<RequirementDisplayRow> SelectedRequirements { get; } = new();

    /// <summary>Total of every commodity needed across all checked modifications (bottom pane).</summary>
    public ObservableCollection<RequirementDisplayRow> PinnedRequirements { get; } = new();
    public ICollectionView PinnedRequirementsView { get; }

    /// <summary>Search results, one row per carrier/station (each listing its matching commodities).</summary>
    public ObservableCollection<CarrierGroupRow> Listings { get; } = new();
    public ICollectionView ListingsView { get; }

    public RelayCommand RefreshInventoryCommand { get; }
    public RelayCommand SearchCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand<string> CopySystemCommand { get; }
    public RelayCommand ApplyModTargetsCommand { get; }
    public RelayCommand ClearModSelectionCommand { get; }
    public RelayCommand ImportWishlistCommand { get; }
    public RelayCommand BrowseImportFileCommand { get; }
    public RelayCommand ClearImportCommand { get; }
    public RelayCommand ContinuePendingSearchCommand { get; }
    public RelayCommand StartNewSearchCommand { get; }
    public RelayCommand OpenUpdateCommand { get; }
    public RelayCommand DismissUpdateCommand { get; }

    public MainViewModel(
        IReadOnlyList<Component> catalog, IReadOnlyList<Modification> modifications,
        ShipLockerReader locker, ICarrierMarketSource market,
        JournalReader journal, ICoordinateSource? coords)
    {
        _catalog = catalog;
        _modCatalog = modifications;
        _locker = locker;
        _market = market;
        _journal = journal;
        _coords = coords;
        _tradeableNorm = catalog.Select(c => ShipLockerReader.Normalize(c.Name)).ToHashSet();

        ComponentsView = CollectionViewSource.GetDefaultView(Components);
        // Top level: Goods / Assets / Data. Second level: Chemicals/Circuits/Tech under Assets
        // (SubCategory is "" for Data/Goods items, so their sub-group header renders empty/hidden —
        // see the DataGrid's second GroupStyle in MainWindow.xaml).
        ComponentsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ComponentRow.Category)));
        ComponentsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ComponentRow.SubCategory)));

        ModificationsView = CollectionViewSource.GetDefaultView(Modifications);
        ModificationsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModificationRow.Kind)));

        PinnedRequirementsView = CollectionViewSource.GetDefaultView(PinnedRequirements);

        ListingsView = CollectionViewSource.GetDefaultView(Listings);
        // Default sort: freshest first (matches the "Updated" column's SortMemberPath in
        // MainWindow.xaml, so the header shows the matching sort arrow too). Clicking any column
        // header re-sorts as normal — this only sets the starting state.
        ListingsView.SortDescriptions.Add(
            new SortDescription(nameof(CarrierGroupRow.MinAge), ListSortDirection.Ascending));

        RefreshInventoryCommand = new RelayCommand(RefreshInventory);
        SearchCommand = new RelayCommand(SearchSelectedAsync, () => SelectedCount > 0);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        SelectAllCommand = new RelayCommand(SelectAll);
        CopySystemCommand = new RelayCommand<string>(CopySystem);
        ApplyModTargetsCommand = new RelayCommand(ApplyModTargets);
        ClearModSelectionCommand = new RelayCommand(ClearModSelection);
        ImportWishlistCommand = new RelayCommand(ApplyImportTargets);
        BrowseImportFileCommand = new RelayCommand(BrowseImportFile);
        ClearImportCommand = new RelayCommand(ClearImport);
        ContinuePendingSearchCommand = new RelayCommand(ContinuePendingSearch);
        StartNewSearchCommand = new RelayCommand(StartNewSearch);
        OpenUpdateCommand = new RelayCommand(OpenUpdate);
        DismissUpdateCommand = new RelayCommand(() => HasUpdateAvailable = false);

        RefreshInventory();
        LoadPendingSearchIfAny();
        SetupWatcher();
        _ = CheckForUpdateAsync();
    }

    // ---- Inventory ------------------------------------------------------------------------

    public void RefreshInventory()
    {
        // Single source of truth for "have we already seen this write" — both the file watcher
        // and the polling fallback in SetupWatcher funnel through here, so this is the one place
        // that needs to stay in sync with the file's timestamp.
        _lastSeenWriteUtc = _locker.LastWriteUtc;

        // Covers every ShipLocker.json category (Items/Components/Consumables/Data), keyed by
        // normalised name — shared below for both the component picker and the mod requirements.
        var counts = _locker.ReadAllCounts();

        if (Components.Count == 0)
        {
            foreach (var c in _catalog)
            {
                var row = new ComponentRow(c, counts.GetValueOrDefault(ShipLockerReader.Normalize(c.Name)));
                row.PropertyChanged += OnRowPropertyChanged;
                Components.Add(row);
            }
        }
        else
        {
            // Auto-deselect anything a ticked search no longer needs to buy: once the game's
            // inventory catches up to the target (e.g. after buying it from a carrier), there's
            // no reason to keep searching for it.
            int caughtUp = 0;
            foreach (var row in Components)
            {
                row.Have = counts.GetValueOrDefault(ShipLockerReader.Normalize(row.Component.Name));
                if (row.IsSelected && !row.IsShort)
                {
                    row.IsSelected = false;
                    caughtUp++;
                }
            }
            if (caughtUp > 0)
                Status = $"Inventory updated — {caughtUp} component(s) now fully stocked and deselected.";

            // Keep "Where to buy" results' Needed column live too, not just the picker's -
            // otherwise it stays frozen at whatever it was when Search was last clicked, instead
            // of tracking purchases the same way the picker already does.
            if (Listings.Count > 0)
            {
                var neededByNorm = BuildNeededByNorm();
                foreach (var group in Listings)
                    foreach (var item in group.Items)
                        if (neededByNorm.TryGetValue(ShipLockerReader.Normalize(item.Component), out var need))
                            item.Needed = need;
            }
        }

        RebuildModifications(counts);

        InventoryStatus = _locker.Exists
            ? $"Inventory: ShipLocker.json  (updated {FormatLocal(_locker.LastWriteUtc)})"
            : "Inventory: ShipLocker.json not found — is Elite Dangerous installed for this user?";

        ComponentsView.Refresh();
    }

    /// <summary>(Re)builds modification rows against current inventory, preserving selection.</summary>
    private void RebuildModifications(Dictionary<string, int> haveByNorm)
    {
        var previouslySelected = Modifications.Where(m => m.IsSelected).Select(m => m.Name).ToHashSet();
        var previouslyViewed = SelectedModification?.Name;

        foreach (var old in Modifications)
            old.PropertyChanged -= OnModRowPropertyChanged;
        Modifications.Clear();

        foreach (var mod in _modCatalog)
        {
            var row = new ModificationRow(mod, haveByNorm, _tradeableNorm)
            {
                IsSelected = previouslySelected.Contains(mod.Name)
            };
            row.PropertyChanged += OnModRowPropertyChanged;
            Modifications.Add(row);
        }

        // Re-point the viewed row to the freshly-built instance and refresh both panes.
        SelectedModification = previouslyViewed == null
            ? null
            : Modifications.FirstOrDefault(m => m.Name == previouslyViewed);
        RebuildPinnedRequirements();
        OnPropertyChanged(nameof(ModSelectionSummary));
    }

    private void SetupWatcher()
    {
        try
        {
            var dir = Path.GetDirectoryName(_locker.FilePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                _watcher = new FileSystemWatcher(dir, "ShipLocker.json")
                {
                    // FileName included in case the game writes via a temp-file-then-rename
                    // pattern rather than an in-place write — Renamed is wired up below for
                    // exactly that case.
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                FileSystemEventHandler handler = (_, _) =>
                    Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshInventory));
                _watcher.Changed += handler;
                _watcher.Created += handler;
                _watcher.Renamed += (_, _) =>
                    Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshInventory));
            }
        }
        catch
        {
            // Watching is a nicety; ignore if the folder can't be watched.
        }

        // Belt-and-braces fallback: FileSystemWatcher is known to miss or delay events on some
        // setups (buffered writes, antivirus scanning, cloud-synced Saved Games folders). A plain
        // threadpool Timer (not DispatcherTimer) is used deliberately - Windows can put an
        // unfocused/background window's UI thread into "Efficiency Mode" (EcoQoS) power
        // throttling, which delays DispatcherTimer ticks and is almost certainly why updates
        // previously stalled while the app wasn't focused (see the process-wide opt-out in
        // App.xaml.cs, which addresses this from the other side too). Polling on a threadpool
        // thread here means detection itself is unaffected by that UI-thread throttling; only the
        // resulting RefreshInventory call is marshalled back via Dispatcher.BeginInvoke, same as
        // the FileSystemWatcher handlers above.
        _lastSeenWriteUtc = _locker.LastWriteUtc;

        _pollTimer = new System.Threading.Timer(_ =>
        {
            if (_locker.LastWriteUtc != _lastSeenWriteUtc)
                Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshInventory));
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    // ---- Search ---------------------------------------------------------------------------

    /// <summary>How many components are currently ticked for searching.</summary>
    public int SelectedCount => Components.Count(c => c.IsSelected);

    /// <summary>Label for the search button, e.g. "Search prices (3)".</summary>
    public string SearchButtonLabel =>
        SelectedCount > 0 ? $"Search prices ({SelectedCount})" : "Search prices";

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComponentRow.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(SearchButtonLabel));
            SearchCommand.RaiseCanExecuteChanged();
        }
    }

    private void ClearSelection()
    {
        foreach (var row in Components)
            row.IsSelected = false;
    }

    /// <summary>Ticks every component - for mass-testing the market source against every key at once.</summary>
    private void SelectAll()
    {
        foreach (var row in Components)
            row.IsSelected = true;
    }

    /// <summary>Copies a system name to the clipboard for pasting into the in-game galaxy map.</summary>
    private void CopySystem(string? system)
    {
        if (string.IsNullOrWhiteSpace(system)) return;
        try
        {
            Clipboard.SetText(system);
            Status = $"Copied system “{system}” to clipboard.";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't copy system: {ex.Message}";
        }
    }

    // ---- Distance from current location ---------------------------------------------------

    private string _currentSystemLabel = "";
    /// <summary>e.g. "Distances from Kitchang Mu" — shown above the results.</summary>
    public string CurrentSystemLabel
    {
        get => _currentSystemLabel;
        private set => SetProperty(ref _currentSystemLabel, value);
    }

    /// <summary>
    /// Sets each listing's Distance to the light-year distance from the commander's current
    /// system (read from the journal), resolving system coordinates via the coordinate source.
    /// If the location or a coordinate source isn't available, distances are left untouched.
    /// </summary>
    private async Task ApplyDistancesFromCurrentLocationAsync(List<CarrierListing> listings)
    {
        var loc = _journal.GetCurrentLocation();
        if (loc == null)
        {
            CurrentSystemLabel = "Current location unknown (no journal) — distances unavailable.";
            return;
        }

        CurrentSystemLabel = $"Distances from {loc.System}";
        if (_coords == null) return; // e.g. offline/mock mode — keep source-provided distances

        IReadOnlyDictionary<string, SystemCoords> map;
        try
        {
            map = await _coords.GetCoordsAsync(listings.Select(l => l.System));
        }
        catch
        {
            return; // leave distances as-is on lookup failure
        }

        foreach (var l in listings)
        {
            if (map.TryGetValue(ShipLockerReader.Normalize(l.System), out var c))
            {
                double dx = c.X - loc.X, dy = c.Y - loc.Y, dz = c.Z - loc.Z;
                l.DistanceLy = Math.Round(Math.Sqrt(dx * dx + dy * dy + dz * dz));
            }
            else
            {
                l.DistanceLy = null; // unresolved -> blank rather than a misleading reference distance
            }
        }
    }

    // ---- Modifications --------------------------------------------------------------------

    private ModificationRow? _selectedModification;
    public ModificationRow? SelectedModification
    {
        get => _selectedModification;
        set
        {
            if (SetProperty(ref _selectedModification, value))
            {
                RebuildSelectedRequirements();
                OnPropertyChanged(nameof(SelectedRequirementsHeader));
            }
        }
    }

    private void OnModRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModificationRow.IsSelected)
            || e.PropertyName == nameof(ModificationRow.Quantity))
        {
            OnPropertyChanged(nameof(ModSelectionSummary));
            RebuildPinnedRequirements();
            // If the clicked mod's quantity changed, its scaled "Required" needs refreshing too.
            if (ReferenceEquals(sender, SelectedModification))
            {
                RebuildSelectedRequirements();
                OnPropertyChanged(nameof(SelectedRequirementsHeader));
            }
        }
    }

    /// <summary>Header for the top pane, e.g. "Requirements — Greater Range  ×2".</summary>
    public string SelectedRequirementsHeader =>
        SelectedModification == null
            ? "Requirements"
            : $"Requirements — {SelectedModification.Name}  ×{SelectedModification.Quantity}";

    private static IEnumerable<RequirementDisplayRow> Scale(ModificationRow m) =>
        m.Requirements.Select(r => new RequirementDisplayRow
        {
            Commodity = r.Commodity,
            Required = r.Amount * m.Quantity,
            Have = r.Have,
            Tradeable = r.Tradeable
        });

    private void RebuildSelectedRequirements()
    {
        SelectedRequirements.Clear();
        if (SelectedModification != null)
            foreach (var r in Scale(SelectedModification))
                SelectedRequirements.Add(r);
    }

    /// <summary>Totals every commodity across all checked modifications into one row each.</summary>
    private void RebuildPinnedRequirements()
    {
        PinnedRequirements.Clear();
        var totals = new Dictionary<string, RequirementDisplayRow>();
        foreach (var m in Modifications.Where(m => m.IsSelected))
        {
            foreach (var r in Scale(m))
            {
                var key = ShipLockerReader.Normalize(r.Commodity);
                totals[key] = totals.TryGetValue(key, out var existing)
                    ? existing with { Required = existing.Required + r.Required }
                    : r;
            }
        }
        foreach (var row in totals.Values.OrderBy(r => r.Commodity))
            PinnedRequirements.Add(row);
        PinnedRequirementsView.Refresh();
    }

    /// <summary>Live summary of the currently ticked modifications (before applying).</summary>
    public string ModSelectionSummary
    {
        get
        {
            var selected = Modifications.Where(m => m.IsSelected).ToList();
            if (selected.Count == 0) return "No modifications selected.";

            // Aggregate requirements (× each mod's quantity), then compare to inventory.
            var need = new Dictionary<string, (int amount, bool tradeable, int have)>();
            foreach (var m in selected)
                foreach (var r in m.Requirements)
                {
                    var key = ShipLockerReader.Normalize(r.Commodity);
                    var cur = need.GetValueOrDefault(key);
                    need[key] = (cur.amount + r.Amount * m.Quantity, r.Tradeable, r.Have);
                }

            int buy = need.Values.Where(v => v.tradeable).Sum(v => Math.Max(0, v.amount - v.have));
            return $"{selected.Count} mod(s) selected, buy {buy} component units from carriers.";
        }
    }

    /// <summary>
    /// Sets each component's target to the total required by the selected modifications, then
    /// ticks every component that's now short — so Find Carriers is ready to search immediately.
    /// </summary>
    private void ApplyModTargets()
    {
        var agg = new Dictionary<string, int>();
        foreach (var m in Modifications.Where(m => m.IsSelected))
            foreach (var r in m.Mod.Requirements)
            {
                var key = ShipLockerReader.Normalize(r.Commodity);
                agg[key] = agg.GetValueOrDefault(key) + r.Amount * m.Quantity;
            }

        foreach (var row in Components)
        {
            row.Target = agg.GetValueOrDefault(ShipLockerReader.Normalize(row.Name), 0);
            row.IsSelected = row.IsShort;
        }

        int selected = Modifications.Count(m => m.IsSelected);
        int ticked = Components.Count(c => c.IsSelected);
        TargetsActive = selected > 0;
        ComponentsView.Refresh();
        Status = selected == 0
            ? "Targets cleared (no modifications selected)."
            : $"Applied {selected} modification(s) — {ticked} component(s) selected. Go to Find Carriers → Search.";
    }

    private void ClearModSelection()
    {
        foreach (var m in Modifications)
            m.IsSelected = false;
        OnPropertyChanged(nameof(ModSelectionSummary));
    }

    // ---- Import (EDOMH wishlist) -----------------------------------------------------------

    private string _importText = "";
    /// <summary>Raw text pasted or loaded from an EDOMH wishlist export.</summary>
    public string ImportText
    {
        get => _importText;
        set => SetProperty(ref _importText, value);
    }

    /// <summary>A real EDOMH wishlist is a few KB at most; this is a generous sanity cap, not a
    /// realistic limit, against accidentally (or deliberately) loading a huge file that would
    /// take a long time to parse line-by-line and briefly freeze the UI doing it.</summary>
    private const long MaxImportFileBytes = 2 * 1024 * 1024;

    private void BrowseImportFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Load EDOMH wishlist export"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var info = new FileInfo(dlg.FileName);
            if (info.Length > MaxImportFileBytes)
            {
                Status = $"{Path.GetFileName(dlg.FileName)} is too large ({info.Length / 1024} KB) " +
                         "to be a real wishlist export — not loading it.";
                return;
            }

            ImportText = File.ReadAllText(dlg.FileName);
            Status = $"Loaded {Path.GetFileName(dlg.FileName)}.";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't read file: {ex.Message}";
        }
    }

    private void ClearImport()
    {
        ImportText = "";
        ImportPreview.Clear();
    }

    /// <summary>
    /// Parses the pasted/loaded EDOMH wishlist text, sets each matching component's target to
    /// the wishlist's Required amount, and ticks every component that's still short against
    /// current inventory — mirroring <see cref="ApplyModTargets"/> so Find Carriers is ready to
    /// search immediately. Components not present in the wishlist have their target cleared, so
    /// re-importing (or switching between an import and a modification selection) replaces the
    /// prior target set rather than merging with it.
    /// </summary>
    private void ApplyImportTargets()
    {
        var entries = EdomhWishlistParser.Parse(ImportText);
        ImportPreview.Clear();

        if (entries.Count == 0)
        {
            Status = "No wishlist rows found — paste an EDOMH export or load its .txt file.";
            return;
        }

        var byNorm = new Dictionary<string, WishlistEntry>();
        foreach (var e in entries)
            byNorm[ShipLockerReader.Normalize(e.Material)] = e;

        foreach (var row in Components)
        {
            if (byNorm.TryGetValue(ShipLockerReader.Normalize(row.Name), out var entry))
            {
                row.Target = entry.Required;
                row.IsSelected = row.IsShort;
            }
            else
            {
                row.Target = 0;
                row.IsSelected = false;
            }
        }

        int matched = 0;
        foreach (var e in entries)
        {
            bool isMatch = _tradeableNorm.Contains(ShipLockerReader.Normalize(e.Material));
            if (isMatch) matched++;
            ImportPreview.Add(new ImportPreviewRow
            {
                Material = e.Material,
                Required = e.Required,
                Need = e.Need,
                Matched = isMatch
            });
        }

        int ticked = Components.Count(c => c.IsSelected);
        TargetsActive = true;
        ComponentsView.Refresh();
        Status = matched == entries.Count
            ? $"Imported {entries.Count} item(s) — {ticked} component(s) selected. Go to Find Carriers → Search."
            : $"Imported {entries.Count} item(s), {entries.Count - matched} unmatched — "
              + $"{ticked} component(s) selected. Go to Find Carriers → Search.";
    }

    // ---- Update check -----------------------------------------------------------------------

    private string? _updateUrl;

    private bool _hasUpdateAvailable;
    /// <summary>True when GitHub has a newer release than the one currently running — shows the
    /// dismissible update banner. Replaces manually commenting on the forum/Reddit threads.</summary>
    public bool HasUpdateAvailable
    {
        get => _hasUpdateAvailable;
        private set => SetProperty(ref _hasUpdateAvailable, value);
    }

    private string _updateVersionText = "";
    public string UpdateVersionText
    {
        get => _updateVersionText;
        private set => SetProperty(ref _updateVersionText, value);
    }

    private async Task CheckForUpdateAsync()
    {
        var info = await UpdateChecker.CheckAsync().ConfigureAwait(false);
        if (info == null) return;

        // The HTTP continuation resumes off the UI thread (ConfigureAwait(false) above) —
        // property changes must be marshalled back for the banner's bindings to update.
        Application.Current?.Dispatcher.Invoke(() =>
        {
            _updateUrl = info.HtmlUrl;
            UpdateVersionText = info.DisplayVersion;
            HasUpdateAvailable = true;
        });
    }

    private void OpenUpdate()
    {
        if (_updateUrl == null) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(_updateUrl) { UseShellExecute = true });
        }
        catch { /* best effort — never let a failed browser launch throw into the UI */ }
    }

    // ---- Pending search (resume-on-reopen) -------------------------------------------------

    private List<PendingSearchEntry>? _pendingSearch;

    private bool _hasPendingSearchPrompt;
    /// <summary>
    /// True when a targeted search (from Modifications or Import) was left with components still
    /// short when the app last closed — shows the "continue or start new" overlay.
    /// </summary>
    public bool HasPendingSearchPrompt
    {
        get => _hasPendingSearchPrompt;
        private set => SetProperty(ref _hasPendingSearchPrompt, value);
    }

    /// <summary>
    /// Checks for a cache left by <see cref="SavePendingSearch"/> on a previous close. Only
    /// prompts if at least one cached component still exists in the catalog and is still
    /// genuinely short against current inventory — the game state may have moved on (e.g. the
    /// user bought it outside the app, or manually in-session) since the file was written.
    /// </summary>
    private void LoadPendingSearchIfAny()
    {
        var entries = PendingSearchStore.Load();
        if (entries == null || entries.Count == 0) return;

        var stillShort = entries
            .Select(e => (Entry: e, Row: Components.FirstOrDefault(
                c => ShipLockerReader.Normalize(c.Name) == ShipLockerReader.Normalize(e.Name))))
            .Where(x => x.Row != null && x.Entry.Target > x.Row.Have)
            .Select(x => x.Entry)
            .ToList();

        if (stillShort.Count == 0)
        {
            PendingSearchStore.Save(Array.Empty<PendingSearchEntry>()); // fully resolved — clear the stale cache
            return;
        }

        _pendingSearch = stillShort;
        HasPendingSearchPrompt = true;
    }

    /// <summary>Restores the cached targets/selection — mirrors ApplyModTargets/ApplyImportTargets,
    /// but deliberately does not run a search; "Where to buy" stays empty until the user does.</summary>
    private void ContinuePendingSearch()
    {
        if (_pendingSearch != null)
        {
            foreach (var entry in _pendingSearch)
            {
                var row = Components.FirstOrDefault(
                    c => ShipLockerReader.Normalize(c.Name) == ShipLockerReader.Normalize(entry.Name));
                if (row == null) continue;
                row.Target = entry.Target;
                row.IsSelected = row.IsShort;
            }
            TargetsActive = true;
            ComponentsView.Refresh();
            int ticked = Components.Count(c => c.IsSelected);
            Status = $"Resumed previous search — {ticked} component(s) selected. Go to Find Carriers → Search.";
        }
        _pendingSearch = null;
        HasPendingSearchPrompt = false;
    }

    /// <summary>Discards the cached search entirely — the window is left in its normal default state.</summary>
    private void StartNewSearch()
    {
        _pendingSearch = null;
        HasPendingSearchPrompt = false;
        PendingSearchStore.Save(Array.Empty<PendingSearchEntry>());
    }

    /// <summary>
    /// Called from MainWindow's Closing handler: caches any still-incomplete targeted search
    /// (components with a target set that are still short) so it can be offered again next
    /// launch. Overwrites/clears the cache either way, so a fully-completed session leaves no
    /// stale prompt behind.
    /// </summary>
    public void SavePendingSearch()
    {
        var incomplete = Components
            .Where(c => c.Target > 0 && c.IsShort)
            .Select(c => new PendingSearchEntry(c.Name, c.Target))
            .ToList();
        PendingSearchStore.Save(incomplete);
    }

    /// <summary>Normalized component name -> current StillNeeded, for stamping onto listings
    /// (both a fresh search and a live inventory refresh use this).</summary>
    private Dictionary<string, int> BuildNeededByNorm() =>
        Components.ToDictionary(c => ShipLockerReader.Normalize(c.Name), c => c.StillNeeded);

    private async Task SearchSelectedAsync()
    {
        var chosen = Components.Where(c => c.IsSelected).Select(c => c.Component).ToList();
        if (chosen.Count == 0)
        {
            Status = "Tick one or more components, then Search.";
            return;
        }

        IsBusy = true;
        NoResultsFound = false;
        Listings.Clear();
        List<CarrierListing> collected = new();
        bool failed = false;
        bool rateLimited = false;
        try
        {
            Status = chosen.Count == 1
                ? $"Searching for {chosen[0].Name}…"
                : $"Searching for {chosen.Count} components…";
            try
            {
                // One combined request for every ticked component (see RelayMarketSource) —
                // this is also why a failure here can't distinguish partial success per
                // component the way a per-component loop could; it's all-or-nothing now.
                collected.AddRange(await _market.GetListingsAsync(chosen, MarketDirection.Selling));
            }
            catch (Exception ex)
            {
                failed = true;
                if (ex.Message.Contains("503") || ex.Message.Contains("429")
                    || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                    rateLimited = true;
            }

            // Recompute the Distance column relative to the commander's current system.
            await ApplyDistancesFromCurrentLocationAsync(collected);

            // Stamp each listing with how many you still need, so "Where to buy" can show the
            // buy quantity without switching back to Find Carriers.
            var neededByNorm = BuildNeededByNorm();
            foreach (var l in collected)
                l.Needed = neededByNorm.GetValueOrDefault(ShipLockerReader.Normalize(l.Component));

            // One row per carrier/station: a place selling several of the searched commodities
            // lists them together (see CarrierGroupRow) instead of repeating the row per item.
            // Insertion order here just needs to be A consistent order — actual display order is
            // governed by ListingsView's default SortDescription (freshest first, see
            // constructor), which the user can override by clicking any column header.
            var groups = collected
                .GroupBy(l => (l.StationName, l.Callsign, l.System))
                .Select(g => new CarrierGroupRow
                {
                    StationName = g.Key.StationName,
                    Callsign = g.Key.Callsign,
                    System = g.Key.System,
                    DockingAccess = g.First().DockingAccess,
                    DistanceLy = g.First().DistanceLy,
                    Items = g.OrderBy(l => l.Component).ToList()
                })
                .OrderBy(g => g.MinAge);
            foreach (var g in groups)
                Listings.Add(g);

            int carriers = Listings.Count(g => g.IsFleetCarrier);
            if (failed)
            {
                Status = rateLimited
                    ? "Couldn't reach the relay server — it may be temporarily down. Wait a minute and try again."
                    : "Couldn't fetch prices. Check your connection and try again.";
            }
            else
            {
                Status = $"{Listings.Count} places to buy ({carriers} fleet carriers) across "
                       + $"{chosen.Count} component(s).";
                NoResultsFound = Listings.Count == 0;
            }
        }
        finally
        {
            IsBusy = false;
            ListingsView.Refresh();
        }
    }

    // ---- Filters & status -----------------------------------------------------------------

    private bool _targetsActive;
    /// <summary>True once modifications have set targets; drives Target/Need column visibility.</summary>
    public bool TargetsActive
    {
        get => _targetsActive;
        private set => SetProperty(ref _targetsActive, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    private bool _noResultsFound;
    /// <summary>True once a search has completed successfully with zero listings, so the
    /// results panel can say so explicitly instead of just sitting empty.</summary>
    public bool NoResultsFound
    {
        get => _noResultsFound;
        private set => SetProperty(ref _noResultsFound, value);
    }

    private string _status = "Ready.";
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    private string _inventoryStatus = "";
    public string InventoryStatus
    {
        get => _inventoryStatus;
        set => SetProperty(ref _inventoryStatus, value);
    }

    private static string FormatLocal(DateTime? utc)
        => utc.HasValue ? utc.Value.ToLocalTime().ToString("g") : "never";
}
