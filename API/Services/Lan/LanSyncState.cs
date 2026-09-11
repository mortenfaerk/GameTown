namespace API.Services.Lan;

/// <summary>
/// What the last LAN bot sync did, for the settings screen.
///
/// A singleton holding this in memory rather than a table, and the loss on restart is the point
/// rather than a compromise: the question this answers is "is polling working NOW", and a persisted
/// "ok" written before a crash answers it wrongly. An install that has just come up genuinely has not
/// synced yet, and saying so is the honest reading.
///
/// Written from a background worker and read from request threads, so every field goes through the
/// lock. There is no path that needs a consistent multi-field read while a sync is in flight — the
/// screen shows a snapshot and refreshes — so <see cref="Snapshot"/> copying under the lock is enough.
/// </summary>
public sealed class LanSyncState
{
    private readonly Lock _gate = new();

    private bool _running;
    private DateTime? _lastRunUtc;
    private string? _lastReason;
    private int _lastSeen;
    private int _lastMatched;
    private int _lastBound;

    /// <summary>
    /// Claims the right to run a sync, or returns false because one is already in flight.
    ///
    /// This is a real gate, not bookkeeping. A sync reads the bot's list and then writes back to it,
    /// so two overlapping runs would both decide a suggestion is unbound and both PUT for it — the
    /// second one pointlessly re-titling a catalogue entry the first had just set. That was already
    /// possible between the background worker and an admin pressing Sync now; it became likely the
    /// moment contributors could refresh the wishlist themselves.
    ///
    /// A gate rather than a queue: the loser has nothing useful to do afterwards, because the run it
    /// lost to is fetching exactly the same list.
    /// </summary>
    public bool TryBeginRun()
    {
        lock (_gate)
        {
            if (_running) return false;

            _running = true;
            return true;
        }
    }

    /// <summary>
    /// Records the outcome of a run. Always called, including on failure — a sync that could not
    /// reach the bot is a result the screen needs, not an absence of one.
    /// </summary>
    public void MarkFinished(string reason, int seen, int matched, int bound)
    {
        lock (_gate)
        {
            _running = false;
            _lastRunUtc = DateTime.UtcNow;
            _lastReason = reason;
            _lastSeen = seen;
            _lastMatched = matched;
            _lastBound = bound;
        }
    }

    public LanSyncSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new LanSyncSnapshot(_running, _lastRunUtc, _lastReason, _lastSeen, _lastMatched, _lastBound);
        }
    }
}

/// <summary>
/// <paramref name="Matched"/> counts suggestions GameTown newly identified a game for;
/// <paramref name="Bound"/> counts the ones it pushed. They differ when a push failed, and when a
/// row matched on an earlier run only got pushed on this one.
/// </summary>
public readonly record struct LanSyncSnapshot(
    bool Running,
    DateTime? LastRunUtc,
    string? LastReason,
    int Seen,
    int Matched,
    int Bound);
