using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// When a reader is done with a series. Two answers, kept apart on purpose:
/// <para>
/// <see cref="ReadThroughAsync"/> is the automatic one: the series has ended and every chapter its
/// progress total counts is read. It is only asked once the read mark already reached the last chapter
/// (<c>ReadingState.Finished</c>), so a source missing the final chapters, or a total that falls back to
/// the few chapters somebody kept to try a series, never completes it.
/// </para>
/// <para>
/// <see cref="AskAsync"/> is the question for everything else: every chapter the reader kept is
/// read, but chapters they switched off stand between them and the end, or the automatic step was
/// held back. Switching chapters off says
/// nothing about whether they are extras or the rest of the story, so Maki asks instead of guessing.
/// </para>
/// Neither happens while the release outlook expects another chapter
/// (<see cref="ReleaseOutlookService.ExpectsMore"/>): a series can be over on MangaBaka while its
/// translation is still coming out. Both take an explicit user and ignore query filters, because the
/// progress writer runs in background jobs as well as requests.
/// </summary>
public static class SeriesCompletion
{
    public static async Task<bool> ReadThroughAsync(
        MakiDbContext db, int userId, Series series, bool unmonitorSpecials, CancellationToken ct)
    {
        if (series.Status != SeriesStatus.Completed)
        {
            return false;
        }

        var seen = ReadCounts.SeenFor(db, userId).Where(p => p.SeriesId == series.Id);
        var read = await seen.CountAsync(ct);
        if (read == 0)
        {
            return false;
        }

        var readSpecials = await seen.CountAsync(p => db.Chapters.IgnoreQueryFilters().Any(c =>
            c.Id == p.ChapterId && c.Number != null && c.Number != (decimal)(long)c.Number), ct);
        var tallies = await LibraryListRows.ChapterTalliesAsync(
            db.Chapters.IgnoreQueryFilters().Where(c => c.SeriesId == series.Id), ct);
        var totals = LibraryListRows.ProgressTotals(
            series, tallies.GetValueOrDefault(series.Id), read, readSpecials, unmonitorSpecials);
        return totals.Total > 0 && totals.Read >= totals.Total;
    }

    /// <summary>
    /// Whether to ask the reader if they are done with a finished series, and how many chapters they
    /// left out of it; null means no ask. Asked when every chapter they kept is read, nothing they want
    /// is still to download, and either chapters they switched off are all that is left, or the read
    /// mark reached the end and the series was not completed on its own (the outlook held it back, or
    /// they set it back to Reading). Never while more chapters are expected
    /// (<paramref name="expectsMore"/>), for a series that has not ended, for a status other than Plan
    /// to read or Reading, after "not yet", or when the mark reached the last chapter Maki lists while
    /// the total says more exist (a source missing the final chapters).
    /// <paramref name="reading"/> is the chapter the end screen is on, counted as read: its completing
    /// write may not have landed when the screen asks. Closing the series with it is left to the
    /// automatic step.
    /// </summary>
    public static async Task<int?> AskAsync(
        MakiDbContext db, int userId, Series series, bool expectsMore, bool unmonitorSpecials,
        CancellationToken ct, Chapter? reading = null)
    {
        if (series.Status != SeriesStatus.Completed || expectsMore)
        {
            return null;
        }

        var state = await db.UserSeriesStates.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.UserId == userId && s.SeriesId == series.Id)
            .Select(s => new { s.Status, s.CompleteHintDismissedAt })
            .FirstOrDefaultAsync(ct);
        if (state is { CompleteHintDismissedAt: not null } ||
            state?.Status is not (null or ReadingStatus.PlanToRead or ReadingStatus.Reading))
        {
            return null;
        }

        if (reading?.Number is { } number && SeriesFinishRule.Closes(
                series.Status, (double)number, await SeriesFinishRule.HighestNumberAsync(db, series.Id, ct)))
        {
            return null;
        }

        var chapters = db.Chapters.IgnoreQueryFilters().Where(c => c.SeriesId == series.Id);
        var counts = await chapters
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Kept = g.Count(c => c.ChapterFileId != null || c.FileRemovedAt != null),
                Waiting = g.Count(c => c.Wanted && c.ChapterFileId == null && c.FileRemovedAt == null),
                LeftOut = g.Count(c => !c.Wanted && c.ChapterFileId == null && c.FileRemovedAt == null),
            })
            .FirstOrDefaultAsync(ct);
        if (counts is not { Kept: > 0, Waiting: 0 })
        {
            return null;
        }

        var seen = ReadCounts.SeenFor(db, userId);
        var readingId = reading?.Id;
        if (await chapters.AnyAsync(c => (c.ChapterFileId != null || c.FileRemovedAt != null) &&
                                         c.Id != readingId && !seen.Any(p => p.ChapterId == c.Id), ct))
        {
            return null;
        }

        var finished = await db.ReadingStates.IgnoreQueryFilters()
            .AnyAsync(r => r.UserId == userId && r.SeriesId == series.Id && r.Finished, ct);
        if (finished)
        {
            return await ReadThroughAsync(db, userId, series, unmonitorSpecials, ct) ? counts.LeftOut : null;
        }

        return counts.LeftOut > 0 ? counts.LeftOut : null;
    }
}
