using System.Windows.Data;

namespace FleetView.Settings;

/// <summary>A row in the Settings preview's component picker.</summary>
public sealed record SampleComponent(string Group, string SubGroup, string Name, int Have, int Needed, bool Ticked, bool SellTicked)
{
    /// <summary>Same rule as the real picker: a count still needed shows in the Needed colour.</summary>
    public bool IsShort => Needed > 0;
}

/// <summary>A row in the Settings preview's Where to Buy and Where to Sell grids.</summary>
public sealed record SampleListing(string Component, int Count, string Carrier, string Access, string System, int Qty, int Price);

/// <summary>
/// Made-up rows for the Settings preview, so it looks like Find Carriers after a search without
/// touching the real inventory or the relay. Enough rows that the grids scroll, and a carrier of
/// each docking state so the good, bad and unknown colours all show.
/// </summary>
public static class PreviewSamples
{
    /// <summary>Grouped the way the real picker is: Goods, Assets (by kind), Data.</summary>
    public static ListCollectionView Components()
    {
        var view = new ListCollectionView(new List<SampleComponent>
        {
            new("Goods", "", "G-Meds", 2, 0, false, false),
            new("Goods", "", "Weapon Schematic", 1, 0, false, false),
            new("Assets", "Chemicals", "Chemical Catalyst", 6, 0, false, false),
            new("Assets", "Chemicals", "Epoxy Adhesive", 2, 3, true, false),
            new("Assets", "Circuits", "Electrical Fuse", 3, 3, true, false),
            new("Assets", "Circuits", "Memory Chip", 4, 0, false, false),
            new("Assets", "Circuits", "Micro Transformer", 1, 9, true, false),
            new("Assets", "Tech", "Graphene", 9, 0, false, true),
            new("Assets", "Tech", "Motor", 7, 0, false, false),
            new("Assets", "Tech", "Titanium Plating", 12, 0, false, true),
            new("Data", "", "Chemical Inventory", 2, 3, true, false),
            new("Data", "", "Digital Designs", 6, 0, false, false),
        });
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SampleComponent.Group)));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SampleComponent.SubGroup)));
        return view;
    }

    public static IReadOnlyList<SampleListing> Buy { get; } =
    [
        new("Epoxy Adhesive", 3, "Wandering Soul", "Unknown", "HIP 50515", 8, 999),
        new("Electrical Fuse", 3, "Deep Space Depot", "Yes", "Sol", 20, 40_000),
        new("Micro Transformer", 9, "Bargain Bin", "No", "Rhea", 5, 155),
        new("Chemical Inventory", 3, "Grumpy Dragon", "Yes", "Lalande 10797", 18, 300),
        new("Epoxy Adhesive", 3, "Coriolis Run", "Yes", "Narenses", 99, 800),
        new("Electrical Fuse", 3, "Night Market", "Unknown", "Deciat", 40, 1_200),
        new("Micro Transformer", 9, "Far Horizon", "Yes", "Maia", 12, 650),
        new("Chemical Inventory", 3, "Last Light", "No", "Colonia", 6, 210),
        new("Epoxy Adhesive", 3, "Iron Tide", "Yes", "Shinrarta Dezhra", 30, 450),
    ];

    public static IReadOnlyList<SampleListing> Sell { get; } =
    [
        new("Graphene", 9, "Deep Space Depot", "Yes", "Sol", 20, 4_000),
        new("Titanium Plating", 12, "Bargain Bin", "No", "Rhea", 5, 2_500),
        new("Graphene", 9, "Grumpy Dragon", "Unknown", "Lalande 10797", 18, 3_100),
    ];

    /// <summary>The Where to Buy row shown as clicked, so the selected row outline shows.</summary>
    public const int SelectedBuyRow = 1;
}
