using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>One poster on the Home dashboard's "Continue reading" or "Jump back in" rail.</summary>
/// <param name="ChapterLabel">Rendered server-side so the client never has to hold a series' whole
/// chapter list just to turn a chapter id into "Vol.3 Ch.12".</param>
/// <param name="Page">Resume position inside the chapter's slice; 0 means start from the beginning.</param>
/// <param name="PageCount">Slice length snapshotted on the progress row. 0 on Kavita-imported rows,
/// which is what the UI keys off to hide the resume bar.</param>
/// <param name="UnreadChapters">Downloaded chapters in this series still unread.</param>
public record HomeReadingItem(
    int SeriesId,
    string SeriesTitle,
    string? CoverUrl,
    int ChapterId,
    string ChapterLabel,
    int Page,
    int PageCount,
    DateTime LastReadAt,
    int UnreadChapters);

public record HomeReadingResponse(
    IReadOnlyList<HomeReadingItem> ContinueReading,
    IReadOnlyList<HomeReadingItem> JumpBackIn);

/// <summary>A series that recently gained chapter files.</summary>
/// <param name="AddedAt">Newest <c>ChapterFile.DateAdded</c> for this series.</param>
/// <param name="NewChapterCount">Files this series holds within the scanned window — the "+4" badge.</param>
/// <param name="ReadChapterId">Next unread downloaded chapter, for the card's Read affordance;
/// null when everything downloaded has been read.</param>
public record HomeRecentSeriesItem(
    int SeriesId,
    string SeriesTitle,
    string? CoverUrl,
    DateTime AddedAt,
    int NewChapterCount,
    string? NewestChapterLabel,
    int? ReadChapterId);

/// <summary>
/// View-shaped queries for the Home dashboard.
/// <para>
/// Two endpoints rather than one aggregate: reading progress and download state change on
/// completely different triggers, and an aggregate would refetch the expensive half on every cheap
/// change. There is deliberately no stats endpoint — the Library page already derives every tile
/// client-side from the series list Home loads anyway, and a server copy could silently disagree
/// with it.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/home")]
public class HomeController(MakiDbContext db, ContinueReadingService continueReading) : ControllerBase
{
    /// <summary>
    /// How many of the newest chapter files to group for the recently-added rail. An index-ordered
    /// LIMIT: SQLite walks IX_ChapterFiles_DateAdded backwards and stops. A GROUP BY over the whole
    /// table would not use the index at all. Deliberately not a time window — a dormant library
    /// would show an empty rail — and generous enough that one big import can't crowd every other
    /// series out.
    /// </summary>
    private const int RecentFileScan = 1500;

    /// <summary>
    /// How many of the most recently touched <c>ChapterProgress</c> rows to consider for the
    /// reading rails. Same shape as <see cref="RecentFileScan"/> and for the same reason: an
    /// index-ordered LIMIT off IX_ChapterProgress_UpdatedAt, walked backwards and stopped. A
    /// <c>GROUP BY SeriesId</c> over the unbounded table would not use that index and, after a
    /// Kavita read-status import, means aggregating every read chapter in the library on every
    /// single landing-page load. Generous enough that the newest rows still cover far more
    /// distinct series than either rail can show.
    /// </summary>
    private const int RecentProgressScan = 2000;

    /// <summary>
    /// The two reading rails. "Continue reading" is series with a chapter part-way through;
    /// "Jump back in" is series last touched by a <em>finished</em> chapter that still have
    /// something left to read, minus anything already in the first rail.
    /// </summary>
    [HttpGet("reading")]
    public async Task<IActionResult> Reading([FromQuery] int limit = 12, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 40);

        // One bounded, index-ordered pass over the newest progress rows, grouped in memory. Both
        // rails are "most recently touched first", so the newest RecentProgressScan rows contain
        // every series either of them could show — see the constant for why this is not a GROUP BY.
        var recent = await db.ChapterProgress
            .AsNoTracking()
            .OrderByDescending(p => p.UpdatedAt)
            .Take(RecentProgressScan)
            .Select(p => new { p.SeriesId, p.Completed, p.UnreadAt, p.PageIndex, p.UpdatedAt })
            .ToListAsync(ct);

