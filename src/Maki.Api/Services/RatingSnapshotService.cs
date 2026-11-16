using Maki.Core.Entities;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>Writes the reader's rating history (<see cref="RatingSnapshot"/>). Display only.</summary>
public static class RatingSnapshotService
{
    public static readonly TimeSpan CorrectionWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Records one score on the caller's scoped context. A change to the last entry's kind at the same
    /// chapter within <see cref="CorrectionWindow"/> replaces it, since a misclick and its correction
    /// are one score; "still a 7" thirty chapters later, or a rethink days later, is a new entry. The replaced entry keeps its origin, so a corrected prompt
    /// answer is still a checkpoint. Each entry carries the reader's rating breakdown as it stands.
    /// </summary>
    public static async Task RecordAsync(MakiDbContext db, int userId, int seriesId, double rating, bool final,
        double? atChapter, RatingSnapshotOrigin origin, CancellationToken ct)
    {
        var breakdown = await db.UserSeriesStates
            .Where(s => s.UserId == userId && s.SeriesId == seriesId)
            .Select(s => s.RatingBreakdownJson)
            .FirstOrDefaultAsync(ct);
        var last = await db.RatingSnapshots
            .Where(s => s.UserId == userId && s.SeriesId == seriesId)
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);
        if (last is not null && last.Final == final && last.AtChapter == atChapter
            && DateTime.UtcNow - last.CreatedAt < CorrectionWindow)
        {
            if (last.Rating != rating || last.BreakdownJson != breakdown)
            {
                last.Rating = rating;
                last.BreakdownJson = breakdown;
                last.CreatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            return;
        }

        db.RatingSnapshots.Add(new RatingSnapshot
        {
            UserId = userId, SeriesId = seriesId, Rating = rating, Final = final, AtChapter = atChapter,
            BreakdownJson = breakdown, Origin = origin, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
