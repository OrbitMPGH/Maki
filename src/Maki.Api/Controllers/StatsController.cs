using Maki.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

/// <summary>
/// Everything the Stats page reports on: a window of one reader's activity, and the composition of
/// the library itself. Progression (levels, achievements, goals) is <c>GamificationController</c>,
/// which is a resource controller with writes rather than a report.
/// <para>
/// No <c>[Authorize]</c>, so the fail-closed fallback policy applies and any signed-in user reaches
/// their own numbers. The two halves scope differently on purpose: activity is per-user and reads
/// another account only through <see cref="UserViewResolver"/> (Admin), while library composition
/// has no user at all and leans on the root-folder query filters. Keep them apart — one handler
/// holding both rules is how a scoping bug gets in.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/stats")]
public class StatsController(
    ActivityStatsService activity,
    LibraryCompositionService library,
    UserViewResolver userView) : ControllerBase
{
    /// <summary>Distinct years with recorded activity, newest first — for the year picker.</summary>
    [HttpGet("years")]
    public async Task<IActionResult> Years([FromQuery] int? userId, CancellationToken ct)
    {
        if (!userView.TryResolve(userId, out var target))
        {
            return Forbid();
        }

        return Ok(await activity.YearsAsync(target, ct));
    }

    /// <summary>
    /// Reading, downloads and library changes over an inclusive local-date range.
    /// utcOffsetMinutes uses JS getTimezoneOffset() semantics (UTC − local) so day/month
    /// buckets match the user's calendar.
    /// </summary>
    [HttpGet("activity")]
    public async Task<IActionResult> Activity(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to,
        [FromQuery] int utcOffsetMinutes, [FromQuery] int? userId, CancellationToken ct)
    {
        if (!userView.TryResolve(userId, out var target))
        {
            return Forbid();
        }

        if (to < from)
        {
            return BadRequest(new { error = "'to' must not be before 'from'" });
        }

        if (Math.Abs(utcOffsetMinutes) > 14 * 60)
        {
            return BadRequest(new { error = "utcOffsetMinutes out of range" });
        }

        return Ok(await activity.StatsAsync(target, from, to, utcOffsetMinutes, ct));
    }

    /// <summary>
    /// What the collection is made of. No userId: the library is shared, and what a caller may see
    /// is already decided by the root-folder query filters.
    /// </summary>
    [HttpGet("library")]
    public async Task<IActionResult> Library(CancellationToken ct)
    {
        return Ok(await library.GetAsync(ct));
    }
}
