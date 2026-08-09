using Maki.Core.Entities;

namespace Maki.Core.Progress;

/// <summary>
/// Whose milestone an achievement describes. The distinction is not cosmetic: reading is per user
/// here and the library on disk is shared, so "1,000 series" is a fact about the instance that would
/// be a lie told about whoever happened to be signed in.
/// </summary>
public enum AchievementTrack
{
    /// <summary>Earned by this user's own reading.</summary>
    Reader,

    /// <summary>Earned by the library itself, and so identical for everybody on the instance.</summary>
    Library
}

/// <summary>
/// One achievement, and the thresholds at which it pays out.
/// </summary>
/// <param name="Key">
/// Stable identifier, persisted in <c>UserAchievements.Key</c>. Same discipline as the
/// <c>MakiPermission</c> bits: never rename one and never reuse a retired one, or somebody's unlock
/// history silently re-points at a different achievement.
/// </param>
/// <param name="Value">
/// Reads the metric this achievement grades. For an ungraded one it returns 0 or 1.
/// </param>
/// <param name="Tiers">
/// Strictly ascending thresholds. A single-entry list is an ungraded achievement — earned once, with
/// no tier name shown.
/// </param>
/// <param name="Hidden">
/// Not listed until it is earned. Reserved for the small, odd ones: a grid full of locked secrets is
/// a checklist, which is the opposite of a surprise.
/// </param>
public record AchievementDefinition(
    string Key,
    string Name,
    string Description,
    AchievementTrack Track,
    string Icon,
    Func<UserMetrics, long> Value,
    IReadOnlyList<long> Tiers,
    bool Hidden = false)
{
    /// <summary>Whether tier names apply. False for the one-off achievements.</summary>
    public bool Graded => Tiers.Count > 1;

    /// <summary>Highest tier index (1-based) satisfied by <paramref name="metrics"/>; 0 for none.</summary>
    public int TierFor(UserMetrics metrics)
    {
        var value = Value(metrics);
        var tier = 0;
        for (var i = 0; i < Tiers.Count; i++)
        {
            if (value >= Tiers[i])
            {
                tier = i + 1;
            }
        }

        return tier;
    }
}

/// <summary>
/// The shipped achievements. Pure data with no infrastructure dependency, so the whole catalogue is
/// unit-testable the way <c>HomeLayoutSpec</c> is.
/// <para>
/// Tier steps are around 3x and never more than about 5x. That is the difference between a ladder and
/// a cliff: the point of grading an achievement is that the next rung stays visible, and a jump from
/// 2,000 to 10,000 chapters is not a rung, it is the end of the list with extra steps.
/// </para>
/// </summary>
public static class AchievementCatalog
{
    public static readonly string[] TierNames = ["Bronze", "Silver", "Gold", "Platinum", "Diamond", "Legend"];

    public const int MaxTier = 6;

    /// <summary>Hours, as the seconds the metrics actually carry.</summary>
    private static long Hours(int h) => h * 3600L;

    public static readonly IReadOnlyList<AchievementDefinition> All =
    [
        // ---- Reader, graded ----
        new("reader", "Reader", "Chapters read", AchievementTrack.Reader, "book",
            m => m.ChaptersRead + m.VolumesRead, [10, 50, 200, 750, 2500, 8000]),

        new("marathoner", "Marathoner", "Time spent reading", AchievementTrack.Reader, "clock",
            m => m.ReadingSeconds, [Hours(1), Hours(5), Hours(20), Hours(60), Hours(200), Hours(600)]),

        new("completionist", "Completionist", "Series finished", AchievementTrack.Reader, "flag",
            m => m.SeriesFinished, [1, 3, 10, 25, 60, 150]),

        new("explorer", "Explorer", "Different genres read", AchievementTrack.Reader, "compass",
            m => m.DistinctGenres, [3, 6, 10, 15, 22, 30]),

        new("regular", "Regular", "Days with reading", AchievementTrack.Reader, "calendar",
            m => m.DaysRead, [7, 21, 60, 150, 365, 750]),

        new("unbroken", "Unbroken", "Longest daily streak", AchievementTrack.Reader, "flame",
            m => m.LongestStreak, [3, 7, 14, 30, 60, 100]),

        // ---- Reader, one-off ----
        new("first-page", "First Page", "Read your first chapter", AchievementTrack.Reader, "sparkle",
            m => m.ChaptersRead + m.VolumesRead >= 1 ? 1 : 0, [1]),

        new("one-more-chapter", "One More Chapter", "Read for three hours in a single day",
            AchievementTrack.Reader, "moon",
            m => m.BestDaySeconds >= Hours(3) ? 1 : 0, [1]),

        new("lost-weekend", "Lost Weekend", "Read for eight hours across one weekend",
            AchievementTrack.Reader, "sofa",
            m => m.BestWeekendSeconds >= Hours(8) ? 1 : 0, [1]),

        new("long-haul", "The Long Haul", "Finish a series of 300 chapters or more",
            AchievementTrack.Reader, "mountain",
            m => m.LongestSeriesFinished >= 300 ? 1 : 0, [1]),

        new("clean-plate", "Clean Plate", "Read every downloaded chapter of a series",
            AchievementTrack.Reader, "check",
            m => m.SeriesFullyRead >= 1 ? 1 : 0, [1]),

        // Deliberately the four types that describe a real reading tradition. "other" is excluded:
        // it is the provider's shrug, so requiring it would make the achievement turn on metadata
        // quality rather than on what the reader read.
        new("range", "Range", "Read manga, manhwa, manhua and an OEL series", AchievementTrack.Reader, "globe",
            m => m.TypesRead.Contains(SeriesTypes.Manga) && m.TypesRead.Contains(SeriesTypes.Manhwa)
                 && m.TypesRead.Contains(SeriesTypes.Manhua) && m.TypesRead.Contains(SeriesTypes.Oel) ? 1 : 0, [1]),

        // ---- Reader, hidden ----
        new("night-owl", "Night Owl", "Read between 1am and 4am", AchievementTrack.Reader, "owl",
            m => m.ReadAfterMidnight ? 1 : 0, [1], Hidden: true),

        new("dawn-patrol", "Dawn Patrol", "Read between 5am and 7am", AchievementTrack.Reader, "sunrise",
            m => m.ReadAtDawn ? 1 : 0, [1], Hidden: true),

        new("back-from-the-dead", "Back from the Dead", "Pick a series back up after three months away",
            AchievementTrack.Reader, "ghost",
            m => m.ResumedAbandonedSeries ? 1 : 0, [1], Hidden: true),

        new("new-year-new-chapter", "New Year, New Chapter", "Read on the first of January",
            AchievementTrack.Reader, "confetti",
            m => m.ReadOnNewYearsDay ? 1 : 0, [1], Hidden: true),

        // ---- Library, graded ----
        new("librarian", "Librarian", "Series in the library", AchievementTrack.Library, "library",
            m => m.LibrarySeries, [10, 40, 120, 350, 800, 2000]),

        new("archivist", "Archivist", "Chapters downloaded", AchievementTrack.Library, "download",
            m => m.ChaptersDownloaded, [100, 500, 2500, 10000, 40000, 150000]),
    ];

    public static AchievementDefinition? Find(string key) =>
        All.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));

    /// <summary>Display name of a tier, or null for an ungraded achievement.</summary>
    public static string? TierName(AchievementDefinition definition, int tier) =>
        definition.Graded && tier >= 1 && tier <= TierNames.Length ? TierNames[tier - 1] : null;
}
