using Maki.Core.Kavita;

namespace Maki.Api.Services;

/// <summary>
/// Lets the scrobble tick's Kavita pass reuse the volumes it fetched last time for a series nothing
/// has touched since, while <see cref="KavitaLiveReadSync"/> holds a connection to Kavita's hub.
/// <para>
/// Every <c>UserProgressUpdate</c> marks its series dirty. A series is fetched again when it is
/// dirty, was never fetched, or its summary page counts moved; everything else in the pass (the
/// read marks, Rewind's deltas, the tracker pushes of Maki's own reading) still runs for every
/// series, on the cached volumes. A full sweep, fetching every series, runs while the hub is not
/// connected, on the first pass after every connect or reconnect, and every <see cref="FullSweepEvery"/>.
/// </para>
/// </summary>
public sealed class KavitaLiveCoverage(TimeProvider time)
{
    public static readonly TimeSpan FullSweepEvery = TimeSpan.FromHours(6);

    private sealed record Cached(int Pages, int PagesRead, List<KavitaProgress.KavitaVolumeDto> Volumes);

    private readonly object _lock = new();
    private readonly HashSet<int> _dirty = [];
    // The dirty set a pass took, until that pass completes; a pass that throws hands it to the next.
    private HashSet<int>? _unfinished;
    private readonly Dictionary<int, Cached> _volumes = [];
    private long _epoch;
    private bool _connected;
    private long _sweptEpoch = -1;
    private DateTimeOffset _sweptAt;

    public void Connected()
    {
        lock (_lock)
        {
            _epoch++;
            _connected = true;
            _volumes.Clear();
        }
    }

    public void Disconnected()
    {
        lock (_lock)
        {
            if (!_connected)
            {
                return;
            }

            _epoch++;
            _connected = false;
            _volumes.Clear();
        }
    }

    public void Saw(int kavitaSeriesId)
    {
        lock (_lock)
        {
            _dirty.Add(kavitaSeriesId);
        }
    }

    /// <summary>Starts a Kavita pass, taking the series marked dirty so far.</summary>
    public Pass Begin()
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            var full = !_connected || _sweptEpoch != _epoch || now < _sweptAt || now - _sweptAt >= FullSweepEvery;
            var dirty = _dirty.ToHashSet();
            _dirty.Clear();
            if (_unfinished is not null)
            {
                dirty.UnionWith(_unfinished);
            }

            _unfinished = dirty;
            if (full)
            {
                _volumes.Clear();
            }

            return new Pass(this, full, _epoch, now, dirty);
        }
    }

    public sealed class Pass
    {
        private readonly KavitaLiveCoverage _owner;
        private readonly long _epoch;
        private readonly DateTimeOffset _startedAt;
        private readonly HashSet<int> _dirty;

        internal Pass(KavitaLiveCoverage owner, bool full, long epoch, DateTimeOffset startedAt, HashSet<int> dirty)
        {
            _owner = owner;
            Full = full;
            _epoch = epoch;
            _startedAt = startedAt;
            _dirty = dirty;
        }

        public bool Full { get; }

        /// <summary>The volumes fetched last time, or null when this series has to be fetched again.</summary>
        public List<KavitaProgress.KavitaVolumeDto>? Reuse(KavitaClient.KavitaSeriesSummary series)
        {
            if (Full || _dirty.Contains(series.Id))
            {
                return null;
            }

            lock (_owner._lock)
            {
                return _owner._volumes.TryGetValue(series.Id, out var cached) &&
                       cached.Pages == series.Pages && cached.PagesRead == series.PagesRead
                    ? cached.Volumes
                    : null;
            }
        }

        public void Fetched(KavitaClient.KavitaSeriesSummary series, List<KavitaProgress.KavitaVolumeDto> volumes)
        {
            lock (_owner._lock)
            {
                if (_owner._connected && _owner._epoch == _epoch)
                {
                    _owner._volumes[series.Id] = new Cached(series.Pages, series.PagesRead, volumes);
                }
            }
        }

        /// <summary>A series whose fetch failed, fetched again on the next pass.</summary>
        public void Failed(int kavitaSeriesId)
        {
            lock (_owner._lock)
            {
                _owner._volumes.Remove(kavitaSeriesId);
                _owner._dirty.Add(kavitaSeriesId);
            }
        }

        /// <summary>The pass went through every series. A full sweep then covers this connection.</summary>
        public void Complete()
        {
            lock (_owner._lock)
            {
                if (ReferenceEquals(_owner._unfinished, _dirty))
                {
                    _owner._unfinished = null;
                }

                if (Full && _owner._connected && _owner._epoch == _epoch)
                {
                    _owner._sweptEpoch = _epoch;
                    _owner._sweptAt = _startedAt;
                }
            }
        }
    }
}
