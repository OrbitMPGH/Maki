using System.Diagnostics;
using System.Globalization;
using Maki.Api.Auth;
using Maki.Api.Localization;
using Maki.Api.Services.Upscaling;
using Maki.Core.Configuration;
using Maki.Core.Images.Upscaling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

/// <summary>Server page upscaling: the tier switch, the models, the cache and a speed test.</summary>
[ApiController]
[Route("api/v1/upscaling")]
[Authorize(Policy = Policies.Admin)]
public class UpscalingController(
    IAppSettings settings,
    UpscaleModelCatalog catalog,
    UpscaleModelStore store,
    OnnxPageUpscaler upscaler,
    UpscaleCache cache,
    UpscaleQueue queue,
    ILocalizer localizer,
    UpscalePrepareService? prepare = null) : ControllerBase
{
    /// <param name="MaxThreads">The highest thread count the server accepts (its processor count).</param>
    /// <param name="JpegXlAvailable">Whether libjxl loaded, without which <paramref name="JpegXl"/> changes nothing.</param>
    public record SettingsDto(
        bool Enabled, IReadOnlyList<string> Models, int Threads, int CacheMb, int MaxThreads, bool JpegXl, bool JpegXlAvailable,
        PrepareDto Prepare);

    /// <summary>Prepare chapters ahead. See <see cref="PrepareOptions"/>.</summary>
    public record PrepareDto(bool Enabled, int Days, int Series, int Chapters, int Mb);

    /// <param name="Chapters">Chapters the last plan held.</param>
    /// <param name="Pages">Their pages, made or not.</param>
    /// <param name="Waiting">Pages of them still to make.</param>
    /// <param name="Bytes">What their made pages take up.</param>
    public record PreparedDto(int Chapters, int Pages, int Waiting, long Bytes);

    /// <param name="Status">"missing", "downloading", "ready" or "failed".</param>
    /// <param name="Error">Why the download failed, in the caller's language. Null unless failed.</param>
    /// <param name="Bundled">Ships with Maki: never downloaded, cannot be deleted.</param>
    public record CatalogEntryDto(
        string Id, string Name, long? Bytes, string License, string Author, string Source, string Status, string? Error,
        bool Bundled);

    public record CacheDto(long Bytes, int Files);

    public record QueueDto(int Pending, int Working);

    public record StatusDto(
        SettingsDto Settings, IReadOnlyList<CatalogEntryDto> Catalog, CacheDto Cache, QueueDto Queue, PreparedDto Prepared);

    /// <param name="JpegXl">Null leaves the stored value alone, for a client that predates the setting.</param>
    /// <param name="Prepare">Null leaves the stored values alone, for a client that predates them.</param>
    public record SaveSettingsRequest(
        bool Enabled, IReadOnlyList<string>? Models, int Threads, int CacheMb, bool? JpegXl = null, PrepareDto? Prepare = null);

    public record BenchmarkRequest(string? Model);

    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken ct) => Ok(await StatusAsync(ct));

    [HttpPut("settings")]
    public async Task<IActionResult> Save([FromBody] SaveSettingsRequest request, CancellationToken ct)
    {
        if (request.Threads < 1 || request.Threads > Math.Max(Environment.ProcessorCount, 1))
        {
            return this.Fail(localizer, "error.upscaling.invalidThreads", new { min = 1, max = Math.Max(Environment.ProcessorCount, 1) });
        }

        if (request.CacheMb is < UpscaleOptions.MinCacheMb or > UpscaleOptions.MaxCacheMb)
        {
            return this.Fail(localizer, "error.upscaling.invalidCache", new { min = UpscaleOptions.MinCacheMb, max = UpscaleOptions.MaxCacheMb });
        }

        if (request.Prepare is { } ahead && (ahead.Days is < 1 or > PrepareOptions.MaxDays
                || ahead.Series is < 1 or > PrepareOptions.MaxSeries
                || ahead.Chapters is < 1 or > PrepareOptions.MaxChapters
                || ahead.Mb is < PrepareOptions.MinMb or > PrepareOptions.MaxMb))
        {
            return this.Fail(localizer, "error.upscaling.invalidPrepare");
        }

        var models = new List<UpscaleModelId>();
        foreach (var raw in request.Models ?? [])
        {
            if (!UpscaleModelId.TryParse(raw, out var id))
            {
                return this.Fail(localizer, "error.upscaling.unknownModel");
            }

            if (!models.Contains(id))
            {
                models.Add(id);
            }
        }

        var firstSave = await settings.GetAsync(SettingKeys.UpscalingModels, ct) is null;
        var before = await UpscaleOptions.LoadAsync(settings, ct);
        await settings.SetAsync(SettingKeys.UpscalingEnabled, request.Enabled ? "true" : "false", ct);
        await settings.SetAsync(SettingKeys.UpscalingModels, string.Join(',', models.Select(m => m.Value)), ct);
        await settings.SetAsync(SettingKeys.UpscalingThreads, request.Threads.ToString(CultureInfo.InvariantCulture), ct);
        await settings.SetAsync(SettingKeys.UpscalingCacheMb, request.CacheMb.ToString(CultureInfo.InvariantCulture), ct);
        if (request.JpegXl is { } jpegXl)
        {
            await settings.SetAsync(SettingKeys.UpscalingJpegXl, jpegXl ? "true" : "false", ct);
        }

        if (request.Prepare is { } p)
        {
            await settings.SetAsync(SettingKeys.UpscalingPrepare, p.Enabled ? "true" : "false", ct);
            await settings.SetAsync(SettingKeys.UpscalingPrepareDays, p.Days.ToString(CultureInfo.InvariantCulture), ct);
            await settings.SetAsync(SettingKeys.UpscalingPrepareSeries, p.Series.ToString(CultureInfo.InvariantCulture), ct);
            await settings.SetAsync(SettingKeys.UpscalingPrepareChapters, p.Chapters.ToString(CultureInfo.InvariantCulture), ct);
            await settings.SetAsync(SettingKeys.UpscalingPrepareMb, p.Mb.ToString(CultureInfo.InvariantCulture), ct);
        }

        foreach (var id in models.Where(m => firstSave || !before.Models.Contains(m)))
        {
            if (store.GetStatus(id).State is UpscaleModelState.Missing or UpscaleModelState.Failed)
            {
                StartDownload(id);
            }
        }

        queue.Wake();
        prepare?.Nudge();
        if (request.CacheMb < before.CacheMb)
        {
            await cache.EnforceCapAsync(ct);
        }

        return Ok(await StatusAsync(ct));
    }

    [HttpPost("models/{id}/download")]
    public async Task<IActionResult> Download(string id, CancellationToken ct)
    {
        if (!UpscaleModelId.TryParse(id, out var model))
        {
            return this.NotFoundMessage(localizer, "error.upscaling.unknownModel");
        }

        StartDownload(model);
        return Ok(await StatusAsync(ct));
    }

    /// <summary>Unloads the model, deletes its file and every page it made.</summary>
    [HttpDelete("models/{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (!UpscaleModelId.TryParse(id, out var model))
        {
            return this.NotFoundMessage(localizer, "error.upscaling.unknownModel");
        }

        if (store.IsBundled(model))
        {
            return this.Conflict(localizer, "error.upscaling.modelBundled");
        }

        upscaler.ReleaseModel(model);
        try
        {
            await store.DeleteAsync(model);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return this.Conflict(localizer, "error.upscaling.modelInUse");
        }

        queue.ForgetModel(model.Value);
        await cache.RemoveModelAsync(model.Value, ct);
        return Ok(await StatusAsync(ct));
    }

    [HttpPost("cache/clear")]
    public async Task<IActionResult> ClearCache(CancellationToken ct)
    {
        await cache.ClearAsync(ct);
        return Ok(await StatusAsync(ct));
    }

    /// <summary>Times one built-in sample page through a downloaded model. Advice for the admin, never a gate.</summary>
    [HttpPost("benchmark")]
    public async Task<IActionResult> Benchmark([FromBody] BenchmarkRequest request, CancellationToken ct)
    {
        if (!UpscaleModelId.TryParse(request.Model, out var model) || !store.IsReady(model))
        {
            return this.Conflict(localizer, "error.enhance.modelUnavailable");
        }

        var page = UpscaleSamplePage.Create();
        var watch = Stopwatch.StartNew();
        var result = await upscaler.UpscaleAsync(page, model, ct);
        watch.Stop();
        if (result.Outcome != UpscaleOutcome.Ready)
        {
            return this.Conflict(localizer, "error.upscaling.benchmarkFailed");
        }

        return Ok(new { seconds = Math.Round(watch.Elapsed.TotalSeconds, 2) });
    }

    private void StartDownload(UpscaleModelId id)
    {
        queue.ForgetModel(id.Value);
        _ = store.DownloadAsync(id, CancellationToken.None);
    }

    private async Task<StatusDto> StatusAsync(CancellationToken ct)
    {
        var options = await UpscaleOptions.LoadAsync(settings, ct);
        var entries = catalog.All.Select(info =>
        {
            var status = store.GetStatus(info.Id);
            return new CatalogEntryDto(
                info.Id.Value,
                info.DisplayName,
                status.Bytes,
                info.License,
                info.Author,
                info.SourceUrl,
                status.State switch
                {
                    UpscaleModelState.Downloading => "downloading",
                    UpscaleModelState.Ready => "ready",
                    UpscaleModelState.Failed => "failed",
                    _ => "missing"
                },
                status is { State: UpscaleModelState.Failed, ErrorKey: { } key } ? localizer.Get(key) : null,
                store.IsBundled(info.Id));
        }).ToList();

        var (bytes, files) = cache.Stats();
        var (heldBytes, _, _) = cache.HeldStats();
        var snapshot = queue.Snapshot();
        var ahead = options.PrepareAhead;
        var last = prepare?.Last ?? PrepareSnapshot.Empty;
        return new StatusDto(
            new SettingsDto(options.Enabled, options.Models.Select(m => m.Value).ToList(), options.Threads, options.CacheMb,
                Math.Max(Environment.ProcessorCount, 1), options.JpegXl, Maki.Core.Images.JpegXl.IsAvailable,
                new PrepareDto(ahead.Enabled, ahead.Days, ahead.Series, ahead.Chapters, ahead.Mb)),
            entries,
            new CacheDto(bytes, files),
            new QueueDto(snapshot.Pending, snapshot.Working),
            new PreparedDto(last.Chapters, last.Pages, snapshot.Background, heldBytes));
    }
}
