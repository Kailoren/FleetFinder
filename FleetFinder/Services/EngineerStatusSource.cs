using FleetView.Models;

namespace FleetView.Services;

/// <summary>
/// One read for the Engineers tab: the commander's engineer standings, where the commander is, and
/// the engineers' system coordinates found so far.
/// </summary>
/// <param name="Location">The commander's last known position; null when no journal has one.</param>
/// <param name="LookedUp">Coordinates from the coordinate source, keyed by
/// <see cref="ShipLockerReader.Normalize"/>d system name. Empty until a lookup has answered.</param>
public sealed record EngineerReading(
    EngineerProgressSnapshot Progress,
    PlayerLocation? Location,
    IReadOnlyDictionary<string, SystemCoords> LookedUp);

/// <summary>
/// Puts together an <see cref="EngineerReading"/>. Runs on the thread pool, never the UI thread:
/// it reads journals.
/// </summary>
/// <remarks>
/// <para>
/// The data file has no coordinates, so every engineer's system is looked up, the same way
/// carrier systems are (the real source keeps what it finds on disk, so this is a round trip on
/// the first run and none after). A read never waits for the lookup: it starts one in the
/// background when there is something to ask, returns what has been found so far, and the tab
/// shows "distance unknown" until <see cref="CoordinatesFound"/> says to read again.
/// </para>
/// <para>
/// A system is asked about at most once every <see cref="RetryAfter"/>: the tab reads on every
/// journal change, which is every few seconds while the game plays, and a lookup service that is
/// down should not be asked that often. Whatever the source cannot answer stays unknown, and the
/// tab shows no distance for it rather than a guess.
/// </para>
/// </remarks>
public sealed class EngineerStatusSource
{
    /// <summary>How long a system the lookup could not answer is left before asking again.</summary>
    public static TimeSpan RetryAfter { get; } = TimeSpan.FromMinutes(10);

    /// <summary>The longest one lookup is given.</summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(20);

    private readonly JournalReader _journal;
    private readonly EngineerProgressReader _progress;
    private readonly ICoordinateSource? _coords;
    private readonly IReadOnlyList<string> _systems;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, SystemCoords> _found = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _askedAt = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private Task _lookup = Task.CompletedTask;

    /// <param name="coords">Where to look up the engineers' systems; null for none (offline, or
    /// the self-test).</param>
    /// <param name="now">The clock, for the retry wait; the real one when null.</param>
    public EngineerStatusSource(JournalReader journal, IEnumerable<Engineer> engineers, ICoordinateSource? coords,
        Func<DateTime>? now = null)
    {
        _journal = journal;
        _progress = new EngineerProgressReader(journal);
        _coords = coords;
        _now = now ?? (() => DateTime.UtcNow);
        _systems = engineers.Select(e => e.System)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Raised on a thread-pool thread when a lookup has found coordinates the last read
    /// did not have, so the tab can read again and show the distances.</summary>
    public event Action? CoordinatesFound;

    /// <summary>The lookup running now, or the last one; for the self-test to wait on.</summary>
    public Task Lookup
    {
        get
        {
            lock (_gate) return _lookup;
        }
    }

    public EngineerReading Read()
    {
        var progress = _progress.Read();
        var location = _journal.GetCurrentLocation();
        lock (_gate)
        {
            StartLookup();
            return new EngineerReading(progress, location, new Dictionary<string, SystemCoords>(_found, StringComparer.Ordinal));
        }
    }

    /// <summary>Starts a lookup for the systems not found yet and not asked about lately, unless
    /// one is still running. Called under the lock.</summary>
    private void StartLookup()
    {
        if (_coords == null || !_lookup.IsCompleted) return;

        var now = _now();
        var ask = _systems
            .Where(s =>
            {
                var key = ShipLockerReader.Normalize(s);
                return !_found.ContainsKey(key)
                       && (!_askedAt.TryGetValue(key, out var at) || now - at >= RetryAfter);
            })
            .ToList();
        if (ask.Count == 0) return;
        foreach (var s in ask) _askedAt[ShipLockerReader.Normalize(s)] = now;
        var source = _coords;
        _lookup = Task.Run(() => LookUp(source, ask));
    }

    private void LookUp(ICoordinateSource source, List<string> ask)
    {
        int found = 0;
        try
        {
            using var cts = new CancellationTokenSource(LookupTimeout);
            // On the thread pool, and the source never captures a context, so waiting here cannot
            // deadlock.
            var map = source.GetCoordsAsync(ask, cts.Token).GetAwaiter().GetResult();
            lock (_gate)
            {
                foreach (var s in ask)
                {
                    var key = ShipLockerReader.Normalize(s);
                    if (map.TryGetValue(key, out var c)
                        && EdsmCoordinateSource.IsPlausibleCoordinate(c.X)
                        && EdsmCoordinateSource.IsPlausibleCoordinate(c.Y)
                        && EdsmCoordinateSource.IsPlausibleCoordinate(c.Z)
                        && _found.TryAdd(key, c))
                        found++;
                }
            }
        }
        catch (Exception ex)
        {
            // Offline, timed out, or the source broke: those distances stay unknown until the
            // retry wait is up.
            DiagnosticLog.Note($"Engineer system coordinates could not be looked up ({ex.GetType().Name}).");
        }
        if (found > 0) CoordinatesFound?.Invoke();
    }
}
