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
    public record SettingsRequest(bool Enabled);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var instanceEnabled = await signals.EnabledAsync(ct);
        var enabled = instanceEnabled &&
            await userSettings.GetAsync(SettingKeys.RecommendationsAnimeSignalsEnabled, ct) == "true";
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

        // One dump read for every matched row, rather than one per entry: the panel lists a whole
        // watch history and a point query each would be thousands of opens.
        var mangaTitles = new Dictionary<long, string>();
        var matchedIds = rows.Where(x => x.MangaBakaId is not null)
            .Select(x => x.MangaBakaId!.Value).Distinct().ToList();
        if (matchedIds.Count > 0 && await store.IsAvailableAsync(ct))
        {
            foreach (var hit in await store.GetByIdsAsync(matchedIds,
                         ContentRating.Allowed(user.MaxContentRating), ct))
            {
                if (long.TryParse(hit.ProviderId, out var id))
                {
                    mangaTitles[id] = hit.Title;
                }
            }
        }

        var entries = rows.Select(row =>
        {
            var role = row.MangaBakaId is null
                ? "unmatched"
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
                mangaTitle = row.MangaBakaId is { } id ? mangaTitles.GetValueOrDefault(id) : null,
                role,
            };
        }).ToList();

        return Ok(new
        {
            enabled,
            instanceEnabled,
            lastSyncAtUtc = await signals.LastSyncAtAsync(user.UserId, ct),
            syncing = signals.IsRunning(user.UserId),
            services,
            counts = new
            {
                total = entries.Count,
                matched = entries.Count(x => x.role != "unmatched"),
                positive = entries.Count(x => x.role == "positive"),
                avoided = entries.Count(x => x.role == "avoided"),
                ignored = entries.Count(x => x.role is "neutral" or "unmatched"),
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

        return Ok(new { enabled = request.Enabled });
    }

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

        var summary = await signals.SyncUserAsync(user.UserId, ct);
        return Ok(new
        {
            summary.Fetched,
            summary.Matched,
            summary.Removed,
            summary.Looked,
            lastSyncAtUtc = await signals.LastSyncAtAsync(user.UserId, ct),
        });
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
