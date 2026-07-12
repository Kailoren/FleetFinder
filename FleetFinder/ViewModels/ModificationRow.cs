using FleetView.Models;
using FleetView.Services;

namespace FleetView.ViewModels;

/// <summary>One commodity line inside a modification's detail view.</summary>
public sealed class ModRequirementRow
{
    public string Commodity { get; init; } = "";
    public int Amount { get; init; }
    public int Have { get; init; }
    /// <summary>True if this commodity can be bought from fleet carriers (a catalog component).</summary>
    public bool Tradeable { get; init; }

    public int Short => Math.Max(0, Amount - Have);
    public bool Owned => Have >= Amount;
    /// <summary>"Buy" for tradeable shortfalls, "Collect" for non-tradeable, "" when owned.</summary>
    public string Source => Owned ? "" : (Tradeable ? "Buy" : "Collect");
}

/// <summary>A modification in the picker, with its requirements resolved against inventory.</summary>
public sealed class ModificationRow : ObservableObject
{
    public Modification Mod { get; }
    public string Name => Mod.Name;
    public string Kind => Mod.Kind;
    public IReadOnlyList<ModRequirementRow> Requirements { get; }

    public ModificationRow(Modification mod,
        IReadOnlyDictionary<string, int> haveByNorm, IReadOnlySet<string> tradeableNorm)
    {
        Mod = mod;
        Requirements = mod.Requirements.Select(r =>
        {
            var key = ShipLockerReader.Normalize(r.Commodity);
            return new ModRequirementRow
            {
                Commodity = r.Commodity,
                Amount = r.Amount,
                Have = haveByNorm.GetValueOrDefault(key),
                Tradeable = tradeableNorm.Contains(key)
            };
        }).ToList();
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private int _quantity = 1;
    /// <summary>How many times you want to apply this modification (1–99).</summary>
    public int Quantity
    {
        get => _quantity;
        set => SetProperty(ref _quantity, Math.Clamp(value, 1, 99));
    }

    public RelayCommand IncQuantityCommand => new(() => Quantity++);
    public RelayCommand DecQuantityCommand => new(() => Quantity--);

    public int ItemsOwned => Requirements.Count(r => r.Owned);
    public int ItemsTotal => Requirements.Count;

    /// <summary>True when you already hold everything needed for at least one instance.</summary>
    public bool AllOwned => CanDoCount >= 1;

    /// <summary>How many full instances of this mod you can complete from current inventory.</summary>
    public int CanDoCount
    {
        get
        {
            var reqs = Requirements.Where(r => r.Amount > 0).ToList();
            return reqs.Count == 0 ? 0 : reqs.Min(r => r.Have / r.Amount);
        }
    }

    /// <summary>Recomputes have-derived values after inventory changes.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(ItemsOwned));
        OnPropertyChanged(nameof(AllOwned));
        OnPropertyChanged(nameof(CanDoCount));
        OnPropertyChanged(nameof(Requirements));
    }
}
