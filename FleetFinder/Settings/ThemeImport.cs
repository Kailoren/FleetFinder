using FleetView.Theming;

namespace FleetView.Settings;

/// <summary>Where the Import code box has got to with what was pasted.</summary>
public enum ImportState
{
    /// <summary>Nothing pasted yet.</summary>
    Empty,

    /// <summary>Something pasted that is not a usable code. <see cref="ThemeImport.Message"/> says why.</summary>
    Problem,

    /// <summary>A good code: the box shows the theme's picture and credit, and it can be added.</summary>
    Ready,

    /// <summary>A good code for a theme that is already saved, every colour the same.</summary>
    AlreadyHave,
}

/// <summary>
/// The Import code box: paste a share code, see what theme it is (a picture drawn here from its
/// colours, and the credit line), then Add to my themes. No WPF controls in here.
/// </summary>
/// <remarks>
/// <para>
/// A pasted code is a stranger's text. Everything shown comes from <see cref="ThemeCode.Decode"/>,
/// whose name and commander have been through <see cref="ThemeText"/> and whose colours through
/// theme.json's own check; the pasted text itself is never shown anywhere but the box it was
/// pasted into, and the page puts all of it in plain text, never markup.
/// </para>
/// <para>
/// A bad code shows <see cref="ThemeCodeResult.Message"/> as it is and nothing else happens.
/// Nothing reaches the draft from here: an added theme is a card like any other, and its Apply
/// button is how it gets used.
/// </para>
/// </remarks>
public sealed class ThemeImport
{
    public const string AlreadyHaveMessage = "You already have this theme.";
    public const string NotAddedMessage = "The theme could not be saved in Data\\themes.";

    private readonly ThemeShelf _shelf;

    public ThemeImport(ThemeShelf shelf)
    {
        _shelf = shelf;
        Read(null);
    }

    public ImportState State { get; private set; }

    /// <summary>The theme the code holds, while it is <see cref="ImportState.Ready"/> or
    /// <see cref="ImportState.AlreadyHave"/>.</summary>
    public SharedTheme? Theme { get; private set; }

    /// <summary>What to say about the paste: ThemeCode's sentence for a problem (and for an empty
    /// box), the already-saved note, or empty for a code that can be added.</summary>
    public string Message { get; private set; } = "";

    /// <summary>"Green and Blue theme - CMDR BlueGreen", or just the name; empty without a theme.</summary>
    public string Credit => Theme is { } t ? ThemeShelf.CreditLine(t.Name, t.Commander) : "";

    public bool CanAdd => State == ImportState.Ready;

    /// <summary>The card added, once <see cref="Add"/> has worked.</summary>
    public ThemeCard? Added { get; private set; }

    /// <summary>Reads the box's text again, as it is now.</summary>
    public void Read(string? text)
    {
        var result = ThemeCode.Decode(text);
        Theme = result.Theme;
        if (!result.Ok)
        {
            State = result.Problem == ThemeCodeProblem.Empty ? ImportState.Empty : ImportState.Problem;
            Message = result.Message;
            return;
        }
        bool have = _shelf.Has(result.Theme!);
        State = have ? ImportState.AlreadyHave : ImportState.Ready;
        Message = have ? AlreadyHaveMessage : "";
    }

    /// <summary>
    /// Add to my themes: saves the theme with the picture drawn for it. Returns the new card, or
    /// null when there is nothing to add or it could not be saved (then <see cref="Message"/> says so).
    /// </summary>
    public ThemeCard? Add(byte[]? png)
    {
        if (!CanAdd || Theme == null) return null;
        var card = _shelf.Add(Theme, png);
        if (card == null)
        {
            Message = NotAddedMessage;
            return null;
        }
        Added = card;
        State = ImportState.AlreadyHave;
        Message = AlreadyHaveMessage;
        return card;
    }
}
