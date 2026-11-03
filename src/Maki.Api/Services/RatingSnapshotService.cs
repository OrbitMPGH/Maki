using Maki.Core.Entities;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>Writes the reader's rating history (<see cref="RatingSnapshot"/>). Display only.</summary>
public static class RatingSnapshotService
{
    /// <summary>
    /// Records one score on the caller's scoped context. A repeat of the last entry (same score, same
    /// kind, same chapter) adds nothing; "still a 7" thirty chapters later does.
    /// </summary>
    public static async Task RecordAsync(MakiDbContext db, int userId, int seriesId, int rating, bool final,
        double? atChapter, RatingSnapshotOrigin origin, CancellationToken ct)
    {
        var last = await db.RatingSnapshots
            .Where(s => s.UserId == userId && s.SeriesId == seriesId)
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Select(s => new { s.Rating, s.Final, s.AtChapter })
            .FirstOrDefaultAsync(ct);
        if (last is not null && last.Rating == rating && last.Final == final && last.AtChapter == atChapter)
        {
            return;
        }

        db.RatingSnapshots.Add(new RatingSnapshot
        {
            UserId = userId, SeriesId = seriesId, Rating = rating, Final = final, AtChapter = atChapter,
            Origin = origin, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
