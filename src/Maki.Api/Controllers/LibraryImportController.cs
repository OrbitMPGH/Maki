using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Hubs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/libraryimport")]
// Covers the scan as well as the import: the scan enumerates directories on the host and returns
// their names, so it is a filesystem read even before anything is adopted.
[Authorize(Policy = Policies.ImportLibrary)]
public class LibraryImportController(
    ILocalizer localizer,
    LibraryImportService importService,
    EventBroadcaster events,
    NotificationService notifications,
    IUserLocaleResolver locales,
    InboxService inbox) : ControllerBase
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
            return this.Fail(localizer, "error.libraryImport.noItemsToImport");
        }

        if (request.Items.Count > MaxItemsPerRequest)
        {
            return this.Fail(localizer, "error.libraryImport.tooManyItems",
                new { count = request.Items.Count, max = MaxItemsPerRequest });
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
                var locale = await locales.DefaultAsync(ct);
                notifications.Dispatch(NotificationEventType.ImportCompleted, new NotificationMessage(
                    NotificationEventType.ImportCompleted,
                    Title: localizer.GetFor(locale, "notify.import.completed.title"),
                    Body: localizer.GetFor(locale, "notify.import.completed.body",
                        new { folder = item.FolderName })));
            }
        }

        // One inbox row for the whole request, not one per folder: the client batches 50 at a time,
        // and 50 rows saying the same thing is not a notification, it is a flood. The outbound
        // per-folder Dispatch above is left alone — a chat channel is a log, an inbox is not.
        var imported = results.Count(r => r.Success);
        var failed = results.Count - imported;
        inbox.Raise(InboxEventType.ImportFinished, new InboxMessage(
                Key: failed == 0 ? "inbox.libraryImport.finished" : "inbox.libraryImport.finishedWithErrors",
                Params: InboxMessage.Args(new { imported, failed }),
                Level: failed == 0 ? NotificationLevel.Info :
                    imported > 0 ? NotificationLevel.Warning : NotificationLevel.Error,
                Url: "/import"),
            InboxAudience.Admins);

        return Ok(results);
    }
}
