using Maki.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

/// <summary>
/// Read-only views over how the library's files measure up against their upgrade profiles. Needs
/// only a signed-in caller; each one sees the files in the root folders they have access to.
/// </summary>
[ApiController]
[Route("api/v1/upgrades")]
public class UpgradesController(UpgradeEvaluationService upgrades) : ControllerBase
{
    [HttpGet("cutoff-unmet")]
    public async Task<IActionResult> CutoffUnmet(
        [FromQuery] int? seriesId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = UpgradeEvaluationService.DefaultPageSize, CancellationToken ct = default) =>
        Ok(await upgrades.CutoffUnmetAsync(seriesId, page, pageSize, ct));

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct) => Ok(await upgrades.SummaryAsync(ct));
}
