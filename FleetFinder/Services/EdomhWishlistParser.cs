using System.Text.RegularExpressions;

namespace FleetView.Services;

/// <summary>One parsed row from an EDOMH wishlist export.</summary>
public sealed record WishlistEntry(string Material, int Required, int Need);

/// <summary>
/// Parses a wishlist exported from Elite Dangerous Odyssey Materials Helper (EDOMH) — a
/// whitespace-aligned table of "Material / Available BP+S / Available FC / Available Total /
/// Required / Need" columns. Only the trailing five whitespace-separated integers on a line are
/// significant; everything before them (trimmed) is the material name, so exact column widths
/// don't matter and the header row (no trailing digits) is skipped automatically.
/// </summary>
public static class EdomhWishlistParser
{
    private static readonly Regex RowPattern = new(
        @"^(?<name>.+?)\s+(?<bps>\d+)\s+(?<fc>\d+)\s+(?<total>\d+)\s+(?<required>\d+)\s+(?<need>\d+)\s*$",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    // A real wishlist line (material name + five numeric columns) is well under 200 characters.
    // Capping it bounds this regex's worst case (lazy .+? backtracking against a pathologically
    // long single line) to a fixed small cost regardless of how much text gets pasted/loaded.
    private const int MaxLineLength = 500;

    public static List<WishlistEntry> Parse(string text)
    {
        var result = new List<WishlistEntry>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLineLength) continue;

            Match m;
            try
            {
                m = RowPattern.Match(line);
            }
            catch (RegexMatchTimeoutException)
            {
                continue; // shouldn't happen given MaxLineLength, but never let one bad line hang
            }
            if (!m.Success) continue;

            var name = m.Groups["name"].Value.Trim();
            if (name.Length == 0) continue;

            // TryParse rather than Parse: a line with an implausibly large number (more digits
            // than fit in an int) would otherwise throw OverflowException and abort the whole
            // import instead of just skipping that one line.
            if (!int.TryParse(m.Groups["required"].Value, out var required)) continue;
            if (!int.TryParse(m.Groups["need"].Value, out var need)) continue;

            result.Add(new WishlistEntry(name, required, need));
        }
        return result;
    }
}
