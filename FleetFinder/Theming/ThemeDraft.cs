using System.ComponentModel;
using System.Windows.Media;

namespace FleetView.Theming;

/// <summary>
/// The colours being edited on the Settings page before they are saved, which is what the preview
/// shows. Knows the saved colours and the shipped ones, whether the draft differs from what is
/// saved, and does Save, Revert and Reset to defaults. No WPF controls in here, so the self-test
/// can drive all of it.
/// </summary>
/// <remarks>
/// <para>
/// "Saved" means what the app is showing, which is also what theme.json holds once a save has
/// worked. A draft lives as long as the Settings page, so it survives switching tabs, and nothing
/// writes it anywhere until Save; closing the app simply drops it.
/// </para>
/// <para>
/// The edit calls (<see cref="BeginEdit"/>, <see cref="EndEdit"/>, <see cref="CancelEdit"/>) are
/// for the picker. It changes one role many times while a handle is dragged, and Cancel has to put
/// back the colour that role had when the picker opened, not the one before the last drag step.
/// </para>
/// </remarks>
public sealed class ThemeDraft : INotifyPropertyChanged
{
    private readonly Dictionary<string, Color> _draft;
    private Dictionary<string, Color> _saved;
    private readonly string _filePath;
    private readonly Action<IReadOnlyDictionary<string, Color>> _apply;
    private string? _editKey;
    private Color _editOriginal;

    /// <param name="saved">The colours the app is showing now. Roles left out get their default.</param>
    /// <param name="filePath">Where Save writes theme.json.</param>
    /// <param name="apply">What Save calls to put the colours on the app.</param>
    public ThemeDraft(IReadOnlyDictionary<string, Color> saved, string filePath,
        Action<IReadOnlyDictionary<string, Color>> apply)
    {
        _saved = Complete(saved);
        _draft = new Dictionary<string, Color>(_saved, StringComparer.Ordinal);
        _filePath = filePath;
        _apply = apply;
    }

    /// <summary>A draft of the app's own colours, saved to the app's own theme.json.</summary>
    public static ThemeDraft ForApp() => new(ThemeService.Current, ThemeStore.FilePath, ThemeService.Apply);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised after draft colours change: with the role's key when one changed, or null when any
    /// number of them may have (Revert, Reset to defaults).
    /// </summary>
    public event Action<string?>? ColourChanged;

    /// <summary>Every role's draft colour.</summary>
    public IReadOnlyDictionary<string, Color> Colours => _draft;

    /// <summary>Every role's colour as last saved, which is what the app shows.</summary>
    public IReadOnlyDictionary<string, Color> Saved => _saved;

    public Color this[string key] => _draft[key];

    /// <summary>The draft differs from the saved colours: Save and Revert have something to do.</summary>
    public bool IsDirty => Differs(_draft, _saved);

    /// <summary>The draft is the shipped palette: Reset to defaults has nothing to do.</summary>
    public bool IsDefault => ThemeRoles.All.All(r => _draft[r.Key] == r.Default);

    /// <summary>The role the picker is open on, or null.</summary>
    public string? EditingKey => _editKey;

    /// <summary>
    /// Changes one role's draft colour. A role without alpha is always opaque, whatever comes in,
    /// because theme.json would refuse anything else for it on the next start. Returns whether
    /// the draft changed.
    /// </summary>
    public bool Set(string key, Color colour)
    {
        var role = ThemeRoles.Find(key) ?? throw new ArgumentException($"{key} is not a theme role.", nameof(key));
        if (!role.HasAlpha) colour.A = 255;
        if (_draft[key] == colour) return false;
        Change(() => _draft[key] = colour, key);
        return true;
    }

    /// <summary>Back to the last saved colours.</summary>
    public void Revert()
    {
        EndEdit();
        Replace(_saved);
    }

    /// <summary>The draft becomes the shipped palette. Nothing is saved until Save.</summary>
    public void ResetToDefaults()
    {
        EndEdit();
        Replace(ThemeRoles.Defaults());
    }

    /// <summary>
    /// The draft becomes a whole theme at once: a saved theme's Apply. Like Reset to defaults,
    /// only the draft changes, so the preview shows it, Save keeps it and Revert goes back. Roles
    /// the theme leaves out get their default, and roles without alpha are made opaque, as
    /// <see cref="Set"/> would.
    /// </summary>
    public void UseTheme(IReadOnlyDictionary<string, Color> colours)
    {
        EndEdit();
        var theme = Complete(colours);
        foreach (var role in ThemeRoles.All)
        {
            if (role.HasAlpha) continue;
            var c = theme[role.Key];
            c.A = 255;
            theme[role.Key] = c;
        }
        Replace(theme);
    }

    /// <summary>
    /// Puts the draft on the app and writes it to theme.json. Returns false if the file could not
    /// be written; the colours are on the app all the same (they stay until it closes), so they
    /// count as saved, and the reason is in the log.
    /// </summary>
    public bool Save()
    {
        EndEdit();
        bool wasDirty = IsDirty;
        var snapshot = new Dictionary<string, Color>(_draft, StringComparer.Ordinal);
        _apply(snapshot);
        bool written = ThemeStore.Save(_filePath, snapshot);
        _saved = snapshot;
        if (wasDirty) Raise(nameof(IsDirty));
        return written;
    }

    /// <summary>The picker has opened on <paramref name="key"/>: remember its colour for Cancel.
    /// An edit already open on another role is kept.</summary>
    public void BeginEdit(string key)
    {
        if (ThemeRoles.Find(key) is null) throw new ArgumentException($"{key} is not a theme role.", nameof(key));
        EndEdit();
        _editKey = key;
        _editOriginal = _draft[key];
    }

    /// <summary>The picker closed with OK or by a click outside it: keep what it set.</summary>
    public void EndEdit() => _editKey = null;

    /// <summary>The picker closed with Cancel or Escape: put that one role back as it was when
    /// the picker opened. Nothing else is touched.</summary>
    public void CancelEdit()
    {
        if (_editKey is not { } key) return;
        _editKey = null;
        Set(key, _editOriginal);
    }

    private void Replace(IReadOnlyDictionary<string, Color> colours)
    {
        if (!Differs(_draft, colours)) return;
        Change(() =>
        {
            foreach (var role in ThemeRoles.All)
                _draft[role.Key] = colours[role.Key];
        }, null);
    }

    /// <summary>Makes a change, then says what changed: the colours first, so anything listening
    /// for IsDirty already sees the new draft.</summary>
    private void Change(Action change, string? key)
    {
        bool dirty = IsDirty, isDefault = IsDefault;
        change();
        ColourChanged?.Invoke(key);
        if (dirty != IsDirty) Raise(nameof(IsDirty));
        if (isDefault != IsDefault) Raise(nameof(IsDefault));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static bool Differs(IReadOnlyDictionary<string, Color> a, IReadOnlyDictionary<string, Color> b) =>
        ThemeRoles.All.Any(r => a[r.Key] != b[r.Key]);

    private static Dictionary<string, Color> Complete(IReadOnlyDictionary<string, Color> colours)
    {
        var complete = ThemeRoles.Defaults();
        foreach (var role in ThemeRoles.All)
            if (colours.TryGetValue(role.Key, out var c))
                complete[role.Key] = c;
        return complete;
    }
}
