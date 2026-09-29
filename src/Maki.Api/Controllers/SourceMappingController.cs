using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Localization;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Core.Security;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/sourcemapping")]
[Authorize(Policy = Policies.ManageSources)]
public class SourceMappingController(
    ILocalizer localizer,
    MakiDbContext db,
    SourceRegistry sourceRegistry,
    IAppSettings settings,
    SourceAvailability sourceAvailability,
    SourceMatchQueue sourceMatchQueue,
    SourceComparePreviewService comparePreviews,
    ChapterSyncService chapterSync,
    SourceMappingRemovalService removalService,
    SourceOrderService sourceOrder,
    SourceScoutService scout,
    ICurrentUser currentUser) : ControllerBase
{
    public record CreateMappingRequest(
        int SeriesId, string SourceName, string SourceSeriesId, string Url,
        string? LanguageFilter = null, int? Priority = null);

    public record AutoMatchRequest(int[] SeriesIds);

    public record CompareRequest(int SeriesId, decimal? ChapterNumber = null);

    public record ReorderRequest(int SeriesId, List<int> OrderedMappingIds);

    public record RemoveMappingRequest(bool DeleteFiles = false);

    public record RefreshSnapshotsRequest(int SeriesId, int ExcludeMappingId);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int seriesId, CancellationToken ct)
    {
        var mappings = await db.SourceMappings.Where(m => m.SeriesId == seriesId).ToListAsync(ct);
        return Ok(mappings);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMappingRequest request, CancellationToken ct)
    {
        if (sourceRegistry.Find(request.SourceName) is not { } source)
        {
            return this.Fail(localizer, "error.sourceMapping.unknownSource", new { name = request.SourceName });
        }

        // The registry matches names case-insensitively but the column and its unique index do not,
        // so storing the caller's casing would let "mangadex" and "MangaDex" both map one series.
        var sourceName = source.Name;

        // Through db.Series, whose query filter hides series in root folders the caller holds no
        // grant for. The insert below would not check that on its own.
        if (!await db.Series.AnyAsync(s => s.Id == request.SeriesId, ct))
        {
            return NotFound();
        }

        // Linking a globally switched-off source would create a mapping that never runs;
        // say so rather than storing something inert.
        if (!await sourceAvailability.IsEnabledAsync(sourceName, ct))
        {
            return this.Fail(localizer, "error.sourceMapping.sourceDisabled", new { name = sourceName });
        }

        if (await db.SourceMappings.AnyAsync(
                m => m.SeriesId == request.SeriesId && m.SourceName == sourceName, ct))
        {
            return this.Conflict(localizer, "error.sourceMapping.alreadyMapped");
        }

        var mapping = new SourceMapping
        {
            SeriesId = request.SeriesId,
            SourceName = sourceName,
            SourceSeriesId = request.SourceSeriesId,
            Url = request.Url,
            LanguageFilter = string.IsNullOrWhiteSpace(request.LanguageFilter)
                ? SourceLanguagePreference.SeedFilter(
                    source,
                    await SourceLanguagePreference.LoadAsync(settings, ct))
                : SourceLanguages.Serialize(SourceLanguages.Parse(request.LanguageFilter)),
            Priority = request.Priority ?? await PriorityForAsync(sourceName, ct),
            Enabled = true,
            Origin = SourceMappingOrigin.Manual
        };
        db.SourceMappings.Add(mapping);
        await db.SaveChangesAsync(ct);
        return Ok(mapping);
    }

    /// <summary>
    /// Re-runs auto source matching for the given series. Same path an add takes: flag the row,
    /// hand the id to the background worker, let the SignalR push redraw the page.
    /// <para>
    /// Worth re-running long after an add — a source may have picked the series up since, or a
    /// source that was switched off (or failing) at add time is back. <see cref="SourceMatchService.AutoMatchAsync"/>
    /// only ever adds mappings for sources that have none, so nothing already linked is touched.
    /// </para>
    /// </summary>
    /// <returns>How many series were queued. Ones already matching are skipped, not queued twice.</returns>
    [HttpPost("automatch")]
    public async Task<IActionResult> AutoMatch([FromBody] AutoMatchRequest request, CancellationToken ct)
    {
        var ids = (request.SeriesIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
        {
            return this.Fail(localizer, "error.sourceMapping.noSeriesGiven");
        }

        // Query filters apply, so ids outside the caller's root folders simply don't come back.
        var pending = await db.Series
            .Where(s => ids.Contains(s.Id) && !s.SourceMatchPending)
            .ToListAsync(ct);

        foreach (var series in pending)
        {
            series.SourceMatchPending = true;
        }

        // Committed before enqueueing: the worker drops any series whose flag isn't set, so
        // enqueueing first can race the save and silently do nothing.
        await db.SaveChangesAsync(ct);

        foreach (var series in pending)
        {
            sourceMatchQueue.Enqueue(series.Id);
        }

        return Ok(new { queued = pending.Count });
    }

    /// <summary>
    /// Refreshes chapter listings and their cleanup snapshots for a series. This lives under the
    /// source-management policy so somebody allowed to unlink mappings can satisfy the one-time
    /// snapshot requirement even without the broader metadata-edit permission.
    /// </summary>
    [HttpPost("snapshots/refresh")]
    public async Task<IActionResult> RefreshSnapshots(
        [FromBody] RefreshSnapshotsRequest request, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == request.SeriesId, ct))
        {
            return NotFound();
        }

        var disabled = await sourceAvailability.DisabledAsync(ct);
        var missingMappingIds = await db.SourceMappings
            .Where(m => m.SeriesId == request.SeriesId
                && m.Id != request.ExcludeMappingId
                && m.Enabled
                && !disabled.Contains(m.SourceName)
                && m.ChapterSnapshotAt == null)
            .Select(m => m.Id)
            .ToListAsync(ct);

        var newChapters = await chapterSync.SyncMappingsAsync(
            request.SeriesId, missingMappingIds, ct);

        var failed = await db.SourceMappings
            .Where(m => missingMappingIds.Contains(m.Id) && m.ChapterSnapshotAt == null)
            .Select(m => new MissingChapterSnapshot(m.Id, m.SourceName))
            .ToListAsync(ct);
        if (failed.Count > 0)
        {
            return Conflict(new
            {
                code = "error.sourceMapping.snapshotRefreshFailed",
                error = localizer.Get("error.sourceMapping.snapshotRefreshFailed"),
                missingSnapshots = failed
            });
        }

        return Ok(new { newChapters = newChapters.Count });
    }

    /// <summary>
    /// Starts a side-by-side comparison: a few pages of the same chapter from each of the series'
    /// live sources, so the user can rank them on scan quality rather than on a number.
    /// <para>
    /// Returns as soon as the panels exist. Fetching runs detached (see
    /// <see cref="SourceComparePreviewService"/>) and the client polls <see cref="Compare"/>.
    /// </para>
    /// </summary>
    [HttpPost("compare")]
    public async Task<IActionResult> StartCompare([FromBody] CompareRequest request, CancellationToken ct)
    {
        // Resolved through EF so the series' global query filter decides visibility, exactly as in
        // MediaCoverController: a series outside the caller's root folders is a 404, not a 403.
        if (!await db.Series.AnyAsync(s => s.Id == request.SeriesId, ct))
        {
            return NotFound();
        }

        var disabled = await sourceAvailability.DisabledAsync(ct);
        var candidates = await db.SourceMappings
            .Where(m => m.SeriesId == request.SeriesId && m.Enabled && !disabled.Contains(m.SourceName))
            .OrderBy(m => m.Priority)
            .Select(m => new SourceCompareCandidate(m.Id, m.SourceName, m.SourceSeriesId, m.LanguageFilter))
            .ToListAsync(ct);

        if (candidates.Count < 2)
        {
            return this.Fail(localizer, "error.sourceMapping.needsTwoSources");
        }

        try
        {
            return Ok(comparePreviews.Start(request.SeriesId, candidates, request.ChapterNumber, localizer));
        }
        catch (InvalidOperationException)
        {
            return this.Conflict(localizer, "error.sourceMapping.compareAlreadyRunning");
        }
    }

    [HttpGet("quality")]
    public async Task<IActionResult> Quality(
        [FromQuery] int seriesId, [FromServices] UpgradeEvaluationService upgrades, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return NotFound();
        }

        var estimates = await SourceQualitySamples.EstimatesAsync(db, seriesId, ct);
        var profile = (await upgrades.ForSeriesAsync(seriesId, ct))?.Profile;
        var now = DateTime.UtcNow;
        var mappings = await db.SourceMappings.AsNoTracking().Where(m => m.SeriesId == seriesId).ToListAsync(ct);
        var disabled = await sourceAvailability.DisabledAsync(ct);
        var usable = mappings.Where(m => m.Enabled && !disabled.Contains(m.SourceName)).ToList();
        var order = await sourceOrder.OrderAsync(db, seriesId, usable, ct);
        var qualityOrder = order.Mode == SourceOrderMode.Quality
            ? order
            : await sourceOrder.OrderAsync(db, seriesId, usable, ct, SourceOrderMode.Quality);
        var rest = mappings.Except(usable).OrderBy(m => m.Priority).ThenBy(m => m.Id);
        return Ok(new SourceOrderDto(
            order.SeriesMode is { } own ? SourceOrderService.Name(own) : null,
            SourceOrderService.Name(order.DefaultMode),
            SourceOrderService.Name(order.Mode),
            [.. order.Ordered.Concat(rest).Select(m => m.Id)],
            [.. estimates.Select(e => SourceQualityDto.From(e.Key, e.Value, profile, now))],
            scout.Snapshot(seriesId),
            [.. qualityOrder.Ordered.Select(m => m.Id)],
            mappings.ToDictionary(m => m.Id, m => QualityNames.Tier(
                qualityOrder.Scores.GetValueOrDefault(m.Id)?.Tier
                ?? QualityTierResolver.Resolve(sourceRegistry.Find(m.SourceName)?.Kind, null, string.Empty, false)))));
    }

    public record ScoutRequest(int SeriesId);

    /// <summary>
    /// Samples a few chapters from every enabled source of the series, in the background. Available
    /// whether or not <see cref="SettingKeys.SourcesScoutOnMatch"/> is on; poll <c>GET quality</c>.
    /// </summary>
    [HttpPost("scout")]
    public async Task<IActionResult> Scout([FromBody] ScoutRequest request, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == request.SeriesId, ct))
        {
            return NotFound();
        }

        return Accepted(scout.Start(request.SeriesId));
    }

    public record OrderModeRequest(int SeriesId, string? Mode);

    /// <summary>Sets or clears (null) a series' own source order mode.</summary>
    [HttpPut("ordermode")]
    public async Task<IActionResult> SetOrderMode([FromBody] OrderModeRequest request, CancellationToken ct)
    {
        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == request.SeriesId, ct);
        if (series is null)
        {
            return NotFound();
        }

        var mode = SourceOrderService.Parse(request.Mode);
        if (request.Mode is not null && mode is null)
        {
            return this.Fail(localizer, "error.sourceMapping.unknownOrderMode", new { mode = request.Mode });
        }

        series.SourceOrderMode = mode;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("compare")]
    public async Task<IActionResult> Compare(
        [FromQuery] int seriesId, [FromServices] UpgradeEvaluationService upgrades, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return NotFound();
        }

        return comparePreviews.Snapshot(seriesId, localizer) is { } snapshot
            ? Ok(await SourceCompareQuality.FillAsync(db, upgrades, sourceRegistry, snapshot, ct))
            : NotFound();
    }

    /// <summary>
    /// Serves one sampled page. <paramref name="sourceName"/> is resolved through the registry
    /// before it is used, so a caller-supplied string never becomes a path segment.
    /// </summary>
    [HttpGet("compare/image/{seriesId:int}/{sourceName}/{index:int}")]
    public async Task<IActionResult> CompareImage(int seriesId, string sourceName, int index, CancellationToken ct)
    {
        if (sourceRegistry.Find(sourceName) is not { } source ||
            !await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return NotFound();
        }

        if (comparePreviews.PageFile(seriesId, source.Name, index) is not { } path)
        {
            return NotFound();
        }

        // Cache-busted by the ?v= token the service puts on each URL, so a re-run never serves
        // the previous run's image.
        Response.Headers.CacheControl = "private, max-age=3600";
        return PhysicalFile(path, ContentTypeFor(path));
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".avif" => "image/avif",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };

    /// <summary>
    /// Rewrites a series' whole source order in one call, most preferred first. A drag-to-reorder UI
    /// changes every rank at once, so doing it through <see cref="Update"/> would fire one request
    /// per mapping and leave a half-applied order behind if any of them failed.
    /// </summary>
    [HttpPut("priority")]
    public async Task<IActionResult> Reorder([FromBody] ReorderRequest request, CancellationToken ct)
    {
        var ids = request.OrderedMappingIds ?? [];
        if (ids.Count == 0)
        {
            return this.Fail(localizer, "error.sourceMapping.noMappingsGiven");
        }

        // The whole series, not just the submitted ids: a caller only ever ranks what it could
        // show, and the comparison view leaves out sources that are switched off globally. Numbering
        // only the submitted ones leaves those at whatever priority they already held, so a mapping
        // excluded from the ranking can sit at 1 alongside the winner the user just chose — and once
        // its source is re-enabled, ChapterSourceResolver's OrderBy(Priority) breaks that tie
        // however SQLite feels like it. Renumbering everything keeps the order total.
        var all = await db.SourceMappings
            .Where(m => m.SeriesId == request.SeriesId)
            .ToListAsync(ct);

        var ranked = ids.Distinct().ToList();
        var byId = all.ToDictionary(m => m.Id);
        var rankedIds = ranked.ToHashSet();
        if (ranked.Any(id => !byId.ContainsKey(id)))
        {
            return this.Fail(localizer, "error.sourceMapping.mappingMismatch");
        }

        // Position in the submitted list, 1-based — the same convention PriorityForAsync and
        // SourceMatchService.AutoMatchAsync assign.
        for (var i = 0; i < ranked.Count; i++)
        {
            byId[ranked[i]].Priority = i + 1;
        }

        // Everything the caller didn't rank keeps its relative order, behind everything it did.
        var next = ranked.Count + 1;
        foreach (var mapping in all
                     .Where(m => !rankedIds.Contains(m.Id))
                     .OrderBy(m => m.Priority)
                     .ThenBy(m => m.Id))
        {
            mapping.Priority = next++;
        }

        await db.SaveChangesAsync(ct);
        return Ok(ranked.Select(id => byId[id]).ToList());
    }

    /// <summary>
    /// 1-based position of the source in the configured priority order, matching
    /// what <see cref="SourceMatchService.AutoMatchAsync"/> assigns on auto-match.
    /// A source publishing none of the enabled languages is never auto-matched at all, so it ranks
    /// after every source that is, keeping base order among its own kind.
    /// </summary>
    private async Task<int> PriorityForAsync(string sourceName, CancellationToken ct)
    {
        var baseOrder = SourceMatchService.OrderSources(
            sourceRegistry.All, await settings.GetAsync(SettingKeys.SourcePriorityOrder, ct));
        var languages = await SourceLanguagePreference.LoadAsync(settings, ct);

        bool IsWanted(ISource s) => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase);

        var ranked = SourceLanguagePreference.Rank(baseOrder, languages);
        var index = ranked.FindIndex(IsWanted);
        if (index >= 0)
        {
            return index + 1;
        }

        var unranked = SourceLanguagePreference.Unranked(baseOrder, languages);
        var tail = unranked.FindIndex(IsWanted);
        return ranked.Count + (tail < 0 ? unranked.Count : tail) + 1;
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] SourceMapping update, CancellationToken ct)
    {
        var mapping = await db.SourceMappings.FindAsync([id], ct);
        if (mapping is null)
        {
            return NotFound();
        }

        mapping.Priority = update.Priority;
        mapping.Enabled = update.Enabled;

        // Normalized before comparing, so "en" and "EN, en" — and the null the default serializes
        // back to — don't read as a change and needlessly void the snapshot.
        var languageFilter = SourceLanguages.Serialize(SourceLanguages.Parse(update.LanguageFilter));
        if (!string.Equals(mapping.LanguageFilter, languageFilter, StringComparison.OrdinalIgnoreCase))
        {
            // The stored links describe the old filter and cannot safely support source cleanup
            // until the mapping has produced a fresh listing.
            mapping.ChapterSnapshotAt = null;
        }
        mapping.LanguageFilter = languageFilter;
        await db.SaveChangesAsync(ct);
        return Ok(mapping);
    }

    /// <summary>
    /// Removes a mapping and reconciles chapters from stored source snapshots. The operation makes
    /// no source requests; an upgraded series needs one ordinary refresh before partial cleanup.
    /// </summary>
    [HttpPost("{id:int}/remove")]
    public async Task<IActionResult> RemoveWithCleanup(
        int id, [FromBody] RemoveMappingRequest request, CancellationToken ct)
    {
        if (request.DeleteFiles && !currentUser.Has(MakiPermission.DeleteSeries))
        {
            return Forbid();
        }

        try
        {
            var result = await removalService.RemoveAsync(id, request.DeleteFiles, ct);
            return result is null ? NotFound() : Ok(result);
        }
        catch (MissingChapterSnapshotsException ex)
        {
            return Conflict(new
            {
                code = "error.sourceMapping.refreshBeforeRemove",
                error = localizer.Get("error.sourceMapping.refreshBeforeRemove"),
                missingSnapshots = ex.Mappings
            });
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var mapping = await db.SourceMappings.FindAsync([id], ct);
        if (mapping is null)
        {
            return NotFound();
        }

        db.SourceMappings.Remove(mapping);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
