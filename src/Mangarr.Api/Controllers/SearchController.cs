using Mangarr.Core.Metadata;
using Mangarr.Core.Sources;
using Microsoft.AspNetCore.Mvc;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/search")]
public class SearchController(
    IEnumerable<IMetadataProvider> metadataProviders,
    SourceRegistry sourceRegistry) : ControllerBase
{
    /// <summary>Search a specific site source, for manually linking a series.</summary>
    [HttpGet("source")]
    public async Task<IActionResult> SearchSource(
        [FromQuery] string sourceName, [FromQuery] string query, CancellationToken ct)
    {
        var source = sourceRegistry.Find(sourceName);
        if (source is null)
        {
            return BadRequest(new { error = $"Unknown source: {sourceName}" });
        }

        var results = await source.SearchAsync(query, ct);
        return Ok(results);
    }

    [HttpGet("sources")]
    public IActionResult ListSources()
    {
        return Ok(sourceRegistry.All.Select(s => new
        {
            s.Name,
            s.DisplayName,
            s.BaseUrl,
            NeedsFlareSolverr = s.Capabilities.HasFlag(SourceCapabilities.NeedsFlareSolverr)
        }));
    }

    [HttpGet("metadata")]
    public async Task<IActionResult> SearchMetadata([FromQuery] string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "query is required" });
        }

        var provider = metadataProviders.First();
        var results = await provider.SearchAsync(query, ct);
        return Ok(results);
    }
}
