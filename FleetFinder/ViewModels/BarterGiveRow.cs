namespace FleetView.ViewModels;

/// <summary>
/// One Assets item (Chemicals/Circuits/Tech) available to trade in for the currently selected
/// "want" item, with an editable amount capped at how many you actually hold. Wraps a live
/// <see cref="ComponentRow"/> rather than copying its Have - XAML binds straight through
/// "Row.Have"/"Row.Name", which WPF's property-path binding keeps in sync automatically via
/// ComponentRow's own INotifyPropertyChanged, no manual forwarding needed here.
/// </summary>
public sealed class BarterGiveRow : ObservableObject
{
    public ComponentRow Row { get; }

    /// <summary>Bartender point cost to acquire this item as the "want" - used for
    /// <see cref="MainViewModel.BarterWantValue"/> when this row is selected. NOT what giving up
    /// this item nets you; see <see cref="SellValue"/> for that.</summary>
    public int BuyValue { get; }

    /// <summary>Bartender point value gained per unit when this item is given/traded in - always
    /// lower than <see cref="BuyValue"/>. Drives <see cref="PointsContributed"/>.</summary>
    public int SellValue { get; }

    /// <summary>Flat credit price the bartender pays for this item, informational only.</summary>
    public int CreditValue => Row.Component.CreditValue ?? 0;

    /// <summary>Chemicals/Circuits/Tech - drives the grouped display and which other rows are
    /// zeroed out when this one is picked as the "want" item.</summary>
    public string SubCategory => Row.SubCategory;

    public BarterGiveRow(ComponentRow row, int buyValue, int sellValue)
    {
        Row = row;
        BuyValue = buyValue;
        SellValue = sellValue;
    }

    private int _giveAmount;
    /// <summary>How many of this item to trade in, 0 to however many you currently hold.</summary>
    public int GiveAmount
    {
        get => _giveAmount;
        set
        {
            int clamped = Math.Clamp(value, 0, Row.Have);
            if (SetProperty(ref _giveAmount, clamped))
                OnPropertyChanged(nameof(PointsContributed));
        }
    }

    /// <summary>Points this row contributes toward the want item if traded in now - computed off
    /// the SELL value (trade-in rate), not the BUY value.</summary>
    public int PointsContributed => GiveAmount * SellValue;

    public RelayCommand IncGiveCommand => new(() => GiveAmount++);
    public RelayCommand DecGiveCommand => new(() => GiveAmount--);
}
