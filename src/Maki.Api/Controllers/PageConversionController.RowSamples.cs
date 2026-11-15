using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Images;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

public partial class PageConversionController
{
    /// <param name="Row">Index of the rule in <c>Plan.Rules</c>, or -1 for the files no rule matches.</param>
    /// <param name="Kind">"greyscale" or "colour".</param>
    public record RowSamplesRequest(ScopeRequest? Plan, int Row, string? Kind);

    /// <summary>
    /// Up to three pages of one kind from the files one row of an unsaved plan converts, so the
    /// wizard can show a target on the pages it will be used for. Empty when none of them has such a page.
    /// </summary>
    [HttpPost("samples/row")]
    public async Task<IActionResult> RowSamples([FromBody] RowSamplesRequest request, CancellationToken ct)
    {
        if (request.Plan is null || request.Kind is not ("greyscale" or "colour"))
        {
            return this.Fail(localizer, "error.pageConversion.ruleInvalid");
        }

        if (UnsavedPlan(request.Plan, out var plan) is { } refused)
        {
            return refused;
        }

        var rules = plan!.Rules.OrderBy(r => r.Order).ToList();
        if (request.Row < -1 || request.Row >= rules.Count)
        {
            return this.Fail(localizer, "error.pageConversion.ruleInvalid");
        }

        var files = await plans.ScopeFilesAsync(ct);
        var series = await PageConversionPlanService.SeriesFactsAsync(db, null, ct);
        var ids = files
            .Where(f => !f.PendingImport)
            .Where(f => PageConversionPlanService.Classify(plan, rules, f, series.GetValueOrDefault(f.SeriesId)) is
                { Action: PlanAction.Convert } placement &&
                (request.Row < 0
                    ? placement.Place == PageConversionPlanService.Place.Default
                    : placement.Place == PageConversionPlanService.Place.Rule && placement.Rule == request.Row))
            .Select(f => f.Id)
            .ToList();
        return Ok(new SamplesDto(await previews.SamplesFromAsync(ids, request.Kind == "colour", ct)));
    }
}
