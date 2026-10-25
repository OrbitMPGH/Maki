using Maki.Api.Configuration;
using Maki.Api.Hubs;
using Maki.Core.Configuration;
using Maki.Core.Images.Upscaling;

namespace Maki.Api.Services.Upscaling;

/// <summary>
/// The one consumer of <see cref="UpscaleQueue"/>. Idles while the server tier is off, and drops
/// whatever was pending when it finds it off. A page that fails is reported as failed and the loop
/// carries on. A page for a model the admin turned off or deleted fails without being cached.
/// </summary>
public sealed class UpscaleWorker(
    UpscaleQueue queue,
    UpscaleCache cache,
    IPageUpscaler upscaler,
    UpscaleModelStore store,
    ReaderArchiveCache archives,
    AppPaths paths,
    IAppSettings settings,
    EventBroadcaster broadcaster,
    ILogger<UpscaleWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleWait = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await cache.EnsureSeededAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not index the upscale cache");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunOnceAsync(stoppingToken))
                {
                    await queue.WaitAsync(IdleWait, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Upscale worker loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    /// <summary>Runs the next job. False when there was nothing to run.</summary>
    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var options = await UpscaleOptions.LoadAsync(settings, ct);
        if (!options.Enabled)
        {
            queue.Clear();
            return false;
        }

        var job = queue.TryDequeue();
        if (job is null)
        {
            return false;
        }

        var (outcome, reason) = UpscaleModelId.TryParse(job.Key.Model, out var model) && options.Models.Contains(model)
            ? await RunAsync(job, model, ct)
            : (UpscaleOutcome.Failed, UpscaleReasons.ModelUnavailable);
        var state = outcome switch
        {
            UpscaleOutcome.Ready => "ready",
            UpscaleOutcome.Skipped => "skipped",
            _ => "failed"
        };

        foreach (var (userId, target) in queue.Complete(job, outcome, reason))
        {
            try
            {
                await broadcaster.PageEnhanced(userId, target.ChapterId, target.Page, job.Key.Model, state);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not tell user {UserId} about an upscaled page", userId);
            }
        }

        return true;
    }

    private async Task<(UpscaleOutcome Outcome, string? Reason)> RunAsync(UpscaleJob job, UpscaleModelId model, CancellationToken ct)
    {
        try
        {
            var page = await ReaderPageCache.ReadPageBytesAsync(paths, archives, job.Slice, job.Key.AbsIndex, ct);
            if (page is null)
            {
                return (UpscaleOutcome.Skipped, UpscaleReasons.Unreadable);
            }

            var result = await upscaler.UpscaleAsync(page, job.Key.Model, ct);
            if (result is { Outcome: UpscaleOutcome.Ready, Bytes: { } bytes, Extension: { } extension })
            {
                if (!store.IsReady(model))
                {
                    return (UpscaleOutcome.Failed, UpscaleReasons.ModelUnavailable);
                }

                try
                {
                    await cache.WriteAsync(job.Key, bytes, extension, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogInformation(ex, "Could not cache upscaled page {Page} of chapter file {FileId}",
                        job.Key.AbsIndex, job.Key.ChapterFileId);
                    return (UpscaleOutcome.Failed, UpscaleReasons.CacheWriteFailed);
                }

                return (UpscaleOutcome.Ready, null);
            }

            return (result.Outcome == UpscaleOutcome.Ready ? UpscaleOutcome.Failed : result.Outcome, result.Reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            queue.Complete(job, UpscaleOutcome.Failed, null);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upscaling page {Page} of chapter file {FileId} with {Model} failed",
                job.Key.AbsIndex, job.Key.ChapterFileId, job.Key.Model);
            return (UpscaleOutcome.Failed, UpscaleReasons.InferenceFailed);
        }
    }
}
