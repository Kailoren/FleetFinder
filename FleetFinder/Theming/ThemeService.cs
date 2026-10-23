using System.Windows;
using System.Windows.Media;

namespace FleetView.Theming;

/// <summary>
/// Puts the theme's brushes where the XAML finds them: one frozen <see cref="SolidColorBrush"/>
/// per role, under the role's key, in the application's resources.
/// </summary>
/// <remarks>
/// <para>
/// Every colour in MainWindow.xaml is <c>{DynamicResource RoleKey}</c>, so recolouring anything is
/// "put a new brush under that key" and WPF updates whatever uses it. Brushes are replaced, never
/// changed in place: they are frozen (shared across threads and cheaper to render), and a frozen
/// brush cannot be changed anyway.
/// </para>
/// <para>
/// The Settings preview uses the same mechanism one level down. It gets its own
/// <see cref="ResourceDictionary"/> from <see cref="BuildDictionary"/>, holding draft brushes under
/// the same keys, and since a DynamicResource lookup walks up from the element that asked, anything
/// inside the preview finds the draft before it reaches the application. That holds through the
/// window's implicit styles, style triggers and template triggers; the screenshot harness checks
/// it on the real window. One catch: a value the window resolves for itself and hands down by
/// inheritance (its Background, its text colour) is resolved at the window, so the preview root
/// has to set those again inside its own scope.
/// </para>
/// </remarks>
public static class ThemeService
{
    /// <summary>The colours the application is showing now, every role present.</summary>
    public static IReadOnlyDictionary<string, Color> Current { get; private set; } = ThemeRoles.Defaults();

    /// <summary>
    /// Startup: reads theme.json (defaults for anything missing or broken) and applies it. Called
    /// from <see cref="App.OnStartup"/> before the window exists. Never throws over the file;
    /// <see cref="ThemeStore.Load()"/> already turned every problem into a default.
    /// </summary>
    public static void ApplySaved() => Apply(ThemeStore.Load());

    /// <summary>
    /// Makes these the application's colours. Roles left out of <paramref name="colours"/> get
    /// their default; unknown keys are ignored.
    /// </summary>
    public static void Apply(IReadOnlyDictionary<string, Color> colours)
    {
        var complete = Complete(colours);
        if (Application.Current is { } app)
            ApplyTo(app.Resources, complete);
        Current = complete;
    }

    /// <summary>
    /// A new dictionary with a brush for every role (and the SystemColors aliases), for an element
    /// that should show colours of its own: the Settings preview sets this as its Resources.
    /// </summary>
    public static ResourceDictionary BuildDictionary(IReadOnlyDictionary<string, Color> colours)
    {
        var dictionary = new ResourceDictionary();
        ApplyTo(dictionary, Complete(colours));
        return dictionary;
    }

    /// <summary>
    /// Writes a brush for every role into <paramref name="target"/>, skipping roles whose brush
    /// there is already that colour.
    /// </summary>
    /// <remarks>
    /// The skip is not an optimisation for its own sake: replacing an entry in the application's
    /// resources makes WPF walk every window looking for references to it, so applying a theme
    /// that changed two colours should cost two of those walks, not one per role.
    /// </remarks>
    public static void ApplyTo(ResourceDictionary target, IReadOnlyDictionary<string, Color> colours)
    {
        foreach (var role in ThemeRoles.All)
            SetBrush(target, role.Key, colours.TryGetValue(role.Key, out var c) ? c : role.Default);
    }

    /// <summary>
    /// Replaces one role's brush in <paramref name="target"/>, and any SystemColors key that
    /// follows that role. For the picker's live drag, one role at a time.
    /// </summary>
    public static void SetBrush(ResourceDictionary target, string roleKey, Color colour)
    {
        if (target[roleKey] is SolidColorBrush existing && existing.Color == colour) return;

        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        target[roleKey] = brush;
        foreach (var (resourceKey, aliasOf) in ThemeRoles.SystemAliases)
            if (aliasOf == roleKey)
                target[resourceKey] = brush;
    }

    private static Dictionary<string, Color> Complete(IReadOnlyDictionary<string, Color> colours)
    {
        var complete = ThemeRoles.Defaults();
        foreach (var role in ThemeRoles.All)
            if (colours.TryGetValue(role.Key, out var c))
                complete[role.Key] = c;
        return complete;
    }
}
