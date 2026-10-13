using Maki.Core.Progress;

namespace Maki.Api.Localization;

public static class AchievementText
{
    public static string AchievementName(this ILocalizer localizer, string key) =>
        localizer.Get($"achievement.{key}.name");

    public static string AchievementDescription(this ILocalizer localizer, string key) =>
        localizer.Get($"achievement.{key}.description");

    public static string? AchievementTier(this ILocalizer localizer, AchievementDefinition definition, int tier) =>
        AchievementCatalog.TierName(definition, tier) is null ? null : localizer.Get($"achievement.tier.{tier}");
}
