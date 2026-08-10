using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Progress;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Compares the catalogue against a user's recomputed metrics and records what they have earned.
/// <para>
/// Idempotent and forward-only. It runs on every chapter completion <em>and</em> lazily whenever the
/// progress endpoints are read, which is deliberate: reads that arrive through the Kavita scrobble
/// pass or OPDS never touch the reader's completion path, so without the lazy call those users would
/// never unlock anything. Running twice has to be free, and the unique index on
/// <c>(UserId, Key, Tier)</c> is the backstop when two calls race.
/// </para>
/// <para>
/// It never deletes. An achievement whose metric later falls back below its threshold stays earned —
/// see <see cref="UserAchievement"/> for why.
/// </para>
/// </summary>
public class AchievementService(
    MakiDbContext db,
    UserMetricsService metrics,
    IUserSettingsStore userSettings,
    InboxService inbox,
    TimeProvider clock,
    ILogger<AchievementService> logger)
{
    /// <summary>
    /// Evaluates and persists. Returns only what was newly unlocked by <em>this</em> call, which is
    /// what the reader's toast shows.
    /// </summary>
    public async Task<IReadOnlyList<UserAchievement>> EvaluateAsync(int userId, CancellationToken ct = default)
    {
        if (!await EnabledForAsync(userId, ct))
        {
            return [];
        }

        var snapshot = await metrics.GetAsync(userId, ct);
        return await EvaluateAsync(userId, snapshot, ct);
    }

    /// <summary>Overload for callers that already hold a snapshot, so a page load computes it once.</summary>
    public async Task<IReadOnlyList<UserAchievement>> EvaluateAsync(
        int userId, UserMetrics snapshot, CancellationToken ct = default)
    {
        var held = await db.UserAchievements.IgnoreQueryFilters()
            .Where(a => a.UserId == userId)
            .Select(a => new { a.Key, a.Tier })
            .ToListAsync(ct);

        var already = held.Select(h => (h.Key, h.Tier)).ToHashSet();
        var now = clock.GetUtcNow().UtcDateTime;
        var unlocked = new List<UserAchievement>();

        foreach (var definition in AchievementCatalog.All)
        {
            var tier = definition.TierFor(snapshot);

            // Every tier up to the one earned, not just the top one. A user who imports a large
            // history, or who first enables the feature years in, has genuinely passed the lower
            // rungs, and a grid showing Legend with Bronze still locked would be nonsense.
            for (var t = 1; t <= tier; t++)
            {
                if (already.Contains((definition.Key, t)))
                {
                    continue;
                }

                unlocked.Add(new UserAchievement
                {
                    UserId = userId,
                    Key = definition.Key,
                    Tier = t,
                    UnlockedAt = now,
                });
            }
        }

        if (unlocked.Count == 0)
        {
            // Still check the level: reading chapters raises it without unlocking anything.
            await NotifyLevelAsync(userId, snapshot, held.Select(h => h.Tier), ct);
            return [];
        }

        db.UserAchievements.AddRange(unlocked);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            // Another call got there first — the completion path and a page load racing. The rows are
            // already recorded, so there is nothing to repair; this call simply has nothing new to
            // report. Detached so the failed inserts do not stay pending on the shared context.
            foreach (var row in unlocked)
            {
                db.Entry(row).State = EntityState.Detached;
            }

            logger.LogDebug("Achievement unlock raced for user {UserId}; rows already recorded", userId);
            await NotifyLevelAsync(userId, snapshot, held.Select(h => h.Tier), ct);
            return [];
        }

        NotifyUnlocks(userId, unlocked);
        await NotifyLevelAsync(
            userId, snapshot, held.Select(h => h.Tier).Concat(unlocked.Select(u => u.Tier)), ct);

        return unlocked;
    }

    /// <summary>
    /// One inbox row per achievement, at the highest tier earned in this pass — not one per tier.
    /// Crossing several rungs at once is normal (the evaluator awards every rung up to the one
    /// earned) and the reader's toast already collapses them the same way; three rows saying
    /// Bronze, Silver, Gold of the same badge is a worse record of the same fact.
    /// </summary>
    private void NotifyUnlocks(int userId, List<UserAchievement> unlocked)
    {
        foreach (var group in unlocked.GroupBy(u => u.Key))
        {
            var top = group.MaxBy(u => u.Tier)!;
            var definition = AchievementCatalog.All.FirstOrDefault(d => d.Key == top.Key);
            if (definition is null)
            {
                continue;
            }

            var tierName = definition.Graded && top.Tier >= 1 && top.Tier <= AchievementCatalog.TierNames.Length
                ? $" · {AchievementCatalog.TierNames[top.Tier - 1]}"
                : string.Empty;

            inbox.Raise(InboxEventType.AchievementUnlocked, new InboxMessage(
                    Title: "Achievement unlocked",
                    Body: $"{definition.Name}{tierName} — {definition.Description}",
                    Url: "/stats"),
                InboxAudience.User(userId));
        }
    }

    /// <summary>
    /// Announces a level the user has not been told about yet.
    /// <para>
    /// Levels are pure arithmetic and are never stored (see <see cref="LevelMath"/>), so this keeps
    /// the last announced one in <c>progress.lastnotifiedlevel</c> purely to have something to diff
    /// against. The first evaluation for a user <b>seeds it silently</b>: without that, shipping this
    /// would hand every existing account a level-up notification for a level they reached months ago.
    /// </para>
    /// <para>
    /// Only ever moves forward. A retune of the curve that lowers somebody's level writes nothing and
    /// announces nothing, so they are not told they were demoted and are not re-told when they climb
    /// back to where they already were.
    /// </para>
    /// </summary>
    private async Task NotifyLevelAsync(
        int userId, UserMetrics snapshot, IEnumerable<int> tiers, CancellationToken ct)
    {
        try
        {
            var level = LevelMath.LevelForXp(LevelMath.Xp(
                snapshot.ChaptersRead, snapshot.VolumesRead, snapshot.ReadingSeconds,
                snapshot.SeriesFinished, tiers));

            var stored = await userSettings.GetAsync(userId, SettingKeys.ProgressLastNotifiedLevel, ct);

            if (!int.TryParse(stored, out var lastNotified))
            {
                await userSettings.SetAsync(
                    userId, SettingKeys.ProgressLastNotifiedLevel, level.ToString(), ct);
                return;
            }

            if (level <= lastNotified)
            {
                return;
            }

            await userSettings.SetAsync(userId, SettingKeys.ProgressLastNotifiedLevel, level.ToString(), ct);

            inbox.Raise(InboxEventType.LevelUp, new InboxMessage(
                    Title: $"Level {level}",
                    Body: lastNotified + 1 == level
                        ? $"You reached level {level}."
                        : $"You reached level {level}, up from {lastNotified}.",
                    Url: "/stats"),
                InboxAudience.User(userId));
        }
        catch (Exception ex)
        {
            // Never the reason a chapter fails to mark as read.
            logger.LogWarning(ex, "Could not evaluate level notification for user {UserId}", userId);
        }
    }

    /// <summary>
    /// The master switch. Checked here rather than at each call site so that turning the feature off
    /// stops it writing as well as stops it rendering.
    /// </summary>
    public async Task<bool> EnabledForAsync(int userId, CancellationToken ct = default) =>
        ProgressSpec.Parse(await userSettings.GetAsync(userId, SettingKeys.UserGamification, ct)).Enabled;

    /// <summary>
    /// Marks unlocks as shown, so the reader's toast fires once.
    /// <para>
    /// Acknowledging any row marks <em>every</em> unseen tier of the same achievement, not just the
    /// id passed in. Crossing several tiers at once is normal — the evaluator awards every rung up
    /// to the one earned — and the UI deliberately collapses those into a single "Archivist · Gold"
    /// toast. Marking only the acknowledged row would leave the lower tiers unseen, and the next
    /// page load would announce the same achievement again at Silver, then at Bronze.
    /// </para>
    /// </summary>
    public async Task MarkSeenAsync(int userId, IReadOnlyCollection<int> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var keys = await db.UserAchievements.IgnoreQueryFilters()
            .Where(a => a.UserId == userId && ids.Contains(a.Id))
            .Select(a => a.Key)
            .Distinct()
            .ToListAsync(ct);

        if (keys.Count == 0)
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await db.UserAchievements.IgnoreQueryFilters()
            .Where(a => a.UserId == userId && a.SeenAt == null && keys.Contains(a.Key))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.SeenAt, now), ct);
    }

    /// <summary>
    /// SQLite reports a unique-index conflict with an extended result code. Matching the extended
    /// codes and never the primary 19 is the same discipline the reading-progress writer uses: 19 also
    /// covers foreign-key and NOT NULL failures, which no retry can fix and which must not be
    /// swallowed as a benign race.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 or 1555 };
}
