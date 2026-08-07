using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace FleetView.Services;

/// <summary>
/// The app's single log file (Data\fleetview-crash.log, next to the exe). Written to by the
/// unhandled-exception handlers in <see cref="App"/> and by the market-fetch path in
/// MainViewModel, so that a "prices won't load" report arrives with something concrete attached
/// - which host was called, how the call failed, and how long it took - instead of only the
/// on-screen "couldn't fetch prices", which is identical for every possible cause.
///
/// Logging is strictly best effort: every entry point swallows its own errors, because failing
/// to record diagnostics must never become a second fault stacked on the one being recorded.
/// </summary>
public static class DiagnosticLog
{
    /// <summary>
    /// Fetch failures recur (a user in a broken state can retry a search all evening), unlike the
    /// one-off crashes this file used to hold, so it needs a ceiling. Past this the oldest half is
    /// dropped, keeping the recent entries, which are the ones being reported on.
    /// </summary>
    private const long MaxBytes = 256 * 1024;

    /// <summary>How far down the InnerException chain to record. The useful cause of a network
    /// failure is usually one or two levels down (HttpRequestException -> SocketException, or
    /// -> AuthenticationException for a TLS problem), never deeper than this.</summary>
    private const int MaxInnerDepth = 4;

    private static readonly object Gate = new();
    private static bool _headerWritten;

    /// <summary>Full path to the log file, so the UI can point a user straight at it.</summary>
    public static string FilePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Data", "fleetview-crash.log");

    /// <summary>Records an unhandled exception (startup failure or dispatcher exception).</summary>
    /// <remarks>
    /// The dump is scrubbed of the user's profile directory first. Every other entry point here
    /// takes text the caller judged safe to record, precisely so this file can be handed to someone
    /// else - and this one writes <c>ex.ToString()</c>, which carries whatever paths the exception
    /// and its stack frames mention. A failed read of ShipLocker.json names it in full, and that
    /// path contains the Windows account name.
    /// </remarks>
    public static void Crash(Exception ex) =>
        Write($"CRASH{Environment.NewLine}{Indent(Scrub(ex.ToString()))}");

