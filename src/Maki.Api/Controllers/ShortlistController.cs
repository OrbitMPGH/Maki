using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

public sealed record ShortlistSaveRequest(string? SavedFrom, string? SavedFromDetail = null, string? Note = null);

public sealed record ShortlistNoteRequest(string? Note);

/// <summary>
/// The caller's shortlist. Any signed-in user may keep one, so there is no permission policy. Every
/// write is idempotent, so the client sends no mutation ids.
/// </summary>
[ApiController]
[Route("api/v1/shortlist")]
public class ShortlistController(ShortlistService shortlist, ILocalizer localizer) : ControllerBase
{
    [HttpGet("ids")]
    public async Task<IActionResult> Ids(CancellationToken ct) => Ok(await shortlist.IdsAsync(ct));

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await shortlist.ListAsync(ct));

    [HttpPut("{mangaBakaId:int}")]
    public async Task<IActionResult> Save(int mangaBakaId, [FromBody] ShortlistSaveRequest? body, CancellationToken ct)
    {
        if (mangaBakaId <= 0)
        {
            return NotFound();
        }

        return Answer(await shortlist.SaveAsync(
            mangaBakaId, body?.SavedFrom, body?.SavedFromDetail, body?.Note, ct));
    }

    [HttpPatch("{mangaBakaId:int}")]
    public async Task<IActionResult> UpdateNote(int mangaBakaId, [FromBody] ShortlistNoteRequest? body, CancellationToken ct)
    {
        if (mangaBakaId <= 0 || await shortlist.UpdateNoteAsync(mangaBakaId, body?.Note, ct) is not { } entry)
        {
            return NotFound();
        }

        return Ok(ShortlistEntryDto.From(entry));
    }

    [HttpPost("{mangaBakaId:int}/acknowledge")]
    public async Task<IActionResult> Acknowledge(int mangaBakaId, CancellationToken ct)
    {
        if (mangaBakaId <= 0 || await shortlist.AcknowledgeAsync(mangaBakaId, ct) is not { } entry)
        {
            return NotFound();
        }

        return Ok(ShortlistEntryDto.From(entry));
    }

    [HttpDelete("{mangaBakaId:int}")]
    public async Task<IActionResult> Remove(int mangaBakaId, CancellationToken ct)
    {
        if (mangaBakaId <= 0 || await shortlist.RemoveAsync(mangaBakaId, ct) is not { } entry)
        {
            return NoContent();
        }

        return Ok(ShortlistEntryDto.From(entry));
    }

    [HttpPost("{mangaBakaId:int}/restore")]
    public async Task<IActionResult> Restore(int mangaBakaId, [FromBody] ShortlistEntryDto? body, CancellationToken ct)
    {
        if (mangaBakaId <= 0)
        {
            return NotFound();
        }

        if (body is null)
        {
            return BadRequest();
        }

        return Answer(await shortlist.RestoreAsync(mangaBakaId, body, ct));
    }

    private IActionResult Answer(ShortlistInsertResult result) => result.Outcome switch
    {
        ShortlistInsertOutcome.NotInCatalogue => this.NotFoundMessage(localizer, "error.shortlist.notInCatalogue"),
        ShortlistInsertOutcome.InLibrary => this.Conflict(localizer, "error.shortlist.inLibrary"),
        ShortlistInsertOutcome.Full => this.Conflict(localizer, "error.shortlist.full", new { max = ShortlistEntry.MaxPerUser }),
        _ => Ok(ShortlistEntryDto.From(result.Entry!)),
    };
}
