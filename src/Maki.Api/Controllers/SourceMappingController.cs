using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/sourcemapping")]
public class SourceMappingController(MakiDbContext db, SourceRegistry sourceRegistry) : ControllerBase
{
    public record CreateMappingRequest(
        int SeriesId, string SourceName, string SourceSeriesId, string Url,
        string? LanguageFilter = null, int Priority = 1);

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
            Priority = request.Priority,
            Enabled = true
        };
        db.SourceMappings.Add(mapping);
        await db.SaveChangesAsync(ct);
        return Ok(mapping);
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
