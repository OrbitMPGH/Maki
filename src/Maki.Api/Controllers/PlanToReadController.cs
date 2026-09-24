using Maki.Api.Localization;
using Maki.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>The caller's own "Want to read" list. No user parameter: it answers only for whoever asked.</summary>
[ApiController]
[Route("api/v1/plan-to-read")]
public class PlanToReadController(PlanToReadService plans, ILocalizer localizer) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await plans.ListAsync(ct));

    [HttpPut("{providerId:long}")]
    public async Task<IActionResult> Add(long providerId, [FromBody] PlanToReadCommand command, CancellationToken ct)
    {
        try { return Ok(await plans.AddAsync(providerId, command, ct)); }
        catch (FeedbackConflictException ex) { return this.Conflict(localizer, ex.Key, ex.Args); }
        catch (FeedbackNotFoundException ex) { return this.NotFoundMessage(localizer, ex.Key, ex.Args); }
        catch (FeedbackMetadataUnavailableException ex) { return this.Unavailable(localizer, ex.Key, ex.Args); }
        catch (FeedbackValidationException ex) { return this.Fail(localizer, ex.Key, ex.Args); }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            return this.Conflict(localizer, "error.planToRead.changed");
        }
    }

    [HttpDelete("{providerId:long}")]
    public async Task<IActionResult> Remove(long providerId, CancellationToken ct)
    {
        await plans.RemoveAsync(providerId, ct);
        return NoContent();
    }
}
