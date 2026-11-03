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
    ILocalizer localizer) : ControllerBase
{
    /// <param name="MaxThreads">The highest thread count the server accepts (its processor count).</param>
    public record SettingsDto(bool Enabled, IReadOnlyList<string> Models, int Threads, int CacheMb, int MaxThreads);

    /// <param name="Status">"missing", "downloading", "ready" or "failed".</param>
    /// <param name="Error">Why the download failed, in the caller's language. Null unless failed.</param>
    public record CatalogEntryDto(
        string Id, string Name, long? Bytes, string License, string Author, string Source, string Status, string? Error);

    public record CacheDto(long Bytes, int Files);

    public record QueueDto(int Pending, int Working);

    public record StatusDto(SettingsDto Settings, IReadOnlyList<CatalogEntryDto> Catalog, CacheDto Cache, QueueDto Queue);

    public record SaveSettingsRequest(bool Enabled, IReadOnlyList<string>? Models, int Threads, int CacheMb);

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

        var before = await UpscaleOptions.LoadAsync(settings, ct);
        await settings.SetAsync(SettingKeys.UpscalingEnabled, request.Enabled ? "true" : "false", ct);
        await settings.SetAsync(SettingKeys.UpscalingModels, string.Join(',', models.Select(m => m.Value)), ct);
        await settings.SetAsync(SettingKeys.UpscalingThreads, request.Threads.ToString(CultureInfo.InvariantCulture), ct);
        await settings.SetAsync(SettingKeys.UpscalingCacheMb, request.CacheMb.ToString(CultureInfo.InvariantCulture), ct);

        foreach (var id in models.Where(m => !before.Models.Contains(m)))
        {
            if (store.GetStatus(id).State is UpscaleModelState.Missing or UpscaleModelState.Failed)
            {
                StartDownload(id);
            }
        }

        queue.Wake();
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
                status is { State: UpscaleModelState.Failed, ErrorKey: { } key } ? localizer.Get(key) : null);
        }).ToList();

        var (bytes, files) = cache.Stats();
        var snapshot = queue.Snapshot();
        return new StatusDto(
            new SettingsDto(options.Enabled, options.Models.Select(m => m.Value).ToList(), options.Threads, options.CacheMb,
                Math.Max(Environment.ProcessorCount, 1)),
            entries,
            new CacheDto(bytes, files),
            new QueueDto(snapshot.Pending, snapshot.Working));
    }
}