        // Tombstones excluded: a chapter the user just marked unread is the most recently touched
        // incomplete row, and resuming into it would hijack "Continue reading". It is still unread,
        // so the Jump-back-in resolver below offers it in its proper place.
        var inProgressSeries = recent
            .Where(p => !p.Completed && p.UnreadAt == null && p.PageIndex > 0)
            .GroupBy(p => p.SeriesId)
            .Select(g => new { SeriesId = g.Key, Last = g.Max(p => p.UpdatedAt) })
            .OrderByDescending(x => x.Last)
            .Take(limit)
            .ToList();

        var continuedIds = inProgressSeries.Select(x => x.SeriesId).ToList();
        var continuedSet = continuedIds.ToHashSet();

        // Over-fetch: a candidate is dropped below if every downloaded chapter in it is already
        // read. The old multiplier of 2 was too tight — a user who finishes series outright got a
        // short or empty rail, because the handful of candidates fetched were exactly the ones
        // with nothing left. Bounded all the same, since each survivor costs a chapter lookup in
        // NextForAsync below.
        var finishedSeries = recent
            .Where(p => p.Completed && !continuedSet.Contains(p.SeriesId))
            .GroupBy(p => p.SeriesId)
            .Select(g => new { SeriesId = g.Key, Last = g.Max(p => p.UpdatedAt) })
            .OrderByDescending(x => x.Last)
            .Take(limit * 8)
            .ToList();

        var allIds = continuedIds.Concat(finishedSeries.Select(x => x.SeriesId)).ToList();
        if (allIds.Count == 0)
        {
            return Ok(new HomeReadingResponse([], []));
        }

