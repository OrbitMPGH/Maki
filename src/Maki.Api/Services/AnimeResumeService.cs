using System.Linq.Expressions;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Core.Recommendations;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public enum AnimeResumeError { None, SeriesNotFound, NotEnabled, Unavailable }

public record AnimeResumeApplyResult(int Updated, int? ResumeChapterId, decimal CoveredTo);

/// <summary>
/// "Start where the anime ended": joins the reader's anime list against a series' anime coverage
/// and their own progress on it. Every surface returns nothing unless anime signals are on for this
/// reader, the same gate the sync runs behind.
/// </summary>
public class AnimeResumeService(
    MakiDbContext db,
    AnimeSignalSyncService signals,
    ReaderService reader,
    ReadingProgressService progress)
{
    private int UserId => db.Scope.UserId;

    private sealed record SeriesInfo(
        int Id,
        string Title,
        int? MangaBakaId,
        int? AniListId,
        int? MalId,
        string? AnimeStart,
        string? AnimeEnd,
        int? TotalChapters,
        string? CoverPath,
        DateTime? LastMetadataRefresh);

    private sealed record ChapterRow(
        int Id, int SeriesId, decimal? Number, int? Volume, string? Title, bool IsOneShot, bool HasFile);

    /// <param name="ReadTo">Highest number genuinely read.</param>
    /// <param name="HighestCompleted">Highest number read or watched.</param>
    /// <param name="UnmarkedIds">Every chapter row, in any language, of a number up to the frontier nobody finished.</param>
    private sealed record LibraryState(
        decimal? ReadTo,
        decimal? HighestCompleted,
        int UnmarkedCount,
        IReadOnlyList<int> UnmarkedIds,
        ChapterRow? Resume);

    public async Task<bool> SeriesVisibleAsync(int seriesId, CancellationToken ct) =>
        await db.Series.AsNoTracking().AnyAsync(s => s.Id == seriesId, ct);

    public async Task<SeriesAnimeResumeDto?> ForSeriesAsync(int seriesId, CancellationToken ct)
    {
        if (!await signals.EnabledForAsync(UserId, ct)) return null;

        var series = await SeriesAsync(seriesId, ct);
        if (series is null) return null;

        var chapters = await ChaptersAsync([series.Id], ct);
        var resume = Resolve(await RowsForSeriesAsync(series, ct), series, chapters);
        if (resume is null) return null;

        var completed = await CompletedAsync([series.Id], ct);
        var library = Library(resume.CoveredTo, chapters, completed);
        if (library.ReadTo > resume.CoveredTo) return null;

        var dismissedAt = await db.UserSeriesStates.AsNoTracking()
            .Where(s => s.SeriesId == series.Id)
            .Select(s => s.AnimeResumeDismissedAt)
            .FirstOrDefaultAsync(ct);
        if (Suppressed(resume, library, dismissedAt)) return null;

        return new SeriesAnimeResumeDto(
            resume.AnimeTitle,
            resume.Services,
            resume.Score,
            AnimeResumeBasisNames.Of(resume.Basis),
            resume.CoveredTo,
            library.Resume?.Number ?? NextAfter(resume.CoveredTo),
            resume.CoveredLabel,
            resume.NextLabel,
            resume.NextFrom,
            resume.NextTo,
            library.ReadTo,
            library.Resume?.Id,
            library.Resume is { } r ? ChapterLabel.For(r.Number, r.Volume, r.Title, r.IsOneShot) : null,
            library.Resume?.HasFile ?? false,
            library.UnmarkedCount);
    }

    public async Task<CatalogueAnimeResumeDto?> ForCatalogueAsync(
        long mangaBakaId, string? animeStart, string? animeEnd, int? totalChapters, CancellationToken ct)
    {
        if (!await signals.EnabledForAsync(UserId, ct)) return null;

        var rows = await Rows(db.AnimeSignals.Where(x => x.UserId == UserId && x.MangaBakaId == mangaBakaId))
            .ToListAsync(ct);
        if (rows.Count == 0) return null;

        var resume = AnimeResumeResolver.Resolve(
            AnimeSignalGrouping.Watched(rows), AnimeCoverage.Parse(animeStart, animeEnd), totalChapters);
        if (resume is null) return null;

        var inLibrary = await db.Series.AsNoTracking()
            .Where(s => s.MangaBakaId != null && s.MangaBakaId.Value == mangaBakaId)
            .OrderBy(s => s.Id)
            .Select(s => (int?)s.Id)
            .FirstOrDefaultAsync(ct);

        return new CatalogueAnimeResumeDto(
            resume.AnimeTitle,
            resume.Services,
            resume.Score,
            AnimeResumeBasisNames.Of(resume.Basis),
            resume.CoveredTo,
            NextAfter(resume.CoveredTo),
            resume.CoveredLabel,
            resume.NextLabel,
            resume.NextFrom,
            resume.NextTo,
            inLibrary);
    }

    /// <summary>
    /// Library series whose anime the reader finished and whose reading has not caught up with it.
    /// Once they have read or watched past the frontier, Jump back in covers the series instead.
    /// </summary>
    public async Task<IReadOnlyList<HomeAnimeResumeItem>> RailAsync(int limit, CancellationToken ct)
    {
        if (!await signals.EnabledForAsync(UserId, ct)) return [];

        var rows = await Rows(db.AnimeSignals.Where(x => x.UserId == UserId && x.MangaBakaId != null))
            .ToListAsync(ct);
        var candidates = rows
            .Where(r => r.Status is AnimeWatchStatus.Completed or AnimeWatchStatus.Watching)
            .Select(r => r.MangaBakaId!.Value)
            .Where(id => id is > 0 and <= int.MaxValue)
            .Select(id => (int)id)
            .Distinct()
            .ToList();
        if (candidates.Count == 0) return [];

        var series = await SeriesQuery(s => s.MangaBakaId != null && candidates.Contains(s.MangaBakaId.Value))
            .ToListAsync(ct);
        if (series.Count == 0) return [];

        var seriesIds = series.Select(s => s.Id).ToList();
        var states = await db.UserSeriesStates.AsNoTracking()
            .Where(s => seriesIds.Contains(s.SeriesId))
            .Select(s => new { s.SeriesId, s.HiddenFromHomeAt, s.AnimeResumeDismissedAt })
            .ToListAsync(ct);
        var hidden = states.Where(s => s.HiddenFromHomeAt != null).Select(s => s.SeriesId).ToHashSet();
        var dismissed = states.Where(s => s.AnimeResumeDismissedAt != null)
            .GroupBy(s => s.SeriesId)
            .ToDictionary(g => g.Key, g => g.First().AnimeResumeDismissedAt!.Value);

        var visible = series.Where(s => !hidden.Contains(s.Id)).ToList();
        var visibleIds = visible.Select(s => s.Id).ToList();
        var chaptersBySeries = (await ChaptersAsync(visibleIds, ct)).ToLookup(c => c.SeriesId);
        var completed = await CompletedAsync(visibleIds, ct);
        var rowsByManga = rows.ToLookup(r => r.MangaBakaId!.Value);

        var items = new List<HomeAnimeResumeItem>();
        foreach (var s in visible)
        {
            var chapters = chaptersBySeries[s.Id].ToList();
            var resume = Resolve(rowsByManga[s.MangaBakaId!.Value].ToList(), s, chapters);
            if (resume is null) continue;

            var library = Library(resume.CoveredTo, chapters, completed);
            if (library.HighestCompleted >= resume.CoveredTo) continue;
            if (Suppressed(resume, library, dismissed.TryGetValue(s.Id, out var at) ? at : null)) continue;

            items.Add(new HomeAnimeResumeItem(
                s.Id,
                s.Title,
                SeriesDto.CoverUrlFor(s.Id, s.CoverPath, s.LastMetadataRefresh),
                resume.AnimeTitle,
                resume.Score,
                resume.CoveredTo,
                resume.CoveredLabel,
                library.Resume?.Id,
                library.Resume is { } r ? ChapterLabel.For(r.Number, r.Volume, r.Title, r.IsOneShot) : null));
        }

        return items
            .OrderBy(i => i.Score is null ? 1 : 0)
            .ThenByDescending(i => i.Score)
            .ThenBy(i => i.SeriesTitle, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Ticks chapters up to the frontier off as watched, then raises the reading high-water mark to
    /// it whatever was ticked. The raise is what keeps the first real read after the anime from
    /// being counted as the whole run: <c>MarkWatchedAsync</c> only raises it as far as the
    /// downloaded chapters go.
    /// </summary>
    /// <param name="coveredToOverride">The reader's own end of the range, clamped to the end of the
    /// next season when there is one and to the resolved frontier otherwise.</param>
    public async Task<(AnimeResumeError Error, AnimeResumeApplyResult? Result)> ApplyAsync(
        int seriesId, bool markWatched, decimal? coveredToOverride, CancellationToken ct)
    {
        var (error, series, chapters, resume) = await ResolveForWriteAsync(seriesId, ct);
        if (error != AnimeResumeError.None) return (error, null);

        var upper = resume!.NextTo is { } nextTo ? Math.Max(resume.CoveredTo, nextTo) : resume.CoveredTo;
        var coveredTo = Math.Clamp(coveredToOverride ?? resume.CoveredTo, 1, upper);

        var updated = 0;
        if (markWatched)
        {
            var library = Library(coveredTo, chapters!, await CompletedAsync([series!.Id], ct));
            var ids = library.UnmarkedIds.Take(ReaderController.MaxBulkChapters).ToList();
            updated = await reader.MarkWatchedAsync(ids, ct);
        }

        await progress.ImportSilentAsync(UserId, series!.Id, kavitaSeriesId: null, series.Title,
            (double)coveredTo, 0, ct);

        var resumeChapter = ResumeAfter(coveredTo, chapters!);
        return (AnimeResumeError.None, new AnimeResumeApplyResult(updated, resumeChapter?.Id, coveredTo));
    }

    /// <summary>Hides the callout while it would name the same frontier. A new season brings it back.</summary>
    public async Task<AnimeResumeError> DismissAsync(int seriesId, CancellationToken ct)
    {
        var (error, series, _, resume) = await ResolveForWriteAsync(seriesId, ct);
        if (error != AnimeResumeError.None) return error;

        var state = await db.UserSeriesStates.FirstOrDefaultAsync(s => s.SeriesId == series!.Id, ct);
        if (state is null)
        {
            state = new UserSeriesState { SeriesId = series!.Id };
            db.UserSeriesStates.Add(state);
        }

        state.AnimeResumeDismissedAt = (double)resume!.CoveredTo;
        state.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return AnimeResumeError.None;
    }

    public async Task<AnimeResumeError> UndismissAsync(int seriesId, CancellationToken ct)
    {
        if (!await SeriesVisibleAsync(seriesId, ct)) return AnimeResumeError.SeriesNotFound;

        var state = await db.UserSeriesStates.FirstOrDefaultAsync(s => s.SeriesId == seriesId, ct);
        if (state?.AnimeResumeDismissedAt is null) return AnimeResumeError.None;

        state.AnimeResumeDismissedAt = null;
        state.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return AnimeResumeError.None;
    }

    private async Task<(AnimeResumeError, SeriesInfo?, List<ChapterRow>?, AnimeResume?)> ResolveForWriteAsync(
        int seriesId, CancellationToken ct)
    {
        var series = await SeriesAsync(seriesId, ct);
        if (series is null) return (AnimeResumeError.SeriesNotFound, null, null, null);
        if (!await signals.EnabledForAsync(UserId, ct)) return (AnimeResumeError.NotEnabled, null, null, null);

        var chapters = await ChaptersAsync([series.Id], ct);
        var resume = Resolve(await RowsForSeriesAsync(series, ct), series, chapters);
        return resume is null
            ? (AnimeResumeError.Unavailable, null, null, null)
            : (AnimeResumeError.None, series, chapters, resume);
    }

    private static AnimeResume? Resolve(IReadOnlyList<AnimeSignalRow> rows, SeriesInfo series, List<ChapterRow> chapters)
    {
        if (rows.Count == 0) return null;

        var lastKnown = chapters.Where(c => c.Number is not null).Select(c => c.Number).Max()
            ?? series.TotalChapters;
        return AnimeResumeResolver.Resolve(
            AnimeSignalGrouping.Watched(rows),
            AnimeCoverage.Parse(series.AnimeStart, series.AnimeEnd),
            lastKnown);
    }

    /// <summary>
    /// Shared by the series callout and the Home rail: nothing to tick off and nothing read means
    /// there is nothing to offer, and a dismissal holds until the frontier moves.
    /// </summary>
    private static bool Suppressed(AnimeResume resume, LibraryState library, double? dismissedAt) =>
        (library.UnmarkedCount == 0 && library.ReadTo is null) || dismissedAt == (double)resume.CoveredTo;

    /// <summary>Chapter identity is number plus language, so everything here counts numbers, not rows.</summary>
    private static LibraryState Library(
        decimal coveredTo, IReadOnlyList<ChapterRow> chapters, IReadOnlyDictionary<int, bool> completed)
    {
        var numbered = chapters.Where(c => c.Number is not null).ToList();

        var completedNumbers = numbered.Where(c => completed.ContainsKey(c.Id))
            .Select(c => c.Number!.Value).ToHashSet();
        var readTo = numbered.Where(c => completed.TryGetValue(c.Id, out var watched) && !watched)
            .Select(c => c.Number).Max();

        var unmarked = numbered
            .Where(c => c.Number <= coveredTo && !completedNumbers.Contains(c.Number!.Value))
            .OrderBy(c => c.Number)
            .ThenBy(c => c.Id)
            .ToList();

        return new LibraryState(
            readTo,
            completedNumbers.Count > 0 ? completedNumbers.Max() : null,
            unmarked.Select(c => c.Number!.Value).Distinct().Count(),
            unmarked.Select(c => c.Id).ToList(),
            ResumeAfter(coveredTo, chapters));
    }

    private static ChapterRow? ResumeAfter(decimal coveredTo, IReadOnlyList<ChapterRow> chapters) =>
        chapters
            .Where(c => c.Number > coveredTo)
            .OrderBy(c => c.Number)
            .ThenBy(c => c.HasFile ? 0 : 1)
            .ThenBy(c => c.Id)
            .FirstOrDefault();

    private static decimal NextAfter(decimal coveredTo) => Math.Floor(coveredTo) + 1;

    private IQueryable<SeriesInfo> SeriesQuery(Expression<Func<Series, bool>> filter) =>
        db.Series.AsNoTracking().Where(filter).Select(s => new SeriesInfo(
            s.Id, s.Title, s.MangaBakaId, s.AniListId, s.MalId, s.AnimeStart, s.AnimeEnd,
            s.TotalChapters, s.CoverPath, s.LastMetadataRefresh));

    private async Task<SeriesInfo?> SeriesAsync(int seriesId, CancellationToken ct) =>
        await SeriesQuery(s => s.Id == seriesId).FirstOrDefaultAsync(ct);

    /// <summary>
    /// The reader's anime rows for one series: by MangaBaka id, or by the tracker manga ids when
    /// the series never matched MangaBaka.
    /// </summary>
    private async Task<List<AnimeSignalRow>> RowsForSeriesAsync(SeriesInfo series, CancellationToken ct)
    {
        var mine = db.AnimeSignals.Where(x => x.UserId == UserId);
        if (series.MangaBakaId is int mangaBakaId)
        {
            return await Rows(mine.Where(x => x.MangaBakaId == mangaBakaId)).ToListAsync(ct);
        }

        if (series.AniListId is null && series.MalId is null) return [];

        long? aniList = series.AniListId;
        long? mal = series.MalId;
        return await Rows(mine.Where(x =>
                (aniList != null && x.AniListMangaId == aniList) || (mal != null && x.MalMangaId == mal)))
            .ToListAsync(ct);
    }

    private static IQueryable<AnimeSignalRow> Rows(IQueryable<AnimeSignal> query) =>
        query.AsNoTracking().Select(x => new AnimeSignalRow(
            x.Service, x.AnimeId, x.MalAnimeId, x.Title, x.Score, x.Status, x.MangaBakaId,
            x.Format, x.StartDate, x.EndDate, x.Episodes, x.Progress));

    private async Task<List<ChapterRow>> ChaptersAsync(IReadOnlyCollection<int> seriesIds, CancellationToken ct) =>
        seriesIds.Count == 0
            ? []
            : await db.Chapters.AsNoTracking()
                .Where(c => seriesIds.Contains(c.SeriesId))
                .Select(c => new ChapterRow(
                    c.Id, c.SeriesId, c.Number, c.Volume, c.Title, c.IsOneShot, c.ChapterFileId != null))
                .ToListAsync(ct);

    /// <summary>Completed progress rows, chapter id to whether the completion is only a watched tick.</summary>
    private async Task<Dictionary<int, bool>> CompletedAsync(IReadOnlyCollection<int> seriesIds, CancellationToken ct) =>
        seriesIds.Count == 0
            ? []
            : await db.ChapterProgress.AsNoTracking()
                .Where(p => seriesIds.Contains(p.SeriesId) && p.Completed)
                .ToDictionaryAsync(p => p.ChapterId, p => p.Watched, ct);
}
