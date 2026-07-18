using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Notifications;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/libraryimport")]
public class LibraryImportController(
    LibraryImportService importService, EventBroadcaster events, NotificationService notifications) : ControllerBase
{
    public record ImportRequest(int RootFolderId, List<ImportRequestItem> Items, bool UpdateComicInfo = true);

    /// <summary>
    /// Ceiling on one request's batch. Each item is imported serially and can involve a metadata
    /// lookup plus rewriting every CBZ in the folder, so an unbounded list means an HTTP call that
    /// runs for many minutes and dies to a proxy timeout with no usable response. The client sends
    /// batches of this size; live progress still arrives over SignalR either way.
    /// </summary>
    public const int MaxItemsPerRequest = 50;

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
        if (request.Items.Count == 0)
        {
            return BadRequest(new { error = "No items to import" });
        }

        if (request.Items.Count > MaxItemsPerRequest)
        {
            return BadRequest(new
            {
                error = $"Too many items in one request ({request.Items.Count}); import in batches of {MaxItemsPerRequest} or fewer",
            });
        }

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

            if (result.Success)
            {
                notifications.Dispatch(NotificationEventType.ImportCompleted, new NotificationMessage(
                    NotificationEventType.ImportCompleted,
                    Title: "Import completed",
                    Body: $"Imported '{item.FolderName}' into the library"));
            }
        }

        return Ok(results);
    }
}
