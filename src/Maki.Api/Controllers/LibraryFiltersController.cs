using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using System.Text.Json;
using Maki.Api.Dtos;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// Named Library filter presets ("ongoing, behind, action"). The spec is stored as opaque JSON and
/// applied by the Library grid — see <see cref="LibraryFilterSpec"/>.
/// <para>
/// Readable by any signed-in user, writable only by an admin: the presets are currently one
/// instance-wide list, so an unprivileged account could otherwise rename or delete everyone else's.
/// The restriction goes away when saved filters become per-user.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/library/filters")]
public class LibraryFiltersController(MakiDbContext db, ILogger<LibraryFiltersController> logger) : ControllerBase
{
    /// <summary>
    /// Stored specs are camelCase to match every other JSON surface in the app, and read back
    /// case-insensitively so presets written by builds that stored PascalCase still apply. A
    /// name mismatch here doesn't throw — it silently yields an all-defaults spec — so the
    /// leniency is what stops an old preset quietly turning into "no filter".
    /// </summary>
    private static readonly JsonSerializerOptions SpecJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var filters = await db.SavedFilters
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Id)
            .ToListAsync(ct);
        return Ok(filters.Select(ToDto));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveFilterRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return BadRequest(new { error = "Name is required" });
        }

        var filter = new SavedFilter
        {
            Name = name,
            Spec = JsonSerializer.Serialize(request.Spec, SpecJson),
            SortOrder = await db.SavedFilters.CountAsync(ct),
            Created = DateTime.UtcNow,
        };
        db.SavedFilters.Add(filter);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(filter));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] SaveFilterRequest request, CancellationToken ct)
    {
        var filter = await db.SavedFilters.FindAsync([id], ct);
        if (filter is null)
        {
            return NotFound();
        }

        if (request.Name?.Trim() is { Length: > 0 } name)
        {
            filter.Name = name;
        }

        filter.Spec = JsonSerializer.Serialize(request.Spec, SpecJson);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(filter));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var filter = await db.SavedFilters.FindAsync([id], ct);
        if (filter is null)
        {
            return NotFound();
        }

        db.SavedFilters.Remove(filter);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private SavedFilterDto ToDto(SavedFilter f)
    {
        LibraryFilterSpec spec;
        try
        {
            spec = JsonSerializer.Deserialize<LibraryFilterSpec>(f.Spec, SpecJson) ?? new LibraryFilterSpec();
        }
        catch (JsonException ex)
        {
            // A spec written by an older/newer build shouldn't take the whole filter bar down.
            logger.LogWarning(ex, "Saved filter {Id} has an unreadable spec; serving an empty one", f.Id);
            spec = new LibraryFilterSpec();
        }

        return new SavedFilterDto(f.Id, f.Name, spec, f.SortOrder);
    }
}
