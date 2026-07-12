namespace FleetView.ViewModels;

/// <summary>One parsed row from an EDOMH wishlist import, shown in the Import tab preview grid.</summary>
public sealed class ImportPreviewRow
{
    public string Material { get; init; } = "";
    public int Required { get; init; }
    public int Need { get; init; }

    /// <summary>Whether this material matched a catalog component (and so was targeted/selected).</summary>
    public bool Matched { get; init; }
}
