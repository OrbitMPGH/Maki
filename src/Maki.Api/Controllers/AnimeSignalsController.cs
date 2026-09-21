using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// The anime half of a reader's taste: what their connected trackers say they watched, what each
/// entry matched to in the catalogue, and which of those are steering recommendations.
/// </summary>
[ApiController]
[Route("api/v1/recommendations/anime-signals")]
public class AnimeSignalsController(
    ILocalizer localizer,
    AnimeSignalSyncService signals,
    AnimeSignalSources animeSources,
    MangaBakaLocalStore store,
    ICurrentUser user,
    IUserSettings userSettings,
    MakiDbContext db,
    IServiceScopeFactory scopeFactory,
    ILogger<AnimeSignalsController> logger) : ControllerBase
{
    /// <param name="Strength">
    /// An <c>AnimeSignalStrength</c> name, or null to leave the level alone. Nullable because the
    /// switch and the dial are two controls on one panel and each saves on its own: a toggle that
    /// also posted a level would reset a dial the reader had moved.
    /// </param>
    public record SettingsRequest(bool Enabled, string? Strength = null);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var instanceEnabled = await signals.EnabledAsync(ct);
        var enabled = instanceEnabled &&
            await userSettings.GetAsync(SettingKeys.RecommendationsAnimeSignalsEnabled, ct) == "true";
        var strength = AnimeSignalPolicy.ParseStrength(
            await userSettings.GetAsync(SettingKeys.RecommendationsAnimeSignalsStrength, ct));
        var services = (await animeSources.ConnectedAsync(user.UserId, ct))
            .Select(AnimeSignalSources.NameOf)
            .ToList();

        var stored = await db.AnimeSignals.AsNoTracking()
            .Where(x => x.UserId == user.UserId)
            .Select(x => new AnimeSignalRow(
                x.Service, x.AnimeId, x.MalAnimeId, x.Title, x.Score, x.Status, x.MangaBakaId))
            .ToListAsync(ct);

        // The same grouping the seeds are built from, so the panel lists what actually steers the
        // recommender rather than the raw rows behind it. A reader who scrobbles to both trackers
        // would otherwise see every show twice, and every season of a franchise as its own opinion.
        var rows = AnimeSignalGrouping.Group(stored)
            .OrderByDescending(x => x.Score ?? 0)
            .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // The reader's own evidence, which replaces a signal rather than adding to it. Asked here
        // with its own queries rather than by building a whole SeedSnapshot, which would read the
        // library, the overrides and every progress row to answer a question about three id sets -
        // but through the same type the seed builder applies, so the badge cannot claim a title is
        // steering recommendations while the recommender is skipping it.
        var precedence = new AnimeSignalPrecedence(
            (await db.Series.AsNoTracking().Where(s => s.MangaBakaId != null)
                .Select(s => (long)s.MangaBakaId!.Value).Distinct().ToListAsync(ct)).ToHashSet(),
            new HashSet<long>(
                (await RecommendationFeedbackService.LikedAsync(db, user.UserId, ct))
                .Concat(await RecommendationFeedbackService.DislikedAsync(db, user.UserId, ct))),
            (await db.RecommendationSignalOverrides.AsNoTracking()
                .Where(x => x.UserId == user.UserId && x.IgnoreAsSeed)
                .Select(x => x.ProviderId).ToListAsync(ct)).ToHashSet());

        // One dump read for every matched row, rather than one per entry: the panel lists a whole
        // watch history and a point query each would be thousands of opens.
        var mangaTitles = new Dictionary<long, (string Title, string? CoverUrl)>();
        var matchedIds = rows.Where(x => x.MangaBakaId is not null)
            .Select(x => x.MangaBakaId!.Value).Distinct().ToList();
        if (matchedIds.Count > 0 && await store.IsAvailableAsync(ct))
        {
            foreach (var hit in await store.GetByIdsAsync(matchedIds,
                         ContentRating.Allowed(user.MaxContentRating), ct))
            {
                if (long.TryParse(hit.ProviderId, out var id))
                {
                    mangaTitles[id] = (hit.Title, hit.ThumbUrl ?? hit.CoverUrl);
                }
            }
        }

        var entries = rows.Select(row =>
        {
            var supersededBy = row.MangaBakaId is { } matched
                ? precedence.Reason(matched)
                : AnimeSupersededBy.None;
            // Superseded outranks the signal's own role, because it is the one the reader can act
            // on: "this says nothing, you already told us about the book yourself" is the answer to
            // the question a duplicate-looking row raises. The role underneath is still sent, so
            // the row can say what it would have counted as.
            var role = row.MangaBakaId is null
                ? "unmatched"
                : supersededBy != AnimeSupersededBy.None
                    ? "superseded"
                    : row.Role switch
                    {
                        AnimeSignalRole.Positive => "positive",
                        AnimeSignalRole.Avoided => "avoided",
                        _ => "neutral",
                    };
            return new
            {
                key = row.Key,
                services = row.Services,
                animeCount = row.AnimeCount,
                title = row.Title,
                score = row.Score,
                status = row.Status.ToString(),
                mangaBakaId = row.MangaBakaId,
                mangaTitle = row.MangaBakaId is { } id ? mangaTitles.GetValueOrDefault(id).Title : null,
                mangaCoverUrl = row.MangaBakaId is { } cid ? mangaTitles.GetValueOrDefault(cid).CoverUrl : null,
                role,
                supersededBy = supersededBy switch
                {
                    AnimeSupersededBy.Library => "library",
                    AnimeSupersededBy.Feedback => "feedback",
                    AnimeSupersededBy.Ignored => "ignored",
                    _ => null,
                },
            };
        }).ToList();

        return Ok(new
        {
            enabled,
            instanceEnabled,
            lastSyncAtUtc = await signals.LastSyncAtAsync(user.UserId, ct),
            syncing = signals.IsRunning(user.UserId),
            progress = signals.Progress(user.UserId) is { } p ? new { looked = p.Looked, total = p.Total } : null,
            services,
            strength = AnimeSignalPolicy.NameOf(strength),
            // The dial's own arithmetic, so the panel can say what a level costs without keeping a
            // second copy of the numbers that would drift the first time they were tuned.
            strengths = Enum.GetValues<AnimeSignalStrength>().Select(level => new
            {
                value = AnimeSignalPolicy.NameOf(level),
                ratingShare = AnimeSignalPolicy.RatingShareOf(level),
                topSeedWeight = AnimeSignalPolicy.SeedWeightOf(AnimeWatchStatus.Completed, 10, level),
            }),
            // One count per role and nothing derived, so the client never has to subtract its way
            // to a number. It used to send `ignored` for neutral-plus-unmatched and the panel read
            // it as the unmatched chip, which over-counted that chip by every neutral entry.
            counts = new
            {
                total = entries.Count,
                matched = entries.Count(x => x.role != "unmatched"),
                positive = entries.Count(x => x.role == "positive"),
                avoided = entries.Count(x => x.role == "avoided"),
                neutral = entries.Count(x => x.role == "neutral"),
                superseded = entries.Count(x => x.role == "superseded"),
                unmatched = entries.Count(x => x.role == "unmatched"),
            },
            entries,
        });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> Settings([FromBody] SettingsRequest request, CancellationToken ct)
    {
        if (request.Enabled && !await signals.EnabledAsync(ct))
        {
            return this.Fail(localizer, "error.animeSignals.disabled");
        }

        await userSettings.SetAsync(SettingKeys.RecommendationsAnimeSignalsEnabled,
            request.Enabled ? "true" : null, ct);

        if (request.Strength is { Length: > 0 } raw)
        {
            // Stored normalized, and the default is stored as null rather than as "balanced": an
            // unset key is what every other default here means, and writing the word would pin this
            // reader to today's default if it ever moved.
            var level = AnimeSignalPolicy.ParseStrength(raw);
            await userSettings.SetAsync(SettingKeys.RecommendationsAnimeSignalsStrength,
                level == AnimeSignalPolicy.DefaultStrength ? null : AnimeSignalPolicy.NameOf(level), ct);
        }

        if (request.Enabled)
        {
            // This one user, in the background. Triggering the Quartz job instead would walk every
            // opted-in account and reset the instance's 24-hour clock on somebody else's behalf, and
            // would do nothing at all if a pass happened to be running already.
            StartBackgroundSync(user.UserId);
        }
        else
        {
            // Opting out takes the rows with it. Leaving them would keep a copy of somebody's watch
            // history that nothing reads and no screen shows, and a later opt-in would rather have a
            // fresh list than one from an unknown date.
            var stale = await db.AnimeSignals.Where(x => x.UserId == user.UserId).ToListAsync(ct);
            if (stale.Count > 0)
            {
                db.AnimeSignals.RemoveRange(stale);
                await db.SaveChangesAsync(ct);
            }

            await userSettings.SetAsync(SettingKeys.RecommendationsAnimeSignalsLastSync, null, ct);
        }

        return Ok(new
        {
            enabled = request.Enabled,
            strength = AnimeSignalPolicy.NameOf(AnimeSignalPolicy.ParseStrength(
                await userSettings.GetAsync(SettingKeys.RecommendationsAnimeSignalsStrength, ct))),
        });
    }

    /// <summary>
    /// Starts a pass in the background and returns at once, the same way opting in does: a first
    /// sync is minutes of throttled lookups, and running it inline made this request itself the
    /// timeout it exists to avoid. The panel watches <c>GET</c>'s <c>syncing</c>/<c>progress</c>
    /// fields instead of a response here.
    /// </summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        if (!await signals.EnabledForAsync(user.UserId, ct))
        {
            return this.Fail(localizer, "error.animeSignals.notEnabled");
        }

        if (signals.IsRunning(user.UserId))
        {
            return this.Fail(localizer, "error.animeSignals.syncRunning");
        }

        StartBackgroundSync(user.UserId);
        return Accepted();
    }

    /// <summary>
    /// Fire-and-forget, on its own scope, because the request's one dies with the response and a
    /// first sync is minutes of throttled lookups. The service's per-user guard is what keeps a
    /// double toggle from running two of these.
    /// </summary>
    private void StartBackgroundSync(int userId)
    {
        if (signals.IsRunning(userId))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AnimeSignalSyncService>()
                    .SyncUserAsync(userId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "First anime signal sync failed for user {UserId}", userId);
            }
        });
    }
}