    /// <summary>
    /// Replaces the user's profile directory with <c>%USERPROFILE%</c> wherever it appears.
    /// </summary>
    /// <remarks>
    /// Not a general redactor, and not claimed to be one: it removes the identifier that this app's
    /// own paths reliably contain - the account name, via Saved Games, AppData and the desktop -
    /// and leaves everything else as written. A stack trace is still a stack trace.
    /// </remarks>
    private static string Scrub(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile)
            ? text
            : text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Records a condition that was handled and deliberately not shown to the user: a file that
    /// couldn't be read, a saved-state file that parsed but held nonsense, a configuration
    /// override that was rejected. These used to be bare <c>catch { }</c> blocks, which meant the
    /// only signal that something was wrong was the absence of a feature working.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Callers pass their own text rather than an exception so nothing here decides for them what
    /// is safe to record: no path, system name or component name should reach this file unless the
    /// caller judged it diagnostic and not personal.
    /// </para>
    /// <para>
    /// An identical message is recorded at most once per <see cref="NoteRepeatWindow"/>. Unlike
    /// <see cref="Crash"/> and <see cref="FetchFailed"/>, which follow something the user did,
    /// several of these sit on the one-second inventory poll - a ShipLocker.json this process
    /// cannot open would otherwise write the same line sixty times a minute and push everything
    /// else out through <see cref="TrimIfOversize"/>.
    /// </para>
    /// <para>
    /// A window rather than once-per-session, because suppressing forever interacts badly with
    /// that trim: an early note can be dropped by a later trim and, having already been recorded,
    /// would never be written again - leaving no trace of it anywhere. Re-recording periodically
    /// also distinguishes a condition that happened once from one that is still happening.
    /// </para>
    /// </remarks>
    public static void Note(string message)
    {
        message = SafeText.SingleLine(message);

        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (Noted.TryGetValue(message, out var last) && now - last < NoteRepeatWindow)
                return;

            ForgetExpiredNotes(now);
            Noted[message] = now;
        }
        Write($"NOTE  {message}");
    }

    /// <summary>
    /// Most distinct messages the repeat-suppression table will hold. Caller holds
    /// <see cref="Gate"/>.
    /// </summary>
    /// <remarks>
    /// The table is keyed by the caller's own text, and several callers interpolate a value into
    /// it - a filename, a byte count, a URL. Nothing ever removed an entry, so a message carrying
    /// something that varies (a ShipLocker.json whose size changes every time it is written, on a
    /// once-per-second poll) added a new key each time and the table grew for as long as the app
    /// stayed open. Expired entries are dropped first, since an entry past its window has no effect
    /// on anything; the cap is what covers the case where they are all still live.
    /// </remarks>
    private const int MaxNotedMessages = 500;

    private static void ForgetExpiredNotes(DateTime now)
    {
        if (Noted.Count < MaxNotedMessages) return;

        foreach (var key in Noted.Where(kv => now - kv.Value >= NoteRepeatWindow)
                                 .Select(kv => kv.Key).ToList())
        {
            Noted.Remove(key);
        }

        // Every entry is still inside its window. Suppression is a courtesy rather than a
        // guarantee, so the table is emptied rather than allowed past the cap - the worst outcome
        // is that a handful of messages are recorded once more than they would have been.
        if (Noted.Count >= MaxNotedMessages) Noted.Clear();
    }

    /// <summary>How long an identical <see cref="Note"/> is suppressed for after being recorded.
    /// Long enough that a once-a-second condition costs twelve lines an hour, short enough that
    /// an ongoing problem keeps saying so.</summary>
    private static readonly TimeSpan NoteRepeatWindow = TimeSpan.FromMinutes(5);

    /// <summary>When each distinct <see cref="Note"/> message was last recorded.</summary>
    private static readonly Dictionary<string, DateTime> Noted = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a market fetch that threw. <paramref name="source"/> is the relay base URL actually
    /// in use, which matters because it can be overridden by FLEETVIEW_RELAY_URL and because older
    /// installs may still be pointed at a different endpoint than the current build's default.
    /// Only counts and technical detail are recorded, never component names, system names or the
    /// commander's location.
    /// </summary>
    public static void FetchFailed(string direction, int componentCount, string source,
        TimeSpan elapsed, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FETCH FAILED  {direction}  {componentCount} component(s)  {elapsed.TotalSeconds:0.0}s");
        sb.AppendLine($"  source : {SafeText.SingleLine(source)}");
        sb.AppendLine($"  status : {DescribeStatus(ex)}");
        sb.AppendLine($"  error  : {Describe(ex)}");
        foreach (var inner in InnerChain(ex))
            sb.AppendLine($"  inner  : {inner}");
        Write(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// Records a fetch that succeeded but returned nothing. Worth a line of its own: it looks the
    /// same to a user reporting "it won't fetch prices", but it proves DNS, TLS and the relay
    /// itself are all fine and moves the question to the query or the data.
    /// </summary>
    public static void FetchEmpty(string direction, int componentCount, string source, TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FETCH OK, 0 rows  {direction}  {componentCount} component(s)  {elapsed.TotalSeconds:0.0}s");
        sb.AppendLine($"  source : {SafeText.SingleLine(source)}");
        Write(sb.ToString().TrimEnd());
    }

    /// <summary>The HTTP status, when the request got far enough to have one. Read from the typed
    /// property rather than matched out of the message text, so it is unaffected by the wording or
    /// language of the exception.</summary>
    private static string DescribeStatus(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (e is HttpRequestException { StatusCode: { } code })
                return $"HTTP {(int)code} {code}";

        // No status attached means one of two quite different things: the request never got a
        // response at all (DNS, TLS, refused, timed out), or it got a good one and then failed
        // while reading it - RelayMarketSource checks the status before touching the body, so a
        // JsonException here means the relay answered 2xx with something this build can't parse.
        // The error line printed underneath tells the two apart.
        return "none attached (see error below)";
    }

    /// <summary>Type name plus message, with the socket error code spelled out where there is one
    /// - HostNotFound, ConnectionRefused and TimedOut all surface as the same generic
    /// HttpRequestException message otherwise.</summary>
    /// <remarks>
    /// The message is flattened to one line first. This file is line-oriented - one timestamped
    /// entry per line - and an exception message is not always this app's own text: a JsonException
    /// quotes what it failed to parse, which for a market fetch is a server's response body. A
    /// newline in there would end the entry and let the rest of it pose as further ones, in the
    /// file a user is invited to send on when reporting a problem.
    /// </remarks>
    private static string Describe(Exception ex) => ex switch
    {
        SocketException se =>
            $"{se.GetType().Name} ({se.SocketErrorCode}): {SafeText.SingleLine(se.Message)}",
        _ => $"{ex.GetType().Name}: {SafeText.SingleLine(ex.Message)}"
    };

    private static IEnumerable<string> InnerChain(Exception ex)
    {
        Exception? e = ex.InnerException;
        for (int depth = 0; e != null && depth < MaxInnerDepth; e = e.InnerException, depth++)
            yield return Describe(e);
    }

    private static string Indent(string text) =>
        string.Join(Environment.NewLine,
            text.Split('\n').Select(l => "  " + l.TrimEnd('\r')));

    /// <summary>
    /// Serialized on <see cref="Gate"/> because the buy and sell fetches run concurrently and can
    /// fail at the same instant; two threads appending to one file would otherwise collide on a
    /// sharing violation and lose the entry.
    /// </summary>
    private static void Write(string entry)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                TrimIfOversize();
                if (!_headerWritten)
                {
                    File.AppendAllText(FilePath, SessionHeader());
                    _headerWritten = true;
                }
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:s}  {entry}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch { /* logging must never throw */ }
    }

    /// <summary>Stamped once per process, so any excerpt a user pastes carries the build and OS
    /// it came from without repeating them on every entry.</summary>
    private static string SessionHeader()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        return $"==== FleetFinder {version} | {RuntimeInformation.OSDescription} " +
               $"| session started {DateTime.Now:s} ===={Environment.NewLine}";
    }

    /// <summary>
    /// Caller holds <see cref="Gate"/>. Swallows its own failures.
    /// </summary>
    /// <remarks>
    /// Its own try/catch, rather than sharing <see cref="Write"/>'s. Trimming and appending sat in
    /// one block, so a trim that threw - the file locked by a text editor the user opened to read
    /// it, most plausibly - skipped the append underneath it and discarded the entry that had
    /// prompted the write. Housekeeping failing is not a reason to lose the thing being recorded;
    /// the file grows past its ceiling until a later trim succeeds, which is the lesser problem.
    /// </remarks>
    private static void TrimIfOversize()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length <= MaxBytes) return;

            var text = File.ReadAllText(FilePath);
            File.WriteAllText(FilePath,
                $"(older entries trimmed){Environment.NewLine}{text[(text.Length / 2)..]}");
            // The trim may have taken the session header with it; re-stamp on the next write so the
            // remaining entries still say which build produced them.
            _headerWritten = false;
        }
        catch { /* trimming is maintenance; the entry still has to be written */ }
    }
}
