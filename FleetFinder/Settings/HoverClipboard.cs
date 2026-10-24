using System.Windows.Input;
using System.Windows.Media;
using FleetView.Services;
using FleetView.Theming;

namespace FleetView.Settings;

/// <summary>What a key press over the colour list should do.</summary>
public enum HoverAction { None, Copy, Paste }

/// <summary>
/// Copying a colour from one row of the Settings list and pasting it onto another without
/// opening the picker (C7): hover a row, Ctrl+C, hover another, Ctrl+V. No WPF controls in here,
/// so the self-test can drive all of it with a pretend clipboard.
/// </summary>
/// <remarks>
/// <para>
/// Copy puts the row's draft colour on the clipboard as <c>#RRGGBB</c> text, and paste reads
/// whatever text is there, so a colour copied from a web page or another program pastes just as
/// well. Paste takes exactly what the picker's HEX field takes (<see cref="ColourMaths.TryParseHex(string?, out Color)"/>:
/// three, six or eight digits, with or without <c>#</c>, spaces round it ignored).
/// </para>
/// <para>
/// The colour goes into the draft like a picker change: the preview follows, "Unsaved changes"
/// shows, Save keeps it and Revert undoes it. The row pasted onto keeps its own alpha, as it does
/// in the picker, so pasting onto the dimming behind the dialog changes its colour and leaves it
/// see-through. Copy never writes alpha, for the same reason.
/// </para>
/// </remarks>
public sealed class HoverClipboard
{
    private readonly ThemeDraft _draft;

    public HoverClipboard(ThemeDraft draft, IClipboardText clipboard)
    {
        _draft = draft;
        Clipboard = clipboard;
    }

    /// <summary>The clipboard copy and paste use. The real one unless a test swaps it.</summary>
    public IClipboardText Clipboard { get; set; }

    public const string PastedNote = "Pasted";
    public const string NothingToPasteNote = "No colour on the clipboard";
    public const string ClipboardBusyNote = "The clipboard is busy, try again";

    /// <summary>
    /// The keys only count as copy and paste when Settings is showing, the mouse is over a row,
    /// nothing is open over the page (the picker), and the keyboard is not in a text box, where
    /// Ctrl+C and Ctrl+V have to keep copying and pasting text. Ctrl and nothing else: Ctrl+Shift+C
    /// and the rest are left alone.
    /// </summary>
    public static HoverAction ActionFor(Key key, ModifierKeys modifiers, bool settingsShowing, bool rowUnderMouse,
        bool overlayOpen, bool typingInTextBox)
    {
        if (!settingsShowing || !rowUnderMouse || overlayOpen || typingInTextBox) return HoverAction.None;
        if (modifiers != ModifierKeys.Control) return HoverAction.None;
        return key switch
        {
            Key.C => HoverAction.Copy,
            Key.V => HoverAction.Paste,
            _ => HoverAction.None,
        };
    }

    /// <summary>A colour as clipboard text: <c>#FF7500</c>, capitals, never alpha.</summary>
    public static string Format(Color colour) => $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";

    /// <summary>Clipboard text as a colour, by the HEX field's own rule.</summary>
    public static bool TryRead(string? text, out Color colour) => ColourMaths.TryParseHex(text, out colour);

    /// <summary>The pasted colour with the target's own alpha.</summary>
    public static Color KeepAlpha(Color pasted, Color target) => Color.FromArgb(target.A, pasted.R, pasted.G, pasted.B);

    /// <summary>Copies a role's draft colour. Returns the note to show by the row.</summary>
    public string Copy(string key)
    {
        var text = Format(_draft[key]);
        return Clipboard.SetText(text) ? "Copied " + text : ClipboardBusyNote;
    }

    /// <summary>Pastes the clipboard's colour into a role's draft. Returns the note to show by the row.</summary>
    public string Paste(string key)
    {
        if (!TryRead(Clipboard.GetText(), out var pasted)) return NothingToPasteNote;
        _draft.Set(key, KeepAlpha(pasted, _draft[key]));
        return PastedNote;
    }
}
