using System.IO;

namespace FleetView.Services;

/// <summary>
/// Tells the one-second poll whether the game has written to its journal since the last look, so
/// the Engineers tab can follow jumps and engineer progress without reading journals every second.
/// </summary>
/// <remarks>
/// Each look checks the newest journal's size and write time, which is one small file system call.
/// Finding which journal is newest means listing the folder, which can hold years of journals, so
/// that is done only every <see cref="RelistEvery"/>: a new session's journal is noticed within
/// that, and every write after it within a second. The poll timer can overlap itself when a look
/// is slow, so a look that finds another one running just says nothing changed.
/// </remarks>
public sealed class JournalGrowthProbe
{
    public static TimeSpan RelistEvery { get; } = TimeSpan.FromSeconds(10);

    private readonly JournalReader _journal;
    private readonly Func<DateTime> _now;
    private string? _newest;
    private DateTime _listedAt = DateTime.MinValue;
    private (long Length, DateTime Written)? _seen;
    private bool _primed;
    private int _busy;

    /// <param name="now">The clock, for the relist wait; the real one when null.</param>
    public JournalGrowthProbe(JournalReader journal, Func<DateTime>? now = null)
    {
        _journal = journal;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>True when the newest journal is a different file, or has grown or been written,
    /// since the last call. The first call only takes a baseline and says false. Never throws.</summary>
    public bool Changed()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return false;
        try
        {
            var now = _now();
            string? newest = _newest;
            if (newest == null || now - _listedAt >= RelistEvery)
            {
                newest = _journal.FilesNewestFirst().FirstOrDefault();
                _listedAt = now;
            }

            (long, DateTime)? state = null;
            if (newest != null)
            {
                try
                {
                    var info = new FileInfo(newest);
                    if (info.Exists) state = (info.Length, info.LastWriteTimeUtc);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Treated as unchanged; the next look tries again.
                    return false;
                }
            }

            bool changed = _primed && (!string.Equals(newest, _newest, StringComparison.OrdinalIgnoreCase) || state != _seen);
            _newest = newest;
            _seen = state;
            _primed = true;
            return changed;
        }
        catch (Exception ex)
        {
            // This runs on a thread-pool timer, where anything unhandled ends the process. Nothing
            // above is expected to throw, so if something does it is noted and the look is
            // simply skipped.
            DiagnosticLog.Note($"Journal growth check failed ({ex.GetType().Name}).");
            return false;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }
}
