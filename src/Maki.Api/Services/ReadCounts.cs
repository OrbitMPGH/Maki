using Maki.Core.Entities;
using Maki.Data;

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
/// </summary>
public static class ReadCounts
{
    public static IQueryable<ChapterProgress> Read(MakiDbContext db) =>
        db.ChapterProgress.Where(p => p.Completed &&
            db.Chapters.Any(c => c.Id == p.ChapterId && c.ChapterFileId != null));
}
