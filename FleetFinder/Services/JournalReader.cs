using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FleetView.Services;

/// <summary>The commander's current star system and its galactic coordinates.</summary>
public sealed record PlayerLocation(string System, double X, double Y, double Z);

/// <summary>
/// Reads the commander's current location from the Elite Dangerous journal files.
/// The most recent event carrying a "StarPos" (FSDJump / CarrierJump / Location) wins.
/// </summary>
public sealed partial class JournalReader
{
    /// <summary>
    /// Longest journal line this will parse. Real events are a few hundred bytes; the file is
    /// written by another process and this app cannot assume the line ends where the format says
    /// it should. Anything past this is consumed and discarded rather than materialised - see
    /// <see cref="ReadBoundedLine"/> for why the length cannot simply be checked afterwards.
    /// </summary>
    private const int MaxLineLength = 64 * 1024;

    /// <summary>
    /// Longest star system name accepted. Real ones run to about forty characters. This value
    /// leaves the parse boundary as the one place the length is settled, so nothing downstream has
    /// to wonder how long a system name can be.
    /// </summary>
    private const int MaxSystemNameLength = 128;

    private readonly string _dir;

    /// <param name="journalDirectory">
    /// Where the game writes its logs. Null, blank, or anything not a rooted path is taken as "no
    /// journal directory", and <see cref="GetCurrentLocation"/> then reports no location rather
    /// than resolving a relative path against the process's working directory.
    /// </param>
    public JournalReader(string? journalDirectory)
    {
        _dir = journalDirectory ?? "";
    }

