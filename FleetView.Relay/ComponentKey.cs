namespace FleetView.Relay;

/// <summary>
/// Mirrors FleetFinder's <c>ShipLockerReader.Normalize</c> exactly, so a component name coming
/// off EDDN lines up with <c>Data/catalog.json</c>'s "key" field without any separate ID-mapping
/// table (both sides just lower-case + strip non-alphanumerics).
/// </summary>
public static class ComponentKey
{
    // Real component names are a few dozen characters at most. Anything past this isn't real EDDN
    // data - treated as empty rather than stack-allocated, since stackalloc sized directly to an
    // attacker-controlled string length is a remotely triggerable stack overflow (uncatchable in
    // .NET, kills the whole process) once a hostile publisher sends an oversized Name field.
    private const int MaxNameLength = 256;

    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > MaxNameLength) return "";
        Span<char> buf = stackalloc char[s.Length];
        int n = 0;
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) buf[n++] = char.ToLowerInvariant(ch);
        return new string(buf[..n]);
    }
}
