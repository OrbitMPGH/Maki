using Maki.Api.Auth;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Mihon;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

/// <summary>Stateless: the client keeps the parsed list and sends batches, so a restart costs one batch.</summary>
[ApiController]
[Route("api/v1/mihonimport")]
[Authorize(Policy = Policies.ImportLibrary)]
public class MihonImportController(ILocalizer localizer, MihonImportService mihon) : ControllerBase
{
    public const int MaxItemsPerRequest = 50;

    public const long MaxUploadBytes = 100L * 1024 * 1024;

    [HttpPost("parse")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<IActionResult> Parse(IFormFile? file, CancellationToken ct)
    {
        if (!await mihon.DumpAvailableAsync(ct))
        {
            return this.Conflict(localizer, "error.mihon.dumpUnavailable");
        }

        if (file is null || file.Length == 0)
        {
            return this.Fail(localizer, "error.system.noFileUploaded");
        }

        MihonBackup backup;
        try
        {
            // The form is already buffered (to disk past 64 KB), so this reads no request body and
            // the parser holds one manga message at a time rather than the whole backup.
            await using var upload = file.OpenReadStream();
            backup = MihonBackupReader.Read(upload);
        }
        catch (MihonBackupException ex)
        {
            return this.Fail(localizer, ex.Reason switch
            {
                MihonBackupFailure.JsonBackup => "error.mihon.jsonBackup",
                MihonBackupFailure.TooLarge => "error.mihon.tooLarge",
                MihonBackupFailure.Corrupt => "error.mihon.corrupt",
                _ => "error.mihon.notABackup",
            });
        }

        return Ok(new MihonParseResponse(
            backup.Manga.Select(MihonParsedManga.From).ToList(), backup.SkippedNonLibrary, backup.CategoryCount));
    }

    [HttpPost("resolve")]
    public async Task<IActionResult> Resolve([FromBody] MihonResolveRequest request, CancellationToken ct)
    {
        var items = request.Items ?? [];
        if (items.Count > MaxItemsPerRequest)
        {
            return this.Fail(localizer, "error.mihon.tooManyItems", new { count = items.Count, max = MaxItemsPerRequest });
        }

        if (!await mihon.DumpAvailableAsync(ct))
        {
            return this.Conflict(localizer, "error.mihon.dumpUnavailable");
        }

        return Ok(new MihonResolveResponse(await mihon.ResolveAsync(items, ct)));
    }

    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] MihonImportRequest request, CancellationToken ct)
    {
        var items = request.Items ?? [];
        if (items.Count > MaxItemsPerRequest)
        {
            return this.Fail(localizer, "error.mihon.tooManyItems", new { count = items.Count, max = MaxItemsPerRequest });
        }

        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            return this.Fail(localizer, "error.mihon.runIdRequired");
        }

        if (!await mihon.DumpAvailableAsync(ct))
        {
            return this.Conflict(localizer, "error.mihon.dumpUnavailable");
        }

        if (!await mihon.CanUseRootFolderAsync(request.RootFolderId, ct))
        {
            return this.Fail(localizer, "error.series.rootFolderNotFound");
        }

        return Ok(new MihonImportResponse(await mihon.ImportAsync(request, ct)));
    }
}
