using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Gamification;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// A user's progression: level, achievements, streaks and goals.
/// <para>
/// No <c>[Authorize]</c> attribute, so the fail-closed fallback policy applies and any signed-in user
/// reaches it — this is a person's own data and there is nothing here to withhold from them. The one
/// exception is reading somebody <em>else's</em>, which every action funnels through
/// <see cref="ResolveAsync"/> and which requires Admin.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/gamification")]
public class GamificationController(
    MakiDbContext db,
    UserMetricsService metrics,
    AchievementService achievements,
    IUserSettings userSettings,
    IUserSettingsStore userSettingsStore,
    ICurrentUser currentUser,
    UserViewResolver userView,
    TimeProvider clock) : ControllerBase
{
    /// <summary>How many recent unlocks the summary carries, for Home's card.</summary>
    private const int RecentUnlocks = 4;

    /// <summary>
    /// Which user an action is about, via the shared <see cref="UserViewResolver"/>.
    /// <para>
    /// Returns null when allowed, or the result to send back when not. Written this way so no action
    /// can read another user's numbers by forgetting the check — there is no path to the data that
    /// does not pass through here.
    /// </para>
    /// </summary>
    private IActionResult? Resolve(int? requested, out int userId) =>
        userView.TryResolve(requested, out userId) ? null : Forbid();

    private async Task<GamificationSpec> SpecFor(int userId, CancellationToken ct) =>
        GamificationSpec.Parse(userId == currentUser.UserId
            ? await userSettings.GetAsync(SettingKeys.UserGamification, ct)
            : await userSettingsStore.GetAsync(userId, SettingKeys.UserGamification, ct));

    public record GamificationSettingsDto(bool Enabled, bool ShowStreaks, bool ShowOnLeaderboard, string TimeZone);

    /// <summary>
    /// The caller's own preferences plus their time zone. Always self: these are per-user settings,
    /// and an admin editing somebody else's display preferences is not a thing this feature does.
    /// </summary>
    [HttpGet("settings")]
    public async Task<IActionResult> Settings(CancellationToken ct)
    {
        var stored = await userSettings.GetManyAsync(
            [SettingKeys.UserGamification, SettingKeys.UserTimeZone], ct);

        var spec = GamificationSpec.Parse(stored.GetValueOrDefault(SettingKeys.UserGamification));
        return Ok(new GamificationSettingsDto(
            spec.Enabled, spec.ShowStreaks, spec.ShowOnLeaderboard,
            stored.GetValueOrDefault(SettingKeys.UserTimeZone) ?? string.Empty));
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings([FromBody] GamificationSettingsDto request, CancellationToken ct)
    {
        var timeZone = (request.TimeZone ?? string.Empty).Trim();
        if (timeZone.Length > 0 && !IsKnownTimeZone(timeZone))
        {
            return BadRequest(new { error = "Unknown time zone" });
        }

        await userSettings.SetAsync(SettingKeys.UserGamification, GamificationSpec.Serialize(
            new GamificationSpec(request.Enabled, request.ShowStreaks, request.ShowOnLeaderboard)), ct);

        // Blank deletes the row, which reads back as UTC — the same "unset and default are one state"
        // rule the rest of the per-user settings follow.
        await userSettings.SetAsync(SettingKeys.UserTimeZone, timeZone.Length == 0 ? null : timeZone, ct);

        // Day bucketing is part of the cached snapshot, so a zone change has to drop it or the streak
        // stays computed against the old calendar for the next minute.
        metrics.Invalidate(currentUser.UserId);

        return await Settings(ct);
    }

    private static bool IsKnownTimeZone(string id)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// Level, streaks, totals, active goals and the latest unlocks. One request, because Home's card
    /// wants all of it and a section that costs five round trips is a section people switch off.
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] int? userId, CancellationToken ct)
    {
        if (Resolve(userId, out var target) is { } denied)
        {
            return denied;
        }

        var spec = await SpecFor(target, ct);
        if (!spec.Enabled)
        {
            return Ok(Disabled());
        }

        var snapshot = await metrics.GetAsync(target, ct);

        // Evaluate before reading back, so a page load is what unlocks achievements earned through
        // Kavita or OPDS, where nothing ever touched the reader's completion path.
        await achievements.EvaluateAsync(target, snapshot, ct);

        var held = await HeldAsync(target, ct);
        var level = LevelMath.Progress(LevelMath.Xp(
            snapshot.ChaptersRead, snapshot.VolumesRead, snapshot.ReadingSeconds, snapshot.SeriesFinished,
            held.Select(h => h.Tier)));

        var recent = TopTierPerKey(held)
            .OrderByDescending(h => h.UnlockedAt).ThenByDescending(h => h.Id)
            .Take(RecentUnlocks)
            .Select(h => Describe(h, snapshot, held))
            .Where(d => d is not null)
            .Select(d => d!)
            .ToList();

        var unseen = TopTierPerKey(held.Where(h => h.SeenAt is null))
            .OrderBy(h => h.UnlockedAt)
            .Select(h => Describe(h, snapshot, held))
            .Where(d => d is not null)
            .Select(d => d!)
            .ToList();

        return Ok(new GamificationSummaryDto(
            true,
            spec.ShowStreaks,
            ToDto(level),
            snapshot.ChaptersRead + snapshot.VolumesRead,
            snapshot.ReadingSeconds,
            snapshot.SeriesFinished,
            snapshot.DaysRead,
            snapshot.CurrentStreak,
            snapshot.LongestStreak,
            held.Count,
            AchievementCatalog.All.Sum(a => a.Tiers.Count),
            recent,
            await GoalsForAsync(target, ct),
            unseen));
    }

    /// <summary>
    /// The full catalogue with unlock state. Hidden achievements appear only once earned: a grid of
    /// locked secrets is a checklist, which defeats the point of having any.
    /// </summary>
    [HttpGet("achievements")]
    public async Task<IActionResult> Achievements([FromQuery] int? userId, CancellationToken ct)
    {
        if (Resolve(userId, out var target) is { } denied)
        {
            return denied;
        }

        if (!(await SpecFor(target, ct)).Enabled)
        {
            return Ok(Array.Empty<AchievementDto>());
        }

        var snapshot = await metrics.GetAsync(target, ct);
        await achievements.EvaluateAsync(target, snapshot, ct);
        var held = await HeldAsync(target, ct);

        var rows = AchievementCatalog.All
            .Where(a => !a.Hidden || held.Any(h => h.Key == a.Key))
            .Select(a => Describe(a, snapshot, held))
            .ToList();

        return Ok(rows);
    }

    /// <summary>The contribution grid: one entry per local day with activity.</summary>
    [HttpGet("heatmap")]
    public async Task<IActionResult> Heatmap([FromQuery] int? userId, CancellationToken ct)
    {
        if (Resolve(userId, out var target) is { } denied)
        {
            return denied;
        }

        if (!(await SpecFor(target, ct)).Enabled)
        {
            return Ok(Array.Empty<HeatmapDayDto>());
        }

        var snapshot = await metrics.GetAsync(target, ct);
        return Ok(snapshot.Days
            .OrderBy(d => d.Date)
            .Select(d => new HeatmapDayDto(d.Date, d.Chapters, d.Seconds))
            .ToList());
    }

    public record SeenRequest(IReadOnlyList<int> Ids);

    /// <summary>Stamps unlocks as shown. Always about the caller: a toast is not something an admin dismisses for somebody else.</summary>
    [HttpPost("achievements/seen")]
    public async Task<IActionResult> Seen([FromBody] SeenRequest request, CancellationToken ct)
    {
        await achievements.MarkSeenAsync(currentUser.UserId, request.Ids ?? [], ct);
        return NoContent();
    }

    [HttpGet("goals")]
    public async Task<IActionResult> Goals([FromQuery] int? userId, CancellationToken ct)
    {
        if (Resolve(userId, out var target) is { } denied)
        {
            return denied;
        }

        return Ok(await GoalsForAsync(target, ct));
    }

    public record SaveGoalRequest(string Period, string Metric, int Target);

    /// <summary>
    /// Sets or replaces the goal for a period and metric. A target of zero or less deletes it, so the
    /// same control both sets and clears one — "no goal" and "a goal of nothing" are the same state.
    /// Always the caller's own: goals are self-set by definition.
    /// </summary>
    [HttpPut("goals")]
    public async Task<IActionResult> SaveGoal([FromBody] SaveGoalRequest request, CancellationToken ct)
    {
        if (!Enum.TryParse<GoalPeriod>(request.Period, true, out var period) ||
            !Enum.TryParse<GoalMetric>(request.Metric, true, out var metric))
        {
            return BadRequest(new { error = "Unknown period or metric" });
        }

        var existing = await db.ReadingGoals
            .FirstOrDefaultAsync(g => g.Period == period && g.Metric == metric, ct);

        if (request.Target <= 0)
        {
            if (existing is not null)
            {
                db.ReadingGoals.Remove(existing);
                await db.SaveChangesAsync(ct);
            }

            return NoContent();
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (existing is null)
        {
            db.ReadingGoals.Add(new ReadingGoal
            {
                UserId = currentUser.UserId,
                Period = period,
                Metric = metric,
                Target = request.Target,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.Target = request.Target;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return Ok(await GoalsForAsync(currentUser.UserId, ct));
    }

    [HttpDelete("goals/{id:int}")]
    public async Task<IActionResult> DeleteGoal(int id, CancellationToken ct)
    {
        var goal = await db.ReadingGoals.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (goal is null)
        {
            return NotFound();
        }

        db.ReadingGoals.Remove(goal);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// The household leaderboard, listing only users who opted in.
    /// <para>
    /// The one read here that must bypass a query filter, and the reason the opt-in exists: reading is
    /// per user on this instance and the shared thing is the library, so appearing in somebody else's
    /// view is a choice, never a default. Fewer than two participants means there is nothing to
    /// compare, and the empty list is what hides the panel.
    /// </para>
    /// </summary>
    [HttpGet("leaderboard")]
    public async Task<IActionResult> Leaderboard(CancellationToken ct)
    {
        var users = await db.Users
            .Where(u => !u.Disabled && !u.PendingSetup)
            .Select(u => new { u.Id, u.UserName, u.DisplayName })
            .ToListAsync(ct);

        var rows = new List<LeaderboardRowDto>();
        foreach (var user in users)
        {
            var spec = GamificationSpec.Parse(
                await userSettingsStore.GetAsync(user.Id, SettingKeys.UserGamification, ct));
            if (!spec.Enabled || !spec.ShowOnLeaderboard)
            {
                continue;
            }

            var snapshot = await metrics.GetAsync(user.Id, ct);
            var tiers = await db.UserAchievements.IgnoreQueryFilters()
                .Where(a => a.UserId == user.Id).Select(a => a.Tier).ToListAsync(ct);

            var level = LevelMath.LevelForXp(LevelMath.Xp(
                snapshot.ChaptersRead, snapshot.VolumesRead, snapshot.ReadingSeconds,
                snapshot.SeriesFinished, tiers));

            rows.Add(new LeaderboardRowDto(
                user.Id,
                user.DisplayName ?? user.UserName ?? $"User {user.Id}",
                level,
                snapshot.ChaptersRead + snapshot.VolumesRead,
                snapshot.CurrentStreak));
        }

        return Ok(rows.Count < 2
            ? []
            : rows.OrderByDescending(r => r.Level).ThenByDescending(r => r.ChaptersRead).ToList());
    }

    /// <summary>
    /// Keeps only the highest tier held of each achievement.
    /// <para>
    /// The evaluator awards every rung up to the one earned in a single pass, so a first evaluation
    /// — or any jump of more than one tier — produces several rows sharing a timestamp. Rendered
    /// straight, Home's "latest badges" reads "Archivist Gold, Archivist Silver, Archivist Bronze",
    /// and the reader's toast fires three times for what the user experiences as one thing.
    /// </para>
    /// </summary>
    private static IEnumerable<UserAchievement> TopTierPerKey(IEnumerable<UserAchievement> rows) =>
        rows.GroupBy(a => a.Key, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(a => a.Tier).First());

    private async Task<List<UserAchievement>> HeldAsync(int userId, CancellationToken ct) =>
        await db.UserAchievements.IgnoreQueryFilters()
            .Where(a => a.UserId == userId)
            .ToListAsync(ct);

    private async Task<List<ReadingGoalDto>> GoalsForAsync(int userId, CancellationToken ct)
    {
        var goals = await db.ReadingGoals.IgnoreQueryFilters()
            .Where(g => g.UserId == userId)
            .OrderBy(g => g.Period).ThenBy(g => g.Metric)
            .ToListAsync(ct);

        var rows = new List<ReadingGoalDto>(goals.Count);
        foreach (var goal in goals)
        {
            rows.Add(new ReadingGoalDto(
                goal.Id,
                goal.Period.ToString(),
                goal.Metric.ToString(),
                goal.Target,
                await metrics.GoalProgressAsync(userId, goal.Period, goal.Metric, ct)));
        }

        return rows;
    }

    private static AchievementDto Describe(
        AchievementDefinition definition, UserMetrics snapshot, List<UserAchievement> held)
    {
        var tier = definition.TierFor(snapshot);
        var unlockedAt = held
            .Where(h => h.Key == definition.Key && h.Tier == tier)
            .Select(h => (DateTime?)h.UnlockedAt)
            .FirstOrDefault();

        return new AchievementDto(
            definition.Key,
            definition.Name,
            definition.Description,
            definition.Track.ToString(),
            definition.Icon,
            definition.Graded,
            definition.Hidden,
            tier,
            AchievementCatalog.TierName(definition, tier),
            definition.Value(snapshot),
            tier < definition.Tiers.Count ? definition.Tiers[tier] : null,
            definition.Tiers,
            unlockedAt);
    }

    /// <summary>
    /// The stored-row form, for the recent and unseen lists. Null when this build no longer knows the
    /// key — a retired achievement stops rendering rather than breaking the page.
    /// </summary>
    private static AchievementDto? Describe(
        UserAchievement row, UserMetrics snapshot, List<UserAchievement> held)
    {
        var definition = AchievementCatalog.Find(row.Key);
        if (definition is null)
        {
            return null;
        }

        return Describe(definition, snapshot, held) with
        {
            Tier = row.Tier,
            TierName = AchievementCatalog.TierName(definition, row.Tier),
            UnlockedAt = row.UnlockedAt,
            UnlockId = row.Id,
        };
    }

    private static LevelDto ToDto(LevelMath.LevelProgress p) =>
        new(p.Level, p.Xp, p.IntoLevel, p.LevelSpan, p.NextLevelXp, p.Progress);

    private static GamificationSummaryDto Disabled() =>
        new(false, false, new LevelDto(1, 0, 0, 1, 0, 0), 0, 0, 0, 0, 0, 0, 0, 0, [], [], []);
}
