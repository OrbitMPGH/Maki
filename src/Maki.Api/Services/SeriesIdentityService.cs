using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Re-attaches the history a deleted series left behind to the same series added back later.
/// <para>
/// A hard delete severs <c>StatsEvent.SeriesId</c> and <c>ReadingState.SeriesId</c> to NULL rather
/// than cascading, so the rows outlive the series — but nothing pointed them at the new row, and
/// the aggregation saw one history as two: an orphaned half with no cover and no link, and a live
/// half starting from zero. Worse for reading, where the high-water mark restarting means the same
/// chapters can be reported as read a second time.
/// </para>
/// <para>
/// Called on every series create, not just ones known to be re-adds — an add with nothing to adopt
/// costs one indexed lookup that matches no rows.
/// </para>
/// </summary>
public class SeriesIdentityService(MakiDbContext db, ILogger<SeriesIdentityService> logger)
{
    /// <summary>
    /// Adopts orphaned activity and reading rows into <paramref name="series"/>, which must already
    /// be saved. Returns how many rows moved, for logging.
    /// </summary>
    public async Task<(int Events, int ReadingStates)> AdoptOrphansAsync(Series series, CancellationToken ct)
    {
        // Two keys, because a re-added series usually resolves to a provider id while the rows it
        // is adopting may predate the column (repaired to a title key) or have been written when
        // the series had no ids yet. Both are exact matches; nothing fuzzy happens here.
        var key = SeriesIdentity.For(series);
        var titleKey = SeriesIdentity.ForTitle(series.Title);

        // SeriesKey is rewritten to the new series' canonical key too, not just SeriesId: the
        // aggregation groups by SeriesKey first (it survives a hard delete, SeriesId doesn't), so
        // an orphan still carrying its old title key would keep aggregating as a separate entry
        // from the live series' new events even after adoption.
        var events = await db.StatsEvents.IgnoreQueryFilters()
            .Where(e => e.SeriesId == null && e.SeriesKey != null &&
                        (e.SeriesKey == key || e.SeriesKey == titleKey))
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.SeriesId, series.Id)
                                       .SetProperty(e => e.SeriesKey, key), ct);

        var readingStates = await AdoptReadingStatesAsync(series, titleKey, ct);

        if (events > 0 || readingStates > 0)
        {
            logger.LogInformation(
                "Adopted {Events} activity event(s) and {States} reading state(s) from a previous copy of {Title} (key {Key})",
                events, readingStates, series.Title, key);
        }

        return (events, readingStates);
    }

    /// <summary>
    /// Re-points tombstoned reading marks (both keys null — the shape a hard delete leaves) at the
    /// new series.
    /// <para>
    /// Matched on normalized title alone: <c>ReadingState</c> carries no key column, and giving it
    /// one would not help, since the rows that matter here are exactly the ones whose series is
    /// gone. Merged rather than re-pointed blindly, because a user may already have a live row for
    /// this series from a Kavita scan that ran between the delete and the add — two rows with the
    /// same <c>SeriesId</c> are legal, but leaving the further-along one unmerged would let
    /// <c>PickAsync</c> answer with the lower mark and re-emit reads that were already counted.
    /// </para>
    /// </summary>
    private async Task<int> AdoptReadingStatesAsync(Series series, string titleKey, CancellationToken ct)
    {
        // Probe before materializing. The title comparison is a regex normalization SQLite cannot
        // express, so the rows themselves have to come back as tracked entities to be matched — but
        // an install that has never deleted a series has none, and this call sits on the hot path of
        // a bulk import. Answered from IX_ReadingStates_Tombstones, whose filter this predicate
        // matches exactly; the load below reuses the same index.
        if (!await db.ReadingStates.IgnoreQueryFilters()
                .AnyAsync(r => r.SeriesId == null && r.KavitaSeriesId == null, ct))
        {
            return 0;
        }

        var tombstones = await db.ReadingStates.IgnoreQueryFilters()
            .Where(r => r.SeriesId == null && r.KavitaSeriesId == null)
            .ToListAsync(ct);

        var matches = tombstones
            .Where(r => SeriesIdentity.ForTitle(r.Title) == titleKey)
            .ToList();
        if (matches.Count == 0)
        {
            return 0;
        }

        var live = await db.ReadingStates.IgnoreQueryFilters()
            .Where(r => r.SeriesId == series.Id)
            .ToListAsync(ct);

        var adopted = 0;
        foreach (var group in matches.GroupBy(r => r.UserId))
        {
            // Per user: their tombstones plus whatever they already have for this series.
            var existing = live.Where(r => r.UserId == group.Key)
                .OrderByDescending(r => r.MaxChapter)
                .FirstOrDefault();

            if (existing is null)
            {
                // Nothing live to merge into — promote the furthest tombstone and drop the rest.
                var best = group.OrderByDescending(r => r.MaxChapter).First();
                best.SeriesId = series.Id;
                best.Title = series.Title;
                best.UpdatedAt = DateTime.UtcNow;
                foreach (var extra in group.Where(r => r != best))
                {
                    db.ReadingStates.Remove(extra);
                }

                adopted++;
                continue;
            }

            // Forward-only, same rule the tracker itself follows: take the furthest of each mark
            // and the latest progress timestamp, never lower anything.
            foreach (var t in group)
            {
                existing.MaxChapter = Math.Max(existing.MaxChapter, t.MaxChapter);
                existing.MaxVolume = Math.Max(existing.MaxVolume, t.MaxVolume);
                existing.Finished |= t.Finished;
                if (t.LastProgressAt > existing.LastProgressAt)
                {
                    existing.LastProgressAt = t.LastProgressAt;
                }

                db.ReadingStates.Remove(t);
                adopted++;
            }

            existing.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return adopted;
    }
}
