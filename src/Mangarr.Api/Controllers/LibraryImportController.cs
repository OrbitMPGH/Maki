using Mangarr.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/libraryimport")]
public class LibraryImportController(LibraryImportService importService) : ControllerBase
{
    public record ImportRequest(int RootFolderId, List<ImportRequestItem> Items);

    [HttpGet("scan")]
    public async Task<IActionResult> Scan([FromQuery] int rootFolderId, CancellationToken ct)
    {
        try
        {
            var candidates = await importService.ScanAsync(rootFolderId, ct);
            return Ok(candidates);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] ImportRequest request, CancellationToken ct)
    {
        var results = new List<ImportResult>();
        foreach (var item in request.Items)
        {
            try
            {
                results.Add(await importService.ImportAsync(request.RootFolderId, item, ct));
            }
            catch (Exception ex)
            {
                results.Add(new ImportResult(item.FolderName, false, ex.Message));
            }
        }

        return Ok(results);
    }
}
