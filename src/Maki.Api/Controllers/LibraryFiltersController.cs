using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using System.Text.Json;
using Maki.Api.Dtos;
using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// Named Library filter presets ("ongoing, behind, action"). The spec is stored as opaque JSON and
/// applied by the Library grid — see <see cref="LibraryFilterSpec"/>.
/// <para>
/// Presets are per-user: the <c>SavedFilter</c> owner query filter scopes every read and write to the
/// signed-in caller, so each account sees and edits only its own list.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/library/filters")]
public class LibraryFiltersController(
    ILocalizer localizer, MakiDbContext db, ILogger<LibraryFiltersController> logger) : ControllerBase
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
            .Where(f => f.Scope == SavedFilter.LibraryScope)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Id)
            .ToListAsync(ct);
        return Ok(filters.Select(ToDto));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveFilterRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return this.Fail(localizer, "error.libraryFilters.nameRequired");
        }

        var filter = new SavedFilter
        {
            Name = name,
            Spec = JsonSerializer.Serialize(request.Spec, SpecJson),
            // Per-user count: the query filter narrows it, so two users' presets don't interleave
            // their sort order.
            SortOrder = await db.SavedFilters.CountAsync(f => f.Scope == SavedFilter.LibraryScope, ct),
            Created = DateTime.UtcNow,
        };
        db.SavedFilters.Add(filter);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(filter));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] SaveFilterRequest request, CancellationToken ct)
    {
        var filter = await db.SavedFilters.FindAsync([id], ct);
        if (filter is null || filter.Scope != SavedFilter.LibraryScope)
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

    /// <summary>
    /// Makes one preset the view the Library opens with, or clears it. Setting one clears the
    /// caller's previous default, so there is never more than one.
    /// </summary>
    [HttpPut("{id:int}/default")]
    public async Task<IActionResult> SetDefault(int id, [FromBody] SetDefaultFilterRequest request, CancellationToken ct)
    {
        var owned = await db.SavedFilters
            .Where(f => f.Scope == SavedFilter.LibraryScope)
            .ToListAsync(ct);
        var filter = owned.FirstOrDefault(f => f.Id == id);
        if (filter is null)
        {
            return NotFound();
        }

        foreach (var other in owned)
        {
            other.IsDefault = request.IsDefault && other.Id == id;
        }

        await db.SaveChangesAsync(ct);
        return Ok(ToDto(filter));
    }

    /// <summary>
    /// Stores the order the caller arranged their presets in. Ids not in the caller's list are a 404,
    /// so one user can't touch another's rows; presets left out of the list keep their relative
    /// order after the listed ones.
    /// </summary>
    [HttpPut("order")]
    public async Task<IActionResult> Reorder([FromBody] ReorderFiltersRequest request, CancellationToken ct)
    {
        var owned = await db.SavedFilters
            .Where(f => f.Scope == SavedFilter.LibraryScope)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Id)
            .ToListAsync(ct);
        var ids = (request.Ids ?? []).Distinct().ToList();
        if (ids.Any(id => owned.All(f => f.Id != id)))
        {
            return NotFound();
        }

        var ordered = ids.Select(id => owned.First(f => f.Id == id))
            .Concat(owned.Where(f => !ids.Contains(f.Id)))
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].SortOrder = i;
        }

        await db.SaveChangesAsync(ct);
        return Ok(ordered.Select(ToDto));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var filter = await db.SavedFilters.FindAsync([id], ct);
        if (filter is null || filter.Scope != SavedFilter.LibraryScope)
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

        return new SavedFilterDto(f.Id, f.Name, spec, f.SortOrder, f.IsDefault);
    }
}
