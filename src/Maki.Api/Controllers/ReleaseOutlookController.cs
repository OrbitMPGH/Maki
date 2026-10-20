using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

/// <summary>When a series' next chapter is likely, and when Maki will next look for it.</summary>
[ApiController]
[Route("api/v1")]
public class ReleaseOutlookController(ReleaseOutlookService outlooks, IUserSettings userSettings) : ControllerBase
{
    [HttpGet("series/{id:int}/outlook")]
    public async Task<IActionResult> ForSeries(int id, CancellationToken ct) =>
        await outlooks.ForSeriesAsync(id, ct) is { } outlook ? Ok(outlook) : NotFound();

    [HttpGet("upcoming")]
    public async Task<IActionResult> Upcoming(CancellationToken ct)
    {
        var titleLanguage = await userSettings.GetAsync(SettingKeys.UiTitleLanguage, ct);
        var upcoming = await outlooks.UpcomingAsync(ct);
        return Ok(upcoming.Select(x => new UpcomingSeriesDto(
            x.Series.Id,
            SeriesDto.DisplayTitleFor(x.Series, titleLanguage),
            SeriesDto.CoverUrlFor(x.Series.Id, x.Series.CoverPath, x.Series.LastMetadataRefresh),
            x.Outlook)));
    }
}
