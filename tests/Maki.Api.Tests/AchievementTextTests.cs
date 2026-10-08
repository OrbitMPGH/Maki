using Maki.Api.Localization;
using Maki.Core.Progress;

namespace Maki.Api.Tests;

public class AchievementTextTests
{
    private readonly TestLocalizer _localizer = new();

    [Fact]
    public void NameDescriptionAndTierComeFromTheCatalogueKeys()
    {
        var graded = AchievementCatalog.All.First(a => a.Graded);

        Assert.Equal($"achievement.{graded.Key}.name", _localizer.AchievementName(graded.Key));
        Assert.Equal($"achievement.{graded.Key}.description", _localizer.AchievementDescription(graded.Key));
        Assert.Equal("achievement.tier.3", _localizer.AchievementTier(graded, 3));
    }

    [Fact]
    public void AnUngradedAchievementOrTierZeroHasNoTierName()
    {
        var plain = AchievementCatalog.All.First(a => !a.Graded);
        var graded = AchievementCatalog.All.First(a => a.Graded);

        Assert.Null(_localizer.AchievementTier(plain, 1));
        Assert.Null(_localizer.AchievementTier(graded, 0));
    }
}
