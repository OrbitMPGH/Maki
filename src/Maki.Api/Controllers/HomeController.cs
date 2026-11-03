using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>One poster on the Home dashboard's Continue reading list.</summary>
/// <param name="ChapterLabel">Rendered server-side so the client never has to hold a series' whole
/// chapter list just to turn a chapter id into "Vol.3 Ch.12".</param>
/// <param name="Page">Resume position inside the chapter's slice; 0 means start from the beginning.</param>
/// <param name="LastReadAt">When anything in the series was last read, which is what orders the list.</param>
/// <param name="PageCount">Slice length snapshotted on the progress row. 0 on Kavita-imported rows,
/// which is what the UI keys off to hide the resume bar.</param>
/// <param name="UnreadChapters">Downloaded chapters in this series still unread.</param>
/// <param name="CoverAccent">The colour the cover lends to the card's bar and button, as
/// <c>#rrggbb</c>; null when the art has none to lend, and the card keeps the theme accent.</param>
public record HomeReadingItem(
    int SeriesId,
    string SeriesTitle,
    string? CoverUrl,
    int ChapterId,
    string ChapterLabel,
    int Page,
    int PageCount,
    DateTime LastReadAt,
    int UnreadChapters,
    string? CoverAccent);

public record HomeReadingResponse(IReadOnlyList<HomeReadingItem> ContinueReading);

