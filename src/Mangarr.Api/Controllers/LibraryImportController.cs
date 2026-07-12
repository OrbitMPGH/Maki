using Mangarr.Api.Hubs;
using Mangarr.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/libraryimport")]
public class LibraryImportController(LibraryImportService importService, EventBroadcaster events) : ControllerBase
{
    public record ImportRequest(int RootFolderId, List<ImportRequestItem> Items, bool UpdateComicInfo = true);

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
            ImportResult result;
            try
            {
                result = await importService.ImportAsync(request.RootFolderId, item, request.UpdateComicInfo, ct);
            }
            catch (Exception ex)
            {
                result = new ImportResult(item.FolderName, false, ex.Message);
            }

            results.Add(result);
            await events.ImportProgress(item.FolderName, result.Success ? "Imported" : "Failed",
                done: true, success: result.Success, error: result.Error);
        }

        return Ok(results);
    }
}
