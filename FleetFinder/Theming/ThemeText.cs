using System.Globalization;
using System.Text;
using FleetView.Services;

namespace FleetView.Theming;

/// <summary>
/// Cleans the two pieces of text a theme carries, its name and the commander who made it, at every
/// door they come in through: typed into the save box, read from a saved theme file, or decoded
/// from a share code someone pasted.
/// </summary>
/// <remarks>
/// <para>
/// Shared themes put a stranger's text on someone else's screen, next to an Apply button. So the
/// same rules hold wherever it came from: line breaks and tabs become single spaces (a name is one
/// line on a card), anything that changes how text renders without showing itself is dropped
/// (control characters, the bidirectional overrides and isolates that can make a name read
/// backwards, zero-width joiners and every other invisible format character), and the result is
/// trimmed and capped. The cap counts UTF-16 units like <see cref="SafeText"/> does and never cuts a
/// surrogate pair in half.
/// </para>
/// <para>
/// This is stricter than <see cref="SafeText"/>, which the relay and journal paths share: it drops
/// the whole Unicode "format" category rather than a named list, which also catches the tag
/// characters that ride along invisibly after an emoji. The result still goes through
/// <see cref="SafeText.Clean"/> last, so nothing that SafeText would remove survives here.
/// </para>
/// </remarks>
public static class ThemeText
{
    /// <summary>Longest theme name kept. Long enough for "Green and Blue for night flying",
    /// short enough to sit on a small card.</summary>
    public const int MaxNameLength = 40;

    /// <summary>Longest commander name kept. The game's own limit is shorter than this.</summary>
    public const int MaxCommanderLength = 32;

    /// <summary>The theme name as it will be stored and shown. Empty means there is no usable
    /// name, and a theme without one is refused wherever it comes from.</summary>
    public static string CleanName(string? raw) => Clean(raw, MaxNameLength);

    /// <summary>
    /// The commander's name as it will be stored and shown, or empty if there is none (allowed: a
    /// theme can be shared without a credit). A leading "CMDR" is dropped, because the credit line
    /// adds its own and "CMDR CMDR Kailoren" would be the result.
    /// </summary>
    public static string CleanCommander(string? raw)
    {
        var name = Clean(raw, int.MaxValue);
        if (name.StartsWith("CMDR ", StringComparison.OrdinalIgnoreCase)) name = name[5..];
        return Clean(name, MaxCommanderLength);
    }

    private static string Clean(string? raw, int maxLength)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var sb = new StringBuilder(raw.Length);
        bool lastWasSpace = false;
        foreach (var rune in raw.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            // A lone surrogate comes through EnumerateRunes as the replacement character. Neither
            // it nor a real one is something anyone means to put in a name, so both go rather
            // than showing as a box.
            if (rune == Rune.ReplacementChar) continue;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.Surrogate or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
                continue;

            sb.Append(rune.ToString());
            lastWasSpace = false;
        }
        return SafeText.Clean(sb.ToString(), maxLength);
    }
}
