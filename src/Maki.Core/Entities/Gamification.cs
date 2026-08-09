using Maki.Core.Security;

namespace Maki.Core.Entities;

/// <summary>
/// One tier of one achievement, earned by one user. The <em>only</em> thing this feature persists:
/// every number behind it is recomputed from <c>StatsEvents</c> on demand, so an unlock row records
/// a moment, not a running total.
/// <para>
/// One row per tier rather than a single row whose tier is bumped, so the Stats page can say when
/// each rung was reached and the year story can list what was earned inside its window.
/// </para>
/// <para>
/// Unlocks are <b>never revoked</b>. Deleting series drops the library back under a threshold and the
/// badge stays, the same stickiness <c>ChapterProgress.Completed</c> has. Taking one back would make
/// the feature punitive and would need a second reconciliation path that could only ever disagree
/// with the first.
/// </para>
/// </summary>
public class UserAchievement : IUserOwned
{
    public int Id { get; set; }

    public int UserId { get; set; }

    /// <summary>
    /// An <c>AchievementCatalog</c> key. Same discipline as the <c>MakiPermission</c> bits: never
    /// rename one and never reuse a retired one, or somebody's history re-points at a different
    /// achievement. A key this build no longer knows is simply not rendered.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>1-based tier index. Always 1 for an ungraded achievement.</summary>
    public int Tier { get; set; }

    public DateTime UnlockedAt { get; set; }

    /// <summary>
    /// Null until the user has been shown it. What makes the reader's unlock toast fire exactly
    /// once, rather than on every chapter after the one that earned it.
    /// </summary>
    public DateTime? SeenAt { get; set; }
}

/// <summary>Over what span a <see cref="ReadingGoal"/> is measured.</summary>
public enum GoalPeriod
{
    Day = 0,
    Week = 1,
    Month = 2,
    Year = 3
}

/// <summary>What a <see cref="ReadingGoal"/> counts.</summary>
public enum GoalMetric
{
    Chapters = 0,
    Minutes = 1,
    SeriesFinished = 2
}

/// <summary>
/// A target the user set for themselves. Deliberately self-chosen and absent by default: an unasked-for
/// target is a chore, and Maki's whole posture is that the UI reflects state rather than demanding
/// anything.
/// <para>
/// Progress is derived from the event log like everything else here, so the row holds only the target.
/// Clearing a goal deletes the row, mirroring the "an empty spec deletes the row" rule the saved
/// Discover defaults use: "no goal" and "a goal of zero" are the same state and should not be two.
/// </para>
/// </summary>
public class ReadingGoal : IUserOwned
{
    public int Id { get; set; }
    public int UserId { get; set; }

    public GoalPeriod Period { get; set; }
    public GoalMetric Metric { get; set; }

    /// <summary>How many chapters, minutes or finished series the period is aiming at.</summary>
    public int Target { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
