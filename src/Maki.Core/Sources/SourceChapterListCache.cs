using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Maki.Core.Sources;

/// <summary>
/// Short-lived cache of <see cref="ISource.ListChaptersAsync"/> results, keyed by
/// (source, series, language filter).
/// <para>
/// Resolution is per <em>chapter</em> but a chapter list is per <em>series</em>, so queueing a
/// 500-chapter series used to list the same catalog 500 times: once per enqueued item, all fired
/// off in parallel, all funnelling through that source's shared rate limiter. Every one of those
/// requests then sat in the limiter's (unbounded) queue until the HttpClient timeout fired, so the
/// whole batch failed, the retry sweep re-queued it five minutes later, and it did the same thing
/// again. Single-flighting turns that batch back into one listing.
/// </para>
/// <para>
/// Successes only. A failed listing is not cached: a source that was briefly down should be retried
/// on the next item, not remembered as broken for the rest of the TTL.
/// </para>
/// </summary>
public sealed class SourceChapterListCache(TimeProvider time, ILogger<SourceChapterListCache> logger)
{
    /// <summary>
    /// Long enough to collapse one bulk enqueue (which is created in seconds and then resolves for as
    /// long as the rate limiter takes), short enough that a chapter uploaded while a batch is in
    /// flight is picked up by the next one rather than being invisible for an hour.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Bounds worst-case memory at one entry per (source, series) actively being downloaded. Anything
    /// past this drops the coldest entries rather than growing without limit on a huge library.
    /// </summary>
    private const int MaxEntries = 512;

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public IReadOnlyList<SourceChapter>? Chapters;
        public DateTime FetchedAt = DateTime.MinValue;
        public long LastUsedTicks;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>
    /// The source's chapter list, fetched at most once per <see cref="Ttl"/> per key. Concurrent
    /// callers for the same key wait on the first one rather than each issuing their own request;
    /// a waiter whose own token is cancelled gives up without disturbing the others.
    /// </summary>
    public async Task<IReadOnlyList<SourceChapter>> GetAsync(
        ISource source, string sourceSeriesId, string? languageFilter, CancellationToken ct = default)
    {
        var entry = _entries.GetOrAdd(Key(source, sourceSeriesId, languageFilter), _ => new Entry());
        var now = time.GetUtcNow().UtcDateTime;
        Volatile.Write(ref entry.LastUsedTicks, now.Ticks);

        if (IsFresh(entry, now))
        {
            return entry.Chapters!;
        }

        await entry.Gate.WaitAsync(ct);
        try
        {
            // Somebody else refreshed it while this call waited for the gate.
            if (IsFresh(entry, time.GetUtcNow().UtcDateTime))
            {
                return entry.Chapters!;
            }

            var chapters = await source.ListChaptersAsync(sourceSeriesId, languageFilter, ct);
            Fill(entry, chapters);
            return chapters;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// Records a listing somebody else already fetched. <c>ChapterSyncService</c> deliberately lists
    /// uncached, since a refresh has to see the site as it is right now, but a monitored refresh
    /// enqueues the chapters it just discovered immediately afterwards. Resolving those against an
    /// older cached listing would report the brand-new chapter as "not listed", so the fresh result
    /// is seeded here instead: refresh stays honest, and resolution stays consistent with it.
    /// </summary>
    public void Store(
        ISource source, string sourceSeriesId, string? languageFilter, IReadOnlyList<SourceChapter> chapters) =>
        Fill(_entries.GetOrAdd(Key(source, sourceSeriesId, languageFilter), _ => new Entry()), chapters);

    private static string Key(ISource source, string sourceSeriesId, string? languageFilter) =>
        $"{source.Name} {sourceSeriesId} {languageFilter ?? string.Empty}";

    private void Fill(Entry entry, IReadOnlyList<SourceChapter> chapters)
    {
        entry.Chapters = chapters;
        entry.FetchedAt = time.GetUtcNow().UtcDateTime;
        Volatile.Write(ref entry.LastUsedTicks, entry.FetchedAt.Ticks);
        Trim();
    }

    private bool IsFresh(Entry entry, DateTime now) =>
        entry.Chapters is not null && now - entry.FetchedAt < Ttl;

    /// <summary>
    /// Evicts expired entries, then the least recently used ones if still over the cap. Runs only
    /// after a listing lands, never on the cache-hit path.
    /// </summary>
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

        if (_entries.Count > MaxEntries)
        {
            var coldest = _entries
                .OrderBy(pair => Volatile.Read(ref pair.Value.LastUsedTicks))
                .Take(_entries.Count - MaxEntries)
                .ToList();
            foreach (var (key, _) in coldest)
            {
                _entries.TryRemove(key, out _);
            }
        }

        logger.LogDebug("Trimmed chapter-list cache to {Count} entries", _entries.Count);
    }
}
