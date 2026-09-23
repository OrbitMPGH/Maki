using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// Custom rails: a named filter plus a source, drawn as a row on Home or Discover. Private to one
/// user and stored in <c>SavedFilters</c> with the placement as the scope, so the preset
/// controllers never see them. Both pages order their rails in their own layout (<c>rail:{id}</c>
/// keys in <see cref="HomeLayoutSpec"/> and <see cref="DiscoverLayoutSpec"/>);
/// <see cref="SavedFilter.SortOrder"/> is only the order a layout first places new rails in.
/// </summary>
[ApiController]
[Route("api/v1/rails")]
public class CustomRailsController(ILocalizer localizer, MakiDbContext db, CustomRailService rails) : ControllerBase
{
    private const int MaxRails = 30;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? placement, CancellationToken ct)
    {
        var scope = CustomRailPlacements.ScopeFor(placement);
        var query = Rails();
        if (scope is not null)
        {
            query = query.Where(f => f.Scope == scope);
        }

        var list = await query.OrderBy(f => f.SortOrder).ThenBy(f => f.Id).ToListAsync(ct);
        return Ok(list.Select(ToDto));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveCustomRailRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return this.Fail(localizer, "error.customRails.nameRequired");
        }

        if (CustomRailPlacements.ScopeFor(request.Placement) is not { } scope)
        {
            return this.Fail(localizer, "error.customRails.unknownPlacement");
        }

        var spec = (request.Spec ?? CustomRailSpec.Empty).Normalize();
        if (!CustomRailPlacements.Allows(CustomRailPlacements.PlacementOf(scope)!, spec.Source))
        {
            return this.Fail(localizer, "error.customRails.libraryHomeOnly");
        }

        if (await Rails().CountAsync(ct) >= MaxRails)
        {
            return this.Fail(localizer, "error.customRails.tooMany", new { max = MaxRails });
        }

        var rail = new SavedFilter
        {
            Name = name,
            Scope = scope,
            Spec = CustomRailSpec.Serialize(spec),
            SortOrder = await Rails().CountAsync(f => f.Scope == scope, ct),
            Created = DateTime.UtcNow,
        };
        db.SavedFilters.Add(rail);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(rail));
    }

    /// <summary>Changes whichever of name, placement and spec the request carries.</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] SaveCustomRailRequest request, CancellationToken ct)
    {
        var rail = await Rails().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (rail is null)
        {
            return NotFound();
        }

        var scope = rail.Scope;
        if (request.Placement is not null)
        {
            if (CustomRailPlacements.ScopeFor(request.Placement) is not { } moved)
            {
                return this.Fail(localizer, "error.customRails.unknownPlacement");
            }

            scope = moved;
        }

        var spec = request.Spec?.Normalize() ?? CustomRailSpec.Parse(rail.Spec);
        if (!CustomRailPlacements.Allows(CustomRailPlacements.PlacementOf(scope)!, spec.Source))
        {
            return this.Fail(localizer, "error.customRails.libraryHomeOnly");
        }

        if (request.Name?.Trim() is { Length: > 0 } name)
        {
            rail.Name = name;
        }

        if (scope != rail.Scope)
        {
            rail.SortOrder = await Rails().CountAsync(f => f.Scope == scope, ct);
            rail.Scope = scope;
        }

        rail.Spec = CustomRailSpec.Serialize(spec);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(rail));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var rail = await Rails().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (rail is null)
        {
            return NotFound();
        }

        db.SavedFilters.Remove(rail);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{id:int}/items")]
    public async Task<IActionResult> Items(int id, [FromQuery] int limit = 20, CancellationToken ct = default)
    {
        var rail = await Rails().AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (rail is null)
        {
            return NotFound();
        }

        return Ok(await rails.ItemsAsync(CustomRailSpec.Parse(rail.Spec), Math.Clamp(limit, 1, 500), ct));
    }

    /// <summary>How many rows an unsaved spec would give, for the editor. <c>count</c> null means unknown.</summary>
    [HttpPost("count")]
    public async Task<IActionResult> Count([FromBody] CountCustomRailRequest request, CancellationToken ct) =>
        Ok(new { count = await rails.CountAsync(request.Spec ?? CustomRailSpec.Empty, ct) });

    private IQueryable<SavedFilter> Rails() =>
        db.SavedFilters.Where(f => f.Scope == SavedFilter.HomeRailScope || f.Scope == SavedFilter.DiscoverRailScope);

    private static CustomRailDto ToDto(SavedFilter f) =>
        new(f.Id, f.Name, CustomRailPlacements.PlacementOf(f.Scope)!, CustomRailSpec.Parse(f.Spec), f.SortOrder);
}

public record CustomRailDto(int Id, string Name, string Placement, CustomRailSpec Spec, int SortOrder);

public record SaveCustomRailRequest(string? Name, string? Placement, CustomRailSpec? Spec);

public record CountCustomRailRequest(CustomRailSpec? Spec);
