using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// When a read mark reaches the end of a series. Shared by the progress writer, which sets
/// <c>ReadingState.Finished</c>, and the reader manifest, which tells the end screen ahead of the
/// write that finishing this chapter will do it.
/// </summary>
public static class SeriesFinishRule
{
    public static bool Closes(SeriesStatus? status, double mark, decimal? highestNumber) =>
        status == SeriesStatus.Completed && mark > 0 &&
        highestNumber is { } highest && highest > 0 && Math.Floor(mark) >= Math.Floor((double)highest);

    /// <summary>The series' highest chapter number, the end <see cref="Closes"/> compares a mark against.</summary>
    public static Task<decimal?> HighestNumberAsync(MakiDbContext db, int seriesId, CancellationToken ct) =>
        db.Chapters
            .Where(c => c.SeriesId == seriesId && c.Number != null)
            .OrderByDescending(c => c.Number)
            .Select(c => c.Number)
            .FirstOrDefaultAsync(ct);
}
