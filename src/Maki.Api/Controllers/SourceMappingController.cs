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
    SourceMatchQueue sourceMatchQueue) : ControllerBase
{
    public record CreateMappingRequest(
        int SeriesId, string SourceName, string SourceSeriesId, string Url,
        string? LanguageFilter = null, int? Priority = null);

    public record AutoMatchRequest(int[] SeriesIds);

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
            Enabled = true
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
