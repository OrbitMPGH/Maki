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
/// <see cref="AskAsync"/> is the question for everything else: every chapter the reader kept is read
/// and the chapters they switched off account for all that is still unread, or nothing is unread but
/// the automatic step did not run (the outlook held it back, MangaBaka marked the series Completed
/// after the last read, or the reader set it back to Reading). Switching chapters off says nothing
/// about whether they are extras or the rest of the story, so Maki asks instead of guessing.
/// </para>
/// Neither happens while the release outlook expects another chapter
/// (<see cref="ReleaseOutlookService.ExpectsMore"/>): a series can be over on MangaBaka while its
/// translation is still coming out. Both take an explicit user and ignore query filters, because the
/// progress writer runs in background jobs as well as requests.
/// </summary>
public static class SeriesCompletion
{
    public static async Task<bool> ReadThroughAsync(
        MakiDbContext db, int userId, Series series, bool unmonitorSpecials, CancellationToken ct) =>
        series.Status == SeriesStatus.Completed &&
        await TotalsAsync(db, userId, series, unmonitorSpecials, null, ct) is { Total: > 0 } t && t.Read >= t.Total;

    /// <summary>
    /// Whether to ask the reader if they are done with a finished series, and how many chapters they
    /// switched off; null means no ask. Asked when every chapter they kept is read, nothing they want
    /// is still to download, and the chapters still unread by the progress total are no more than the
    /// ones they switched off. That last test is what keeps a source missing the final chapters from
    /// asking: the MangaBaka total stays above what was kept. Never for a series that has not ended,
    /// while more chapters are expected, for a status other than Plan to read or Reading, or after
    /// "not yet". A None series stamps every chapter it discovers Wanted off, so its switches count as
    /// nothing left out there.
    /// <para>
    /// <paramref name="reading"/> is the chapter the end screen is on, counted as read: its completing
    /// write may not have landed when the screen asks. When reading it closes the series with nothing
    /// unread, the automatic step owns it. <paramref name="expectsMore"/> is only called once the cheap
    /// checks pass, since it builds the release outlook.
    /// </para>
    /// </summary>
    public static async Task<int?> AskAsync(
        MakiDbContext db, int userId, Series series, Func<Task<bool>> expectsMore, bool unmonitorSpecials,
        CancellationToken ct, Chapter? reading = null)
    {
        if (series.Status != SeriesStatus.Completed)
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

        var chapters = db.Chapters.IgnoreQueryFilters().Where(c => c.SeriesId == series.Id);
        var seen = ReadCounts.SeenFor(db, userId);
        var counts = await chapters
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Kept = g.Count(c => c.ChapterFileId != null || c.FileRemovedAt != null),
                Waiting = g.Count(c => c.Wanted && c.ChapterFileId == null && c.FileRemovedAt == null &&
                                       !seen.Any(p => p.ChapterId == c.Id)),
                LeftOut = g.Count(c => !c.Wanted && c.ChapterFileId == null && c.FileRemovedAt == null &&
                                       !seen.Any(p => p.ChapterId == c.Id)),
            })
            .FirstOrDefaultAsync(ct);
        if (counts is not { Kept: > 0, Waiting: 0 })
        {
            return null;
        }

        var readingId = reading?.Id;
        if (await chapters.AnyAsync(c => (c.ChapterFileId != null || c.FileRemovedAt != null) &&
                                         c.Id != readingId && !seen.Any(p => p.ChapterId == c.Id), ct))
        {
            return null;
        }

        var leftOut = series.MonitorNewItems == NewChapterMonitorMode.None ? 0 : counts.LeftOut;
        var totals = await TotalsAsync(db, userId, series, unmonitorSpecials, reading, ct);
        var unread = Math.Max(0, totals.Total - (totals.Read ?? 0));
        if (unread > leftOut)
        {
            return null;
        }

        if (unread == 0 && reading?.Number is { } number && SeriesFinishRule.Closes(
                series.Status, (double)number, await SeriesFinishRule.HighestNumberAsync(db, series.Id, ct)))
        {
            return null;
        }

        return await expectsMore() ? null : leftOut;
    }

    /// <summary>
    /// The series' progress totals for a named user. <paramref name="reading"/>, when given and not yet
    /// read, is counted as read.
    /// </summary>
    private static async Task<SeriesProgressScope.Totals> TotalsAsync(
        MakiDbContext db, int userId, Series series, bool unmonitorSpecials, Chapter? reading, CancellationToken ct)
    {
        var seen = ReadCounts.SeenFor(db, userId).Where(p => p.SeriesId == series.Id);
        var read = await seen.CountAsync(ct);
        var readSpecials = await seen.CountAsync(p => db.Chapters.IgnoreQueryFilters().Any(c =>
            c.Id == p.ChapterId && c.Number != null && c.Number != (decimal)(long)c.Number), ct);
        if (reading is not null && !await seen.AnyAsync(p => p.ChapterId == reading.Id, ct))
        {
            read++;
            if (Chapter.IsSpecial(reading.Number))
            {
                readSpecials++;
            }
        }

        var tallies = await LibraryListRows.ChapterTalliesAsync(
            db.Chapters.IgnoreQueryFilters().Where(c => c.SeriesId == series.Id), ReadCounts.SeenFor(db, userId), ct);
        return LibraryListRows.ProgressTotals(
            series, tallies.GetValueOrDefault(series.Id), read, readSpecials, unmonitorSpecials);
    }
}
