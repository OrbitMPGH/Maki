using Mangarr.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/recommendations")]
public class RecommendationController(RecommendationService recommendations) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] bool refresh, CancellationToken ct)
    {
        try
        {
            return Ok(await recommendations.GetAsync(refresh, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
