using Maki.Api.Auth;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// The caller's own import lists: per-tracker settings, a manual run, and the entries a run could
/// not match or the user ignored. The instance switch and interval are admin settings under
/// <c>settings/importlists</c>.
/// </summary>
[ApiController]
[Route("api/v1/importlists")]
[Authorize(Policy = Policies.UseTrackers)]
public class ImportListsController(
    ILocalizer localizer,
    ImportListService importLists,
    IUserSettings userSettings,
    MakiDbContext db,
    ICurrentUser currentUser) : ControllerBase
{
    public const int SkippedLimit = 200;

    public record TrackerPrefsDto(
        bool Enabled, IReadOnlyList<string> Statuses, int? RootFolderId, bool Monitored,
        string MonitorNewItems, int MaxPerRun);

    public record LastRunDto(DateTime At, int Added, int Requested, int Skipped, int Errors, bool DumpUnavailable);

    public record TrackerDto(string Service, string Label, bool Connected, TrackerPrefsDto Prefs, LastRunDto? LastRun);

    public record SkippedDto(int Id, string Service, string RemoteId, string Title, string Reason, DateTime CreatedAt);

    public record ImportListsDto(bool Enabled, int IntervalMinutes, List<TrackerDto> Trackers, List<SkippedDto> Skipped);

    public record PrefsRequest(
        string? Service, bool Enabled, string[]? Statuses, int? RootFolderId, bool Monitored = true,
        string? MonitorNewItems = null, int MaxPerRun = ImportListTrackerPrefs.DefaultMaxPerRun);

    public record RunRequest(string? Service, bool Full = false);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var userId = currentUser.UserId;
        var prefs = ImportListPrefs.Parse(await userSettings.GetAsync(SettingKeys.ImportListPrefs, ct));
        var lastRuns = await userSettings.GetManyAsync(
            [.. importLists.Trackers.Select(t => SettingKeys.ImportListLastRunKey(t.Name))], ct);

        var trackers = new List<TrackerDto>();
        foreach (var tracker in importLists.Trackers)
        {
            var p = ImportListPrefs.For(prefs, tracker.Name);
            var last = ImportListLastRun.Parse(lastRuns.GetValueOrDefault(SettingKeys.ImportListLastRunKey(tracker.Name)));
            trackers.Add(new TrackerDto(
                tracker.Name,
                tracker.Label,
                await tracker.ConfiguredAsync(ct) && await tracker.AuthenticatedAsync(userId, ct),
                new TrackerPrefsDto(p.Enabled, p.Statuses ?? ImportListTrackerPrefs.DefaultStatuses, p.RootFolderId,
                    p.Monitored, p.MonitorNewItems, p.MaxPerRun),
                last is null
                    ? null
                    : new LastRunDto(last.At, last.Added, last.Requested, last.Skipped, last.Errors, last.DumpUnavailable)));
        }

        var skipped = await db.ImportListSkips.AsNoTracking()
            .Where(x => x.UserId == userId &&
                        (x.Reason == ImportListSkipReason.Unmatched || x.Reason == ImportListSkipReason.Ignored))
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Take(SkippedLimit)
            .ToListAsync(ct);

        return Ok(new ImportListsDto(
            await importLists.EnabledAsync(ct),
            await importLists.IntervalMinutesAsync(ct),
            trackers,
            [.. skipped.Select(x => new SkippedDto(x.Id, x.Service, x.RemoteId, x.Title, x.Reason.ToString(), x.CreatedAt))]));
    }

    [HttpPut("prefs")]
    public async Task<IActionResult> SetPrefs([FromBody] PrefsRequest request, CancellationToken ct)
    {
        if (request.Service is not { } service || importLists.FindTracker(service) is null)
        {
            return this.Fail(localizer, "error.importLists.unknownService");
        }

        var statuses = request.Statuses ?? [];
        if (statuses.Length == 0 || statuses.Any(s => !ImportListTrackerPrefs.AllowedStatuses.Contains(s, StringComparer.OrdinalIgnoreCase)))
        {
            return this.Fail(localizer, "error.importLists.statusesInvalid");
        }

        if (request.MaxPerRun is < 1 or > ImportListTrackerPrefs.MaxPerRunLimit)
        {
            return this.Fail(localizer, "error.importLists.maxPerRunInvalid", new { max = ImportListTrackerPrefs.MaxPerRunLimit });
        }

        var monitor = request.MonitorNewItems ?? ImportListTrackerPrefs.DefaultMonitorNewItems;
        if (!Enum.TryParse<NewChapterMonitorMode>(monitor, true, out var mode) || !Enum.IsDefined(mode))
        {
            return this.Fail(localizer, "error.importLists.monitorModeInvalid");
        }

        if (request.RootFolderId is { } rootFolderId &&
            await ImportListService.RootFolderForAsync(db, currentUser.UserId, currentUser.AllRootFolders, rootFolderId, ct)
                != rootFolderId)
        {
            return this.Fail(localizer, "error.importLists.rootFolderNotVisible");
        }

        var all = ImportListPrefs.Parse(await userSettings.GetAsync(SettingKeys.ImportListPrefs, ct));
        all[service] = new ImportListTrackerPrefs(
            request.Enabled, statuses, request.RootFolderId, request.Monitored, mode.ToString(), request.MaxPerRun);
        await userSettings.SetAsync(SettingKeys.ImportListPrefs, ImportListPrefs.Serialize(all), ct);
        return NoContent();
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run([FromBody] RunRequest request, CancellationToken ct)
    {
        if (request.Service is { } service && importLists.FindTracker(service) is null)
        {
            return this.Fail(localizer, "error.importLists.unknownService");
        }

        if (!await importLists.EnabledAsync(ct))
        {
            return this.Conflict(localizer, "error.importLists.disabled");
        }

        if (request.Full)
        {
            return importLists.StartFullRun(currentUser.UserId, request.Service) is null
                ? this.Conflict(localizer, "error.importLists.running")
                : Accepted(new { started = true });
        }

        var result = await importLists.RunUserAsync(currentUser.UserId, request.Service, full: false, ct);
        return result is null ? this.Conflict(localizer, "error.importLists.running") : Ok(result);
    }

    /// <summary>Forgets an unmatched or ignored entry so the next run tries it again.</summary>
    [HttpDelete("skipped/{id:int}")]
    public async Task<IActionResult> DeleteSkipped(int id, CancellationToken ct)
    {
        var row = await OwnRowAsync(id, [ImportListSkipReason.Unmatched, ImportListSkipReason.Ignored], ct);
        if (row is null)
        {
            return NotFound();
        }

        db.ImportListSkips.Remove(row);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Keeps an unmatched entry out of every future run.</summary>
    [HttpPost("skipped/{id:int}/ignore")]
    public async Task<IActionResult> IgnoreSkipped(int id, CancellationToken ct)
    {
        var row = await OwnRowAsync(id, [ImportListSkipReason.Unmatched], ct);
        if (row is null)
        {
            return NotFound();
        }

        row.Reason = ImportListSkipReason.Ignored;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private Task<ImportListSkip?> OwnRowAsync(int id, ImportListSkipReason[] reasons, CancellationToken ct) =>
        db.ImportListSkips.FirstOrDefaultAsync(
            x => x.Id == id && x.UserId == currentUser.UserId && reasons.Contains(x.Reason), ct);
}
