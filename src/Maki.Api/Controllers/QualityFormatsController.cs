using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>Quality formats that upgrade profiles score. Listing is open to anyone signed in; writes are admin-only.</summary>
[ApiController]
[Route("api/v1/quality-formats")]
public class QualityFormatsController(ILocalizer localizer, MakiDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var formats = await db.QualityFormats.AsNoTracking().OrderBy(f => f.Name).ToListAsync(ct);
        var profiles = await db.UpgradeProfiles.AsNoTracking().ToListAsync(ct);
        return Ok(formats.Select(f => QualityFormatDto.From(f, ProfileCount(profiles, f.Id))));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] QualityFormatWriteDto request, CancellationToken ct)
    {
        var format = new QualityFormat();
        if (await ApplyAsync(format, request, null, ct) is { } invalid)
        {
            return invalid;
        }

        db.QualityFormats.Add(format);
        await db.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, QualityFormatDto.From(format, 0));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] QualityFormatWriteDto request, CancellationToken ct)
    {
        var format = await db.QualityFormats.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (format is null)
        {
            return NotFound();
        }

        var before = format.Conditions.ToList();
        if (await ApplyAsync(format, request, id, ct) is { } invalid)
        {
            return invalid;
        }

        format.Version++;
        var profiles = await db.UpgradeProfiles.ToListAsync(ct);
        if (!before.SequenceEqual(format.Conditions))
        {
            // Matching changed, so every memo keyed on a profile that scores this format is stale.
            foreach (var profile in profiles.Where(p => p.FormatScores.Any(s => s.FormatId == id)))
            {
                profile.Version++;
            }
        }

        await db.SaveChangesAsync(ct);
        return Ok(QualityFormatDto.From(format, ProfileCount(profiles, id)));
    }

    /// <summary>Also drops the format from every profile that scored it, bumping those profiles' versions.</summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var format = await db.QualityFormats.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (format is null)
        {
            return NotFound();
        }

        foreach (var profile in await db.UpgradeProfiles.ToListAsync(ct))
        {
            if (profile.FormatScores.Any(s => s.FormatId == id))
            {
                profile.FormatScores = [.. profile.FormatScores.Where(s => s.FormatId != id)];
                profile.Version++;
            }
        }

        db.QualityFormats.Remove(format);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<IActionResult?> ApplyAsync(
        QualityFormat format, QualityFormatWriteDto request, int? id, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return this.Fail(localizer, "error.upgrades.nameRequired");
        }

        if (name.Length > UpgradeProfilesController.MaxNameLength)
        {
            return this.Fail(localizer, "error.upgrades.nameTooLong",
                new { maxLength = UpgradeProfilesController.MaxNameLength });
        }

        if (request.Conditions is not { Count: > 0 })
        {
            return this.Fail(localizer, "error.upgrades.conditionsRequired");
        }

        var conditions = new List<FormatCondition>();
        foreach (var condition in request.Conditions)
        {
            if (!QualityNames.TryParseConditionType(condition.Type, out var type))
            {
                return this.Fail(localizer, "error.upgrades.unknownConditionType", new { type = condition.Type });
            }

            var value = condition.Value?.Trim() ?? "";
            if (value.Length == 0)
            {
                return this.Fail(localizer, "error.upgrades.conditionValueRequired");
            }

            if (QualityScorer.IsRegexType(type) && !QualityScorer.IsValidRegex(value))
            {
                return this.Fail(localizer, "error.upgrades.invalidRegex", new { pattern = value });
            }

            if (QualityScorer.IsNumericType(type) && !QualityScorer.TryParseNumber(value, out _))
            {
                return this.Fail(localizer, "error.upgrades.invalidNumber", new { value });
            }

            conditions.Add(new FormatCondition(type, value, condition.Required, condition.Negate));
        }

        if (await db.QualityFormats.AnyAsync(f => f.Id != id && f.Name == name, ct))
        {
            return this.Conflict(localizer, "error.upgrades.formatNameTaken", new { name });
        }

        format.Name = name;
        format.Conditions = conditions;
        return null;
    }

    private static int ProfileCount(List<UpgradeProfile> profiles, int formatId) =>
        profiles.Count(p => p.FormatScores.Any(s => s.FormatId == formatId));
}
