using Maki.Core.Entities;

namespace Maki.Core.Quality;

/// <summary>
/// Orders a series' sources best first under a profile: an allowed tier before a disallowed one, then
/// a source measured on this series before one that is not (a guess never beats a measurement, even
/// from a preferred tier), then the profile's tier order, then score, then the manual priority.
/// </summary>
public static class SourceRanking
{
    /// <param name="Measured">Scored on its own samples for this series, not on a guess.</param>
    public sealed record Entry(int MappingId, int Priority, QualityScore Score, bool Measured = true);

    public static IReadOnlyList<Entry> Order(UpgradeProfile profile, IEnumerable<Entry> entries) =>
    [
        .. entries
            .OrderByDescending(e => QualityScorer.Allows(profile, e.Score.Tier))
            .ThenByDescending(e => e.Measured)
            .ThenByDescending(e => QualityScorer.Rank(profile, e.Score.Tier))
            .ThenByDescending(e => e.Score.Score)
            .ThenBy(e => e.Priority)
            .ThenBy(e => e.MappingId)
    ];
}
