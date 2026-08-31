using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// The one definition of "a chapter this user has read": a completed <see cref="ChapterProgress"/>
/// row whose chapter is downloaded.
/// <para>
/// Every read count the UI shows has to come from here. The series list, the series page and the
/// reader's own progress meter all render the same number for the same series, and three hand-written
/// copies of the condition drift the first time one of them changes — the failure that reads as a
/// series showing "0 read" on its own page while the grid draws a half-full ring. The rows are
/// already narrowed to the caller by the global query filter, so there is no user predicate here.
/// </para>
/// <para>
/// Deliberately not derived from <c>ReadingState.MaxChapter</c>: that mark is forward-only and covers
/// every chapter numbered below it, so one stale Kavita read leaves a series permanently reporting
/// chapters read that were never opened.
/// </para>
/// <para>
/// The two methods below differ by more than which user they narrow to, and that difference is
/// load-bearing. <see cref="Read"/> is the <em>UI</em> count and includes chapters marked
/// <see cref="ChapterProgress.Watched"/>: the whole point of watching a run off is that it stops
/// showing as left to read. <see cref="ReadFor"/> is the <em>progression</em> count and excludes
/// them, because a season ticked off from the anime must not hand out "fully read" achievements.
/// Anything new that counts reads has to pick a side on purpose.
/// </para>
/// </summary>
public static class ReadCounts
{
    public static IQueryable<ChapterProgress> Read(MakiDbContext db) =>
        db.ChapterProgress.Where(p => p.Completed &&
            db.Chapters.Any(c => c.Id == p.ChapterId && c.ChapterFileId != null));

    /// <summary>
    /// The same condition for a <em>named</em> user, minus watched chapters, bypassing the global
    /// filter. For the paths that
    /// have no ambient user to be narrowed to: an admin reading somebody else's stats, and background
    /// code with an unrestricted scope. The predicate is explicit precisely because the filter is off
    /// here — dropping it would return every user's rows rather than none.
    /// </summary>
    public static IQueryable<ChapterProgress> ReadFor(MakiDbContext db, int userId) =>
        db.ChapterProgress.IgnoreQueryFilters().Where(p => p.UserId == userId && p.Completed &&
            !p.Watched &&
            db.Chapters.IgnoreQueryFilters().Any(c => c.Id == p.ChapterId && c.ChapterFileId != null));
}
