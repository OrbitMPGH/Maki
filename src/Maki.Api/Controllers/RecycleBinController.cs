using System.Globalization;
using Maki.Api.Auth;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// Files users deleted, waiting to be restored or purged. Every delete that bins a file needs
/// <c>DeleteSeries</c>, so seeing, restoring and emptying the bin need it too. Entries are scoped by
/// root folder, like series.
/// </summary>
[ApiController]
[Route("api/v1/recyclebin")]
[Authorize(Policy = Policies.DeleteSeries)]
public class RecycleBinController(
    MakiDbContext db, RecycleBinService bin, IAppSettings settings, ILocalizer localizer) : ControllerBase
{
    public record ChapterRef(decimal? Number, int? Volume, string Language);

    public record EntryDto(
        int Id,
        int SeriesId,
        string SeriesTitle,
        bool SeriesExists,
        string FileName,
        string RelativePath,
        long Size,
        string Reason,
        DateTime DeletedAt,
        string? DeletedBy,
        DateTime ExpiresAt,
        bool Missing,
        IReadOnlyList<ChapterRef> Chapters);

    public record BinDto(int RetentionDays, long TotalBytes, IReadOnlyList<EntryDto> Entries);

    public record RetentionRequest(int Days);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var days = await bin.RetentionDaysAsync(ct);
        var entries = await db.RecycleBin.AsNoTracking()
            .OrderBy(e => e.SeriesTitle).ThenByDescending(e => e.DeletedAtUtc)
            .ToListAsync(ct);
        var seriesIds = entries.Select(e => e.SeriesId).Distinct().ToList();
        var live = (await db.Series.Where(s => seriesIds.Contains(s.Id)).Select(s => new { s.Id, s.RootFolderId }).ToListAsync(ct))
            .ToDictionary(s => s.Id, s => s.RootFolderId);

        var dtos = entries.Select(e => new EntryDto(
            e.Id,
            e.SeriesId,
            e.SeriesTitle,
            live.TryGetValue(e.SeriesId, out var root) && root == e.RootFolderId,
            e.RelativePath.Replace('\\', '/').Split('/')[^1],
            e.RelativePath,
            e.Size,
            ReasonName(e.Reason),
            e.DeletedAtUtc,
            e.DeletedByName,
            e.DeletedAtUtc.AddDays(days),
            RecycleBinService.BinFile(e) is not { } path || !System.IO.File.Exists(path),
            RecycleBinService.Chapters(e).Select(c => new ChapterRef(c.Number, c.Volume, c.Language)).ToList()))
            .ToList();
        return Ok(new BinDto(days, entries.Sum(e => e.Size), dtos));
    }

    [HttpPost("{id:int}/restore")]
    public async Task<IActionResult> Restore(int id, CancellationToken ct)
    {
        var result = await bin.RestoreAsync(id, ct);
        return result.Status switch
        {
            RecycleBinService.RestoreStatus.Linked => Ok(new { linked = true, chapters = result.ChaptersLinked }),
            RecycleBinService.RestoreStatus.Unlinked => Ok(new { linked = false, chapters = 0 }),
            RecycleBinService.RestoreStatus.NotFound => this.NotFoundMessage(localizer, "error.recycleBin.notFound"),
            RecycleBinService.RestoreStatus.FileMissing => this.Gone(localizer, "error.recycleBin.fileMissing"),
            RecycleBinService.RestoreStatus.TargetExists => this.Conflict(localizer, "error.recycleBin.targetExists"),
            RecycleBinService.RestoreStatus.TargetInvalid => this.Fail(localizer, "error.recycleBin.targetInvalid"),
            RecycleBinService.RestoreStatus.CrossVolume => this.Fail(localizer, "error.recycleBin.crossVolume"),
            _ => this.ServerError(localizer, "error.recycleBin.restoreFailed")
        };
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var entry = await db.RecycleBin.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null)
        {
            return this.NotFoundMessage(localizer, "error.recycleBin.notFound");
        }

        return await bin.DeleteAsync(entry, ct)
            ? NoContent()
            : this.ServerError(localizer, "error.recycleBin.deleteFailed");
    }

    [HttpDelete]
    public async Task<IActionResult> EmptyBin(CancellationToken ct)
    {
        var (deleted, failed) = await bin.EmptyAsync(ct);
        return Ok(new { deleted, failed });
    }

    /// <summary>What the delete dialogs quote, without listing the whole bin.</summary>
    [HttpGet("retention")]
    public async Task<IActionResult> GetRetention(CancellationToken ct) => Ok(new { days = await bin.RetentionDaysAsync(ct) });

    [Authorize(Policy = Policies.Admin)]
    [HttpPut("retention")]
    public async Task<IActionResult> SetRetention([FromBody] RetentionRequest request, CancellationToken ct)
    {
        if (request.Days is < 0 or > 365)
        {
            return this.Fail(localizer, "error.recycleBin.retentionRange", new { min = 0, max = 365 });
        }

        await settings.SetAsync(SettingKeys.RecycleBinRetentionDays, request.Days.ToString(CultureInfo.InvariantCulture), ct);
        return NoContent();
    }

    private static string ReasonName(RecycleReason reason) => reason switch
    {
        RecycleReason.RemoveChapter => "removeChapter",
        RecycleReason.SeriesDelete => "seriesDelete",
        _ => "deleteFile"
    };
}
