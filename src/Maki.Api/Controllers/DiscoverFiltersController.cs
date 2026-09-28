using Maki.Api.Localization;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// Named Discover filter presets, private to one user. Stored in the same table as the Library
/// presets under <see cref="SavedFilter.DiscoverScope"/>, with a <see cref="SearchDefaultsSpec"/> as
/// the spec. A preset is a filter only; drawing one as a rail is a custom rail's job
/// (<see cref="CustomRailsController"/>).
/// </summary>
[ApiController]
[Route("api/v1/discover/filters")]
public class DiscoverFiltersController(ILocalizer localizer, MakiDbContext db) : ControllerBase
{
    private const int MaxPresets = 50;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var filters = await Presets()
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Id)
            .ToListAsync(ct);
        return Ok(filters.Select(ToDto));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveDiscoverFilterRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return this.Fail(localizer, "error.libraryFilters.nameRequired");
        }

        var count = await Presets().CountAsync(ct);
        if (count >= MaxPresets)
        {
            return this.Fail(localizer, "error.discoverFilters.tooMany", new { max = MaxPresets });
        }

        var filter = new SavedFilter
        {
            Name = name,
            Scope = SavedFilter.DiscoverScope,
            Spec = SearchDefaultsSpec.Serialize(request.Spec ?? SearchDefaultsSpec.Empty),
            SortOrder = count,
            Created = DateTime.UtcNow,
        };
        db.SavedFilters.Add(filter);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(filter));
    }

    /// <summary>Changes whichever of name and spec the request carries.</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] SaveDiscoverFilterRequest request, CancellationToken ct)
    {
        var filter = await Presets().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (filter is null)
        {
            return NotFound();
        }

        if (request.Name?.Trim() is { Length: > 0 } name)
        {
            filter.Name = name;
        }

        if (request.Spec is { } spec)
        {
            filter.Spec = SearchDefaultsSpec.Serialize(spec);
        }

        await db.SaveChangesAsync(ct);
        return Ok(ToDto(filter));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var filter = await Presets().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (filter is null)
        {
            return NotFound();
        }

        db.SavedFilters.Remove(filter);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private IQueryable<SavedFilter> Presets() =>
        db.SavedFilters.Where(f => f.Scope == SavedFilter.DiscoverScope);

    private static DiscoverFilterDto ToDto(SavedFilter f) =>
        new(f.Id, f.Name, SearchDefaultsSpec.Parse(f.Spec), f.SortOrder);
}

public record DiscoverFilterDto(int Id, string Name, SearchDefaultsSpec Spec, int SortOrder);

public record SaveDiscoverFilterRequest(string? Name, SearchDefaultsSpec? Spec);
