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
public class SourceMappingController(
    MakiDbContext db, SourceRegistry sourceRegistry, IAppSettings settings) : ControllerBase
{
    public record CreateMappingRequest(
        int SeriesId, string SourceName, string SourceSeriesId, string Url,
        string? LanguageFilter = null, int? Priority = null);

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
