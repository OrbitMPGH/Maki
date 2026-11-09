using Maki.Api.Configuration;
using Maki.Api.Hubs;
using Maki.Core.Configuration;
using Maki.Core.Images.Upscaling;
using Maki.Core.Io;

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

        var busy = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RunOnceAsync(stoppingToken))
                {
                    busy = true;
                    continue;
                }

                if (busy)
                {
                    busy = false;
                    ReleaseMemory();
                }

                await queue.WaitAsync(IdleWait, stoppingToken);
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

    /// <summary>
    /// Hands back what a burst of pages left behind once the queue drains: tens to hundreds of MB of
    /// large-object garbage the GC would otherwise keep committed until the next page, and the native
    /// heap ORT and libvips freed.
    /// </summary>
    private static void ReleaseMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        NativeHeap.Trim();
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

        (UpscaleOutcome Outcome, string? Reason) result;
        if (UpscaleModelId.TryParse(job.Key.Model, out var model) && options.Models.Contains(model))
        {
            // An abandoned run of the same page can finish and cache it after this job was queued.
            if (cache.TryGet(job.Key) is not null)
            {
                await TellAsync(queue.Complete(job, UpscaleOutcome.Ready, null), job.Key.Model, "ready");
                return true;
            }

            await TellAsync(queue.WaitersOf(job), job.Key.Model, "working");
            result = await RunAsync(job, model, ct);
        }
        else
        {
            result = (UpscaleOutcome.Failed, UpscaleReasons.ModelUnavailable);
        }

        var state = result.Outcome switch
        {
            UpscaleOutcome.Ready => "ready",
            UpscaleOutcome.Skipped => "skipped",
            _ => "failed"
        };
        await TellAsync(queue.Complete(job, result.Outcome, result.Reason), job.Key.Model, state);

        return true;
    }

    private async Task TellAsync(IEnumerable<KeyValuePair<int, UpscaleTarget>> waiters, string model, string state)
    {
        foreach (var (userId, target) in waiters)
        {
            try
            {
                await broadcaster.PageEnhanced(userId, target.ChapterId, target.Page, model, state);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not tell user {UserId} about an upscaled page", userId);
            }
        }
    }

    private async Task<(UpscaleOutcome Outcome, string? Reason)> RunAsync(UpscaleJob job, UpscaleModelId model, CancellationToken ct)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct, job.Abandoned);
        try
        {
            var page = await ReaderPageCache.ReadPageBytesAsync(paths, archives, job.Slice, job.Key.AbsIndex, run.Token);
            if (page is null)
            {
                return (UpscaleOutcome.Skipped, UpscaleReasons.Unreadable);
            }

            var result = await upscaler.UpscaleAsync(page, job.Key.Model, run.Token);
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
        catch (OperationCanceledException) when (job.Abandoned.IsCancellationRequested)
        {
            logger.LogDebug("Stopped upscaling page {Page} of chapter file {FileId}: its reader moved on",
                job.Key.AbsIndex, job.Key.ChapterFileId);
            return (UpscaleOutcome.Failed, UpscaleReasons.Abandoned);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upscaling page {Page} of chapter file {FileId} with {Model} failed",
                job.Key.AbsIndex, job.Key.ChapterFileId, job.Key.Model);
            return (UpscaleOutcome.Failed, UpscaleReasons.InferenceFailed);
        }
    }
}
