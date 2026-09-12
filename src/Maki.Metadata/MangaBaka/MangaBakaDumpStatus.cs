namespace Maki.Metadata.MangaBaka;

/// <summary>Point-in-time view of a dump refresh, for the settings UI and the progress toast.</summary>
/// <param name="Phase">
/// "idle", "checking" (fetching the published checksum), "downloading", "indexing" (FTS5 and the
/// browse indexes over the staged file) or "installing" (the atomic swap).
/// </param>
/// <param name="DownloadedBytes">Compressed bytes received, which is what the total is measured in.</param>
/// <param name="TotalBytes">Compressed size from Content-Length; null when the server withheld it.</param>
public record MangaBakaDumpProgress(
    bool Running,
    string Phase,
    long DownloadedBytes,
    long? TotalBytes,
    long? BytesPerSecond,
    int? EstimatedSecondsRemaining,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    bool LastInstalled,
    string? LastError);

/// <summary>
/// Thread-safe live status of a dump refresh, shared between <see cref="MangaBakaDumpService"/>
/// (writer) and whoever reports it. A singleton because the refresh runs on a Quartz job and the
/// readers are unrelated requests and a SignalR pump.
/// </summary>
/// <param name="clock">Time source; overridden in tests so the ETA maths is deterministic.</param>
public class MangaBakaDumpStatus(Func<DateTime>? clock = null)
{
    /// <summary>
    /// Smoothing for the bytes-per-second estimate. A 350 MB transfer over a home connection swings
    /// enough that a plain average reads as a stuck ETA for minutes after the link speeds up.
    /// </summary>
    private const double RateSmoothing = 0.3;

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly object _gate = new();
    private bool _running;
    private string _phase = "idle";
    private long _downloaded;
    private long? _total;
    private double _bytesPerSecond;
    private DateTime? _lastReportAt;
    private long _lastReportBytes;
    private DateTime? _startedAt;
    private DateTime? _finishedAt;
    private bool _lastInstalled;
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
            _phase = "checking";
            _downloaded = 0;
            _total = null;
            _bytesPerSecond = 0;
            _lastReportAt = null;
            _lastReportBytes = 0;
            _startedAt = _clock();
            _finishedAt = null;
            _lastError = null;
        }
    }

    public void SetPhase(string phase)
    {
        lock (_gate)
        {
            _phase = phase;
        }
    }

    /// <summary>Enters the download phase with the compressed size, when the server gave one.</summary>
    public void BeginDownload(long? totalBytes)
    {
        lock (_gate)
        {
            _phase = "downloading";
            _total = totalBytes;
            _downloaded = 0;
            _bytesPerSecond = 0;
            _lastReportAt = _clock();
            _lastReportBytes = 0;
        }
    }

    public void ReportDownloaded(long downloadedBytes)
    {
        lock (_gate)
        {
            var now = _clock();
            if (_lastReportAt is { } previous)
            {
                var elapsed = (now - previous).TotalSeconds;
                var bytes = downloadedBytes - _lastReportBytes;

                // Throttled so a per-read update doesn't measure a rate over a microsecond.
                if (elapsed < 0.5)
                {
                    _downloaded = downloadedBytes;
                    return;
                }

                if (bytes > 0)
                {
                    var rate = bytes / elapsed;
                    _bytesPerSecond = _bytesPerSecond > 0
                        ? (RateSmoothing * rate) + ((1 - RateSmoothing) * _bytesPerSecond)
                        : rate;
                }
            }

            _lastReportAt = now;
            _lastReportBytes = downloadedBytes;
            _downloaded = downloadedBytes;
        }
    }

    /// <param name="installed">True when a new dump was swapped in, false when the checksum matched.</param>
    public void End(bool installed, string? error)
    {
        lock (_gate)
        {
            _running = false;
            _phase = "idle";
            _finishedAt = _clock();
            _lastInstalled = installed;
            _lastError = error;
        }
    }

    public MangaBakaDumpProgress Snapshot()
    {
        lock (_gate)
        {
            return new MangaBakaDumpProgress(
                _running, _phase, _downloaded, _total,
                _bytesPerSecond > 0 ? (long)_bytesPerSecond : null,
                EstimateSecondsRemaining(),
                _startedAt, _finishedAt, _lastInstalled, _lastError);
        }
    }

    /// <summary>
    /// Bytes left over the smoothed throughput. Null outside the download phase: the index build
    /// that follows has no measurable unit of work, and reporting the download's ETA through it
    /// would show a countdown that reached zero and then sat there. Caller holds <see cref="_gate"/>.
    /// </summary>
    private int? EstimateSecondsRemaining()
    {
        if (!_running || _phase != "downloading" || _total is not { } total || _bytesPerSecond <= 0)
        {
            return null;
        }

        var remaining = total - _downloaded;
        if (remaining <= 0)
        {
            return 0;
        }

        var seconds = remaining / _bytesPerSecond;
        return (int)Math.Max(0, Math.Min(seconds, TimeSpan.FromDays(1).TotalSeconds));
    }
}
