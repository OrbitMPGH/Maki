using System.Globalization;
using Maki.Api.Auth;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Quality;
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
    PageConversionPlanService plans, ILocalizer localizer) : ControllerBase
{
    public const int MaxRules = 50;
    private const int MaxRuleName = 200;

    private static readonly HashSet<string> FormatNames =
        new(["jpg", "png", "webp", "avif", "gif", "bmp", "mixed", "unknown"], StringComparer.OrdinalIgnoreCase);

    /// <param name="Format">"webp", "avif", or null while conversion is off.</param>
    /// <param name="Stop">"smallest", "compact", "balanced", "high" or "lossless"; sent while off too.</param>
    public record SettingsDto(string? Format, string Stop, bool PngLossless);

    public record SamplesDto(IReadOnlyList<PageConversionSample> Samples);

    /// <param name="ConvertedFiles">Library files whose pages Maki converted.</param>
    /// <param name="PendingFiles">Archives conversion has not looked at yet.</param>
    /// <param name="TrashBytes">Originals still waiting in the upgrade trash.</param>
    /// <param name="OverrideSeries">Series whose own page conversion choice is not Default.</param>
    public record LibraryDto(int ConvertedFiles, long SavedBytes, int PendingFiles, long TrashBytes, int TrashFiles, int TrashDays,
        int RuleCount, int OverrideSeries);

    /// <param name="Action">"skip" or "convert".</param>
    /// <param name="Stop">The stop a matching file is converted at, lowercase; required with "convert".</param>
    public record RuleDto(int? Id, string? Name, PageConversionConditions? Conditions, string Action, string? Stop);

    public record RulesDto(IReadOnlyList<RuleDto>? Rules);

    /// <summary>The settings and the rules, saved together.</summary>
    public record PlanDto(SettingsDto? Settings, IReadOnlyList<RuleDto>? Rules);

    /// <summary>A plan the wizard has not saved yet, to count against the library.</summary>
    public record ScopeRequest(string? Format, string Stop, bool PngLossless, IReadOnlyList<RuleDto>? Rules);

    /// <param name="State">"idle", "running" or "paused".</param>
    /// <param name="Hold">Why a pass paused itself: "lowDisk", "conversionOff" or "settingsChanged"; null otherwise.</param>
    public record PassDto(string State, string? Hold, string? CurrentFile, PageConversionPassTotals? Totals);

    /// <param name="Limit">Pages converted at once, or null for <paramref name="Default"/>.</param>
    /// <param name="Max">The core count, the highest limit accepted.</param>
    public record ParallelDto(int? Limit, int Default, int Max);

    public record ParallelRequest(int? Limit);

    public record StatusDto(SettingsDto Settings, PassDto Pass, LibraryDto Library, ParallelDto Parallel);

    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var stored = await PageConversionOptions.ReadAsync(db, ct);
        var snapshot = await worker.SnapshotAsync(ct);
        var converted = db.ChapterFiles.IgnoreQueryFilters().Where(f => f.OriginalImageFormat != null);
        var convertedFiles = await converted.CountAsync(ct);
        var savedBytes = await converted.SumAsync(f => f.ConversionSavedBytes, ct);
        var pending = await PageConversionWorker.PendingQuery(db).CountAsync(ct);
        var trashed = db.PageConversionHistory.IgnoreQueryFilters().Where(h => h.TrashPath != null);
        var trashFiles = await trashed.CountAsync(ct);
        var trashBytes = await trashed.SumAsync(h => h.TrashBytes ?? 0, ct);
        var options = await UpgradeOptions.LoadAsync(settings, ct);
        var ruleCount = (await PageConversionOptions.LoadRulesAsync(db, ct)).Count;
        var overrideSeries = await db.Series.IgnoreQueryFilters()
            .CountAsync(s => s.PageConversion != PageConversionOverride.Default, ct);

        return Ok(new StatusDto(
            ToDto(stored),
            new PassDto(Name(snapshot.State), snapshot.Hold switch
            {
                PageConversionHold.LowDisk => "lowDisk",
                PageConversionHold.ConversionOff => "conversionOff",
                PageConversionHold.SettingsChanged => "settingsChanged",
                _ => null
            }, snapshot.CurrentFile, snapshot.Pass),
            new LibraryDto(convertedFiles, savedBytes, pending, trashBytes, trashFiles,
                options.TrashRetentionDays, ruleCount, overrideSeries),
            new ParallelDto(PageConversionOptions.ParseParallel(await settings.GetAsync(SettingKeys.PageConversionParallel, ct)),
                ImageWorkGate.Parallelism, ImageWorkGate.MaxConversionLimit)));
    }

    /// <summary>Pages converted at once. Takes effect on the next page, so a running pass needs no restart.</summary>
    [HttpPut("parallel")]
    public async Task<IActionResult> SaveParallel([FromBody] ParallelRequest request, CancellationToken ct)
    {
        if (request.Limit is < 1 || request.Limit > ImageWorkGate.MaxConversionLimit)
        {
            return this.Fail(localizer, "error.pageConversion.parallelInvalid",
                new { max = ImageWorkGate.MaxConversionLimit });
        }

        await settings.SetAsync(SettingKeys.PageConversionParallel,
            request.Limit?.ToString(CultureInfo.InvariantCulture), ct);
        ImageWorkGate.SetConversionLimit(request.Limit);
        return await Status(ct);
    }

    /// <summary>
    /// Changing the settings puts the files the old ones left as they were back in line for the next
    /// pass, where the plan now answers differently for them: a JPEG that WebP could not shrink often
    /// shrinks as AVIF. Converted files stay converted, since converting a converted page again only
    /// loses detail. A running pass is paused first, since it decides by the plan it started with.
    /// </summary>
    [HttpPut("settings")]
    public async Task<IActionResult> Save([FromBody] SettingsDto request, CancellationToken ct)
    {
        if (ParseSettings(request) is not { } stored)
        {
            return InvalidSettings(request);
        }

        var before = await PageConversionOptions.ReadAsync(db, ct);
        var changed = before.Active != stored.Active;
        if (changed)
        {
            await worker.PauseForSettingsAsync(HoldFor(stored), ct);
        }

        foreach (var (key, value) in SettingValues(stored))
        {
            await settings.SetAsync(key, value, ct);
        }

        if (changed)
        {
            await plans.RecheckAsync(await PageConversionOptions.LoadPlanAsync(db, ct), null, ct);
        }

        return await Status(ct);
    }

    [HttpGet("rules")]
    public async Task<IActionResult> Rules(CancellationToken ct) => Ok(await RulesAsync(ct));

    /// <summary>
    /// Replaces the whole list; a rule's order is its place in the array. When the rules decide
    /// differently from before, a running pass is paused and checked files are put back in line
    /// the same way a settings change does.
    /// </summary>
    [HttpPut("rules")]
    public async Task<IActionResult> SaveRules([FromBody] RulesDto request, CancellationToken ct)
    {
        if (ParseRules(request.Rules) is not { } parsed)
        {
            return this.Fail(localizer, "error.pageConversion.ruleInvalid");
        }

        var changed = await RulesChangedAsync(parsed, ct);
        if (changed)
        {
            await worker.PauseForSettingsAsync(HoldFor(await PageConversionOptions.ReadAsync(db, ct)), ct);
        }

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await StageRulesAsync(parsed, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        if (changed)
        {
            await plans.RecheckAsync(await PageConversionOptions.LoadPlanAsync(db, ct), null, ct);
        }

        return Ok(await RulesAsync(ct));
    }

    /// <summary>
    /// Saves the settings and the rules in one transaction, for the wizard's Start. A running pass is
    /// paused once when the plan changes, and one recheck runs against the plan as saved.
    /// </summary>
    [HttpPut("plan")]
    public async Task<IActionResult> SavePlan([FromBody] PlanDto request, CancellationToken ct)
    {
        if (request.Settings is null || ParseSettings(request.Settings) is not { } stored)
        {
            return InvalidSettings(request.Settings);
        }

        if (ParseRules(request.Rules) is not { } parsed)
        {
            return this.Fail(localizer, "error.pageConversion.ruleInvalid");
        }

        var before = await PageConversionOptions.ReadAsync(db, ct);
        var changed = before.Active != stored.Active || await RulesChangedAsync(parsed, ct);
        if (changed)
        {
            await worker.PauseForSettingsAsync(HoldFor(stored), ct);
        }

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var values = SettingValues(stored).ToList();
            var keys = values.Select(v => v.Key).ToList();
            var rows = await db.AppConfig.Where(c => keys.Contains(c.Key)).ToDictionaryAsync(c => c.Key, ct);
            foreach (var (key, value) in values)
            {
                var row = rows.GetValueOrDefault(key);
                if (value is null)
                {
                    if (row is not null)
                    {
                        db.AppConfig.Remove(row);
                    }
                }
                else if (row is null)
                {
                    db.AppConfig.Add(new AppConfigEntry { Key = key, Value = value });
                }
                else
                {
                    row.Value = value;
                }
            }

            await StageRulesAsync(parsed, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        // Written behind the settings service, so its cache has to forget them.
        (settings as SettingsService)?.Invalidate();
        if (changed)
        {
            await plans.RecheckAsync(await PageConversionOptions.LoadPlanAsync(db, ct), null, ct);
        }

        return await Status(ct);
    }

    private static PageConversionOptions.Stored? ParseSettings(SettingsDto request)
    {
        PageConversionFormat? format = null;
        if (request.Format is not null && !string.Equals(request.Format, "off", StringComparison.OrdinalIgnoreCase))
        {
            format = PageConversionSettings.ParseFormat(request.Format);
            if (format is null)
            {
                return null;
            }
        }

        return PageConversionSettings.ParseStop(request.Stop) is { } stop
            ? new PageConversionOptions.Stored(format, stop, request.PngLossless)
            : null;
    }

    private IActionResult InvalidSettings(SettingsDto? request) =>
        request?.Format is not null && !string.Equals(request.Format, "off", StringComparison.OrdinalIgnoreCase) &&
        PageConversionSettings.ParseFormat(request.Format) is null
            ? this.Fail(localizer, "error.pageConversion.formatInvalid")
            : this.Fail(localizer, "error.pageConversion.stopInvalid");

    private static IEnumerable<(string Key, string? Value)> SettingValues(PageConversionOptions.Stored stored) =>
    [
        (SettingKeys.PageConversionFormat, stored.Format is { } f ? PageConversionSettings.FormatName(f) : null),
        (SettingKeys.PageConversionStop, PageConversionSettings.StopName(stored.Stop)),
        (SettingKeys.PageConversionPngLossless, stored.PngLossless ? "true" : null)
    ];

    private static PageConversionHold HoldFor(PageConversionOptions.Stored stored) =>
        stored.Active is null ? PageConversionHold.ConversionOff : PageConversionHold.SettingsChanged;

    private async Task<bool> RulesChangedAsync(IReadOnlyList<PlanRule> parsed, CancellationToken ct) =>
        !(await PageConversionOptions.LoadRulesAsync(db, ct)).Select(Decides).SequenceEqual(parsed.Select(Decides));

    private async Task StageRulesAsync(IReadOnlyList<PlanRule> parsed, CancellationToken ct)
    {
        db.PageConversionRules.RemoveRange(await db.PageConversionRules.ToListAsync(ct));
        db.PageConversionRules.AddRange(parsed.Select(r => new PageConversionRule
        {
            Order = r.Order,
            Name = r.Name,
            ConditionsJson = r.Conditions.ToJson(),
            Skip = r.Skip,
            Stop = r.Stop
        }));
    }

    /// <summary>Where the library's files would land under a plan the wizard has not saved, for its live counts.</summary>
    [HttpPost("scope")]
    public async Task<IActionResult> Scope([FromBody] ScopeRequest request, CancellationToken ct)
    {
        var format = PageConversionFormat.Webp;
        if (request.Format is not null && !string.Equals(request.Format, "off", StringComparison.OrdinalIgnoreCase))
        {
            if (PageConversionSettings.ParseFormat(request.Format) is not { } parsedFormat)
            {
                return this.Fail(localizer, "error.pageConversion.formatInvalid");
            }

            format = parsedFormat;
        }

        if (PageConversionSettings.ParseStop(request.Stop) is not { } stop)
        {
            return this.Fail(localizer, "error.pageConversion.stopInvalid");
        }

        if (ParseRules(request.Rules) is not { } rules)
        {
            return this.Fail(localizer, "error.pageConversion.ruleInvalid");
        }

        return Ok(await plans.ScopeAsync(new PageConversionPlan(format, stop, request.PngLossless, rules), ct));
    }

    /// <summary>
    /// Starts measuring what a plan the wizard has not saved would do, cancelling an estimate still
    /// running for another plan. Nothing is saved; the wizard polls <c>GET estimate</c>.
    /// </summary>
    [HttpPost("estimate")]
    public IActionResult StartEstimate([FromBody] PlanDto request, [FromServices] PageConversionEstimateService estimates)
    {
        if (request.Settings is null || ParseSettings(request.Settings) is not { } stored)
        {
            return InvalidSettings(request.Settings);
        }

        if (ParseRules(request.Rules) is not { } parsed)
        {
            return this.Fail(localizer, "error.pageConversion.ruleInvalid");
        }

        return Ok(estimates.Start(
            new PageConversionPlan(stored.Format ?? PageConversionFormat.Webp, stored.Stop, stored.PngLossless, parsed)));
    }

    [HttpGet("estimate")]
    public IActionResult Estimate([FromServices] PageConversionEstimateService estimates) => Ok(estimates.Current);

    private async Task<RulesDto> RulesAsync(CancellationToken ct)
    {
        var rows = await db.PageConversionRules.AsNoTracking().OrderBy(r => r.Order).ThenBy(r => r.Id).ToListAsync(ct);
        return new RulesDto(rows.Select(r => new RuleDto(r.Id, r.Name,
                PageConversionConditions.TryParse(r.ConditionsJson, out var conditions) ? conditions : new PageConversionConditions(),
                r.Skip ? "skip" : "convert", r.Skip || r.Stop is not { } stop ? null : PageConversionSettings.StopName(stop)))
            .ToList());
    }

    /// <summary>What makes a rule decide a file, leaving out its name.</summary>
    private static (int, string, bool, PageConversionStop?) Decides(PlanRule rule) =>
        (rule.Order, rule.Conditions.ToJson(), rule.Skip, rule.Skip ? null : rule.Stop);

    /// <summary>Null when any rule is invalid: an unknown action, tier or format, a convert without a stop, a bound that is not positive, or too many rules.</summary>
    private static List<PlanRule>? ParseRules(IReadOnlyList<RuleDto>? rules)
    {
        rules ??= [];
        if (rules.Count > MaxRules)
        {
            return null;
        }

        var parsed = new List<PlanRule>(rules.Count);
        foreach (var rule in rules)
        {
            if (rule is null)
            {
                return null;
            }

            var skip = string.Equals(rule.Action, "skip", StringComparison.OrdinalIgnoreCase);
            if (!skip && !string.Equals(rule.Action, "convert", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var stop = PageConversionSettings.ParseStop(rule.Stop);
            if (!skip && stop is null)
            {
                return null;
            }

            var name = rule.Name?.Trim() ?? "";
            var conditions = (rule.Conditions ?? new PageConversionConditions()).Normalised();
            if (name.Length > MaxRuleName ||
                conditions.Tiers?.Any(t => !IsTier(t)) == true ||
                conditions.Formats?.Any(f => !FormatNames.Contains(f)) == true ||
                !Positive(conditions.BppUnder) || !Positive(conditions.BppOver) ||
                conditions.SizeOverBytes <= 0 || conditions.SizeUnderBytes <= 0)
            {
                return null;
            }

            parsed.Add(new PlanRule(parsed.Count, name, conditions, skip, skip ? null : stop));
        }

        return parsed;
    }

    private static bool IsTier(string name) =>
        Enum.GetNames<QualityTier>().Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));

    private static bool Positive(double? value) => value is null || (double.IsFinite(value.Value) && value > 0);

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
}
