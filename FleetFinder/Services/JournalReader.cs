using System.IO;
using System.Text.Json;

namespace FleetView.Services;

/// <summary>The commander's current star system and its galactic coordinates.</summary>
public sealed record PlayerLocation(string System, double X, double Y, double Z);

/// <summary>
/// Reads the commander's current location from the Elite Dangerous journal files.
/// The most recent event carrying a "StarPos" (FSDJump / CarrierJump / Location) wins.
/// </summary>
public sealed class JournalReader
{
    private readonly string _dir;

    public JournalReader(string journalDirectory)
    {
        _dir = journalDirectory;
    }

    public PlayerLocation? GetCurrentLocation()
    {
        if (!Directory.Exists(_dir)) return null;

        // Newest journal first; use the last StarPos in the first file that has one.
        var files = Directory.GetFiles(_dir, "Journal.*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc);

        foreach (var file in files)
        {
            var loc = ScanFile(file);
            if (loc != null) return loc;
        }
        return null;
    }

    private static PlayerLocation? ScanFile(string path)
    {
        PlayerLocation? found = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (!line.Contains("StarPos", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("StarPos", out var sp)
                        || sp.ValueKind != JsonValueKind.Array) continue;

                    var c = sp.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    if (c.Length != 3) continue;

                    string system = root.TryGetProperty("StarSystem", out var ss)
                        ? ss.GetString() ?? "" : "";
                    found = new PlayerLocation(system, c[0], c[1], c[2]); // keep updating -> last wins
                }
                catch { /* skip malformed line */ }
            }
        }
        catch { /* file locked / unreadable */ }
        return found;
    }
}
