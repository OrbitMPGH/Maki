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
///
/// <para>
/// A page runs in two stages: decode and tiles, then colour pass, encode, cache write and the outcome
/// push. The finish runs in the background while the next page's tiles run, one page at a time: the
/// next page's finish waits for it. A page abandoned during its tiles stops; one abandoned while
/// finishing is still finished and cached.
/// </para>
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

    private Task _finishing = Task.CompletedTask;
    private UpscaleWorkKey? _finishingKey;

    /// <summary>The page finishing in the background, or a completed task. Never faults.</summary>
    internal Task Finishing => _finishing;

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
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await RunOnceAsync(stoppingToken))
                    {
                        busy = true;
                        continue;
                    }

                    if (!_finishing.IsCompleted)
                    {
                        await _finishing;
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
        finally
        {
            await _finishing;
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

    /// <summary>
    /// Runs the next job's tiles, waits for the previous page's finish, then starts this one's in the
    /// background (<see cref="Finishing"/>). False when there was nothing to run.
    /// </summary>
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

        if (!UpscaleModelId.TryParse(job.Key.Model, out var model) || !options.Models.Contains(model))
        {
            await CompleteAsync(job, UpscaleOutcome.Failed, UpscaleReasons.ModelUnavailable);
            return true;
        }

        // An abandoned run of the same page can finish and cache it after this job was queued.
        if (_finishingKey == job.Key)
        {
            await _finishing;
        }

        if (cache.TryGet(job.Key) is not null)
        {
            await CompleteAsync(job, UpscaleOutcome.Ready, null);
            return true;
        }

        await TellAsync(queue.WaitersOf(job), job.Key.Model, "working");
        var started = await StartAsync(job, ct);
        await _finishing;
        _finishingKey = job.Key;
        _finishing = Task.Run(() => FinishAsync(job, model, started, ct), CancellationToken.None);
        return true;
    }

    private async Task CompleteAsync(UpscaleJob job, UpscaleOutcome outcome, string? reason)
    {
        var state = outcome switch
        {
            UpscaleOutcome.Ready => "ready",
            UpscaleOutcome.Skipped => "skipped",
            _ => "failed"
        };
        await TellAsync(queue.Complete(job, outcome, reason), job.Key.Model, state);
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

    /// <summary>Reads the page and runs its tiles; abandoning the job cancels this stage.</summary>
    private async Task<PendingUpscale> StartAsync(UpscaleJob job, CancellationToken ct)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct, job.Abandoned);
        try
        {
            var page = await ReaderPageCache.ReadPageBytesAsync(paths, archives, job.Slice, job.Key.AbsIndex, run.Token);
            if (page is null)
            {
                return PendingUpscale.Done(UpscaleResult.Skipped(UpscaleReasons.Unreadable));
            }

            return await upscaler.StartAsync(page, job.Key.Model, run.Token);
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
            return PendingUpscale.Done(UpscaleResult.Failed(UpscaleReasons.Abandoned));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Upscaling page {Page} of chapter file {FileId} with {Model} failed",
                job.Key.AbsIndex, job.Key.ChapterFileId, job.Key.Model);
            return PendingUpscale.Done(UpscaleResult.Failed(UpscaleReasons.InferenceFailed));
        }
    }

    /// <summary>
    /// Colour pass, encode, cache write and the outcome push. Only shutdown cancels it: a page that got
    /// past its tiles is cached even when its reader left. Never throws.
    /// </summary>
    private async Task FinishAsync(UpscaleJob job, UpscaleModelId model, PendingUpscale started, CancellationToken ct)
    {
        try
        {
            var (outcome, reason) = await FinishPageAsync(job, model, started, ct);
            await CompleteAsync(job, outcome, reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            queue.Complete(job, UpscaleOutcome.Failed, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Finishing page {Page} of chapter file {FileId} failed", job.Key.AbsIndex, job.Key.ChapterFileId);
            queue.Complete(job, UpscaleOutcome.Failed, UpscaleReasons.EncodeFailed);
        }
    }

    private async Task<(UpscaleOutcome Outcome, string? Reason)> FinishPageAsync(
        UpscaleJob job, UpscaleModelId model, PendingUpscale started, CancellationToken ct)
    {
        UpscaleResult result;
        try
        {
            result = await started.FinishAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Encoding page {Page} of chapter file {FileId} with {Model} failed",
                job.Key.AbsIndex, job.Key.ChapterFileId, job.Key.Model);
            return (UpscaleOutcome.Failed, UpscaleReasons.EncodeFailed);
        }

        if (result is not { Outcome: UpscaleOutcome.Ready, Bytes: { } bytes, Extension: { } extension })
        {
            return (result.Outcome == UpscaleOutcome.Ready ? UpscaleOutcome.Failed : result.Outcome, result.Reason);
        }

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
}
