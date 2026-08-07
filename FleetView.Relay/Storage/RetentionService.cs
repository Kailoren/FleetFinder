namespace FleetView.Relay.Storage;

/// <summary>
/// Periodically drops listings and carriers nothing has reported on in a long time.
/// </summary>
/// <remarks>
/// The ingestion path only ever inserts and updates, so without this the database records
/// everything EDDN has ever said rather than what is currently true: a carrier that stops
/// broadcasting keeps its rows for good, and every component key that has ever arrived keeps one
/// per carrier per direction. Queries already exclude stale rows, which is why this had no visible
/// symptom - the file growing without bound on a small hosted box is the symptom.
///
/// The window is deliberately generous. This exists to bound growth, not to decide what is worth
/// showing; a listing months old is still returned, with its age shown, and it is the client that
/// decides what to make of that.
/// </remarks>
public sealed class RetentionService : BackgroundService
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly RelayDb _db;
    private readonly ILogger<RetentionService> _log;

    public RetentionService(RelayDb db, ILogger<RetentionService> log)
    {
        _db = db;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not on startup: a relay restarted repeatedly would otherwise run this on every launch,
        // and the first pass is the expensive one.
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                int removed = _db.PruneStale(DateTime.UtcNow - Retention);
                if (removed > 0)
                    _log.LogInformation("Retention pass removed {Count} stale row(s)", removed);
            }
            catch (Exception ex)
            {
                // Housekeeping failing must not stop the listener, which is what this process is
                // actually for. Recorded and retried on the next tick.
                _log.LogWarning(ex, "Retention pass failed");
            }
        }
    }
}
