namespace Maki.Api.Dtos;

/// <summary>
/// One achievement as the client renders it: the definition, where the user stands against it, and
/// when each tier was earned.
/// </summary>
/// <param name="Tier">Highest tier earned, 0 for none.</param>
/// <param name="Value">The user's current figure for the graded metric.</param>
/// <param name="NextThreshold">What the next tier needs, or null at the top.</param>
/// <param name="UnlockedAt">When the current tier was earned, or null.</param>
public record AchievementDto(
    string Key,
    string Name,
    string Description,
    string Track,
    string Icon,
    bool Graded,
    bool Hidden,
    int Tier,
    string? TierName,
    long Value,
    long? NextThreshold,
    IReadOnlyList<long> Tiers,
    DateTime? UnlockedAt,
    /// <summary>
    /// The stored unlock's row id, set only on the lists built from stored rows. The reader's toast
    /// posts it back to stamp the unlock as seen, which is what stops it firing on every chapter
    /// after the one that earned it.
    /// </summary>
    int? UnlockId = null);

/// <param name="Progress">0..1 through the current level.</param>
public record LevelDto(int Level, long Xp, long IntoLevel, long LevelSpan, long NextLevelXp, double Progress);

public record ReadingGoalDto(int Id, string Period, string Metric, int Target, long Progress);

/// <summary>Everything Home's progress card needs, in one request.</summary>
public record GamificationSummaryDto(
    bool Enabled,
    bool ShowStreaks,
    LevelDto Level,
    long ChaptersRead,
    long ReadingSeconds,
    long SeriesFinished,
    long DaysRead,
    long CurrentStreak,
    long LongestStreak,
    int Earned,
    int Total,
    IReadOnlyList<AchievementDto> Recent,
    IReadOnlyList<ReadingGoalDto> Goals,
    IReadOnlyList<AchievementDto> Unseen);

public record HeatmapDayDto(DateOnly Date, int Chapters, int Seconds);

/// <summary>
/// A row of the household leaderboard. Aggregate figures only — never anything per series, since
/// opting in to being compared is not opting in to publishing a reading list.
/// </summary>
public record LeaderboardRowDto(int UserId, string Name, int Level, long ChaptersRead, long CurrentStreak);
