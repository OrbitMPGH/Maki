using Maki.Core.Gamification;

namespace Maki.Core.Tests;

public class AchievementCatalogTests
{
    [Fact]
    public void KeysAreUnique()
    {
        // Keys are persisted in UserAchievements.Key. A duplicate would make two achievements share
        // one unlock history, and the unique index would then reject the second one's tiers.
        var keys = AchievementCatalog.All.Select(a => a.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void KeysAreStableSlugs()
    {
        Assert.All(AchievementCatalog.All, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Key));
            Assert.Equal(a.Key.Trim().ToLowerInvariant(), a.Key);
        });
    }

    [Fact]
    public void TiersAscendAndFitTheTierNames()
    {
        Assert.All(AchievementCatalog.All, a =>
        {
            Assert.NotEmpty(a.Tiers);
            Assert.True(a.Tiers.Count <= AchievementCatalog.MaxTier,
                $"{a.Key} has more tiers than there are names for");

            for (var i = 1; i < a.Tiers.Count; i++)
            {
                Assert.True(a.Tiers[i] > a.Tiers[i - 1],
                    $"{a.Key} tier {i + 1} does not exceed tier {i}");
            }
        });
    }

    [Fact]
    public void NoTierStepIsACliff()
    {
        // The whole reason six tiers exist instead of five: the next rung has to stay in sight. A
        // step much past 5x stops being a ladder and becomes the end of the list.
        Assert.All(AchievementCatalog.All.Where(a => a.Graded), a =>
        {
            for (var i = 1; i < a.Tiers.Count; i++)
            {
                var ratio = (double)a.Tiers[i] / Math.Max(1, a.Tiers[i - 1]);
                Assert.True(ratio <= 5.5,
                    $"{a.Key} jumps {ratio:F1}x from {a.Tiers[i - 1]} to {a.Tiers[i]}");
            }
        });
    }

    [Fact]
    public void GradedMeansMoreThanOneTier()
    {
        Assert.All(AchievementCatalog.All, a => Assert.Equal(a.Tiers.Count > 1, a.Graded));
    }

    [Fact]
    public void UngradedAchievementsShowNoTierName()
    {
        Assert.All(AchievementCatalog.All.Where(a => !a.Graded),
            a => Assert.Null(AchievementCatalog.TierName(a, 1)));
    }

    [Fact]
    public void TierForCountsEveryRungReached()
    {
        var reader = AchievementCatalog.Find("reader");
        Assert.NotNull(reader);

        Assert.Equal(0, reader.TierFor(new UserMetrics { ChaptersRead = 9 }));
        Assert.Equal(1, reader.TierFor(new UserMetrics { ChaptersRead = 10 }));
        Assert.Equal(reader.Tiers.Count, reader.TierFor(new UserMetrics { ChaptersRead = 1_000_000 }));
    }

    [Fact]
    public void HiddenAchievementsAreAllUngraded()
    {
        // A hidden achievement is a surprise, and a surprise with a progress bar is not one.
        Assert.All(AchievementCatalog.All.Where(a => a.Hidden), a => Assert.False(a.Graded));
    }

    [Fact]
    public void EveryAchievementReadsAnEmptySnapshotWithoutThrowing()
    {
        var empty = new UserMetrics();
        Assert.All(AchievementCatalog.All, a => Assert.Equal(0, a.TierFor(empty)));
    }

    [Fact]
    public void LibraryTrackIsOnlyTheLibraryCounters()
    {
        // The split is load-bearing on a multi-user instance: these come from the null-UserId events
        // and describe what is on disk, so presenting them as somebody's reading would be a lie.
        var library = AchievementCatalog.All.Where(a => a.Track == AchievementTrack.Library)
            .Select(a => a.Key).OrderBy(k => k, StringComparer.Ordinal);
        Assert.Equal(["archivist", "librarian"], library);
    }
}
