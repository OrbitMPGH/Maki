using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// Instance-wide upgrade profiles. Anyone signed in may list them, since the add-series form and the
/// series menu offer them as choices; every write is admin-only.
/// </summary>
[ApiController]
[Route("api/v1/upgrade-profiles")]
public class UpgradeProfilesController(ILocalizer localizer, MakiDbContext db) : ControllerBase
{
    public const int MaxNameLength = 60;
    public const int MaxMeasuredWeight = 50;
    public const int MaxDescriptionLength = 300;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var profiles = await db.UpgradeProfiles.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var counts = await SeriesCountsAsync(ct);
        return Ok(profiles.Select(p => UpgradeProfileDto.From(p, counts.GetValueOrDefault(p.Id))));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpgradeProfileWriteDto request, CancellationToken ct)
    {
        var profile = new UpgradeProfile();
        if (await ApplyAsync(profile, request, null, ct) is { } invalid)
        {
            return invalid;
        }

        db.UpgradeProfiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, UpgradeProfileDto.From(profile, 0));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpgradeProfileWriteDto request, CancellationToken ct)
    {
        var profile = await db.UpgradeProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null)
        {
            return NotFound();
        }

        if (await ApplyAsync(profile, request, id, ct) is { } invalid)
        {
            return invalid;
        }

        profile.Version++;
        await db.SaveChangesAsync(ct);
        var counts = await SeriesCountsAsync(ct);
        return Ok(UpgradeProfileDto.From(profile, counts.GetValueOrDefault(id)));
    }

    /// <summary>Refused while any series pins the profile or it is the instance default.</summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var profile = await db.UpgradeProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null)
        {
            return NotFound();
        }

        var defaultId = UpgradeEvaluationService.ParseId(await db.AppConfig
            .Where(c => c.Key == SettingKeys.UpgradesDefaultProfileId)
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct));
        if (defaultId == id || await db.Series.IgnoreQueryFilters().AnyAsync(s => s.UpgradeProfileId == id, ct))
        {
            return this.Conflict(localizer, "error.upgrades.profileInUse", new { name = profile.Name });
        }

        db.UpgradeProfiles.Remove(profile);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Validates <paramref name="request"/> and copies it onto <paramref name="profile"/>, or returns the failure.</summary>
    private async Task<IActionResult?> ApplyAsync(
        UpgradeProfile profile, UpgradeProfileWriteDto request, int? id, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return this.Fail(localizer, "error.upgrades.nameRequired");
        }

        if (name.Length > MaxNameLength)
        {
            return this.Fail(localizer, "error.upgrades.nameTooLong", new { maxLength = MaxNameLength });
        }

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description?.Length > MaxDescriptionLength)
        {
            return this.Fail(localizer, "error.upgrades.descriptionTooLong", new { maxLength = MaxDescriptionLength });
        }

        var tiers = new List<ProfileTier>();
        foreach (var tier in request.Tiers ?? [])
        {
            if (!QualityNames.TryParseTier(tier.Tier, out var parsed))
            {
                return this.Fail(localizer, "error.upgrades.unknownTier", new { tier = tier.Tier });
            }

            tiers.Add(new ProfileTier(parsed, tier.Allowed, tier.Grouped));
        }

        if (!QualityNames.TryParseTier(request.Cutoff, out var cutoff))
        {
            return this.Fail(localizer, "error.upgrades.unknownTier", new { tier = request.Cutoff });
        }

        var candidate = new UpgradeProfile { Tiers = tiers };
        UpgradeProfileDefaults.Normalise(candidate);
        if (!QualityScorer.Allows(candidate, cutoff))
        {
            return this.Fail(localizer, "error.upgrades.cutoffNotAllowed");
        }

        if (request.MinScoreDelta < 0)
        {
            return this.Fail(localizer, "error.upgrades.minScoreDeltaRange");
        }

        if (request.MaxTierScoreDrop < 0)
        {
            return this.Fail(localizer, "error.upgrades.maxTierScoreDropRange");
        }

        if (request.UpgradeUntilScore < 0)
        {
            return this.Fail(localizer, "error.upgrades.upgradeUntilScoreRange");
        }

        if (request.PageTolerancePercent is < 0 or > 100)
        {
            return this.Fail(localizer, "error.upgrades.pageToleranceRange", new { min = 0, max = 100 });
        }

        if (request.ResolutionWeight is < 0 or > MaxMeasuredWeight ||
            request.CompressionWeight is < 0 or > MaxMeasuredWeight)
        {
            return this.Fail(localizer, "error.upgrades.measuredWeightRange", new { min = 0, max = MaxMeasuredWeight });
        }

        var scores = (request.FormatScores ?? [])
            .Where(s => s.Score != 0)
            .DistinctBy(s => s.FormatId)
            .Select(s => new FormatScore(s.FormatId, s.Score))
            .ToList();
        var formatIds = scores.Select(s => s.FormatId).ToList();
        var known = await db.QualityFormats.Where(f => formatIds.Contains(f.Id)).Select(f => f.Id).ToListAsync(ct);
        var missing = formatIds.Except(known).ToList();
        if (missing.Count > 0)
        {
            return this.Fail(localizer, "error.upgrades.formatNotFound", new { formatId = missing[0] });
        }

        if (await db.UpgradeProfiles.AnyAsync(p => p.Id != id && p.Name == name, ct))
        {
            return this.Conflict(localizer, "error.upgrades.profileNameTaken", new { name });
        }

        profile.Name = name;
        profile.Description = description;
        profile.Tiers = candidate.Tiers;
        profile.Cutoff = cutoff;
        profile.UpgradesEnabled = request.UpgradesEnabled;
        profile.MinScoreDelta = request.MinScoreDelta;
        profile.MaxTierScoreDrop = request.MaxTierScoreDrop;
        profile.UpgradeUntilScore = request.UpgradeUntilScore;
        profile.FormatScores = scores;
        profile.ResolutionWeight = request.ResolutionWeight;
        profile.CompressionWeight = request.CompressionWeight;
        profile.PageTolerancePercent = request.PageTolerancePercent;
        profile.AllowReplacingUnknown = request.AllowReplacingUnknown;
        return null;
    }

    /// <summary>Across every root folder: a profile is in use even where the caller cannot see the series.</summary>
    private async Task<Dictionary<int, int>> SeriesCountsAsync(CancellationToken ct) =>
        await db.Series.IgnoreQueryFilters()
            .Where(s => s.UpgradeProfileId != null)
            .GroupBy(s => s.UpgradeProfileId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
}
