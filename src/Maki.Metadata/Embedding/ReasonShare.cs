namespace Maki.Metadata.Embedding;

/// <summary>
/// Which seeds a "Fans of X" card may name. A crowd channel's score for a pick is a sum over the
/// reader's seeds, and a famous title can collect it from many seeds that each push it a little.
/// Naming the largest of those small contributors makes a claim about two works that nothing
/// supports, so a seed is only named when it carries at least
/// <see cref="RecommenderTuning.MinReasonShare"/> of the pick's positive total.
/// </summary>
public static class ReasonShare
{
    /// <summary>
    /// The seeds that clear <paramref name="minShare"/> of the positive total, strongest first.
    /// Empty when none does. A share of 0 or less keeps every positive contributor.
    /// </summary>
    public static List<long> Keep(IReadOnlyList<(long Seed, double Contribution)> contributions, double minShare)
    {
        var positive = contributions.Where(c => c.Contribution > 0).ToList();
        var total = positive.Sum(c => c.Contribution);
        return positive
            .Where(c => minShare <= 0 || c.Contribution >= minShare * total)
            .OrderByDescending(c => c.Contribution)
            .ThenBy(c => c.Seed)
            .Select(c => c.Seed)
            .ToList();
    }
}
