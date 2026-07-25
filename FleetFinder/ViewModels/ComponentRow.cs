using FleetView.Models;

namespace FleetView.ViewModels;

/// <summary>
/// A catalog component joined with the player's current held count, exposing
/// the "still needed" figure that drives the shopping list.
/// </summary>
public sealed class ComponentRow : ObservableObject
{
    public Component Component { get; }

    public ComponentRow(Component component, int have)
    {
        Component = component;
        _have = have;
        _target = 0; // no target until modifications set one
    }

    public string Name => Component.Name;
    /// <summary>Top-level group: Assets, Data or Goods.</summary>
    public string Category => Component.Category;
    /// <summary>Sub-group under Assets (Chemicals/Circuits/Tech); empty for Data/Goods.</summary>
    public string SubCategory => Component.SubCategory;

    private int _target;
    /// <summary>How many are wanted. Driven by the catalog default or by selected modifications.</summary>
    public int Target
    {
        get => _target;
        set
        {
            if (SetProperty(ref _target, value))
            {
                OnPropertyChanged(nameof(StillNeeded));
                OnPropertyChanged(nameof(IsShort));
            }
        }
    }

    private bool _isSelected;
    /// <summary>Whether this component is ticked for the next "where to buy" search.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private bool _sellSelected;
    /// <summary>Whether this component is ticked for the next "where to sell" search (carriers
    /// buying it from you) - independent of <see cref="IsSelected"/>, searched at the same time.</summary>
    public bool SellSelected
    {
        get => _sellSelected;
        set => SetProperty(ref _sellSelected, value);
    }

    private int _have;
    public int Have
    {
        get => _have;
        set
        {
            if (SetProperty(ref _have, value))
            {
                OnPropertyChanged(nameof(StillNeeded));
                OnPropertyChanged(nameof(IsShort));
            }
        }
    }

    /// <summary>How many more are needed to hit the target (never negative). Settable so the grid
    /// cell can be hand-edited: typing a custom amount back-solves <see cref="Target"/> as
    /// Have + value (clamped at 0), which stays correct as Have changes later and gets naturally
    /// overwritten the next time Target is set fresh by Import or Modifications "Apply selected" -
    /// same tick-sync rule those two use (<c>IsSelected = IsShort</c>), so a manual edit ticks or
    /// unticks this row for the next search exactly like they do.</summary>
    public int StillNeeded
    {
        get => Math.Max(0, Target - Have);
        set
        {
            Target = Have + Math.Max(0, value);
            IsSelected = IsShort;
        }
    }

    /// <summary>True when the player still needs some of this component.</summary>
    public bool IsShort => StillNeeded > 0;

    private bool _isHighlighted;
    /// <summary>True while this row is the search box's current jump-to-match target - briefly
    /// outlines the row so it's easy to spot in a long grouped list.</summary>
    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetProperty(ref _isHighlighted, value);
    }
}
