using Maki.Core.Entities;
using Maki.Core.Quality;

namespace Maki.Core.Tests.Quality;

public class SourceRankingTests
{
    private static UpgradeProfile Profile(Action<UpgradeProfile>? configure = null)
    {
        var profile = new UpgradeProfile();
        configure?.Invoke(profile);
        UpgradeProfileDefaults.Normalise(profile);
        return profile;
    }

    private static SourceRanking.Entry Entry(int id, int priority, QualityTier tier, int score) =>
        new(id, priority, new QualityScore(tier, score, []));

    private static int[] Order(UpgradeProfile profile, params SourceRanking.Entry[] entries) =>
        [.. SourceRanking.Order(profile, entries).Select(e => e.MappingId)];

    [Fact]
    public void Tier_comes_before_score_and_score_before_priority()
    {
        Assert.Equal([3, 2, 1, 4], Order(Profile(),
            Entry(1, 1, QualityTier.Aggregator, 5),
            Entry(2, 2, QualityTier.Aggregator, 17),
            Entry(3, 3, QualityTier.Official, -10),
            Entry(4, 4, QualityTier.Aggregator, -12)));
    }

    [Fact]
    public void Priority_breaks_ties_then_id()
    {
        Assert.Equal([2, 1, 3], Order(Profile(),
            Entry(1, 2, QualityTier.Aggregator, 0),
            Entry(2, 1, QualityTier.Aggregator, 0),
            Entry(3, 2, QualityTier.Aggregator, 0)));
    }

    [Fact]
    public void The_profiles_own_tier_order_is_followed_and_a_disallowed_tier_goes_last()
    {
        var profile = Profile(p => p.Tiers =
        [
            new ProfileTier(QualityTier.Scanlator, true),
            new ProfileTier(QualityTier.Official, false),
            new ProfileTier(QualityTier.Aggregator, true)
        ]);

        Assert.Equal([2, 3, 1], Order(profile,
            Entry(1, 1, QualityTier.Official, 50),
            Entry(2, 2, QualityTier.Scanlator, 0),
            Entry(3, 3, QualityTier.Aggregator, 20)));
    }
}
