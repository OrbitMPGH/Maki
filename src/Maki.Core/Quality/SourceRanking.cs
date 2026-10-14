using Maki.Core.Entities;

namespace Maki.Core.Quality;

/// <summary>
/// Orders a series' sources best first under a profile: an allowed tier before a disallowed one,
/// then the profile's tier order, then score, then the manual priority.
/// </summary>
public static class SourceRanking
{
    public sealed record Entry(int MappingId, int Priority, QualityScore Score);

    public static IReadOnlyList<Entry> Order(UpgradeProfile profile, IEnumerable<Entry> entries) =>
    [
        .. entries
            .OrderByDescending(e => QualityScorer.Allows(profile, e.Score.Tier))
            .ThenByDescending(e => QualityScorer.Rank(profile, e.Score.Tier))
            .ThenByDescending(e => e.Score.Score)
            .ThenBy(e => e.Priority)
            .ThenBy(e => e.MappingId)
    ];
}
