namespace Maki.Metadata.Embedding;

/// <summary>Point-in-time view of the embedding index pass, for the settings UI.</summary>
public record EmbeddingIndexSnapshot(
    bool Running,
    string Phase,
    int Embedded,
    int Scanned,
    int? RecommendableTotal,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    int LastEmbedded,
    int LastSkipped,
    string? LastError,
    /// <summary>Seconds left at the recent throughput; null until there's enough to estimate.</summary>
    int? EstimatedSecondsRemaining);

/// <summary>
/// Thread-safe live status of the background embedding indexer, shared between the indexer
/// (writer) and the settings endpoint (reader). Phases: "idle", "preparing" (model download /
/// warm-up), "indexing".
/// </summary>
/// <param name="clock">Time source; overridden in tests so the ETA maths is deterministic.</param>
public class EmbeddingIndexStatus(Func<DateTime>? clock = null)
{
    /// <summary>
    /// Smoothing for the rows-per-second estimate. The pass alternates between embedding (slow)
    /// and skipping unchanged rows (fast), so a plain average over the whole run swings wildly —
    /// this weights recent batches while still damping a single slow one.
    /// </summary>
    private const double RateSmoothing = 0.3;

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly object _gate = new();
    private double _rowsPerSecond;
    private DateTime? _lastReportAt;
    private int _lastReportScanned;
    private bool _running;
    private string _phase = "idle";
    private int _embedded;
    private int _scanned;
    private int? _total;
    private DateTime? _startedAt;
    private DateTime? _finishedAt;
    private int _lastEmbedded;
    private int _lastSkipped;
    private string? _lastError;

    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    public void Begin()
    {
        lock (_gate)
        {
            _running = true;
            _phase = "preparing";
            _embedded = 0;
            _scanned = 0;
            _startedAt = _clock();
            _finishedAt = null;
            _lastError = null;
            _rowsPerSecond = 0;
            _lastReportAt = null;
            _lastReportScanned = 0;
        }
    }

    public void SetPhase(string phase)
    {
        lock (_gate)
        {
            _phase = phase;
        }
    }

    public void SetTotal(int total)
    {
        lock (_gate)
        {
            _total = total;
        }
    }

    public void Report(int scanned, int embedded)
    {
        lock (_gate)
        {
            var now = _clock();
            if (_lastReportAt is { } previous)
            {
                var elapsed = (now - previous).TotalSeconds;
                var rows = scanned - _lastReportScanned;
                if (elapsed > 0 && rows > 0)
                {
                    var rate = rows / elapsed;
                    _rowsPerSecond = _rowsPerSecond > 0
                        ? (RateSmoothing * rate) + ((1 - RateSmoothing) * _rowsPerSecond)
                        : rate;
                }
            }

            _lastReportAt = now;
            _lastReportScanned = scanned;
            _scanned = scanned;
            _embedded = embedded;
        }
    }

    public void End(int embedded, int skipped, string? error)
    {
        lock (_gate)
        {
            _running = false;
            _phase = "idle";
            _embedded = embedded;
            _lastEmbedded = embedded;
            _lastSkipped = skipped;
            _finishedAt = _clock();
            _lastError = error;
        }
    }

    public EmbeddingIndexSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new EmbeddingIndexSnapshot(
                _running, _phase, _embedded, _scanned, _total,
                _startedAt, _finishedAt, _lastEmbedded, _lastSkipped, _lastError,
                EstimateSecondsRemaining());
        }
    }

    /// <summary>
    /// Rows left divided by the smoothed throughput. Null when there's nothing to estimate from:
    /// not running, no total yet, or no two progress reports to measure a rate between. Caller
    /// holds <see cref="_gate"/>.
    /// </summary>
    private int? EstimateSecondsRemaining()
    {
        if (!_running || _phase != "indexing" || _total is not { } total || _rowsPerSecond <= 0)
        {
            return null;
        }

        var remaining = total - _scanned;
        if (remaining <= 0)
        {
            return 0;
        }

        // Progress reports only land on a flush, so a long stretch of skipped rows leaves the
        // last one stale. Charge that silence against the estimate rather than reporting a
        // countdown that has visibly stopped moving.
        var sinceLastReport = _lastReportAt is { } last ? (_clock() - last).TotalSeconds : 0;
        var seconds = (remaining / _rowsPerSecond) - sinceLastReport;
        return (int)Math.Max(0, Math.Min(seconds, TimeSpan.FromDays(1).TotalSeconds));
    }
}