/// <summary>A series that recently gained chapter files it still has unread.</summary>
/// <param name="AddedAt">Newest <c>ChapterFile.DateAdded</c> for this series.</param>
/// <param name="NewChapterCount">Unread files in the series' latest batch, the "4 new" badge.</param>
/// <param name="NewestChapterLabel">The newest unread file in that batch.</param>
/// <param name="ReadChapterId">Next unread downloaded chapter, for the card's Read affordance.</param>
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
public class HomeController(MakiDbContext db, ContinueReadingService continueReading, CoverAccentCache accents)
    : ControllerBase
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
    /// Files of one series count as one batch while each arrived within this long of the next.
    /// A backlog download lands minutes apart; weekly or daily releases fall outside it.
    /// </summary>
    private static readonly TimeSpan BatchGap = TimeSpan.FromHours(12);

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
    /// Most cards one rail returns. The rails scroll, so this is a safety ceiling rather than a
    /// layout choice; the scans above bound what there is to show long before it in practice.
    /// </summary>
    public const int MaxRailItems = 100;

    /// <summary>
    /// The reading list: every series with something left to read, most recently read first,
    /// whether the reader stopped part-way through a chapter or between two. A series resumes its
    /// part-read chapter when that is the last thing read in it. Otherwise it offers the next
    /// unread chapter, at the page reached if that chapter was started earlier, so an older
    /// part-read chapter the reader has since read past counts as abandoned.
    /// </summary>
    [HttpGet("reading")]
    public async Task<IActionResult> Reading([FromQuery] int limit = MaxRailItems, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxRailItems);

        // One bounded, index-ordered pass over the newest progress rows, grouped in memory. The list
        // is "most recently read first", so the newest RecentProgressScan rows contain every series
        // it could show; see the constant for why this is not a GROUP BY. Watched ticks are not
        // reading, so they neither put a series on the list nor bring back a hidden one.
        var recent = await db.ChapterProgress
            .AsNoTracking()
            .OwnedByScopeUser(db)
            .Where(p => !p.Watched)
            .OrderByDescending(p => p.UpdatedAt)
            .Take(RecentProgressScan)
            .Select(p => new { p.SeriesId, p.ChapterId, p.Completed, p.UnreadAt, p.PageIndex, p.PageCount, p.UpdatedAt })
            .ToListAsync(ct);

        // A removed series stays off the list until any of its progress rows moves past the
        // removal. The scan is newest-first, so its first row per series is that series' latest.
        var hiddenAt = await db.UserSeriesStates
            .AsNoTracking()
            .Where(s => s.HiddenFromHomeAt != null)
            .ToDictionaryAsync(s => s.SeriesId, s => s.HiddenFromHomeAt!.Value, ct);
        if (hiddenAt.Count > 0)
        {
            var lastTouched = recent
                .GroupBy(p => p.SeriesId)
                .ToDictionary(g => g.Key, g => g.First().UpdatedAt);
            recent = recent
                .Where(p => !hiddenAt.TryGetValue(p.SeriesId, out var at) || lastTouched[p.SeriesId] > at)
                .ToList();
        }

        // Tombstones excluded: a chapter the user just marked unread is the most recently touched
        // incomplete row, and resuming into it would hijack the list. It is still unread, so the
        // next-chapter resolver below offers it in its proper place.
        //
        // A part-read chapter whose file is gone (deleted or removed on purpose) has nothing to
        // resume into, so its series moves on to the next chapter that is actually there.
        var partReadIds = recent
            .Where(p => !p.Completed && p.UnreadAt == null && p.PageIndex > 0)
            .Select(p => p.ChapterId)
            .Distinct()
            .ToList();
        var onDisk = partReadIds.Count == 0
            ? []
            : (await db.Chapters
                .AsNoTracking()
                .Where(c => partReadIds.Contains(c.Id) && c.ChapterFileId != null)
                .Select(c => c.Id)
                .ToListAsync(ct))
            .ToHashSet();

        var resumable = recent
            .Where(p => !p.Completed && p.UnreadAt == null && p.PageIndex > 0 && onDisk.Contains(p.ChapterId))
            .GroupBy(p => p.ChapterId)
            .ToDictionary(g => g.Key, g => g.First());

        // Each series' newest reading row. Over-fetched: a candidate is dropped below when every
        // downloaded chapter in it is already read, and a user who finishes series outright would
        // otherwise get a short list. Bounded all the same, since each survivor costs a chapter
        // lookup in NextForAsync below.
        var candidates = recent
            .Where(p => p.Completed || (p.UnreadAt == null && p.PageIndex > 0))
            .GroupBy(p => p.SeriesId)
            .Select(g => g.First())
            .OrderByDescending(p => p.UpdatedAt)
            .Take(limit * 8)
            .ToList();

        if (candidates.Count == 0)
        {
            return Ok(new HomeReadingResponse([]));
        }

        var seriesIds = candidates.Select(c => c.SeriesId).ToList();
        var titles = await db.Series
            .AsNoTracking()
            .Active()
            .Where(s => seriesIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Title, s.CoverPath, s.LastMetadataRefresh })
            .ToDictionaryAsync(s => s.Id, ct);

        var resumeLabels = await ChapterLabelsAsync(
            candidates.Where(c => !c.Completed && resumable.ContainsKey(c.ChapterId)).Select(c => c.ChapterId).ToList(), ct);

        // Resolved a chunk at a time, in the same newest-first order, so the chapter lists of the
        // later candidates are only read when the list is still short of `limit`.
        var items = new List<HomeReadingItem>();
        foreach (var chunk in candidates.Chunk(limit * 2))
        {
            var next = await continueReading.NextForAsync(chunk.Select(c => c.SeriesId).ToList(), ct);
            foreach (var latest in chunk)
            {
                if (!titles.TryGetValue(latest.SeriesId, out var series))
                {
                    continue;
                }

                var upNext = next.GetValueOrDefault(latest.SeriesId);
                int chapterId;
                string label;
                if (!latest.Completed && resumable.ContainsKey(latest.ChapterId))
                {
                    chapterId = latest.ChapterId;
                    label = resumeLabels.GetValueOrDefault(chapterId, "Ch.?");
                }
                else if (upNext is not null)
                {
                    chapterId = upNext.ChapterId;
                    label = upNext.Label;
                }
                else
                {
                    continue; // nothing left to read in this series
                }

                var started = resumable.GetValueOrDefault(chapterId);
                items.Add(new HomeReadingItem(
                    series.Id,
                    series.Title,
                    SeriesDto.CoverUrlFor(series.Id, series.CoverPath, series.LastMetadataRefresh),
                    chapterId,
                    label,
                    started?.PageIndex ?? 0,
                    started?.PageCount ?? 0,
                    latest.UpdatedAt,
                    upNext?.UnreadChapters ?? 0,
                    accents.AccentFor(series.Id)));

                if (items.Count == limit)
                {
                    break;
                }
            }

            if (items.Count == limit)
            {
                break;
            }
        }

        return Ok(new HomeReadingResponse(items));
    }

    /// <summary>Takes a series off this user's reading rails until they next read it.</summary>
    [HttpPost("reading/{seriesId:int}/hide")]
    public Task<IActionResult> HideFromReading(int seriesId, CancellationToken ct) =>
        SetHiddenFromHomeAsync(seriesId, DateTime.UtcNow, ct);

    /// <summary>Undoes <see cref="HideFromReading"/>.</summary>
    [HttpDelete("reading/{seriesId:int}/hide")]
    public Task<IActionResult> UnhideFromReading(int seriesId, CancellationToken ct) =>
        SetHiddenFromHomeAsync(seriesId, null, ct);

    private async Task<IActionResult> SetHiddenFromHomeAsync(int seriesId, DateTime? at, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return NotFound();
        }

        try
        {
            await WriteHiddenFromHomeAsync(seriesId, at, ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            // A double click inserts twice for a series with no state row yet; the loser updates
            // the row the winner created.
            db.ChangeTracker.Clear();
            await WriteHiddenFromHomeAsync(seriesId, at, ct);
        }

        return NoContent();
    }

    private async Task WriteHiddenFromHomeAsync(int seriesId, DateTime? at, CancellationToken ct)
    {
        var state = await db.UserSeriesStates.FirstOrDefaultAsync(s => s.SeriesId == seriesId, ct);
        if (state is null)
        {
            if (at is null)
            {
                return;
            }

            state = new UserSeriesState { SeriesId = seriesId };
            db.UserSeriesStates.Add(state);
        }

        state.HiddenFromHomeAt = at;
        state.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
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
    /// get a row. Relinked files and in-place replacements are left out
    /// (<see cref="ChapterFile.IsNewContent"/>).
    /// </para>
    /// <para>
    /// Only a series' latest batch counts (see <see cref="BatchGap"/>), and only the files in it the
    /// user has not read. A series whose latest batch is all read drops off the rail.
    /// </para>
    /// </summary>
    [HttpGet("recently-added")]
    public async Task<IActionResult> RecentlyAdded([FromQuery] int limit = MaxRailItems, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, MaxRailItems);

        // Archived series are left out before the take, or a burst of their files would crowd the rail.
        var recentFiles = await db.ChapterFiles
            .AsNoTracking()
            .Where(ChapterFile.IsNewContent)
            .Where(f => db.Series.Active().Any(s => s.Id == f.SeriesId))
            .OrderByDescending(f => f.DateAdded)
            .Take(RecentFileScan)
            .Select(f => new { f.Id, f.SeriesId, f.DateAdded })
            .ToListAsync(ct);

        // Newest first within a series, cut at the first gap longer than BatchGap.
        var candidates = recentFiles
            .GroupBy(f => f.SeriesId)
            .Select(g =>
            {
                var files = g.OrderByDescending(f => f.DateAdded).ThenByDescending(f => f.Id).ToList();
                var batch = files.Take(1)
                    .Concat(files.Zip(files.Skip(1))
                        .TakeWhile(p => p.First.DateAdded - p.Second.DateAdded <= BatchGap)
                        .Select(p => p.Second))
                    .Select(f => f.Id)
                    .ToList();
                return new { SeriesId = g.Key, AddedAt = files[0].DateAdded, Batch = batch };
            })
            .OrderByDescending(g => g.AddedAt)
            .ToList();

        if (candidates.Count == 0)
        {
            return Ok(new List<HomeRecentSeriesItem>());
        }

        var seriesIds = candidates.Select(g => g.SeriesId).ToList();
        var titles = await db.Series
            .AsNoTracking()
            .Where(s => seriesIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Title, s.CoverPath, s.LastMetadataRefresh })
            .ToDictionaryAsync(s => s.Id, ct);

        // Resolved a chunk at a time, like Reading: series with nothing new left to read drop out,
        // so later candidates are only looked at while the rail is still short.
        var items = new List<HomeRecentSeriesItem>();
        foreach (var chunk in candidates.Chunk(limit * 2))
        {
            var next = await continueReading.NextForAsync(chunk.Select(c => c.SeriesId).ToList(), ct);
            var batchFileIds = chunk.SelectMany(c => c.Batch).ToList();
            var chaptersByFile = (await db.Chapters
                    .AsNoTracking()
                    .Where(c => c.ChapterFileId != null && batchFileIds.Contains(c.ChapterFileId!.Value))
                    .Select(c => new { c.Id, FileId = c.ChapterFileId!.Value, c.Number, c.Volume, c.Title, c.IsOneShot })
                    .ToListAsync(ct))
                .ToLookup(c => c.FileId);

            foreach (var candidate in chunk)
            {
                if (!titles.TryGetValue(candidate.SeriesId, out var series)
                    || !next.TryGetValue(candidate.SeriesId, out var upNext))
                {
                    continue;
                }

                var unreadFiles = candidate.Batch
                    .Where(id => chaptersByFile[id].Any(c => upNext.UnreadChapterIds.Contains(c.Id)))
                    .ToList();
                if (unreadFiles.Count == 0)
                {
                    continue;
                }

                // A volume file backs several chapters; label it with the lowest-numbered one it holds.
                var first = chaptersByFile[unreadFiles[0]]
                    .OrderBy(c => c.Number is null ? 1 : 0).ThenBy(c => c.Number).First();

                items.Add(new HomeRecentSeriesItem(
                    series.Id,
                    series.Title,
                    SeriesDto.CoverUrlFor(series.Id, series.CoverPath, series.LastMetadataRefresh),
                    candidate.AddedAt,
                    unreadFiles.Count,
                    ChapterLabel.For(first.Number, first.Volume, first.Title, first.IsOneShot),
                    upNext.ChapterId));

                if (items.Count == limit)
                {
                    break;
                }
            }

            if (items.Count == limit)
            {
                break;
            }
        }

        return Ok(items);
    }

    /// <summary>
    /// Library series whose anime the reader finished, starting after the last chapter it adapts.
    /// Taken from the action rather than the constructor because only this rail needs it.
    /// </summary>
    [HttpGet("from-anime")]
    public async Task<IActionResult> FromAnime(
        [FromServices] AnimeResumeService animeResume,
        [FromQuery] int limit = AnimeResumeService.RailLimit,
        CancellationToken ct = default)
    {
        return Ok(await animeResume.RailAsync(ct, Math.Clamp(limit, 1, AnimeResumeService.MaxRailLimit)));
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
