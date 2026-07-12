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
    /// <summary>Whether this component is ticked for the next price search.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
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

    /// <summary>How many more are needed to hit the target (never negative).</summary>
    public int StillNeeded => Math.Max(0, Target - Have);

    /// <summary>True when the player still needs some of this component.</summary>
    public bool IsShort => StillNeeded > 0;
}
