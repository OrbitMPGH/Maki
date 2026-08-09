using Maki.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

/// <summary>
/// The windowed activity view behind the Stats page and the Rewind retrospective.
/// <para>
/// No <c>[Authorize]</c>, so the fail-closed fallback policy applies: any signed-in user reaches
/// their own numbers. Reading somebody else's goes through <see cref="UserViewResolver"/> and needs
/// Admin — <see cref="RewindService"/> itself checks nothing, so every action must resolve first.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/rewind")]
public class RewindController(RewindService rewind, UserViewResolver userView) : ControllerBase
{
    /// <summary>Distinct years with recorded activity, newest first — for the year picker.</summary>
    [HttpGet("years")]
    public async Task<IActionResult> Years([FromQuery] int? userId, CancellationToken ct)
    {
        if (!userView.TryResolve(userId, out var target))
        {
            return Forbid();
        }

        return Ok(await rewind.YearsAsync(target, ct));
    }

    /// <summary>
    /// Aggregated stats for an inclusive local-date range. utcOffsetMinutes uses JS
    /// getTimezoneOffset() semantics (UTC − local) so day/month buckets match the
    /// user's calendar.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(
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

        return Ok(await rewind.StatsAsync(target, from, to, utcOffsetMinutes, ct));
    }
}