    public PlayerLocation? GetCurrentLocation()
    {
        if (string.IsNullOrWhiteSpace(_dir)) return null;
        if (!Path.IsPathRooted(_dir)) return null;
        if (!Directory.Exists(_dir)) return null;

        string[] files;
        try
        {
            files = Directory.GetFiles(_dir, "Journal.*.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Note($"Journal directory could not be listed ({ex.GetType().Name}).");
            return null;
        }

        // Newest journal first; use the last StarPos in the first file that has one.
        //
        // Ordered by the timestamp and part number the game puts in the filename, not by the
        // file's modified time. The name is what the game itself recorded when it opened the
        // session; the modified time is a filesystem property that a copy, a restore, a sync
        // client or a backup tool can set to anything, and ordering by it alone let a stale file
        // be preferred over the one actually being written. Modified time stays as the tiebreak
        // for names that do not parse.
        var ordered = files
            .Select(path => (Path: path, Stamp: SessionStamp(path), Written: LastWriteOrMin(path)))
            .OrderByDescending(f => f.Stamp.Time)
            .ThenByDescending(f => f.Stamp.Part)
            .ThenByDescending(f => f.Written)
            .Select(f => f.Path);

        foreach (var file in ordered)
        {
            var loc = ScanFile(file);
            if (loc != null) return loc;
        }
        return null;
    }

    /// <summary>
    /// The session time and part number encoded in a journal filename, or
    /// <see cref="DateTime.MinValue"/> when the name does not match either format the game has
    /// used ("Journal.2026-08-03T142530.01.log" and the older "Journal.260803142530.01.log").
    /// </summary>
    private static (DateTime Time, int Part) SessionStamp(string path)
    {
        var match = JournalNamePattern().Match(Path.GetFileName(path));
        if (!match.Success) return (DateTime.MinValue, 0);

        var stamp = match.Groups["stamp"].Value;
        var format = stamp.Length == 12 ? "yyMMddHHmmss" : "yyyy-MM-dd'T'HHmmss";
        if (!DateTime.TryParseExact(stamp, format, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time))
            return (DateTime.MinValue, 0);

        int.TryParse(match.Groups["part"].Value, NumberStyles.None, CultureInfo.InvariantCulture,
            out int part);
        return (time, part);
    }

    [GeneratedRegex(@"^Journal\.(?<stamp>\d{4}-\d{2}-\d{2}T\d{6}|\d{12})\.(?<part>\d+)\.log$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JournalNamePattern();

    private static DateTime LastWriteOrMin(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private static PlayerLocation? ScanFile(string path)
    {
        PlayerLocation? found = null;
        bool anyOversize = false;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);

            while (ReadBoundedLine(sr, out var line, out bool oversize))
            {
                anyOversize |= oversize;
                if (oversize) continue;
                if (!line.Contains("StarPos", StringComparison.Ordinal)) continue;

                var loc = TryParseLocation(line);
                if (loc != null) found = loc; // keep updating -> last wins
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            // The file being locked mid-write is routine and this is a best-effort read, so it
            // stays non-fatal - but it is recorded now. Swallowing it silently meant a journal
            // this app could never read looked exactly like a commander who had not jumped yet.
            DiagnosticLog.Note(
                $"Journal file could not be read ({ex.GetType().Name}): {Path.GetFileName(path)}");
        }

        if (anyOversize)
            DiagnosticLog.Note(
                $"Skipped one or more journal lines over {MaxLineLength:N0} characters in " +
                Path.GetFileName(path));

        return found;
    }

    /// <summary>
    /// Reads one line into <paramref name="line"/>, returning false at end of file. A line longer
    /// than <see cref="MaxLineLength"/> is consumed to its end and reported through
    /// <paramref name="oversize"/> with <paramref name="line"/> left empty.
    /// </summary>
    /// <remarks>
    /// StreamReader.ReadLine sizes its own buffer from the content, so checking a line's length
    /// after reading it is a check that happens one allocation too late: by then the string exists
    /// at whatever size the file dictated, and a single unterminated line is enough to make that
    /// unbounded. The bound has to be applied while reading, which is what this does.
    /// </remarks>
    private static bool ReadBoundedLine(StreamReader reader, out string line, out bool oversize)
    {
        line = "";
        oversize = false;

        int ch = reader.Read();
        if (ch < 0) return false;

        var sb = new StringBuilder(256);
        while (ch >= 0 && ch != '\n')
        {
            if (ch != '\r')
            {
                if (sb.Length < MaxLineLength) sb.Append((char)ch);
                else oversize = true;
            }
            ch = reader.Read();
        }

        if (!oversize) line = sb.ToString();
        return true;
    }

    private static PlayerLocation? TryParseLocation(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("StarPos", out var sp)
                || sp.ValueKind != JsonValueKind.Array) return null;

            // Checked before enumerating, not after. Select(...).ToArray() materialised the whole
            // array first and only then compared its length to three, so the guard could never
            // refuse an oversized one - it could only measure it once it already existed.
            if (sp.GetArrayLength() != 3) return null;

            var c = new double[3];
            int i = 0;
            foreach (var el in sp.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out double v))
                    return null;
                c[i++] = v;
            }

            string system = root.TryGetProperty("StarSystem", out var ss)
                            && ss.ValueKind == JsonValueKind.String
                ? CleanSystemName(ss.GetString())
                : "";

            return new PlayerLocation(system, c[0], c[1], c[2]);
        }
        catch (JsonException)
        {
            return null; // not a JSON event line, or a partially-written one
        }
    }

    /// <summary>
    /// Bounds and cleans a star system name at the point it enters the application.
    /// </summary>
    /// <remarks>
    /// This file has no dangerous call in it: it is a parser, and the value goes straight into a
    /// record. That is the reason to do it here. Everything downstream - the "Distances from ..."
    /// label, the EDSM query this name is escaped into, the normaliser that sizes a buffer from
    /// its length - treats a PlayerLocation as this app's own data, so this is where it stops
    /// being a string from a file another process wrote and becomes something with known limits.
    /// </remarks>
    private static string CleanSystemName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var span = raw.Length <= MaxSystemNameLength
            ? raw.AsSpan()
            : raw.AsSpan(0, MaxSystemNameLength);

        var sb = new StringBuilder(span.Length);
        foreach (var ch in span)
            if (!char.IsControl(ch)) sb.Append(ch);

        return sb.ToString().Trim();
    }
}
