using System.Runtime.InteropServices;
using System.Windows;

namespace FleetView.Services;

/// <summary>
/// Plain text on the clipboard, behind an interface so the self-test and the screenshot harness
/// can hand the Settings page a pretend clipboard and never touch the real one.
/// </summary>
public interface IClipboardText
{
    /// <summary>The text on the clipboard, or null when there is none or it cannot be read.</summary>
    string? GetText();

    /// <summary>Puts text on the clipboard. False when another program is holding it open.</summary>
    bool SetText(string text);
}

/// <summary>The real Windows clipboard.</summary>
public sealed class SystemClipboardText : IClipboardText
{
    public static SystemClipboardText Instance { get; } = new();

    private SystemClipboardText() { }

    public string? GetText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (ExternalException)
        {
            // Another program has it open. Reading again a moment later works.
            return null;
        }
    }

    public bool SetText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}
