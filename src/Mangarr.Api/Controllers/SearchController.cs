using Mangarr.Core.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/search")]
public class SearchController(IEnumerable<IMetadataProvider> metadataProviders) : ControllerBase
{
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
