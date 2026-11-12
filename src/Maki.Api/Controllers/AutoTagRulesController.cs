using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Tags;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>Auto-tag rules, under the same permission as the tags they apply.</summary>
[ApiController]
[Route("api/v1/tags/rules")]
[Authorize(Policy = Policies.ManageTags)]
public class AutoTagRulesController(ILocalizer localizer, MakiDbContext db, AutoTagRuleService rules) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await db.AutoTagRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct);
        return Ok(rows.Select(ToDto));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveAutoTagRuleRequest request, CancellationToken ct)
    {
        var rule = new AutoTagRule { CreatedAt = DateTime.UtcNow };
        if (await ApplyAsync(rule, request, ct) is { } error)
        {
            return error;
        }

        db.AutoTagRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(rule));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] SaveAutoTagRuleRequest request, CancellationToken ct)
    {
        var rule = await db.AutoTagRules.FindAsync([id], ct);
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

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var rule = await db.AutoTagRules.FindAsync([id], ct);
        if (rule is null)
        {
            return NotFound();
        }

        db.AutoTagRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>How many series "Apply to existing series" would tag right now.</summary>
    [HttpGet("{id:int}/preview")]
    public async Task<IActionResult> Preview(int id, CancellationToken ct)
    {
        var rule = await db.AutoTagRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
        {
            return NotFound();
        }

        return Ok(new { count = (await rules.MatchingExistingAsync(rule, ct)).Count });
    }

    [HttpPost("{id:int}/apply")]
    public async Task<IActionResult> Apply(int id, CancellationToken ct)
    {
        var rule = await db.AutoTagRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
        {
            return NotFound();
        }

        return Ok(new { tagged = await rules.ApplyToExistingAsync(rule, ct) });
    }

    private async Task<IActionResult?> ApplyAsync(AutoTagRule rule, SaveAutoTagRuleRequest request, CancellationToken ct)
    {
        if (!await db.Tags.AnyAsync(t => t.Id == request.TagId, ct))
        {
            return this.Fail(localizer, "error.tags.unknownTagIds");
        }

        var conditions = AutoTagRules.Normalize(request.Conditions?.Select(c => new AutoTagCondition(c.Field, c.Value)));
        if (conditions is null)
        {
            return this.Fail(localizer, "error.autoTagRules.invalidConditions");
        }

        rule.TagId = request.TagId;
        rule.Enabled = request.Enabled;
        rule.ConditionsJson = AutoTagRules.Serialize(conditions);
        return null;
    }

    private static AutoTagRuleDto ToDto(AutoTagRule rule) => new(
        rule.Id, rule.TagId, rule.Enabled,
        AutoTagRules.Parse(rule.ConditionsJson).Select(c => new AutoTagConditionDto(c.Field, c.Value)).ToList());
}
