using FleetView.Theming;

namespace FleetView.Settings;

/// <summary>What pressing Save in the Save as theme box came to.</summary>
public enum SaveThemeOutcome
{
    /// <summary>The name is empty once cleaned: nothing saved, the box asks for one.</summary>
    NeedsName,

    /// <summary>A theme of that name exists: nothing saved yet, the box asks "Replace it?" and the
    /// next Save replaces it.</summary>
    AskReplace,

    Saved,

    /// <summary>The file could not be written: nothing saved, the box says so.</summary>
    Failed,
}

/// <summary>
/// The Save as theme box: a name (required), the commander to credit (prefilled from the journal,
/// may be left empty), and Save. Saves the draft's colours, which are what the preview shows,
/// whether or not they have been saved to the app. No WPF controls in here; the page copies the
/// text boxes in and shows <see cref="Message"/> and <see cref="SaveLabel"/>.
/// </summary>
/// <remarks>
/// A name that is already taken does not overwrite anything on the first press. The box says
/// which theme has that name and asks "Replace it?", and only a second Save, with the name
/// unchanged, replaces it. Changing the name takes the question back.
/// </remarks>
public sealed class SaveThemeForm
{
    public const string NeedsNameMessage = "Give the theme a name.";
    public const string FailedMessage = "The theme could not be saved in Data\\themes.";

    private readonly ThemeShelf _shelf;
    private string _name = "";
    private ThemeCard? _replacing;

    public SaveThemeForm(ThemeShelf shelf, string commander = "")
    {
        _shelf = shelf;
        Commander = ThemeText.CleanCommander(commander);
    }

    /// <summary>The name as typed. Changing it to a different name takes back a replace question.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (_replacing != null && !string.Equals(ThemeText.CleanName(value), ThemeText.CleanName(_name), StringComparison.OrdinalIgnoreCase))
            {
                _replacing = null;
                Message = "";
            }
            _name = value ?? "";
        }
    }

    /// <summary>The commander to credit, as typed. Empty is allowed.</summary>
    public string Commander { get; set; }

    /// <summary>The name as it will be saved.</summary>
    public string CleanName => ThemeText.CleanName(_name);

    /// <summary>The commander as they will be credited.</summary>
    public string CleanCommander => ThemeText.CleanCommander(Commander);

    /// <summary>The theme the next Save would replace, while the box is asking.</summary>
    public ThemeCard? Replacing => _replacing;

    public bool AskingReplace => _replacing != null;

    /// <summary>What the box says under the fields, or empty.</summary>
    public string Message { get; private set; } = "";

    /// <summary>The Save button's text: "Replace" while the box is asking.</summary>
    public string SaveLabel => AskingReplace ? "Replace" : "Save";

    /// <summary>The card saved, once <see cref="Submit"/> has said <see cref="SaveThemeOutcome.Saved"/>.</summary>
    public ThemeCard? Saved { get; private set; }

    /// <summary>
    /// Save pressed (or Enter). <paramref name="snapshot"/> draws the preview's picture and is only
    /// called when something is really going to be saved.
    /// </summary>
    public SaveThemeOutcome Submit(Func<byte[]?> snapshot)
    {
        if (CleanName.Length == 0)
        {
            Message = NeedsNameMessage;
            return SaveThemeOutcome.NeedsName;
        }

        if (_replacing == null && _shelf.Find(CleanName) is { } existing)
        {
            _replacing = existing;
            Message = $"You already have a theme called \"{existing.Name}\". Replace it?";
            return SaveThemeOutcome.AskReplace;
        }

        // The card being replaced may have been deleted meanwhile; then this is a new theme.
        var target = _replacing != null && _shelf.Cards.Contains(_replacing) ? _replacing : null;
        var card = _shelf.SaveDraft(CleanName, CleanCommander, snapshot(), target);
        if (card == null)
        {
            Message = FailedMessage;
            return SaveThemeOutcome.Failed;
        }
        Saved = card;
        _replacing = null;
        Message = "";
        return SaveThemeOutcome.Saved;
    }
}
