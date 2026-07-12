using System.ComponentModel;

namespace FleetView.Models;

/// <summary>
/// A single fleet-carrier (or station) market offer for a component, as reported by
/// FleetView.Relay (our own EDDN-derived database).
/// </summary>
public sealed class CarrierListing : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Which component this listing is for (display name).</summary>
    public string Component { get; set; } = "";

    /// <summary>Carrier/station display name, e.g. "Mutter".</summary>
    public string StationName { get; set; } = "";

    /// <summary>Carrier callsign if present, e.g. "KLL-58Z"; empty for normal stations.</summary>
    public string Callsign { get; set; } = "";

    /// <summary>Star system the carrier was last seen in.</summary>
    public string System { get; set; } = "";

    /// <summary>True if this is a fleet carrier (has a callsign).</summary>
    public bool IsFleetCarrier => !string.IsNullOrEmpty(Callsign);

    /// <summary>"Selling" (carrier sells to you) or "Buying" (carrier buys from you).</summary>
    public string Direction { get; set; } = "";

    /// <summary>Units in stock (when selling) or demand (when buying).</summary>
    public int Amount { get; set; }

    private int _needed;
    /// <summary>
    /// How many of this component you still need. Set at search time and kept live afterward
    /// (see MainViewModel.RefreshInventory), so this updates automatically as you buy things
    /// from a carrier instead of only refreshing on the next search.
    /// </summary>
    public int Needed
    {
        get => _needed;
        set
        {
            if (_needed == value) return;
            _needed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Needed)));
        }
    }

    /// <summary>Unit price in credits.</summary>
    public long Price { get; set; }

    /// <summary>Distance in light years from the reference system, if the source supplied one.</summary>
    public double? DistanceLy { get; set; }

    /// <summary>How long ago the data was reported (e.g. "11 minutes ago").</summary>
    public string UpdatedText { get; set; } = "";

    /// <summary>Absolute time the data was last reported, local time.</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Best-effort age of the data, for sorting.</summary>
    public TimeSpan? Age { get; set; }

    /// <summary>
    /// Whether you can dock: "Yes" (open), "No" (restricted) or "Unknown"
    /// (no docking-access info has been observed for this carrier yet).
    /// </summary>
    public string DockingAccess { get; set; } = "Unknown";
}
