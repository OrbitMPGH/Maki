using Maki.Core.Configuration;
using Maki.Core.Recommendations;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// A user's library in the recommender's seed space, and how much each entry should steer it.
/// </summary>
/// <param name="LibraryIds">
/// Every MangaBaka id the caller's library maps to, ordered by id. Ordered because callers fold it
/// into a cache key, where row order arriving from SQLite would otherwise produce a different key
/// for an unchanged library.
/// </param>
/// <param name="Weights">
/// MangaBaka id -> seed weight, for the entries that carry one. A rated series gets
/// <c>rating / 5.0</c> (10 → 2.0, 5 → 1.0 neutral, 1 → 0.2); an unrated one gets whatever its
/// reading history implies, or no entry at all when there is no history to read. Anything absent
/// is neutral 1.0 by convention, so this is deliberately sparse rather than dense.
/// </param>
public record SeedWeights(IReadOnlyList<long> LibraryIds, IReadOnlyDictionary<long, double> Weights);

/// <summary>
/// Builds the per-user seed weights the recommender steers with.
/// <para>
/// Extracted from <see cref="RecommendationService"/> rather than left inline because a second
/// caller now needs the identical number: the taste profile exists to explain the recommender, and
/// a profile computed from its own copy of this arithmetic would explain something else the first
/// time either side was tuned.
/// </para>
/// </summary>
public class SeedWeightService(BehavioralTasteService taste, TasteTuning tuning, IAppSettings settings)
{
    /// <param name="db">
    /// The caller's context, already narrowed with <c>db.Scope.SetUser</c>. Passed in rather than
    /// resolved so the library is read once per request: both this and
    /// <see cref="BehavioralTasteService"/> read it, and a second context would read it twice.
    /// </param>
    public async Task<SeedWeights> BuildAsync(
        MakiDbContext db, ICurrentUser scope, CancellationToken ct = default)
    {
        var rows = await db.Series
            .Where(s => s.MangaBakaId != null)
            .Select(s => new
            {
                Id = (long)s.MangaBakaId!.Value,
                Rating = db.UserSeriesStates
                    .Where(u => u.SeriesId == s.Id)
                    .Select(u => u.Rating)
                    .FirstOrDefault(),
            })
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        var libraryIds = rows.Select(r => r.Id).ToList();
        var seedWeights = new Dictionary<long, double>();
        foreach (var r in rows.Where(r => r.Rating is >= 1 and <= 10))
        {
            seedWeights[r.Id] = r.Rating!.Value / 5.0;
        }

        // Behavioural seeding runs in the same scope so the library is read once. At the shipped
        // RatingBlendAlpha of 1 a rated seed keeps its rating weight untouched, so this only ever
        // fills in seeds the user never rated.
        if (await TasteWeightingEnabledAsync(ct))
        {
            var behavioural = await taste.WeightsAsync(db, scope.UserId, libraryIds, ct);
            foreach (var (id, weight) in behavioural)
            {
                seedWeights[id] = seedWeights.TryGetValue(id, out var rated)
                    ? TasteWeights.Blend(rated, weight, tuning)
                    : weight;
            }
        }

        return new SeedWeights(libraryIds, seedWeights);
    }

    /// <summary>
    /// Whether behavioural seeding is on. Read per request rather than at startup so the switch takes
    /// effect on the next uncached pool instead of needing a restart; the read is one cached settings
    /// lookup, and the expensive part it guards is a full index scan.
    /// </summary>
    private async Task<bool> TasteWeightingEnabledAsync(CancellationToken ct)
    {
        if (tuning.IsUniform)
        {
            return false;
        }

        var value = await settings.GetAsync(SettingKeys.RecommendationsTasteWeighting, ct);
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }
}
