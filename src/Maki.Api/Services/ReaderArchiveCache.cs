using Maki.Core.Import;
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
/// <para>
/// A miss is computed once per key: a thumbnail strip prefetch arrives as dozens of concurrent
/// requests for the same uncached chapter, and they all await the first one's read. That read
/// runs inline on the first caller rather than through <c>Task.Run</c>: a central directory is one
/// small read at the end of the file, and a hop would only move the blocking to another pool
/// thread. It goes through <see cref="ReaderArchiveHandles"/>, so the page requests that follow
/// reuse the directory it parsed.
/// </para>
/// </summary>
public class ReaderArchiveCache(ILogger<ReaderArchiveCache> logger, TimeProvider? time = null)
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

    private sealed record Entry(long Size, string Path, ArchiveInfo Info)
    {
        /// <summary>Monotonic tick of the last read, for the eviction sweep.</summary>
        public long LastUsed { get; set; }
    }

    private readonly ConcurrentDictionary<int, Entry> _cache = new();
    private readonly ConcurrentDictionary<(int Id, long Size), Task<ArchiveInfo>> _loading = new();
    private readonly ReaderArchiveHandles _handles = new(time);
    private long _clock;
    private long _invalidations;
    private int _loads;

    /// <summary>How many misses actually read an archive. For tests.</summary>
    internal int Loads => Volatile.Read(ref _loads);

    internal ReaderArchiveHandles Handles => _handles;

    public async Task<ArchiveInfo> GetAsync(int chapterFileId, long size, string absolutePath, CancellationToken ct = default)
    {
        if (TryGetCached(chapterFileId, size, out var hit))
        {
            return hit;
        }

        var key = (chapterFileId, size);
        var load = new TaskCompletionSource<ArchiveInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = _loading.GetOrAdd(key, load.Task);
        if (pending != load.Task)
        {
            return await pending.WaitAsync(ct);
        }

        try
        {
            if (!TryGetCached(chapterFileId, size, out var info))
            {
                // A load that an Invalidate overtook may have read the old file, so it answers
                // the callers already waiting on it but is not kept.
                var generation = Interlocked.Read(ref _invalidations);
                info = await LoadAsync(absolutePath);
                if (Interlocked.Read(ref _invalidations) == generation)
                {
                    _cache[chapterFileId] = new Entry(size, absolutePath, info) { LastUsed = Interlocked.Increment(ref _clock) };
                    Trim();
                }
            }

            load.SetResult(info);
            return info;
        }
        catch (Exception e)
        {
            load.SetException(e);
            _ = load.Task.Exception; // observed, so an unawaited faulted load doesn't raise UnobservedTaskException
            throw;
        }
        finally
        {
            _loading.TryRemove(new KeyValuePair<(int, long), Task<ArchiveInfo>>(key, load.Task));
        }
    }

    /// <summary>
    /// One page of a library archive, read out of memory so the response can stream it after the
    /// archive's lock is released. A PDF page is rendered exactly as <see cref="CbzReader"/> does.
    /// </summary>
    public async Task<Stream?> OpenPageAsync(string absolutePath, string entryName, CancellationToken ct)
    {
        if (ComicFile.IsPdf(absolutePath))
        {
            return await CbzReader.OpenPageAsync(absolutePath, entryName, ct);
        }

        return await _handles.ReadPageAsync(absolutePath, entryName, ct);
    }

    /// <summary>
    /// Drops a file's cached page list and parsed directory. Needed because the size guard above
    /// cannot catch a replacement that happens to land on the same byte count (a re-download of
    /// the same chapter usually does), and stale page names mean the reader serves the wrong images.
    /// </summary>
    public void Invalidate(int chapterFileId)
    {
        Interlocked.Increment(ref _invalidations);
        foreach (var key in _loading.Keys.Where(k => k.Id == chapterFileId))
        {
            _loading.TryRemove(key, out _);
        }

        if (_cache.TryRemove(chapterFileId, out var entry))
        {
            _handles.Invalidate(entry.Path);
        }
    }

    private bool TryGetCached(int chapterFileId, long size, out ArchiveInfo info)
    {
        if (_cache.TryGetValue(chapterFileId, out var cached) && cached.Size == size)
        {
            cached.LastUsed = Interlocked.Increment(ref _clock);
            info = cached.Info;
            return true;
        }

        info = null!;
        return false;
    }

    private async Task<ArchiveInfo> LoadAsync(string absolutePath)
    {
        Interlocked.Increment(ref _loads);
        List<string> pages;
        if (ComicFile.IsPdf(absolutePath))
        {
            pages = CbzReader.PageNames(absolutePath);
        }
        else
        {
            try
            {
                pages = await _handles.PageNamesAsync(absolutePath, CancellationToken.None) ?? [];
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Could not read {Path}", absolutePath);
                pages = [];
            }
        }

        if (pages.Count == 0)
        {
            var format = ComicFile.IsPdf(absolutePath) ? ArchiveSignature.Format.Unknown : ArchiveSignature.Detect(absolutePath);
            if (ArchiveSignature.ComicExtension(format) is not null)
            {
                logger.LogWarning(
                    "No readable pages in {Path}: it is a {Format} archive under a .cbz name. A rescan of the series rebuilds it as a CBZ",
                    absolutePath, format);
            }
            else
            {
                logger.LogWarning("No readable pages in {Path}", absolutePath);
            }
        }

        return new ArchiveInfo(pages, VolumeChapterScanner.BoundariesInNames(pages));
    }

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
