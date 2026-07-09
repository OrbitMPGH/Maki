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
    Mangarr.Core.Indexers.ProwlarrClient prowlarr,
    Mangarr.Core.Download.QBittorrentClient qbittorrent,
    ConfigFileProvider configFile) : ControllerBase
{
    public record FlareSolverrSettings(string? Url);
    public record ProwlarrSettings(string? Url, string? ApiKey);
    public record QBittorrentSettings(string? Url, string? Username, string? Password, string? Category);

    [HttpGet("prowlarr")]
    public async Task<IActionResult> GetProwlarr(CancellationToken ct) => Ok(new ProwlarrSettings(
        await settings.GetAsync(SettingKeys.ProwlarrUrl, ct),
        await settings.GetAsync(SettingKeys.ProwlarrApiKey, ct)));

    [HttpPut("prowlarr")]
    public async Task<IActionResult> SetProwlarr([FromBody] ProwlarrSettings request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.ProwlarrUrl, request.Url, ct);
        await settings.SetAsync(SettingKeys.ProwlarrApiKey, request.ApiKey, ct);
        return Ok(request);
    }

    [HttpPost("prowlarr/test")]
    public async Task<IActionResult> TestProwlarr([FromBody] ProwlarrSettings request, CancellationToken ct)
    {
        var url = request.Url ?? await settings.GetAsync(SettingKeys.ProwlarrUrl, ct);
        var apiKey = request.ApiKey ?? await settings.GetAsync(SettingKeys.ProwlarrApiKey, ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
        {
            return BadRequest(new { error = "URL and API key are required" });
        }

        return await prowlarr.PingAsync(url, apiKey, ct)
            ? Ok(new { success = true })
            : StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = "Prowlarr did not respond (check URL/API key)" });
    }

    [HttpGet("qbittorrent")]
    public async Task<IActionResult> GetQBittorrent(CancellationToken ct) => Ok(new QBittorrentSettings(
        await settings.GetAsync(SettingKeys.QBittorrentUrl, ct),
        await settings.GetAsync(SettingKeys.QBittorrentUsername, ct),
        await settings.GetAsync(SettingKeys.QBittorrentPassword, ct),
        await settings.GetAsync(SettingKeys.QBittorrentCategory, ct) ?? "mangarr"));

    [HttpPut("qbittorrent")]
    public async Task<IActionResult> SetQBittorrent([FromBody] QBittorrentSettings request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.QBittorrentUrl, request.Url, ct);
        await settings.SetAsync(SettingKeys.QBittorrentUsername, request.Username, ct);
        await settings.SetAsync(SettingKeys.QBittorrentPassword, request.Password, ct);
        await settings.SetAsync(SettingKeys.QBittorrentCategory, request.Category, ct);
        return Ok(request);
    }

    [HttpPost("qbittorrent/test")]
    public async Task<IActionResult> TestQBittorrent([FromBody] QBittorrentSettings request, CancellationToken ct)
    {
        var url = request.Url ?? await settings.GetAsync(SettingKeys.QBittorrentUrl, ct);
        if (string.IsNullOrWhiteSpace(url))
        {
            return BadRequest(new { error = "URL is required" });
        }

        var username = request.Username ?? await settings.GetAsync(SettingKeys.QBittorrentUsername, ct) ?? string.Empty;
        var password = request.Password ?? await settings.GetAsync(SettingKeys.QBittorrentPassword, ct) ?? string.Empty;

        return await qbittorrent.PingAsync(url, username, password, ct)
            ? Ok(new { success = true })
            : StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = "qBittorrent login failed" });
    }

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