        var titles = await db.Series
            .AsNoTracking()
            .Where(s => allIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Title, s.CoverPath })
            .ToDictionaryAsync(s => s.Id, ct);

        var next = await continueReading.NextForAsync(allIds, ct);

        // The actual in-progress rows for just the series above, newest per series.
        var resumeRows = (await db.ChapterProgress
                .Where(p => continuedIds.Contains(p.SeriesId)
                    && !p.Completed && p.UnreadAt == null && p.PageIndex > 0)
                .Select(p => new { p.SeriesId, p.ChapterId, p.PageIndex, p.PageCount, p.UpdatedAt })
                .ToListAsync(ct))
            .GroupBy(p => p.SeriesId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.UpdatedAt).First());

        var resumeLabels = await ChapterLabelsAsync(
            resumeRows.Values.Select(r => r.ChapterId).ToList(), ct);

        var continueRail = new List<HomeReadingItem>();
        foreach (var entry in inProgressSeries)
        {
            if (!titles.TryGetValue(entry.SeriesId, out var series) ||
                !resumeRows.TryGetValue(entry.SeriesId, out var row))
            {
                continue;
            }

            continueRail.Add(new HomeReadingItem(
                series.Id,
                series.Title,
                SeriesDto.CoverUrlFor(series.Id, series.CoverPath),
                row.ChapterId,
                resumeLabels.GetValueOrDefault(row.ChapterId, "Ch.?"),
                row.PageIndex,
                row.PageCount,
                row.UpdatedAt,
                next.GetValueOrDefault(entry.SeriesId)?.UnreadChapters ?? 0));
        }

        var jumpRail = new List<HomeReadingItem>();
        foreach (var entry in finishedSeries)
        {
            if (!titles.TryGetValue(entry.SeriesId, out var series) ||
                !next.TryGetValue(entry.SeriesId, out var upNext))
            {
                continue; // nothing left to read in this series
            }

            jumpRail.Add(new HomeReadingItem(
                series.Id,
                series.Title,
                SeriesDto.CoverUrlFor(series.Id, series.CoverPath),
                upNext.ChapterId,
                upNext.Label,
                0,
                0,
                entry.Last,
                upNext.UnreadChapters));

            if (jumpRail.Count == limit)
            {
                break;
            }
        }

        return Ok(new HomeReadingResponse(continueRail, jumpRail));
    }

    /// <summary>
    /// Series that recently gained chapter files, newest first.
    /// <para>
    /// Sourced from <c>ChapterFile.DateAdded</c>, not <c>StatsEvents</c>: the stats log is
    /// aggregated to one row per series per day so it cannot name the newest chapter, its
    /// <c>SeriesId</c> is set null on delete, and it was itself derived from these timestamps.
    /// </para>
    /// <para>
    /// Caveat worth knowing when the rail looks odd: a first-time adopt/import of an existing
    /// on-disk library stamps <c>DateAdded</c> = now on every file it discovers, so everything
    /// looks freshly added right after one. Rescans do not restamp — only files never seen before
    /// get a row.
    /// </para>
    /// </summary>
    [HttpGet("recently-added")]
    public async Task<IActionResult> RecentlyAdded([FromQuery] int limit = 12, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 40);

        var recentFiles = await db.ChapterFiles
            .AsNoTracking()
            .OrderByDescending(f => f.DateAdded)
            .Take(RecentFileScan)
            .Select(f => new { f.Id, f.SeriesId, f.DateAdded })
            .ToListAsync(ct);

        var bySeries = recentFiles
            .GroupBy(f => f.SeriesId)
            .Select(g => new
            {
                SeriesId = g.Key,
                AddedAt = g.Max(f => f.DateAdded),
                Count = g.Count(),
                NewestFileId = g.OrderByDescending(f => f.DateAdded).ThenByDescending(f => f.Id).First().Id
            })
            .OrderByDescending(g => g.AddedAt)
            .Take(limit)
            .ToList();

        if (bySeries.Count == 0)
        {
            return Ok(Array.Empty<HomeRecentSeriesItem>());
        }

        var seriesIds = bySeries.Select(g => g.SeriesId).ToList();

        var titles = await db.Series
            .AsNoTracking()
            .Where(s => seriesIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Title, s.CoverPath })
            .ToDictionaryAsync(s => s.Id, ct);

        var newestFileIds = bySeries.Select(g => g.NewestFileId).ToList();
        var labelByFile = (await db.Chapters
                .AsNoTracking()
                .Where(c => c.ChapterFileId != null && newestFileIds.Contains(c.ChapterFileId!.Value))
                .Select(c => new { FileId = c.ChapterFileId!.Value, c.Number, c.Volume, c.Title, c.IsOneShot })
                .ToListAsync(ct))
            // A volume file backs several chapters; label it with the lowest-numbered one it holds.
            .GroupBy(c => c.FileId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var first = g.OrderBy(c => c.Number is null ? 1 : 0).ThenBy(c => c.Number).First();
                    return ChapterLabel.For(first.Number, first.Volume, first.Title, first.IsOneShot);
                });

        var next = await continueReading.NextForAsync(seriesIds, ct);

        var items = bySeries
            .Where(g => titles.ContainsKey(g.SeriesId))
            .Select(g =>
            {
                var series = titles[g.SeriesId];
                return new HomeRecentSeriesItem(
                    series.Id,
                    series.Title,
                    SeriesDto.CoverUrlFor(series.Id, series.CoverPath),
                    g.AddedAt,
                    g.Count,
                    labelByFile.GetValueOrDefault(g.NewestFileId),
                    next.GetValueOrDefault(g.SeriesId)?.ChapterId);
            })
            .ToList();

        return Ok(items);
    }

    /// <summary>Labels for a set of chapter ids, in one query.</summary>
    private async Task<Dictionary<int, string>> ChapterLabelsAsync(
        IReadOnlyCollection<int> chapterIds, CancellationToken ct)
    {
        if (chapterIds.Count == 0)
        {
            return [];
        }

        return (await db.Chapters
                .AsNoTracking()
                .Where(c => chapterIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Number, c.Volume, c.Title, c.IsOneShot })
                .ToListAsync(ct))
            .ToDictionary(
                c => c.Id,
                c => ChapterLabel.For(c.Number, c.Volume, c.Title, c.IsOneShot));
    }

}
