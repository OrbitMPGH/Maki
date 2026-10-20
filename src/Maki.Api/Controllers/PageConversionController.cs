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

namespace Maki.Api.Controllers;

/// <summary>Page conversion: the format setting, the library pass and what it has saved so far.</summary>
[ApiController]
[Route("api/v1/pageconversion")]
[Authorize(Policy = Policies.Admin)]
public class PageConversionController(
    MakiDbContext db, IAppSettings settings, PageConversionWorker worker, ILocalizer localizer) : ControllerBase
{
    /// <param name="Format">"off", "webp" or "avif".</param>
    public record SettingsDto(string Format, int WebpQuality, int AvifQuality);

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
    /// Changing the format puts the files the old one left as they were back in line for the next
    /// pass: a JPEG that WebP could not shrink often shrinks as AVIF. Converted files stay converted,
    /// since converting a converted page again only loses detail.
    /// </summary>
    [HttpPut("settings")]
    public async Task<IActionResult> Save([FromBody] SettingsDto request, CancellationToken ct)
    {
        PageConversionFormat? format = null;
        if (!string.Equals(request.Format, "off", StringComparison.OrdinalIgnoreCase))
        {
            format = PageConversionSettings.Parse(request.Format);
            if (format is null)
            {
                return this.Fail(localizer, "error.pageConversion.formatInvalid");
            }
        }

        if (request.WebpQuality is < 1 or > 100 || request.AvifQuality is < 1 or > 100)
        {
            return this.Fail(localizer, "error.pageConversion.qualityRange", new { min = 1, max = 100 });
        }

        var before = await PageConversionOptions.ReadAsync(db, ct);
        await settings.SetAsync(SettingKeys.PageConversionFormat,
            format is { } f ? PageConversionSettings.Name(f) : null, ct);
        await settings.SetAsync(SettingKeys.PageConversionWebpQuality,
            request.WebpQuality.ToString(CultureInfo.InvariantCulture), ct);
        await settings.SetAsync(SettingKeys.PageConversionAvifQuality,
            request.AvifQuality.ToString(CultureInfo.InvariantCulture), ct);

        var after = await PageConversionOptions.ReadAsync(db, ct);
        if (after.Active is { } active && active != before.Active)
        {
            await db.ChapterFiles.IgnoreQueryFilters()
                .Where(c => c.ConversionCheckedAtUtc != null && c.OriginalImageFormat == null)
                .ExecuteUpdateAsync(set => set.SetProperty(c => c.ConversionCheckedAtUtc, (DateTime?)null), ct);
        }

        return await Status(ct);
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start(CancellationToken ct)
    {
        if (await PageConversionOptions.LoadAsync(db, ct) is null)
        {
            return this.Conflict(localizer, "error.pageConversion.off");
        }

        await worker.StartAsync(ct);
        return await Status(ct);
    }

    [HttpPost("pause")]
    public async Task<IActionResult> Pause(CancellationToken ct)
    {
        await worker.PauseAsync(ct);
        return await Status(ct);
    }

    private static SettingsDto ToDto(PageConversionOptions.Stored stored) => new(
        stored.Format is { } f ? PageConversionSettings.Name(f) : "off", stored.WebpQuality, stored.AvifQuality);

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
