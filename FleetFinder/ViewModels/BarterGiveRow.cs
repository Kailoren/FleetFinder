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

    /// <summary>Bartender point value of this item - what each unit given contributes.</summary>
    public int BarterValue { get; }

    public BarterGiveRow(ComponentRow row, int barterValue)
    {
        Row = row;
        BarterValue = barterValue;
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

    public int PointsContributed => GiveAmount * BarterValue;

    public RelayCommand IncGiveCommand => new(() => GiveAmount++);
    public RelayCommand DecGiveCommand => new(() => GiveAmount--);
}
