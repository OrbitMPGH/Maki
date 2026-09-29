using System.Globalization;
using Maki.Core.Configuration;
using Maki.Core.Inbox;
using Maki.Core.Recommendations;
using Maki.Data;
using Maki.Metadata.Catalogue;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>A catalogue series new since the last check, and which followed credit it came in under.</summary>
public sealed record FollowedRelease(long MangaBakaId, CatalogueCredit Credit);

/// <summary>
/// Tells people when someone they follow has a new series in the catalogue.
///
/// <para>
/// "New" is "above the instance watermark" (<see cref="SettingKeys.DiscoverFollowingWatermark"/>):
/// MangaBaka numbers series as they are added, and the dump carries no added-on date to use instead.
/// One instance-wide number rather than one per follow, because a follow made today must not
/// announce the back catalogue either, and only ids the previous pass had not reached yet are ever
/// looked at. The first pass on an instance records the watermark and announces nothing, the same
/// silent seeding level-ups get.
/// </para>
///
/// <para>
/// A series whose credits only arrive in a later dump is missed: it was already below the
/// watermark by then. Remembering every id would catch it, at the cost of a per-user set that grows
/// with every publisher somebody follows.
/// </para>
/// </summary>
public class FollowedCreatorReleaseService(
    IServiceScopeFactory scopeFactory,
    IAppSettings settings,
    CatalogueIndexCache catalogueIndex,
    MangaBakaLocalStore store,
    InboxService inbox,
    TimeProvider time,
    ILogger<FollowedCreatorReleaseService> logger)
{
    /// <summary>Up to this many new titles become one notification each; more become a single summary.</summary>
    public const int MaxSingleNotifications = 3;

    /// <summary>
    /// A new entry dated more than this many years back is somebody filling in an old work, not a
    /// release. Undated entries pass: announcements often have no date yet.
    /// </summary>
    private const int MaxAgeYears = 1;

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (!await store.IsAvailableAsync(ct) || await catalogueIndex.GetAsync(ct) is not { } catalogue ||
            catalogue.Credits.IsEmpty)
        {
            return;
        }

        var credits = catalogue.Credits;
        var stored = await settings.GetAsync(SettingKeys.DiscoverFollowingWatermark, ct);
        if (!long.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var watermark))
        {
            await SetWatermarkAsync(credits.MaxSeriesId, ct);
            logger.LogInformation("Follow check seeded at MangaBaka id {Watermark}", credits.MaxSeriesId);
            return;
        }

        if (credits.MaxSeriesId <= watermark)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var follows = await db.UserSettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.Key == SettingKeys.DiscoverFollowing)
            .Join(db.Users.Where(u => !u.Disabled && !u.PendingSetup), s => s.UserId, u => u.Id,
                (s, u) => new { s.UserId, s.Value, u.MaxContentRating })
            .ToListAsync(ct);

        var perUser = follows
            .Select(f => (f.UserId, f.MaxContentRating,
                Releases: Match(FollowedCreatorsSpec.Parse(f.Value), credits, watermark)))
            .Where(f => f.Releases.Count > 0)
            .ToList();

        if (perUser.Count > 0)
        {
            var owned = (await db.Series
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(s => s.MangaBakaId != null)
                    .Select(s => (long)s.MangaBakaId!.Value)
                    .ToListAsync(ct))
                .ToHashSet();

            var hiddenByUser = await db.UserSettings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => s.Key == SettingKeys.DiscoverHidden)
                .ToDictionaryAsync(s => s.UserId, s => HiddenContentSpec.Parse(s.Value).Terms, ct);

            // One hydration per ceiling rather than per user: the ceiling is the only thing about a
            // reader the dump read depends on.
            var byCeiling = new Dictionary<string, Dictionary<long, MangaBakaRecommendation>>(StringComparer.Ordinal);
            var releasedIds = perUser.SelectMany(u => u.Releases).Select(r => r.MangaBakaId).Distinct().ToList();
            var thisYear = time.GetUtcNow().Year;

            foreach (var (userId, ceiling, releases) in perUser)
            {
                var allowed = ContentRating.Allowed(ceiling);
                if (!byCeiling.TryGetValue(allowed[^1], out var hydrated))
                {
                    hydrated = (await store.GetByIdsAsync(releasedIds, allowed, ct))
                        .Where(r => long.TryParse(r.ProviderId, out _))
                        .ToDictionary(r => long.Parse(r.ProviderId, CultureInfo.InvariantCulture));
                    byCeiling[allowed[^1]] = hydrated;
                }

                var hidden = new RecommendationFilters(Hidden: hiddenByUser.GetValueOrDefault(userId));
                var wanted = releases
                    .Where(r => !owned.Contains(r.MangaBakaId))
                    .Select(r => (Release: r, Item: hydrated.GetValueOrDefault(r.MangaBakaId)))
                    .Where(x => x.Item is not null)
                    .Where(x => x.Item!.Year is not int year || year >= thisYear - MaxAgeYears)
                    // Tags are not on the hydrated row, so only hidden genres can be tested here.
                    .Where(x => hidden.MatchesNames(x.Item!.MatchedGenres, []))
                    .ToList();

                await TryNotifyAsync(userId, wanted.Select(x => (x.Release, x.Item!)).ToList(), ct);
            }
        }

        await SetWatermarkAsync(credits.MaxSeriesId, ct);
        logger.LogInformation(
            "Follow check covered MangaBaka ids {From} to {To} for {Users} follower(s)",
            watermark + 1, credits.MaxSeriesId, perUser.Count);
    }

    /// <summary>
    /// Every work above <paramref name="watermark"/> credited to one of the follows, each once,
    /// under the first follow that names it.
    /// </summary>
    public static IReadOnlyList<FollowedRelease> Match(FollowedCreatorsSpec spec, CreditIndex index, long watermark)
    {
        var found = new List<FollowedRelease>();
        var seen = new HashSet<long>();
        foreach (var credit in spec.Creators ?? [])
        {
            var role = CreditIndex.ParseRole(credit.Role);
            if (!index.TryResolveFuzzy(credit.Name, role, CatalogueOptions.Default.CreditResolveMaxDistance, out var nameId))
            {
                continue;
            }

            foreach (var id in index.WorksOf(nameId, role))
            {
                if (id > watermark && seen.Add(id))
                {
                    found.Add(new FollowedRelease(id, credit));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// One retry for a transient write failure, then the reader is skipped. The watermark moves
    /// regardless: holding it back for a reader whose write can never succeed (deleted mid-run, say)
    /// would re-notify everyone else on every later run.
    /// </summary>
    private async Task TryNotifyAsync(
        int userId, IReadOnlyList<(FollowedRelease Release, MangaBakaRecommendation Item)> releases, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await NotifyAsync(userId, releases, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not save follow notifications for user {UserId} (attempt {Attempt})", userId, attempt);
                if (attempt == 2)
                {
                    return;
                }
            }
        }
    }

    private async Task NotifyAsync(
        int userId, IReadOnlyList<(FollowedRelease Release, MangaBakaRecommendation Item)> releases, CancellationToken ct)
    {
        if (releases.Count == 0)
        {
            return;
        }

        if (releases.Count > MaxSingleNotifications)
        {
            await inbox.RaiseOrThrowAsync(InboxEventType.FollowedCreatorRelease, new InboxMessage(
                "inbox.followedReleases",
                new Dictionary<string, object?> { ["count"] = releases.Count },
                Url: "/discover"), InboxAudience.User(userId), ct);
            return;
        }

        foreach (var (release, item) in releases)
        {
            // The name as the reader followed it, not the index's display spelling, which can change
            // between dumps and would title the notification with a name they never picked.
            var name = release.Credit.Name;
            var role = release.Credit.Role is { } r ? $"&role={r}" : string.Empty;
            await inbox.RaiseOrThrowAsync(InboxEventType.FollowedCreatorRelease, new InboxMessage(
                "inbox.followedRelease",
                // A catalogue title, not a library series, so there is no per-user display title to
                // resolve at read time; the series is not in anybody's library yet.
                new Dictionary<string, object?> { ["creator"] = name, ["title"] = item.Title },
                Url: $"/creator/{Uri.EscapeDataString(name)}?open={release.MangaBakaId}{role}"),
                InboxAudience.User(userId), ct);
        }
    }

    private Task SetWatermarkAsync(long value, CancellationToken ct) =>
        settings.SetAsync(SettingKeys.DiscoverFollowingWatermark, value.ToString(CultureInfo.InvariantCulture), ct);
}
