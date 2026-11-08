using Maki.Core.Images.Upscaling;

namespace Maki.Api.Services.Upscaling;

/// <summary>One page of one archive version through one model, whoever asked for it.</summary>
public readonly record struct UpscaleWorkKey(int ChapterFileId, string ArchiveVersion, int AbsIndex, string Model);

/// <summary>The chapter and page index a reader used to ask, which is what the push names back to them.</summary>
public readonly record struct UpscaleTarget(int ChapterId, int Page);

/// <summary>
/// One reader: a user and the session id their reader tab sent, so two tabs or devices on one account
/// keep separate lanes. Null for clients that send none, which share one lane per user.
/// </summary>
public readonly record struct UpscaleReader(int UserId, string? Session = null);

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

    private readonly CancellationTokenSource _abandon = new();

    internal Dictionary<UpscaleReader, UpscaleTarget> Waiters { get; } = [];

    internal bool Working { get; set; }

    /// <summary>Cancelled when every reader left this job while it ran.</summary>
    public CancellationToken Abandoned => _abandon.Token;

    internal void Abandon() => _abandon.Cancel();
}

/// <summary>
/// Pages waiting for the server upscaler. A page several readers want is one job with all of them
/// waiting on it. Each reader (a user's tab) has a FIFO lane of at most <see cref="MaxPendingPerUser"/>
/// pages, or a window plus the whole chapter's rest. Users are served round robin, so one reader
/// paging fast cannot starve another, however many tabs they have open. On a user's turn their tabs
/// that asked while visible in the last <see cref="ActiveWindow"/> take turns, every one's window
/// before any one's rest; tabs not active lately only run once those have nothing pending, so a tab
/// left open elsewhere never slows the ones being read. A job nobody waits on any more is dropped
/// before it starts. One already running finishes and lands in the cache, unless its last reader
/// paged past it or moved to another chapter or model: then it is abandoned, so the page they
/// turned to does not wait out one nobody will look at.
/// </summary>
public sealed class UpscaleQueue(TimeProvider? time = null)
{
    /// <summary>How recently a tab must have asked, while visible, to share its user's turns.</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>When a reader last asked while visible, and a sequence number fixing their place in the rotation.</summary>
    private readonly record struct Touch(DateTimeOffset At, long First);

    /// <summary>A reader's pending pages in order; the first <see cref="Window"/> are their reading window, the rest backlog.</summary>
    private sealed class Lane(List<UpscaleWorkKey> keys, int window)
    {
        public List<UpscaleWorkKey> Keys { get; } = keys;

        public int Window { get; private set; } = window;

        public UpscaleWorkKey PopFront()
        {
            var key = Keys[0];
            Keys.RemoveAt(0);
            Window = Math.Max(0, Window - 1);
            return key;
        }

        public void Remove(UpscaleWorkKey key)
        {
            var at = Keys.IndexOf(key);
            if (at < 0)
            {
                return;
            }

            Keys.RemoveAt(at);
            if (at < Window)
            {
                Window--;
            }
        }
    }

    public const int MaxPendingPerUser = 32;

    /// <summary>The cap for a reader who asked for their whole chapter (the reader's "Whole chapter" setting).</summary>
    public const int MaxChapterPages = 1000;

    /// <summary>Tabs with their own lane per user; a further tab shares the user's session-less lane.</summary>
    public const int MaxSessionsPerUser = 4;

    private const int MemoCapacity = 1024;
    private static readonly TimeSpan MemoLifetime = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<UpscaleWorkKey, UpscaleJob> _jobs = [];
    private readonly Dictionary<UpscaleReader, Lane> _lanes = [];
    private readonly Dictionary<UpscaleReader, Touch> _touched = [];
    private long _touchSeq;
    private readonly Dictionary<int, UpscaleReader> _lastServed = [];
    private readonly Dictionary<int, string> _sharedBy = [];
    private readonly Queue<int> _turns = new();
    private readonly HashSet<int> _inTurns = [];
    private readonly Dictionary<UpscaleWorkKey, (UpscaleOutcome Outcome, DateTime At, long Seq)> _memo = [];
    private readonly Queue<(UpscaleWorkKey Key, long Seq)> _memoOrder = new();
    private long _memoSeq;
    private readonly SemaphoreSlim _signal = new(0);

