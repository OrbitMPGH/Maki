using Maki.Core.Entities;

namespace Maki.Core.Quality;

public static class UpgradeProfileDefaults
{
    /// <summary>Highest first.</summary>
    public static IReadOnlyList<QualityTier> DefaultOrder { get; } =
        [QualityTier.Volume, QualityTier.Official, QualityTier.Scanlator, QualityTier.Aggregator, QualityTier.Unknown];

    /// <summary>
    /// Drops duplicate and undefined tiers, keeping the first occurrence, then appends any tier still
    /// missing in <see cref="DefaultOrder"/> as allowed and ungrouped. The first tier has nothing
    /// above it to group with.
    /// </summary>
    public static void Normalise(UpgradeProfile profile)
    {
        var seen = new HashSet<QualityTier>();
        var tiers = new List<ProfileTier>(DefaultOrder.Count);
        foreach (var tier in profile.Tiers)
        {
            if (Enum.IsDefined(tier.Tier) && seen.Add(tier.Tier))
            {
                tiers.Add(tiers.Count == 0 && tier.Grouped ? tier with { Grouped = false } : tier);
            }
        }

        foreach (var tier in DefaultOrder)
        {
            if (seen.Add(tier))
            {
                tiers.Add(new ProfileTier(tier, true));
            }
        }

        profile.Tiers = tiers;
    }
}
