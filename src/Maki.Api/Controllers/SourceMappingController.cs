using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/sourcemapping")]
[Authorize(Policy = Policies.ManageSources)]
public class SourceMappingController(
    MakiDbContext db,
    SourceRegistry sourceRegistry,
    IAppSettings settings,
    SourceAvailability sourceAvailability,
    SourceMatchQueue sourceMatchQueue,
    SourceComparePreviewService comparePreviews) : ControllerBase
{
    public record CreateMappingRequest(
        int SeriesId, string SourceName, string SourceSeriesId, string Url,
        string? LanguageFilter = null, int? Priority = null);

    public record AutoMatchRequest(int[] SeriesIds);

    public record CompareRequest(int SeriesId, decimal? ChapterNumber = null);

    public record ReorderRequest(int SeriesId, List<int> OrderedMappingIds);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int seriesId, CancellationToken ct)
    {
        var mappings = await db.SourceMappings.Where(m => m.SeriesId == seriesId).ToListAsync(ct);
        return Ok(mappings);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMappingRequest request, CancellationToken ct)
    {
        if (sourceRegistry.Find(request.SourceName) is null)
        {
            return BadRequest(new { error = $"Unknown source: {request.SourceName}" });
        }

        // Linking a globally switched-off source would create a mapping that never runs;
        // say so rather than storing something inert.
        if (!await sourceAvailability.IsEnabledAsync(request.SourceName, ct))
        {
            return BadRequest(new { error = $"{request.SourceName} is switched off in Settings → Source priority" });
        }

        if (await db.SourceMappings.AnyAsync(
                m => m.SeriesId == request.SeriesId && m.SourceName == request.SourceName, ct))
        {
            return Conflict(new { error = "Series already has a mapping for this source" });
        }

        var mapping = new SourceMapping
        {
            SeriesId = request.SeriesId,
            SourceName = request.SourceName,
            SourceSeriesId = request.SourceSeriesId,
            Url = request.Url,
            LanguageFilter = request.LanguageFilter,
            Priority = request.Priority ?? await PriorityForAsync(request.SourceName, ct),
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
            return BadRequest(new { error = "No series given" });
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
            return BadRequest(new { error = "Comparing needs at least two enabled sources for this series" });
        }

        try
        {
            return Ok(comparePreviews.Start(request.SeriesId, candidates, request.ChapterNumber));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpGet("compare")]
    public async Task<IActionResult> Compare([FromQuery] int seriesId, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return NotFound();
        }

        return comparePreviews.Snapshot(seriesId) is { } snapshot ? Ok(snapshot) : NotFound();
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
            return BadRequest(new { error = "No mappings given" });
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
            return BadRequest(new { error = "Mapping list does not match this series" });
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
    /// Unknown sources fall to the end of the list.
    /// </summary>
    private async Task<int> PriorityForAsync(string sourceName, CancellationToken ct)
    {
        var ordered = SourceMatchService.OrderSources(
            sourceRegistry.All, await settings.GetAsync(SettingKeys.SourcePriorityOrder, ct));
        var index = ordered.FindIndex(
            s => string.Equals(s.Name, sourceName, StringComparison.OrdinalIgnoreCase));
        return (index < 0 ? ordered.Count : index) + 1;
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
        mapping.LanguageFilter = update.LanguageFilter;
        await db.SaveChangesAsync(ct);
        return Ok(mapping);
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