    /// <summary>
    /// Makes <paramref name="items"/> this reader's pending work, current page first. Their pending
    /// pages of another chapter or model are dropped, and so are older pages behind the lowest page
    /// asked for in this chapter: the reader paged past them before they were done. A running one
    /// is abandoned when nobody else waits on it. Older pages ahead stay behind the new ones while
    /// there is room under the cap, except <paramref name="notWanted"/>: pages the reader shows plain,
    /// dropped like the pages behind. Returns a state per item (only the first <paramref name="cap"/>
    /// items are taken, <see cref="MaxPendingPerUser"/> unless the reader asked for the whole chapter).
    /// The first <paramref name="window"/> items are the reader's window and the rest backlog; null
    /// makes them all window. A request from a hidden tab (<paramref name="visible"/> false) does not
    /// make that tab active.
    /// </summary>
    public IReadOnlyList<UpscaleJobState> Replace(
        int userId,
        int chapterId,
        string model,
        IReadOnlyList<UpscaleRequestItem> items,
        int cap = MaxPendingPerUser,
        IReadOnlySet<UpscaleWorkKey>? notWanted = null,
        string? session = null,
        int? window = null,
        bool visible = true)
    {
        notWanted ??= new HashSet<UpscaleWorkKey>();
        var taken = items.Take(cap).ToList();
        var states = new UpscaleJobState[taken.Count];
        var added = false;
        lock (_gate)
        {
            var reader = ReaderFor(userId, session);
            var first = _touched.TryGetValue(reader, out var was) ? was.First : ++_touchSeq;
            if (visible)
            {
                _touched[reader] = new Touch(_time.GetUtcNow(), first);
            }
            else if (!_touched.ContainsKey(reader))
            {
                _touched[reader] = new Touch(DateTimeOffset.MinValue, first);
            }

            if (session is null)
            {
                _sharedBy.Remove(userId);
            }
            else if (reader.Session is null)
            {
                _sharedBy[userId] = session;
            }

            var old = _lanes.TryGetValue(reader, out var lane) ? lane.Keys : [];
            var windowAdded = 0;
            var requested = taken.Select(i => i.Key).ToHashSet();
            var floor = taken.Where(i => i.Target.ChapterId == chapterId)
                .Select(i => i.Target.Page)
                .DefaultIfEmpty(int.MinValue)
                .Min();
            var next = new List<UpscaleWorkKey>();
            var inNext = new HashSet<UpscaleWorkKey>();
            for (var i = 0; i < taken.Count; i++)
            {
                var item = taken[i];
                if (!_jobs.TryGetValue(item.Key, out var job) || job.Abandoned.IsCancellationRequested)
                {
                    job = new UpscaleJob(item.Key, item.Slice);
                    _jobs[item.Key] = job;
                    added = true;
                }

                job.Waiters[reader] = item.Target;
                if (job.Working)
                {
                    states[i] = UpscaleJobState.Working;
                    continue;
                }

                states[i] = UpscaleJobState.Queued;
                if (inNext.Add(item.Key))
                {
                    next.Add(item.Key);
                    added = true;
                    if (i < window)
                    {
                        windowAdded++;
                    }
                }
            }

            foreach (var key in old)
            {
                if (requested.Contains(key) || !_jobs.TryGetValue(key, out var job))
                {
                    continue;
                }

                var sameChapter = job.Waiters.TryGetValue(reader, out var target)
                                  && target.ChapterId == chapterId && key.Model == model;
                if (sameChapter && target.Page >= floor && !job.Working && next.Count < cap && !notWanted.Contains(key))
                {
                    if (inNext.Add(key))
                    {
                        next.Add(key);
                    }
                }
                else
                {
                    Leave(job, reader);
                }
            }

            foreach (var job in _jobs.Values)
            {
                if (job.Working && !requested.Contains(job.Key) && job.Waiters.TryGetValue(reader, out var target)
                    && (target.ChapterId != chapterId || job.Key.Model != model || target.Page < floor || notWanted.Contains(job.Key)))
                {
                    job.Waiters.Remove(reader);
                    if (job.Waiters.Count == 0)
                    {
                        job.Abandon();
                    }
                }
            }

            if (next.Count == 0)
            {
                _lanes.Remove(reader);
            }
            else
            {
                _lanes[reader] = new Lane(next, window is null ? next.Count : windowAdded);
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

    /// <summary>
    /// Drops everything this reader has pending, and abandons a running page nobody else waits on.
    /// Called when they leave the reader with the whole chapter still going. Only the reader with this
    /// <paramref name="session"/> is touched; the user's other tabs keep theirs. A tab folded into the
    /// shared lane clears it only while it was the last to fill it, since other tabs use it too.
    /// </summary>
    public void Cancel(int userId, string? session = null)
    {
        var reader = new UpscaleReader(userId, session);
        lock (_gate)
        {
            if (session is not null && !_touched.ContainsKey(reader))
            {
                if (!_sharedBy.TryGetValue(userId, out var by) || by != session)
                {
                    return;
                }

                reader = new UpscaleReader(userId, null);
            }

            if (reader.Session is null)
            {
                _sharedBy.Remove(userId);
            }

            _touched.Remove(reader);
            if (_lanes.Remove(reader, out var lane))
            {
                foreach (var key in lane.Keys)
                {
                    if (_jobs.TryGetValue(key, out var job))
                    {
                        Leave(job, reader);
                    }
                }
            }

            foreach (var job in _jobs.Values.Where(j => j.Working && j.Waiters.ContainsKey(reader)).ToList())
            {
                job.Waiters.Remove(reader);
                if (job.Waiters.Count == 0)
                {
                    job.Abandon();
                }
            }
        }
    }

    /// <summary>
    /// The reader a request belongs to. A user keeps at most <see cref="MaxSessionsPerUser"/> tabs with
    /// lanes of their own; a further tab, while the others still have work, shares the session-less lane
    /// older clients use, so a client inventing sessions cannot hold more than a few lanes.
    /// </summary>
    private UpscaleReader ReaderFor(int userId, string? session)
    {
        var reader = new UpscaleReader(userId, session);
        if (session is null || _touched.ContainsKey(reader))
        {
            return reader;
        }

        var mine = _touched.Keys.Where(r => r.UserId == userId && r.Session is not null).ToList();
        if (mine.Count < MaxSessionsPerUser)
        {
            return reader;
        }

        foreach (var idle in mine.Where(r => !_lanes.ContainsKey(r) && !_jobs.Values.Any(j => j.Waiters.ContainsKey(r))))
        {
            _touched.Remove(idle);
        }

        return mine.Count(_touched.ContainsKey) < MaxSessionsPerUser ? reader : new UpscaleReader(userId, null);
    }

    /// <summary>
    /// The next job: one page from the next user in turn, from the lane <see cref="NextReader"/> picks
    /// among theirs. Null when nothing is waiting.
    /// </summary>
    public UpscaleJob? TryDequeue()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var rounds = _turns.Count;
            while (rounds-- > 0)
            {
                var user = _turns.Dequeue();
                _inTurns.Remove(user);
                UpscaleJob? picked = null;
                while (picked is null && NextReader(user, now) is { } reader)
                {
                    var lane = _lanes[reader];
                    var key = lane.PopFront();
                    if (lane.Keys.Count == 0)
                    {
                        _lanes.Remove(reader);
                    }

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
                    _lastServed[user] = reader;
                }

                if (picked is null)
                {
                    continue;
                }

                picked.Working = true;
                foreach (var other in picked.Waiters.Keys)
                {
                    if (_lanes.TryGetValue(other, out var otherLane))
                    {
                        otherLane.Remove(picked.Key);
                        if (otherLane.Keys.Count == 0)
                        {
                            _lanes.Remove(other);
                        }
                    }
                }

                if (_lanes.Keys.Any(r => r.UserId == user) && _inTurns.Add(user))
                {
                    _turns.Enqueue(user);
                }

                return picked;
            }

            return null;
        }
    }

    /// <summary>
    /// Whose lane serves this user's turn. Tabs active in the last <see cref="ActiveWindow"/> take
    /// turns in a fixed rotation, those with window pages left first, so every active tab's window
    /// comes before any active tab's backlog. Once every active lane is empty, the most recently
    /// active of the rest goes.
    /// </summary>
    private UpscaleReader? NextReader(int user, DateTimeOffset now)
    {
        var lanes = _lanes.Where(l => l.Key.UserId == user)
            .Select(l => (Reader: l.Key, l.Value.Window, Touch: _touched.GetValueOrDefault(l.Key)))
            .ToList();
        if (lanes.Count == 0)
        {
            return null;
        }

        var active = lanes.Where(l => now - l.Touch.At <= ActiveWindow).OrderBy(l => l.Touch.First).ToList();
        if (active.Count == 0)
        {
            return lanes.OrderByDescending(l => l.Touch.At).ThenByDescending(l => l.Touch.First).First().Reader;
        }

        var pool = active.Any(l => l.Window > 0) ? active.Where(l => l.Window > 0).ToList() : active;
        var last = _lastServed.TryGetValue(user, out var served) ? _touched.GetValueOrDefault(served).First : long.MinValue;
        var after = pool.FindIndex(l => l.Touch.First > last);
        return pool[after < 0 ? 0 : after].Reader;
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

    /// <summary>Who is waiting on <paramref name="job"/> right now.</summary>
    public IReadOnlyList<KeyValuePair<int, UpscaleTarget>> WaitersOf(UpscaleJob job)
    {
        lock (_gate)
        {
            return ByUser(job);
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
                || reason is UpscaleReasons.ModelUnavailable or UpscaleReasons.UnknownModel or UpscaleReasons.CacheWriteFailed
                    or UpscaleReasons.Abandoned)
            {
                _memo.Remove(job.Key);
            }
            else
            {
                Remember(job.Key, outcome);
            }

            return ByUser(job);
        }
    }

    /// <summary>The push goes to a user, not a tab, so two tabs waiting on one page are told once.</summary>
    private static List<KeyValuePair<int, UpscaleTarget>> ByUser(UpscaleJob job) =>
        job.Waiters.Select(w => KeyValuePair.Create(w.Key.UserId, w.Value)).Distinct().ToList();

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
            _touched.Clear();
            _lastServed.Clear();
            _sharedBy.Clear();
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

    internal int PendingFor(int userId, string? session = null)
    {
        lock (_gate)
        {
            return _lanes.TryGetValue(new UpscaleReader(userId, session), out var lane) ? lane.Keys.Count : 0;
        }
    }

    private void Leave(UpscaleJob job, UpscaleReader reader)
    {
        job.Waiters.Remove(reader);
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
