namespace Maki.Api.Services;

/// <summary>What the image caches currently occupy on disk, plus how many posters are missing.</summary>
public record ImageCacheUsage(
    int CoverFiles,
    long CoverBytes,
    int ThumbnailFiles,
    long ThumbnailBytes,
    int SeriesTotal,
    /// <summary>Series whose poster is absent or does not decode, i.e. what a non-forced run fixes.</summary>
    int CoversMissing);

/// <summary>Point-in-time view of an image-cache rebuild, for the settings UI.</summary>
public record ImageCacheRebuildSnapshot(
    bool Running,
    /// <summary>"idle", "clearing" (thumbnails) or "covers".</summary>
    string Phase,
    /// <summary>Whether the run re-downloads every poster or only the missing/unreadable ones.</summary>
    bool Force,
    int Processed,
    int Total,
    int Downloaded,
    int Failed,
    /// <summary>Series that carry no provider id, so there is nowhere to fetch a poster from.</summary>
    int Skipped,
    int ThumbnailsCleared,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? LastError);

/// <summary>
/// Thread-safe live status of the image-cache rebuild, shared between the job (writer) and the
/// system endpoint (reader). The counters are not reset at the end of a pass, so the same fields
/// describe the run in flight and, once <see cref="ImageCacheRebuildSnapshot.Running"/> goes false,
/// the last one that finished — the UI needs both and a separate set of Last* fields would only
/// have to be kept in step.
/// </summary>
public class ImageCacheRebuildStatus
{
    private readonly object _gate = new();
    private bool _running;
    private string _phase = "idle";
    private bool _force;
    private int _processed;
    private int _total;
    private int _downloaded;
    private int _failed;
    private int _skipped;
    private int _thumbnailsCleared;
    private DateTime? _startedAt;
    private DateTime? _finishedAt;
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

    /// <summary>
    /// Claims the run. False when one is already in flight, which is what the endpoint reports back
    /// instead of queueing a second pass behind the first.
    /// </summary>
    public bool TryBegin(bool force)
    {
        lock (_gate)
        {
            if (_running)
            {
                return false;
            }

            _running = true;
            _phase = "clearing";
            _force = force;
            _processed = 0;
            _total = 0;
            _downloaded = 0;
            _failed = 0;
            _skipped = 0;
            _thumbnailsCleared = 0;
            _startedAt = DateTime.UtcNow;
            _finishedAt = null;
            _lastError = null;
            return true;
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

    public void ReportThumbnailsCleared(int count)
    {
        lock (_gate)
        {
            _thumbnailsCleared = count;
        }
    }

    public void ReportCover(bool downloaded, bool failed, bool skipped)
    {
        lock (_gate)
        {
            _processed++;
            if (downloaded) _downloaded++;
            if (failed) _failed++;
            if (skipped) _skipped++;
        }
    }

    public void End(string? error)
    {
        lock (_gate)
        {
            _running = false;
            _phase = "idle";
            _finishedAt = DateTime.UtcNow;
            _lastError = error;
        }
    }

    /// <summary>
    /// Memo for the disk-usage figures, which cost a recursive walk of the thumbnail cache: the
    /// settings card polls every second and a half while a rebuild runs, and re-walking a
    /// six-figure thumbnail folder on every poll would cost more than the rebuild itself.
    /// </summary>
    private static readonly TimeSpan UsageTtl = TimeSpan.FromSeconds(30);
    private ImageCacheUsage? _usage;
    private DateTime _usageAt;

    public ImageCacheUsage? CachedUsage()
    {
        lock (_gate)
        {
            return _usage is not null && DateTime.UtcNow - _usageAt < UsageTtl ? _usage : null;
        }
    }

    public void CacheUsage(ImageCacheUsage usage)
    {
        lock (_gate)
        {
            _usage = usage;
            _usageAt = DateTime.UtcNow;
        }
    }

    /// <summary>Drops the memo, so the next read reflects a pass that just finished.</summary>
    public void InvalidateUsage()
    {
        lock (_gate)
        {
            _usage = null;
        }
    }

    public ImageCacheRebuildSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ImageCacheRebuildSnapshot(
                _running, _phase, _force, _processed, _total, _downloaded, _failed, _skipped,
                _thumbnailsCleared, _startedAt, _finishedAt, _lastError);
        }
    }
}
