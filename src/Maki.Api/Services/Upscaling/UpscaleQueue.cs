using Maki.Core.Images.Upscaling;

namespace Maki.Api.Services.Upscaling;

/// <summary>One page of one archive version through one model, whoever asked for it.</summary>
public readonly record struct UpscaleWorkKey(int ChapterFileId, string ArchiveVersion, int AbsIndex, string Model);

/// <summary>The chapter and page index a reader used to ask, which is what the push names back to them.</summary>
public readonly record struct UpscaleTarget(int ChapterId, int Page);

public sealed record UpscaleRequestItem(UpscaleWorkKey Key, UpscaleTarget Target, ReaderService.PageSlice Slice);

public enum UpscaleJobState
{
    Queued,
    Working
}

public sealed record UpscaleQueueSnapshot(int Pending, int Working, IReadOnlyDictionary<UpscaleWorkKey, UpscaleJobState> States);

public sealed class UpscaleJob(UpscaleWorkKey key, ReaderService.PageSlice slice)
{
    public UpscaleWorkKey Key { get; } = key;

    public ReaderService.PageSlice Slice { get; } = slice;

    internal Dictionary<int, UpscaleTarget> Waiters { get; } = [];

    internal bool Working { get; set; }
}

/// <summary>
/// Pages waiting for the server upscaler. A page several readers want is one job with all of them
/// waiting on it. Each reader has a FIFO lane of at most <see cref="MaxPendingPerUser"/> pages, and
/// lanes are served round robin, so one reader paging fast cannot starve another. A job nobody
/// waits on any more is dropped before it starts; one already running finishes and lands in the cache.
/// </summary>
public sealed class UpscaleQueue
{
    public const int MaxPendingPerUser = 12;

    private const int MemoCapacity = 1024;
    private static readonly TimeSpan MemoLifetime = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<UpscaleWorkKey, UpscaleJob> _jobs = [];
    private readonly Dictionary<int, List<UpscaleWorkKey>> _lanes = [];
    private readonly Queue<int> _turns = new();
    private readonly HashSet<int> _inTurns = [];
    private readonly Dictionary<UpscaleWorkKey, (UpscaleOutcome Outcome, DateTime At, long Seq)> _memo = [];
    private readonly Queue<(UpscaleWorkKey Key, long Seq)> _memoOrder = new();
    private long _memoSeq;
    private readonly SemaphoreSlim _signal = new(0);

    /// <summary>
    /// Makes <paramref name="items"/> this reader's pending work, current page first. Their pending
    /// pages of another chapter or model are dropped; older pending pages of the same chapter and
    /// model stay behind the new ones while there is room under the cap. Returns a state per item
    /// (only the first <see cref="MaxPendingPerUser"/> items are taken).
    /// </summary>
    public IReadOnlyList<UpscaleJobState> Replace(
        int userId, int chapterId, string model, IReadOnlyList<UpscaleRequestItem> items)
    {
        var taken = items.Take(MaxPendingPerUser).ToList();
        var states = new UpscaleJobState[taken.Count];
        var added = false;
        lock (_gate)
        {
            var old = _lanes.TryGetValue(userId, out var lane) ? lane : [];
            var requested = taken.Select(i => i.Key).ToHashSet();
            var next = new List<UpscaleWorkKey>();
            for (var i = 0; i < taken.Count; i++)
            {
                var item = taken[i];
                if (!_jobs.TryGetValue(item.Key, out var job))
                {
                    job = new UpscaleJob(item.Key, item.Slice);
                    _jobs[item.Key] = job;
                    added = true;
                }

                job.Waiters[userId] = item.Target;
                if (job.Working)
                {
                    states[i] = UpscaleJobState.Working;
                    continue;
                }

                states[i] = UpscaleJobState.Queued;
                if (!next.Contains(item.Key))
                {
                    next.Add(item.Key);
                    added = true;
                }
            }

            foreach (var key in old)
            {
                if (requested.Contains(key) || !_jobs.TryGetValue(key, out var job))
                {
                    continue;
                }

                var sameChapter = job.Waiters.TryGetValue(userId, out var target)
                                  && target.ChapterId == chapterId && key.Model == model;
                if (sameChapter && !job.Working && next.Count < MaxPendingPerUser)
                {
                    next.Add(key);
                }
                else
                {
                    Leave(job, userId);
                }
            }

            if (next.Count == 0)
            {
                _lanes.Remove(userId);
            }
            else
            {
                _lanes[userId] = next;
                if (_inTurns.Add(userId))
                {
                    _turns.Enqueue(userId);
                }
            }
        }

        if (added)
        {
            Wake();
        }

        return states;
    }

