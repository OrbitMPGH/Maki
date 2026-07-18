using Maki.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/rewind")]
public class RewindController(RewindService rewind) : ControllerBase
{
    /// <summary>Distinct years with recorded activity, newest first — for the year picker.</summary>
    [HttpGet("years")]
    public async Task<IActionResult> Years(CancellationToken ct)
    {
        return Ok(await rewind.YearsAsync(ct));
    }

    /// <summary>
    /// Aggregated stats for an inclusive local-date range. utcOffsetMinutes uses JS
    /// getTimezoneOffset() semantics (UTC − local) so day/month buckets match the
    /// user's calendar.
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(
        [FromQuery] DateOnly from, [FromQuery] DateOnly to,
        [FromQuery] int utcOffsetMinutes, CancellationToken ct)
    {
        if (to < from)
        {
            return BadRequest(new { error = "'to' must not be before 'from'" });
        }

        if (Math.Abs(utcOffsetMinutes) > 14 * 60)
        {
            return BadRequest(new { error = "utcOffsetMinutes out of range" });
        }

        return Ok(await rewind.StatsAsync(from, to, utcOffsetMinutes, ct));
    }
}
