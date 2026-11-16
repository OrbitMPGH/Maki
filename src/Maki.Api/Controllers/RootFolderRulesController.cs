using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Core.Security;
using Maki.Core.Tags;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

public record RootFolderRuleDto(
    int Id, int RootFolderId, int Position, bool Enabled, IReadOnlyList<IReadOnlyList<AutoTagConditionDto>> Groups);

public record SaveRootFolderRuleRequest(int RootFolderId, bool Enabled, List<List<AutoTagConditionDto>>? Groups);

public record ReorderRootFolderRulesRequest(List<int>? Ids);

/// <param name="RuleId">The rule that picked the folder, or null when none matched.</param>
/// <param name="RootFolderId">Where the series goes; null when no rule matched and the caller keeps its own default.</param>
public record RootFolderRouteDto(int? RootFolderId, int? RuleId);

/// <summary>
/// Root folder rules: which folder a series added without one lands in. Managing them is admin-only,
/// like the folders themselves; the route preview is open to anyone who may add a series and only
/// ever names a folder that caller can use.
/// </summary>
[ApiController]
[Route("api/v1/rootfolder")]
public class RootFolderRulesController(
    ILocalizer localizer, MakiDbContext db, RootFolderRoutingService routing, ICurrentUser currentUser,
    IEnumerable<IMetadataProvider> metadataProviders) : ControllerBase
{
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("rules")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await db.RootFolderRules.AsNoTracking().OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync(ct);
        return Ok(rows.Select(ToDto));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("rules")]
    public async Task<IActionResult> Create([FromBody] SaveRootFolderRuleRequest request, CancellationToken ct)
    {
        var last = await db.RootFolderRules.MaxAsync(r => (int?)r.Position, ct) ?? -1;
        var rule = new RootFolderRule { CreatedAt = DateTime.UtcNow, Position = last + 1 };
        if (await ApplyAsync(rule, request, ct) is { } error)
        {
            return error;
        }

        db.RootFolderRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(rule));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("rules/{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] SaveRootFolderRuleRequest request, CancellationToken ct)
    {
        var rule = await db.RootFolderRules.FindAsync([id], ct);
        if (rule is null)
        {
            return NotFound();
        }

        if (await ApplyAsync(rule, request, ct) is { } error)
        {
            return error;
        }

        await db.SaveChangesAsync(ct);
        return Ok(ToDto(rule));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpDelete("rules/{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var rule = await db.RootFolderRules.FindAsync([id], ct);
        if (rule is null)
        {
            return NotFound();
        }

        db.RootFolderRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Sets the order rules are tried in. Ids left out keep their place after the listed ones.</summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPut("rules/order")]
    public async Task<IActionResult> Reorder([FromBody] ReorderRootFolderRulesRequest request, CancellationToken ct)
    {
        var ids = request.Ids ?? [];
        var rules = await db.RootFolderRules.OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync(ct);
        var ordered = ids.Distinct()
            .Select(id => rules.FirstOrDefault(r => r.Id == id))
            .OfType<RootFolderRule>()
            .Concat(rules.Where(r => !ids.Contains(r.Id)))
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Position = i;
        }

        await db.SaveChangesAsync(ct);
        return Ok(ordered.Select(ToDto));
    }

    /// <summary>Which rule would send a library series where, were it added now by whoever added it.</summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("rules/test")]
    public async Task<IActionResult> Test([FromQuery] int seriesId, CancellationToken ct)
    {
        var series = await db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null)
        {
            return NotFound();
        }

        var (via, by) = (await AutoTagRuleService.ProvenanceAsync(db, seriesId, ct)).GetValueOrDefault(seriesId);
        var subject = new AutoTagSubject(series.Genres, series.Type, series.ContentRating, series.RootFolderId, via, by);
        var route = await routing.MatchAsync(subject, null, ct);
        return Ok(new RootFolderRouteDto(route?.RootFolderId, route?.RuleId));
    }

    /// <summary>
    /// The folder the rules pick for a series about to be added, so the add form can preselect it.
    /// Null when no rule matches: the form then keeps its own default, which is also the server's.
    /// </summary>
    /// <param name="addedFrom">As the add request sends it: <c>library</c>, <c>recommendation</c> or <c>request</c>.</param>
    /// <param name="addedBy">The requester, for an admin approving a request; ignored for anyone else.</param>
    [Authorize(Policy = Policies.AddSeries)]
    [HttpGet("route")]
    public async Task<IActionResult> Route(
        [FromQuery] string? providerId, [FromQuery] string? addedFrom, [FromQuery] int? addedBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return NotFound();
        }

        var metadata = await metadataProviders.First().GetAsync(providerId, ct);
        if (metadata is null)
        {
            return NotFound();
        }

        var admin = currentUser.Has(MakiPermission.Admin);
        var series = SeriesMetadataMapper.NewFromMetadata(metadata);
        var subject = RootFolderRoutingService.SubjectFor(
            series, addedFrom, admin && addedBy is > 0 ? addedBy : currentUser.UserId);
        IReadOnlyCollection<int>? usable = currentUser.AllRootFolders || admin ? null : currentUser.RootFolderIds;
        var route = await routing.MatchAsync(subject, usable, ct);
        return Ok(new RootFolderRouteDto(route?.RootFolderId, route?.RuleId));
    }

    private async Task<IActionResult?> ApplyAsync(RootFolderRule rule, SaveRootFolderRuleRequest request, CancellationToken ct)
    {
        if (!await db.RootFolders.AnyAsync(f => f.Id == request.RootFolderId, ct))
        {
            return this.Fail(localizer, "error.rootFolderRules.unknownRootFolder");
        }

        var groups = ConditionGroups.Normalize(RuleConditionDtos.Groups(request.Groups, null), AutoTagFields.ForRootFolderRules);
        if (groups is null)
        {
            return this.Fail(localizer, "error.rootFolderRules.invalidConditions");
        }

        rule.RootFolderId = request.RootFolderId;
        rule.Enabled = request.Enabled;
        rule.ConditionsJson = ConditionGroups.Serialize(groups);
        return null;
    }

    private static RootFolderRuleDto ToDto(RootFolderRule rule) => new(
        rule.Id, rule.RootFolderId, rule.Position, rule.Enabled,
        RuleConditionDtos.From(ConditionGroups.Parse(rule.ConditionsJson)));
}