    /// <summary>The next job, taking one page from the next reader's lane in turn. Null when nothing is waiting.</summary>
    public UpscaleJob? TryDequeue()
    {
        lock (_gate)
        {
            var rounds = _turns.Count;
            while (rounds-- > 0)
            {
                var user = _turns.Dequeue();
                _inTurns.Remove(user);
                if (!_lanes.TryGetValue(user, out var lane))
                {
                    continue;
                }

                UpscaleJob? picked = null;
                while (lane.Count > 0 && picked is null)
                {
                    var key = lane[0];
                    lane.RemoveAt(0);
                    if (!_jobs.TryGetValue(key, out var job) || job.Working)
                    {
                        continue;
                    }

                    if (job.Waiters.Count == 0)
                    {
                        _jobs.Remove(key);
                        continue;
                    }

                    picked = job;
                }

                if (lane.Count == 0)
                {
                    _lanes.Remove(user);
                }
                else if (_inTurns.Add(user))
                {
                    _turns.Enqueue(user);
                }

                if (picked is null)
                {
                    continue;
                }

                picked.Working = true;
                foreach (var other in picked.Waiters.Keys)
                {
                    if (other != user && _lanes.TryGetValue(other, out var otherLane))
                    {
                        otherLane.Remove(picked.Key);
                        if (otherLane.Count == 0)
                        {
                            _lanes.Remove(other);
                        }
                    }
                }

                return picked;
            }

            return null;
        }
    }

    /// <summary>Waits until work may have arrived, or the timeout passes.</summary>
    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);

    /// <summary>Wakes the worker, which then re-reads the settings.</summary>
    public void Wake()
    {
        lock (_signal)
        {
            if (_signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
    }

    /// <summary>
    /// Ends a job and returns everyone waiting on it at that moment, including readers who asked
    /// while it ran. A failed or skipped page is remembered for a while so it is not queued again
    /// on every page turn.
    /// </summary>
    public IReadOnlyList<KeyValuePair<int, UpscaleTarget>> Complete(UpscaleJob job, UpscaleOutcome outcome, string? reason)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(job.Key, out var current) && ReferenceEquals(current, job))
            {
                _jobs.Remove(job.Key);
            }

            job.Working = false;
            if (outcome == UpscaleOutcome.Ready
                || reason is UpscaleReasons.ModelUnavailable or UpscaleReasons.UnknownModel or UpscaleReasons.CacheWriteFailed)
            {
                _memo.Remove(job.Key);
            }
            else
            {
                Remember(job.Key, outcome);
            }

            return job.Waiters.ToList();
        }
    }

    /// <summary>Failed or Skipped when this page ended that way in the last few minutes, else null.</summary>
    public UpscaleOutcome? RecentOutcome(UpscaleWorkKey key)
    {
        lock (_gate)
        {
            if (!_memo.TryGetValue(key, out var memo))
            {
                return null;
            }

            if (DateTime.UtcNow - memo.At > MemoLifetime)
            {
                _memo.Remove(key);
                return null;
            }

            return memo.Outcome;
        }
    }

    /// <summary>Forgets remembered failures for a model whose file was replaced or deleted.</summary>
    public void ForgetModel(string model)
    {
        lock (_gate)
        {
            foreach (var key in _memo.Keys.Where(k => k.Model == model).ToList())
            {
                _memo.Remove(key);
            }
        }
    }

    /// <summary>Drops every job that has not started.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var key in _jobs.Where(j => !j.Value.Working).Select(j => j.Key).ToList())
            {
                _jobs.Remove(key);
            }

            _lanes.Clear();
            _turns.Clear();
            _inTurns.Clear();
        }
    }

    public UpscaleQueueSnapshot Snapshot()
    {
        lock (_gate)
        {
            var states = _jobs.ToDictionary(j => j.Key, j => j.Value.Working ? UpscaleJobState.Working : UpscaleJobState.Queued);
            var working = states.Values.Count(s => s == UpscaleJobState.Working);
            return new UpscaleQueueSnapshot(states.Count - working, working, states);
        }
    }

    internal int PendingFor(int userId)
    {
        lock (_gate)
        {
            return _lanes.TryGetValue(userId, out var lane) ? lane.Count : 0;
        }
    }

    private void Leave(UpscaleJob job, int userId)
    {
        job.Waiters.Remove(userId);
        if (job.Waiters.Count == 0 && !job.Working)
        {
            _jobs.Remove(job.Key);
        }
    }

    internal int MemoOrderCount
    {
        get
        {
            lock (_gate)
            {
                return _memoOrder.Count;
            }
        }
    }

    /// <summary>
    /// The order queue keeps a sequence number per entry, so a key remembered again, or forgotten
    /// elsewhere, leaves a stale entry that is skipped rather than evicting a fresh one. Stale entries
    /// are compacted away once they outnumber the live ones.
    /// </summary>
    private void Remember(UpscaleWorkKey key, UpscaleOutcome outcome)
    {
        var seq = ++_memoSeq;
        _memo[key] = (outcome, DateTime.UtcNow, seq);
        _memoOrder.Enqueue((key, seq));
        while (_memo.Count > MemoCapacity && _memoOrder.TryDequeue(out var oldest))
        {
            if (IsCurrent(oldest))
            {
                _memo.Remove(oldest.Key);
            }
        }

        if (_memoOrder.Count > 2 * Math.Max(_memo.Count, MemoCapacity / 4))
        {
            var live = _memoOrder.Where(IsCurrent).ToList();
            _memoOrder.Clear();
            foreach (var entry in live)
            {
                _memoOrder.Enqueue(entry);
            }
        }
    }

    private bool IsCurrent((UpscaleWorkKey Key, long Seq) entry) =>
        _memo.TryGetValue(entry.Key, out var memo) && memo.Seq == entry.Seq;
}
