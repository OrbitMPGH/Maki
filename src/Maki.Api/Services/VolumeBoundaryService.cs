using System.Collections.Concurrent;
using Maki.Api.Controllers;
using Maki.Core.Entities;
using Maki.Core.Parsing;
using Maki.Core.Scrobbling;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Chapter start pages inside Maki's own multi-chapter volume archives, which is what lets
/// <see cref="Maki.Core.Kavita.KavitaProgress.Compute"/> tell which chapters of a half-read Kavita
/// volume are finished. Shared by every Kavita path (scrobble tick, read import, live sync) so they
/// all reach the same answer for the same payload. A singleton because the scans are cached per
/// <see cref="ChapterFile"/> and re-run only when the file's size changes.
/// </summary>
public class VolumeBoundaryService(IServiceScopeFactory scopeFactory)
{
    private readonly ConcurrentDictionary<int, (long Size, VolumeChapterProgress.ChapterFileBoundaries Boundaries)>
        _cache = new();

    /// <summary>
    /// What <see cref="For"/> needs from the database, for many series at once: the files several
    /// chapters share, grouped by series, and the root folder each such series lives in.
    /// </summary>
    public sealed record Source(
        ILookup<int, int> SharedFilesBySeries,
        Dictionary<int, (string RelativePath, long Size)> Files,
        Dictionary<int, string> RootPaths);

    public async Task<Source> LoadAsync(
        int userId, bool allRootFolders, IReadOnlyCollection<int> seriesIds, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        db.Scope.SetUser(userId, allRootFolders);

        var chapterFiles = await db.Chapters.AsNoTracking()
            .Where(c => seriesIds.Contains(c.SeriesId) && c.ChapterFileId != null)
            .Select(c => new { c.SeriesId, FileId = c.ChapterFileId!.Value })
            .ToListAsync(ct);

        var shared = chapterFiles
            .GroupBy(c => c.FileId)
            .Where(g => g.Count() > 1)
            .Select(g => (SeriesId: g.First().SeriesId, FileId: g.Key))
            .ToList();
        var sharedFileIds = shared.Select(x => x.FileId).ToList();
        var sharedSeriesIds = shared.Select(x => x.SeriesId).Distinct().ToList();

        var files = sharedFileIds.Count == 0
            ? []
            : await db.ChapterFiles.AsNoTracking()
                .Where(f => sharedFileIds.Contains(f.Id))
                .Select(f => new { f.Id, f.RelativePath, f.Size })
                .ToDictionaryAsync(f => f.Id, f => (f.RelativePath, f.Size), ct);
        var roots = sharedSeriesIds.Count == 0
            ? []
            : await db.Series.AsNoTracking()
                .Where(s => sharedSeriesIds.Contains(s.Id))
                .Select(s => new { s.Id, s.RootFolder!.Path })
                .ToDictionaryAsync(s => s.Id, s => s.Path, ct);

        return new Source(shared.ToLookup(x => x.SeriesId, x => x.FileId), files, roots);
    }

    /// <summary>One series, for callers that resolve series one at a time. The series id is already
    /// chosen, so root-folder grants have nothing left to filter.</summary>
    public async Task<Dictionary<int, VolumeChapterProgress.ChapterFileBoundaries>> ForSeriesAsync(
        int userId, int seriesId, CancellationToken ct) =>
        For(await LoadAsync(userId, allRootFolders: true, [seriesId], ct), seriesId);

    /// <summary>
    /// Page boundaries of every multi-chapter volume archive belonging to one Maki series, keyed by
    /// volume number. Only archives where several <see cref="Chapter"/> rows share one
    /// <see cref="ChapterFile"/> (import/rescan grouped them) qualify; Maki's own per-chapter
    /// downloads need no refinement.
    /// </summary>
    public Dictionary<int, VolumeChapterProgress.ChapterFileBoundaries> For(Source source, int seriesId)
    {
        var result = new Dictionary<int, VolumeChapterProgress.ChapterFileBoundaries>();
        if (!source.RootPaths.TryGetValue(seriesId, out var rootFolderPath) || string.IsNullOrEmpty(rootFolderPath))
        {
            return result;
        }

        foreach (var fileId in source.SharedFilesBySeries[seriesId])
        {
            if (!source.Files.TryGetValue(fileId, out var file))
            {
                continue;
            }

            // A volume-range file (chapters spanning several volume numbers) has no
            // single Kavita "volume" to attach page boundaries to, so skip it.
            if (!int.TryParse(ChapterController.VolumeFileLabel(file.RelativePath), out var volumeNumber))
            {
                continue;
            }

            if (_cache.TryGetValue(fileId, out var cached) && cached.Size == file.Size)
            {
                result[volumeNumber] = cached.Boundaries;
                continue;
            }

            var absolutePath = Path.Combine(rootFolderPath, file.RelativePath);
            var (totalPages, boundaries) = VolumeChapterScanner.ScanCbzBoundaries(absolutePath);
            if (boundaries.Count == 0)
            {
                continue;
            }

            var entry = new VolumeChapterProgress.ChapterFileBoundaries(totalPages, boundaries);
            _cache[fileId] = (file.Size, entry);
            result[volumeNumber] = entry;
        }

        return result;
    }
}
