using Mangarr.Api.Services;
using Mangarr.Metadata.MangaBaka;
using Microsoft.AspNetCore.Mvc;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/recommendations")]
public class RecommendationController(
    RecommendationService recommendations,
    MangaBakaLocalStore store,
    JikanReviewClient reviews) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Get([FromBody] RecommendationRequest? request, CancellationToken ct)
    {
        try
        {
            return Ok(await recommendations.GetAsync(request ?? new RecommendationRequest(), ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Rich detail for one MangaBaka series (for the Discover detail card).</summary>
    [HttpGet("detail/{id:long}")]
    public async Task<IActionResult> Detail(long id, CancellationToken ct)
    {
        if (!await store.IsAvailableAsync(ct))
        {
            return BadRequest(new { error = "The local MangaBaka database is not available." });
        }

        var detail = await store.GetDetailAsync(id, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    /// <summary>A few MyAnimeList reviews for a series (lazy; best-effort via Jikan).</summary>
    [HttpGet("reviews/{malId:int}")]
    public async Task<IActionResult> Reviews(int malId, CancellationToken ct) =>
        Ok(await reviews.GetReviewsAsync(malId, ct));
}
