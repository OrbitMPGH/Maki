using System.Globalization;
using Maki.Api.Auth;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Images;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Maki.Api.Controllers;

/// <summary>Page conversion: the format setting, the library pass and what it has saved so far.</summary>
[ApiController]
[Route("api/v1/pageconversion")]
[Authorize(Policy = Policies.Admin)]
public class PageConversionController(
    MakiDbContext db, IAppSettings settings, PageConversionWorker worker, PageConversionPreviewService previews,
    ILocalizer localizer) : ControllerBase
{
    /// <param name="Format">"webp", "avif", or null while conversion is off.</param>
    /// <param name="Stop">"smallest", "compact", "balanced", "high" or "lossless"; sent while off too.</param>
    public record SettingsDto(string? Format, string Stop, bool PngLossless);

    public record SamplesDto(IReadOnlyList<PageConversionSample> Samples);

    /// <param name="ConvertedFiles">Library files whose pages Maki converted.</param>
    /// <param name="PendingFiles">Archives conversion has not looked at yet.</param>
    /// <param name="TrashBytes">Originals still waiting in the upgrade trash.</param>
    public record LibraryDto(int ConvertedFiles, long SavedBytes, int PendingFiles, long TrashBytes, int TrashFiles, int TrashDays);

    /// <param name="State">"idle", "running" or "paused".</param>
    /// <param name="Hold">Why a pass paused itself: "lowDisk" or "conversionOff"; null otherwise.</param>
    public record PassDto(string State, string? Hold, string? CurrentFile, PageConversionPassTotals? Totals);

    public record StatusDto(SettingsDto Settings, PassDto Pass, LibraryDto Library);

    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var stored = await PageConversionOptions.ReadAsync(db, ct);
        var snapshot = await worker.SnapshotAsync(ct);
        var converted = db.ChapterFiles.IgnoreQueryFilters().Where(f => f.OriginalImageFormat != null);
        var convertedFiles = await converted.CountAsync(ct);
        var savedBytes = await converted.SumAsync(f => f.ConversionSavedBytes, ct);
        var pending = await PageConversionWorker.PendingQuery(db).CountAsync(ct);
        var (trashBytes, trashFiles) = TrashedOriginals(await db.RootFolders.Select(r => r.Path).ToListAsync(ct));
        var options = await UpgradeOptions.LoadAsync(settings, ct);

        return Ok(new StatusDto(
            ToDto(stored),
            new PassDto(Name(snapshot.State), snapshot.Hold switch
            {
                PageConversionHold.LowDisk => "lowDisk",
                PageConversionHold.ConversionOff => "conversionOff",
                _ => null
            }, snapshot.CurrentFile, snapshot.Pass),
            new LibraryDto(convertedFiles, savedBytes, pending, trashBytes, trashFiles,
                options.TrashRetentionDays)));
    }

    /// <summary>
    /// Changing the settings puts the files the old ones left as they were back in line for the next
    /// pass: a JPEG that WebP could not shrink often shrinks as AVIF. Converted files stay converted,
    /// since converting a converted page again only loses detail.
    /// </summary>
    [HttpPut("settings")]
    public async Task<IActionResult> Save([FromBody] SettingsDto request, CancellationToken ct)
    {
        PageConversionFormat? format = null;
        if (request.Format is not null && !string.Equals(request.Format, "off", StringComparison.OrdinalIgnoreCase))
        {
            format = PageConversionSettings.ParseFormat(request.Format);
            if (format is null)
            {
                return this.Fail(localizer, "error.pageConversion.formatInvalid");
            }
        }

        if (PageConversionSettings.ParseStop(request.Stop) is not { } stop)
        {
            return this.Fail(localizer, "error.pageConversion.stopInvalid");
        }

        var before = await PageConversionOptions.ReadAsync(db, ct);
        await settings.SetAsync(SettingKeys.PageConversionFormat,
            format is { } f ? PageConversionSettings.FormatName(f) : null, ct);
        await settings.SetAsync(SettingKeys.PageConversionStop, PageConversionSettings.StopName(stop), ct);
        await settings.SetAsync(SettingKeys.PageConversionPngLossless, request.PngLossless ? "true" : null, ct);

        var after = await PageConversionOptions.ReadAsync(db, ct);
        if (after.Active is { } active && active != before.Active)
        {
            await db.ChapterFiles.IgnoreQueryFilters()
                .Where(c => c.ConversionCheckedAtUtc != null && c.OriginalImageFormat == null)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.ConversionCheckedAtUtc, (DateTime?)null), ct);
        }

        return await Status(ct);
    }

    /// <summary>Up to three pages for the wizard to compare on: greyscale, colour and the heaviest tier.</summary>
    [HttpGet("samples")]
    public async Task<IActionResult> Samples(CancellationToken ct) =>
        Ok(new SamplesDto(await previews.SamplesAsync(ct)));

    [HttpGet("samples/{fileId:int}")]
    public async Task<IActionResult> Sample(int fileId, CancellationToken ct) =>
        await previews.SampleAsync(fileId, ct) is { } sample ? Ok(sample) : NotFound();

    /// <summary>
    /// One page as the pass would write it, or as stored with <paramref name="original"/>. The sizes
    /// ride in headers; <c>X-Maki-Kept</c> is 1 when the page would be left as it is, and the body is
    /// then the stored page. <c>X-Maki-Kept-Reason</c> then names why: notsmaller, toosmall, toolarge,
    /// animated, highbitdepth, lossless, alreadycompact, or unreadable when it does not decode.
    /// </summary>
    [HttpGet("preview/{fileId:int}/{page:int}")]
    public async Task<IActionResult> Preview(int fileId, int page, [FromQuery] string? format, [FromQuery] string? stop,
        [FromQuery] bool pngLossless, [FromQuery] bool original, CancellationToken ct)
    {
        PageConversionSettings? wanted = null;
        if (!original)
        {
            if (PageConversionSettings.ParseStop(stop) is not { } parsedStop)
            {
                return this.Fail(localizer, "error.pageConversion.stopInvalid");
            }

            var parsedFormat = format is null && parsedStop == PageConversionStop.Lossless
                ? PageConversionFormat.Webp
                : PageConversionSettings.ParseFormat(format);
            if (parsedFormat is not { } f)
            {
                return this.Fail(localizer, "error.pageConversion.formatInvalid");
            }

            wanted = new PageConversionSettings(f, parsedStop, pngLossless);
        }

        var preview = await previews.PreviewAsync(fileId, page, wanted, ct);
        if (preview is not null && !System.IO.File.Exists(preview.Path))
        {
            preview = await previews.PreviewAsync(fileId, page, wanted, ct);
        }

        if (preview is null || !System.IO.File.Exists(preview.Path))
        {
            return NotFound();
        }

        Response.Headers["X-Maki-Original-Bytes"] = preview.OriginalBytes.ToString(CultureInfo.InvariantCulture);
        Response.Headers["X-Maki-Encoded-Bytes"] = preview.EncodedBytes.ToString(CultureInfo.InvariantCulture);
        Response.Headers["X-Maki-Kept"] = preview.Kept ? "1" : "0";
        if (preview.Kept && preview.KeptReason is { } reason)
        {
            Response.Headers["X-Maki-Kept-Reason"] = reason;
        }

        Response.Headers.CacheControl = "private, no-cache";
        var etag = new EntityTagHeaderValue($"\"{preview.Key}\"");
        if (Request.GetTypedHeaders().IfNoneMatch?.Any(t => t.Compare(etag, useStrongComparison: false)) == true)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return PhysicalFile(preview.Path, preview.ContentType, lastModified: null, entityTag: etag);
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start(CancellationToken ct)
    {
        if (await PageConversionOptions.LoadAsync(db, ct) is null)
        {
            return this.Conflict(localizer, "error.pageConversion.off");
        }

        await worker.StartPassAsync(ct);
        return await Status(ct);
    }

    [HttpPost("pause")]
    public async Task<IActionResult> Pause(CancellationToken ct)
    {
        await worker.PauseAsync(ct);
        return await Status(ct);
    }

    private static SettingsDto ToDto(PageConversionOptions.Stored stored) => new(
        stored.Format is { } f ? PageConversionSettings.FormatName(f) : null, PageConversionSettings.StopName(stored.Stop),
        stored.PngLossless);

    private static string Name(PageConversionPassState state) => state switch
    {
        PageConversionPassState.Running => "running",
        PageConversionPassState.Paused => "paused",
        _ => "idle"
    };

    /// <summary>The <c>conv-</c> files under each root's upgrade trash, which no history row counts.</summary>
    private static (long Bytes, int Files) TrashedOriginals(IEnumerable<string> roots)
    {
        long bytes = 0;
        var files = 0;
        foreach (var root in roots)
        {
            var trash = Path.Combine(root, UpgradeTrash.FolderName);
            try
            {
                if (!Directory.Exists(trash)) continue;
                foreach (var series in Directory.EnumerateDirectories(trash))
                {
                    foreach (var file in new DirectoryInfo(series).EnumerateFiles(PageConversionService.TrashPrefix + "*"))
                    {
                        bytes += file.Length;
                        files++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return (bytes, files);
    }
}
