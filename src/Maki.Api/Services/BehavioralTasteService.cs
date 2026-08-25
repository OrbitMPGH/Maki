using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Derives per-series seed weights from what a user has actually read, for the recommender to use on
/// seeds they never rated. Explicit ratings are sparse; completed chapters, time banked and how
/// recently they were read are not, and they say much the same thing.
/// <para>
/// Returns MangaBaka ids because that is the seed space the recommender works in — a library series
/// with no <c>MangaBakaId</c> cannot be a seed and so cannot carry a weight either. Series the user
/// has never opened produce no entry at all and stay at the implicit neutral weight: an unread
/// series is a backlog, not a dislike.
/// </para>
/// </summary>
public class BehavioralTasteService(TasteTuning tuning)
{
    /// <param name="db">
    /// The caller's context. Passed in rather than resolved because the only caller already opened a
    /// child scope and narrowed it, and a second context would read the library twice.
    /// </param>
    /// <param name="userId">
    /// Whose reading to read. Explicit, because every query below bypasses the global filter — see
    /// <see cref="ReadCounts.ReadFor"/> for why that is the right shape here and why dropping the
    /// predicate would return everybody's rows rather than nobody's.
    /// </param>
    /// <param name="visibleIds">
    /// MangaBaka ids of the library as the caller can see it, already read under the scoped query.
    /// Intersecting against it is what puts root-folder visibility back, since the reading queries
    /// themselves run with filters off.
    /// </param>
    public async Task<IReadOnlyDictionary<long, double>> WeightsAsync(
        MakiDbContext db, int userId, IReadOnlyCollection<long> visibleIds, CancellationToken ct = default)
    {
        if (tuning.IsUniform || userId <= 0 || visibleIds.Count == 0)
        {
            return new Dictionary<long, double>();
        }

        var read = await ReadCounts.ReadFor(db, userId)
            .GroupBy(p => p.SeriesId)
            .Select(g => new
            {
                SeriesId = g.Key,
                Completed = g.Count(),
                Seconds = g.Sum(p => (long)p.ReadSeconds),
                LastReadAt = g.Max(p => p.UpdatedAt),
            })
            .ToListAsync(ct);

        if (read.Count == 0)
        {
            return new Dictionary<long, double>();
        }

        var candidates = read.Select(r => r.SeriesId).ToList();

        var downloaded = await db.Chapters.IgnoreQueryFilters()
            .Where(c => candidates.Contains(c.SeriesId) && c.ChapterFileId != null)
            .GroupBy(c => c.SeriesId)
            .Select(g => new { SeriesId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SeriesId, x => x.Count, ct);

        // Fully-incognito series are dropped here rather than anywhere upstream: their ChapterProgress
        // rows exist (only the StatsEvents are suppressed), so this is the second aggregate that has
        // to write the gate out by hand — UserMetricsService.FullyReadAsync is the other one.
        // ScrobbleOnly is deliberately kept: it already counts in Rewind and read history.
        var mangaBakaIds = await db.Series.IgnoreQueryFilters()
            .Where(s => candidates.Contains(s.Id)
                        && s.MangaBakaId != null
                        && s.Incognito != IncognitoMode.Full)
            .Select(s => new { s.Id, MangaBakaId = (long)s.MangaBakaId!.Value })
            .ToDictionaryAsync(x => x.Id, x => x.MangaBakaId, ct);

        var visible = visibleIds as IReadOnlySet<long> ?? visibleIds.ToHashSet();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var weights = new Dictionary<long, double>();

        foreach (var row in read)
        {
            if (!mangaBakaIds.TryGetValue(row.SeriesId, out var id) || !visible.Contains(id))
            {
                continue;
            }

            var signal = new SeriesReadSignal(
                row.Completed, downloaded.GetValueOrDefault(row.SeriesId), row.Seconds, row.LastReadAt);
            var weight = TasteWeights.Weight(signal, today, tuning);
            if (Math.Abs(weight - TasteWeights.Neutral) < 1e-9)
            {
                continue; // nothing to say about this seed; leaving it out keeps the cache key short
            }

            // MangaBakaId carries no unique index, so two local series can map to one catalogue entry
            // (a split release, a re-add that kept both rows). Keep the strongest evidence rather than
            // whichever row the scan happened to reach last, so the weight does not depend on row order.
            weights[id] = weights.TryGetValue(id, out var existing) ? Math.Max(existing, weight) : weight;
        }

        return weights;
    }
}
