using System.Collections.Concurrent;
using Maki.Core.Parsing;
using Maki.Core.Reading;

namespace Maki.Api.Services;

/// <summary>
/// Caches the page list and embedded chapter boundaries of each library archive, so paging
/// through a chapter does not reopen and re-enumerate the zip on every image request.
/// Keyed by ChapterFile id and invalidated when the file's size changes — the same rule the
/// scrobble sync's boundary cache used before this replaced it.
/// <para>
/// Bounded, because this is a singleton and the entries are not small: a volume CBZ contributes
/// several hundred page-name strings, and an unbounded dictionary would keep every archive ever
/// opened for the lifetime of the process. Eviction is approximate LRU — good enough for a cache
/// whose miss cost is one zip directory read.
/// </para>
/// </summary>
public class ReaderArchiveCache(ILogger<ReaderArchiveCache> logger)
{
    /// <summary>
    /// How many archives to keep. A reader session touches one archive at a time, so this only
    /// has to cover casual jumping between series; it is a memory ceiling, not a working set.
    /// </summary>
    private const int Capacity = 256;

    /// <summary>
    /// Page names in reading order plus, in that same order, the zero-based page index at
    /// which each embedded chapter marker first appears.
    /// </summary>
    public record ArchiveInfo(
        IReadOnlyList<string> Pages,
        IReadOnlyList<(decimal Chapter, int PageIndex)> Boundaries);

    private sealed record Entry(long Size, ArchiveInfo Info)
    {
        /// <summary>Monotonic tick of the last read, for the eviction sweep.</summary>
        public long LastUsed { get; set; }
    }

    private readonly ConcurrentDictionary<int, Entry> _cache = new();
    private long _clock;

    public ArchiveInfo Get(int chapterFileId, long size, string absolutePath)
    {
        if (_cache.TryGetValue(chapterFileId, out var cached) && cached.Size == size)
        {
            cached.LastUsed = Interlocked.Increment(ref _clock);
            return cached.Info;
        }

        var pages = CbzReader.PageNames(absolutePath);
        var info = new ArchiveInfo(pages, VolumeChapterScanner.BoundariesInNames(pages));
        if (pages.Count == 0)
        {
            logger.LogWarning("No readable pages in {Path}", absolutePath);
        }

        _cache[chapterFileId] = new Entry(size, info) { LastUsed = Interlocked.Increment(ref _clock) };
        Trim();
        return info;
    }

    /// <summary>
    /// Drops a file's cached page list. Needed because the size guard above cannot catch a
    /// replacement that happens to land on the same byte count — a re-download of the same
    /// chapter usually does — and stale page names mean the reader serves the wrong images.
    /// </summary>
    public void Invalidate(int chapterFileId) => _cache.TryRemove(chapterFileId, out _);

    /// <summary>Evicts the least recently used entries once over capacity.</summary>
    private void Trim()
    {
        if (_cache.Count <= Capacity)
        {
            return;
        }

        foreach (var key in _cache
                     .OrderBy(kv => kv.Value.LastUsed)
                     .Take(_cache.Count - Capacity)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            _cache.TryRemove(key, out _);
        }
    }
}
