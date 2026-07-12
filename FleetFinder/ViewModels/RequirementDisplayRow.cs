namespace FleetView.ViewModels;

/// <summary>
/// A requirement line for display, with the amount already scaled by the modification's
/// selected quantity. Used by the clicked-mod pane, and aggregated across mods for the
/// pinned-totals pane.
/// </summary>
public sealed record RequirementDisplayRow
{
    public string Commodity { get; init; } = "";
    public int Required { get; init; }
    public int Have { get; init; }
    public bool Tradeable { get; init; }

    public int Short => Math.Max(0, Required - Have);
    public bool Owned => Have >= Required;
    public string Source => Owned ? "" : (Tradeable ? "Buy" : "Collect");
}
