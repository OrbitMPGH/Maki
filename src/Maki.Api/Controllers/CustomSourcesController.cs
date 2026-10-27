using System.Text.Json;
using Maki.Api.Auth;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Sources.Custom;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

/// <summary>
/// Custom source definitions: list, export, test, save and delete. Admin only, every action on its
/// own, because a definition makes the server fetch whatever host it names (pinned by
/// <c>CustomSourcesControllerTests</c>; the inventory test checks policies by name only for
/// <c>SettingsController</c>).
/// </summary>
[ApiController]
[Route("api/v1/custom-sources")]
public class CustomSourcesController(ILocalizer localizer, CustomSourceService service, CustomSourceProbe probe) : ControllerBase
{
    public record RenderedProblem(string Code, string Error);

    public record ProbeSiteRequest(string? SeriesUrl, string? ChapterUrl);

    public record ProbeSearchRequest(string? BaseUrl, string? SeriesTemplate, string? SeriesUrl, string? Query);

    [Authorize(Policy = Policies.Admin)]
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var entries = await service.ListAsync(ct);
        var rows = new List<object>();
        foreach (var entry in entries)
        {
            var name = entry.Id is null ? null : Core.Sources.Custom.CustomSourceDefinition.NamePrefix + entry.Id;
            rows.Add(new
            {
                entry.Id,
                SourceName = name,
                Name = entry.Definition?.Name ?? entry.Raw["name"]?.ToString() ?? entry.Id,
                Valid = entry.Definition is not null,
                entry.Problem,
                LinkedSeries = name is null ? 0 : await service.LinkedSeriesAsync(name, ct),
                Definition = entry.Raw
            });
        }

        return Ok(rows);
    }

    /// <summary>The stored definition as a file to share, without where it was installed from.</summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("{id}")]
    public async Task<IActionResult> Export(string id, CancellationToken ct) =>
        await service.FindAsync(id, ct) is { } entry
            ? Ok(CustomSourceService.Export(entry))
            : this.NotFoundMessage(localizer, "error.customSource.notFound", new { id });

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] JsonElement body, CancellationToken ct)
    {
        var (definition, parseProblem) = CustomSourceService.Parse(body);
        if (definition is null)
        {
            return Problems([parseProblem!]);
        }

        var problems = service.Validate(definition);
        if (problems.Count > 0)
        {
            return Problems(problems);
        }

        var report = await CustomSourceTester.RunAsync(service.Build(definition), ct);
        return Ok(new
        {
            report.Passed,
            Steps = report.Steps.Select(s => new
            {
                s.Name,
                s.Status,
                Error = s.ErrorKey is { } key ? localizer.Get(key) : s.Error,
                s.Detail
            })
        });
    }

    /// <summary>
    /// The wizard's first step: reads a pasted series page (and chapter page) and answers with a
    /// draft definition and the guesses behind it. Saves nothing.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost("probe")]
    public async Task<IActionResult> Probe([FromBody] ProbeSiteRequest request, CancellationToken ct)
    {
        try
        {
            var result = await probe.ProbeSiteAsync(request.SeriesUrl, request.ChapterUrl, ct);
            return Ok(new
            {
                result.Draft,
                result.ViaFlareSolverr,
                result.Title,
                result.SeriesId,
                result.Engine,
                result.Chapters,
                result.Pages,
                result.ChapterError,
                HostTaken = HostTaken(result.Draft)
            });
        }
        catch (CustomSourceProbe.ProbeFailed ex)
        {
            return this.Fail(localizer, ex.Key, ex.Args);
        }
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("probe/search")]
    public async Task<IActionResult> ProbeSearch([FromBody] ProbeSearchRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await probe.ProbeSearchAsync(request.BaseUrl, request.SeriesTemplate, request.SeriesUrl, request.Query, ct));
        }
        catch (CustomSourceProbe.ProbeFailed ex)
        {
            return this.Fail(localizer, ex.Key, ex.Args);
        }
    }

    /// <summary>The source that already reads this host, so the wizard can offer to edit it instead.</summary>
    private object? HostTaken(Core.Sources.Custom.CustomSourceDefinition draft) =>
        service.SourceForHost(draft.BaseUrl) is { } source
            ? new { source.Name, source.DisplayName, Custom = source is CustomSource custom ? custom.Definition.Id : null }
            : null;

    /// <summary>
    /// Creates or replaces the definition with this id. A change that could re-key the chapters of
    /// linked series answers 409 with what it found until it is sent again with <c>confirm=true</c>.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPut("{id}")]
    public async Task<IActionResult> Save(string id, [FromBody] JsonElement body, [FromQuery] bool confirm, CancellationToken ct)
    {
        var (definition, parseProblem) = CustomSourceService.Parse(body);
        if (definition is null)
        {
            return Problems([parseProblem!]);
        }

        if (!string.Equals(definition.Id, id, StringComparison.Ordinal))
        {
            return this.Fail(localizer, "error.customSource.idMismatch");
        }

        return await service.SaveAsync(definition, confirm, ct) switch
        {
            CustomSourceService.Refused refused => Problems(refused.Problems),
            CustomSourceService.NeedsConfirmation needs => Conflict(new
            {
                code = "error.customSource.identityChange",
                error = localizer.Get("error.customSource.identityChange", new { count = needs.LinkedSeries }),
                needs.Reasons,
                needs.LinkedSeries,
                needs.Changes
            }),
            CustomSourceService.Saved saved => Ok(new { saved.Created, SourceName = saved.Definition.SourceName }),
            _ => throw new InvalidOperationException("Unexpected save result")
        };
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct) =>
        await service.DeleteAsync(id, ct)
            ? NoContent()
            : this.NotFoundMessage(localizer, "error.customSource.notFound", new { id });

    private BadRequestObjectResult Problems(IReadOnlyList<CustomSourceProblem> problems)
    {
        var rendered = problems.Select(p => new RenderedProblem(p.Key, localizer.Get(p.Key, p.Args))).ToList();
        return BadRequest(new { code = rendered[0].Code, error = rendered[0].Error, problems = rendered });
    }
}
