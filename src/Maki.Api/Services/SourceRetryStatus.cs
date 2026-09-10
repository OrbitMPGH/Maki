namespace Maki.Api.Services;

/// <summary>Point-in-time view of a source retry, for the Health workspace.</summary>
public record SourceRetrySnapshot(
    bool Running,
    /// <summary>Source the run is (or last was) retrying. Empty before the first run.</summary>
    string SourceName,
    int Processed,
    int Total,
    /// <summary>Series whose mapping on that source now refreshes again.</summary>
    int Recovered,
    /// <summary>Series still failing against it.</summary>
    int Failed,
    DateTime? StartedAt,
    DateTime? FinishedAt);

/// <summary>
/// Thread-safe live status of the source retry pass, shared between <c>SourceRetryJob</c> (writer)
/// and the health overview (reader). Counters survive the end of a pass so the same fields describe
/// the run in flight and the last one that finished, as with <see cref="ImageCacheRebuildStatus"/>.
/// </summary>
public class SourceRetryStatus
{
    private readonly object _gate = new();
    private bool _running;
    private string _source = "";
    private int _processed;
    private int _total;
    private int _recovered;
    private int _failed;
    private DateTime? _startedAt;
    private DateTime? _finishedAt;

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
    /// rather than queueing a second pass behind the first: both would walk the same series and the
    /// second would spend its whole run re-requesting what the first had just fetched.
    /// </summary>
    public bool TryBegin(string sourceName)
    {
        lock (_gate)
        {
            if (_running)
            {
                return false;
            }

            _running = true;
            _source = sourceName;
            _processed = 0;
            _total = 0;
            _recovered = 0;
            _failed = 0;
            _startedAt = DateTime.UtcNow;
            _finishedAt = null;
            return true;
        }
    }

    public void SetTotal(int total)
    {
        lock (_gate)
        {
            _total = total;
        }
    }

    public void ReportSeries(bool recovered)
    {
        lock (_gate)
        {
            _processed++;
            if (recovered) _recovered++;
            else _failed++;
        }
    }

    public void End()
    {
        lock (_gate)
        {
            _running = false;
            _finishedAt = DateTime.UtcNow;
        }
    }

    public SourceRetrySnapshot Snapshot()
    {
        lock (_gate)
        {
            return new SourceRetrySnapshot(_running, _source, _processed, _total, _recovered, _failed, _startedAt, _finishedAt);
        }
    }
}
