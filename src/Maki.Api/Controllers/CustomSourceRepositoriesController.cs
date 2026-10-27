using Maki.Api.Auth;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Sources.Custom;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

/// <summary>
/// Repositories of custom source definitions, and the updates they bring. Admin only, every action
/// on its own, for the same reason as <see cref="CustomSourcesController"/>: adding a repository
/// makes the server fetch whatever URL it names (pinned by <c>CustomSourceRepositoriesControllerTests</c>).
/// </summary>
[ApiController]
[Route("api/v1/custom-sources")]
public class CustomSourceRepositoriesController(ILocalizer localizer, CustomRepositoryService repositories) : ControllerBase
{
    public record UrlRequest(string? Url);

    public record AutoUpdateRequest(string? Url, bool AutoUpdate);

    public record InstallRequest(string? Url, string? Id);

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("repositories")]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(Rendered(await repositories.ListAsync(ct)));

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("repositories")]
    public async Task<IActionResult> Add([FromBody] UrlRequest request, CancellationToken ct) =>
        Result(await repositories.AddAsync(request.Url, ct));

    /// <summary>Removes a repository; what was installed from it keeps working as hand-made sources.</summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpDelete("repositories")]
    public async Task<IActionResult> Remove([FromQuery] string url, CancellationToken ct) =>
        await repositories.RemoveAsync(url, ct) is var result && result is int detached ? Ok(new { detached }) : Result(result);

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("repositories/autoupdate")]
    public async Task<IActionResult> SetAutoUpdate([FromBody] AutoUpdateRequest request, CancellationToken ct) =>
        Result(await repositories.SetAutoUpdateAsync(request.Url ?? "", request.AutoUpdate, ct));

    /// <summary>Fetches one repository's index again, or every repository's when no URL is given.</summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost("repositories/refresh")]
    public async Task<IActionResult> Refresh([FromBody] UrlRequest request, CancellationToken ct)
    {
        await repositories.RefreshAsync(request.Url, ct);
        return Ok(Rendered(await repositories.ListAsync(ct)));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("repositories/install")]
    public async Task<IActionResult> Install([FromBody] InstallRequest request, [FromQuery] bool confirm, CancellationToken ct) =>
        Result(await repositories.InstallAsync(request.Url ?? "", request.Id ?? "", confirm, ct));

    [Authorize(Policy = Policies.Admin)]
    [HttpGet("{id}/update")]
    public async Task<IActionResult> PreviewUpdate(string id, CancellationToken ct) =>
        Result(await repositories.PreviewUpdateAsync(id, ct));

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{id}/update")]
    public async Task<IActionResult> ApplyUpdate(string id, [FromQuery] bool confirm, CancellationToken ct) =>
        Result(await repositories.ApplyUpdateAsync(id, confirm, ct));

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{id}/revert")]
    public async Task<IActionResult> Revert(string id, [FromQuery] bool confirm, CancellationToken ct) =>
        Result(await repositories.RevertAsync(id, confirm, ct));

    private IActionResult Result(object result) => result switch
    {
        CustomRepositoryService.Refused refused => this.Fail(localizer, refused.Key, Args(refused.Args)),
        CustomRepositoryService.TestFailed failed => BadRequest(new
        {
            code = "error.customRepository.installTestFailed",
            error = localizer.Get("error.customRepository.installTestFailed"),
            steps = failed.Report.Steps.Select(s => new
            {
                s.Name,
                s.Status,
                Error = s.ErrorKey is { } key ? localizer.Get(key) : s.Error,
                s.Detail
            })
        }),
        CustomSourceService.Refused refused => BadRequest(new
        {
            code = refused.Problems[0].Key,
            error = localizer.Get(refused.Problems[0].Key, refused.Problems[0].Args),
            problems = refused.Problems.Select(p => new CustomSourcesController.RenderedProblem(p.Key, localizer.Get(p.Key, p.Args)))
        }),
        CustomSourceService.NeedsConfirmation needs => Conflict(new
        {
            code = "error.customSource.identityChange",
            error = localizer.Get("error.customSource.identityChange", new { count = needs.LinkedSeries }),
            needs.Reasons,
            needs.LinkedSeries,
            needs.Changes
        }),
        CustomSourceService.Saved saved => Ok(new { saved.Created, SourceName = saved.Definition.SourceName }),
        CustomRepositoryService.RepositoryView view => Ok(Rendered([view])[0]),
        _ => Ok(result)
    };

    private object[] Rendered(IReadOnlyList<CustomRepositoryService.RepositoryView> views) =>
        views.Select(v => (object)new
        {
            v.Url,
            v.Name,
            v.AutoUpdate,
            v.Secure,
            v.LastCheckedUtc,
            LastError = v.LastError is { } e ? localizer.Get(e.Key, Args(e.Args)) : null,
            v.Definitions
        }).ToArray();

    private static IReadOnlyDictionary<string, object?>? Args(Dictionary<string, string>? args) =>
        args?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
}
