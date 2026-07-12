namespace FleetView.Relay;

/// <summary>
/// Mirrors FleetFinder's <c>ShipLockerReader.Normalize</c> exactly, so a component name coming
/// off EDDN lines up with <c>Data/catalog.json</c>'s "key" field without any separate ID-mapping
/// table (both sides just lower-case + strip non-alphanumerics).
/// </summary>
public static class ComponentKey
{
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        Span<char> buf = stackalloc char[s.Length];
        int n = 0;
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) buf[n++] = char.ToLowerInvariant(ch);
        return new string(buf[..n]);
    }
}
