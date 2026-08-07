using System.Text;

namespace FleetView.Services;

/// <summary>
/// Reduces a string that arrived from outside this app to one that is safe to display, store and
/// pass on.
/// </summary>
/// <remarks>
/// <para>
/// Three places needed this and each had grown its own version: the relay's answers, the game's
/// journal, and the fields written into the log. They disagreed about which characters to remove
/// and none of them handled the cut landing inside a surrogate pair, so the same hostile name was
/// treated three different ways depending on which door it came in through. One implementation
/// means one answer to "what is a name allowed to contain here".
/// </para>
/// <para>
/// What is removed is the set of characters that change how text is rendered without being visible
/// in it: control characters, the bidirectional overrides and isolates, the line and paragraph
/// separators that <see cref="char.IsControl"/> does not cover, and the zero-width joiners. A
/// system name shown next to a price the user is about to fly to should read as what it is.
/// </para>
/// </remarks>
internal static class SafeText
{
    /// <summary>
    /// Strips invisible formatting characters and truncates to <paramref name="maxLength"/>,
    /// counting in UTF-16 code units but never cutting a surrogate pair in half.
    /// </summary>
    /// <remarks>
    /// Filtering happens before truncation, so removed characters do not consume budget and the
    /// result is as long as it can legitimately be.
    /// </remarks>
    public static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var sb = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var ch in value)
        {
            if (IsInvisible(ch)) continue;
            if (sb.Length >= maxLength) break;
            sb.Append(ch);
        }

        // A high surrogate in the last position means its pair was cut off by the length limit, and
        // a lone surrogate is not well-formed text - it is dropped rather than passed on to a grid
        // cell, a query string or a buffer sized from it.
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Replaces the line breaks in a value about to be written into a line-oriented file, so it
    /// cannot pose as further entries.
    /// </summary>
    /// <remarks>
    /// Used for text this app did not write - an exception message can quote a server's response
    /// body - going into the diagnostic log, where every entry is one timestamped line and a
    /// newline in the middle of one is indistinguishable from the start of the next.
    /// </remarks>
    public static string SingleLine(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\r' or '\n' or LineSeparator or ParagraphSeparator) sb.Append("\\n");
            else if (char.IsControl(ch)) sb.Append(' ');
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    // Named rather than written inline, because these are invisible: a literal one in the source
    // below would be a line nobody can read or review.
    private const char LineSeparator = '\u2028';
    private const char ParagraphSeparator = '\u2029';
    private const char ArabicLetterMark = '\u061C';
    private const char ZeroWidthSpace = '\u200B';
    private const char ZeroWidthNonJoiner = '\u200C';
    private const char ZeroWidthJoiner = '\u200D';
    private const char LeftToRightMark = '\u200E';
    private const char RightToLeftMark = '\u200F';
    private const char FirstEmbedding = '\u202A';   // LRE, through RLO
    private const char LastEmbedding = '\u202E';
    private const char FirstIsolate = '\u2066';     // LRI, through PDI
    private const char LastIsolate = '\u2069';
    private const char ZeroWidthNoBreakSpace = '\uFEFF';

    /// <summary>
    /// True for a character that changes how surrounding text renders without being visible itself.
    /// </summary>
    /// <remarks>
    /// <see cref="char.IsControl"/> covers only U+0000-U+001F and U+007F-U+009F, so the separators
    /// and format characters below have to be named individually - they are in different Unicode
    /// categories despite doing the same kind of thing to a rendered line.
    /// </remarks>
    private static bool IsInvisible(char ch) =>
        char.IsControl(ch)
        || ch is LineSeparator or ParagraphSeparator
        || ch is ArabicLetterMark
        || ch is ZeroWidthSpace or ZeroWidthNonJoiner or ZeroWidthJoiner
        || ch is LeftToRightMark or RightToLeftMark
        || ch is >= FirstEmbedding and <= LastEmbedding
        || ch is >= FirstIsolate and <= LastIsolate
        || ch is ZeroWidthNoBreakSpace;
}
