using Mangarr.Api.Configuration;
using Mangarr.Api.Services;
using Mangarr.Core.Configuration;
using Mangarr.Core.Http;
using Microsoft.AspNetCore.Mvc;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/settings")]
public class SettingsController(
    SettingsService settings,
    FlareSolverrClient flareSolverr,
    ConfigFileProvider configFile) : ControllerBase
{
    public record FlareSolverrSettings(string? Url);

    [HttpGet("flaresolverr")]
    public async Task<IActionResult> GetFlareSolverr(CancellationToken ct)
    {
        var url = await settings.GetAsync(SettingKeys.FlareSolverrUrl, ct);
        return Ok(new FlareSolverrSettings(url));
    }

    [HttpPut("flaresolverr")]
    public async Task<IActionResult> SetFlareSolverr([FromBody] FlareSolverrSettings request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.FlareSolverrUrl, request.Url, ct);
        return Ok(new FlareSolverrSettings(request.Url));
    }

    [HttpPost("flaresolverr/test")]
    public async Task<IActionResult> TestFlareSolverr([FromBody] FlareSolverrSettings request, CancellationToken ct)
    {
        var url = request.Url ?? await settings.GetAsync(SettingKeys.FlareSolverrUrl, ct);
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "No FlareSolverr URL configured" });
        }

        var ok = await flareSolverr.PingAsync(url, ct);
        return ok
            ? Ok(new { success = true })
            : StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = "FlareSolverr did not respond" });
    }

    [HttpGet("general")]
    public IActionResult GetGeneral()
    {
        return Ok(new { apiKey = configFile.Config.ApiKey, port = configFile.Config.Port });
    }
}
