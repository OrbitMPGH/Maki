using Maki.Api.Localization;
using Maki.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

/// <summary>
/// The series page's "start where the anime ended" callout. Series visibility rides the global
/// query filter, so a series outside the caller's root folders reads as missing.
/// </summary>
[ApiController]
[Route("api/v1/series/{seriesId:int}/anime-resume")]
public class AnimeResumeController(ILocalizer localizer, AnimeResumeService animeResume) : ControllerBase
{
    /// <param name="CoveredTo">The reader's own end of the range; null keeps the resolved one.</param>
    public record ApplyRequest(bool MarkWatched, decimal? CoveredTo = null);

    [HttpGet]
    public async Task<IActionResult> Get(int seriesId, CancellationToken ct)
    {
        if (!await animeResume.SeriesVisibleAsync(seriesId, ct))
        {
            return this.NotFoundMessage(localizer, "error.animeResume.seriesNotFound");
        }

        var resume = await animeResume.ForSeriesAsync(seriesId, ct);
        return resume is null ? NoContent() : Ok(resume);
    }

    [HttpPost("apply")]
    public async Task<IActionResult> Apply(int seriesId, ApplyRequest request, CancellationToken ct)
    {
        var (error, result) = await animeResume.ApplyAsync(seriesId, request.MarkWatched, request.CoveredTo, ct);
        return error == AnimeResumeError.None ? Ok(result) : Failure(error);
    }

    [HttpPost("dismiss")]
    public async Task<IActionResult> Dismiss(int seriesId, CancellationToken ct)
    {
        var error = await animeResume.DismissAsync(seriesId, ct);
        return error == AnimeResumeError.None ? NoContent() : Failure(error);
    }

    [HttpDelete("dismiss")]
    public async Task<IActionResult> Undismiss(int seriesId, CancellationToken ct)
    {
        var error = await animeResume.UndismissAsync(seriesId, ct);
        return error == AnimeResumeError.None ? NoContent() : Failure(error);
    }

    private IActionResult Failure(AnimeResumeError error) => error switch
    {
        AnimeResumeError.SeriesNotFound => this.NotFoundMessage(localizer, "error.animeResume.seriesNotFound"),
        AnimeResumeError.NotEnabled => this.Fail(localizer, "error.animeResume.notEnabled"),
        _ => this.NotFoundMessage(localizer, "error.animeResume.unavailable"),
    };
}
