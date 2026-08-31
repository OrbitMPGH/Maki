using System.Collections.Concurrent;

namespace Maki.Core.Sources;

/// <summary>
/// Short-lived cache of <see cref="ISource.GetExternalIdsAsync"/> results, keyed by (source, series).
/// <para>
/// The lookup is a page fetch per candidate against the source's shared rate limiter, and the same
/// candidates come back every time matching is re-run for a series: the "match sources" button, the
/// startup re-queue of anything still flagged <c>SourceMatchPending</c>, and a retried match after a
/// source was briefly down all re-search the same titles and get the same top few hits. Single-flighting
/// keeps a burst of those to one fetch per candidate.
/// </para>
/// <para>
/// An empty result is cached — a site that publishes no tracker links for a title still publishes none
/// a minute later, and re-fetching to rediscover that is the case this exists to avoid. A *failed*
/// lookup is not: a source that was briefly down should be retried, not remembered as having no ids.
/// </para>
/// </summary>
public sealed class SourceExternalIdCache(TimeProvider time)
{
    /// <summary>
    /// Long enough to cover a library import and the retries that follow it, short enough that a site
    /// correcting a wrong tracker link is picked up the same day rather than needing a restart.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    /// <summary>Bounds memory at a few candidates per series being matched; the coldest go first.</summary>
    private const int MaxEntries = 512;

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public IReadOnlyDictionary<string, string>? Ids;
        public bool Filled;
        public DateTime FetchedAt = DateTime.MinValue;
        public long LastUsedTicks;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>
    /// The source's external ids for a series, fetched at most once per <see cref="Ttl"/>. Concurrent
    /// callers for the same key wait on the first rather than each issuing their own request.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>?> GetAsync(
        ISource source, string sourceSeriesId, CancellationToken ct = default)
    {
        var entry = _entries.GetOrAdd($"{source.Name} {sourceSeriesId}", _ => new Entry());
        var now = time.GetUtcNow().UtcDateTime;
        Volatile.Write(ref entry.LastUsedTicks, now.Ticks);

        if (IsFresh(entry, now))
        {
            return entry.Ids;
        }

        await entry.Gate.WaitAsync(ct);
        try
        {
            if (IsFresh(entry, time.GetUtcNow().UtcDateTime))
            {
                return entry.Ids;
            }

            var ids = await source.GetExternalIdsAsync(sourceSeriesId, ct);
            entry.Ids = ids;
            entry.Filled = true;
            entry.FetchedAt = time.GetUtcNow().UtcDateTime;
            Volatile.Write(ref entry.LastUsedTicks, entry.FetchedAt.Ticks);
            Trim();
            return ids;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private bool IsFresh(Entry entry, DateTime now) => entry.Filled && now - entry.FetchedAt < Ttl;

    /// <summary>Evicts expired entries, then the least recently used if still over the cap.</summary>
    private void Trim()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;
        foreach (var (key, entry) in _entries)
        {
            // Skip an entry whose gate is held: a fetch is in flight against it, and dropping it now
            // would only make the next caller start a second one.
            if (!IsFresh(entry, now) && entry.Gate.CurrentCount == 1)
            {
                _entries.TryRemove(key, out _);
            }
        }

        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        var coldest = _entries
            .OrderBy(pair => Volatile.Read(ref pair.Value.LastUsedTicks))
            .Take(_entries.Count - MaxEntries)
            .ToList();
        foreach (var (key, _) in coldest)
        {
            _entries.TryRemove(key, out _);
        }
    }
}
