using System.Collections.Concurrent;
using Maki.Core.Parsing;
using Maki.Core.Reading;

namespace Maki.Api.Services;

/// <summary>
/// Caches the page list and embedded chapter boundaries of each library archive, so paging
/// through a chapter does not reopen and re-enumerate the zip on every image request.
/// Keyed by ChapterFile id and invalidated when the file's size changes — the same rule the
/// scrobble sync's boundary cache used before this replaced it.
/// </summary>
public class ReaderArchiveCache(ILogger<ReaderArchiveCache> logger)
{
    /// <summary>
    /// Page names in reading order plus, in that same order, the zero-based page index at
    /// which each embedded chapter marker first appears.
    /// </summary>
    public record ArchiveInfo(
        IReadOnlyList<string> Pages,
        IReadOnlyList<(decimal Chapter, int PageIndex)> Boundaries);

    private readonly ConcurrentDictionary<int, (long Size, ArchiveInfo Info)> _cache = new();

    public ArchiveInfo Get(int chapterFileId, long size, string absolutePath)
    {
        if (_cache.TryGetValue(chapterFileId, out var cached) && cached.Size == size)
        {
            return cached.Info;
        }

        var pages = CbzReader.PageNames(absolutePath);
        var info = new ArchiveInfo(pages, VolumeChapterScanner.BoundariesInNames(pages));
        if (pages.Count == 0)
        {
            logger.LogWarning("No readable pages in {Path}", absolutePath);
        }

        _cache[chapterFileId] = (size, info);
        return info;
    }

    public void Invalidate(int chapterFileId) => _cache.TryRemove(chapterFileId, out _);
}
