using Maki.Api.Auth;
using Maki.Core.Configuration;
using Maki.Api.Localization;
using Maki.Metadata.PageShare;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

/// <summary>
/// The page-share table's switch and status, beside the other crowd artifacts' endpoints under
/// <c>settings/recommendations</c>. On by default and no UI, same as the EASE switch.
/// </summary>
[ApiController]
[Route("api/v1/settings")]
public class SettingsPageShareController(
    IAppSettings settings,
    PageShareInstaller installer,
    PageShareCache cache,
    ILocalizer localizer) : ControllerBase
{
    public record PageShareStatus(bool Enabled, bool Installed, int SeriesCount, DateTime? GeneratedAt);

    public record PageShareRequest(bool Enabled);

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("recommendations/page-share")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var enabled = await installer.IsEnabledAsync(ct);
        var table = enabled ? await cache.GetAsync(ct) : null;
        return Ok(new PageShareStatus(
            enabled,
            table is not null || (!enabled && System.IO.File.Exists(cache.DatabasePath)),
            table?.Count ?? 0,
            table?.GeneratedAt));
    }

    /// <summary>
    /// Turns the penalty off, leaving every channel alone. Takes effect on the next uncached pool,
    /// since the flag is part of the cache key; off also stops the download.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPut("recommendations/page-share")]
    public async Task<IActionResult> Set([FromBody] PageShareRequest request, CancellationToken ct)
    {
        await settings.SetAsync(SettingKeys.RecommendationsPageShare, request.Enabled ? "true" : "false", ct);
        return Ok(new { request.Enabled });
    }

    /// <summary>Downloads the table now, ignoring the freshness check but not the safety ones.</summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost("recommendations/page-share/download")]
    public async Task<IActionResult> Download(CancellationToken ct)
    {
        var result = await installer.InstallAsync(force: true, ct);
        var reason = result.Reason.StartsWith("install.", StringComparison.Ordinal)
            ? localizer.Get(result.Reason, result.ReasonArgs)
            : result.Reason;
        return Ok(new { installed = result.Installed, reason, rowCount = result.RowCount });
    }
}
