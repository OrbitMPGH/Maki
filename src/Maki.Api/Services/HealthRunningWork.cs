using System.Diagnostics;
using Maki.Api.Jobs;
using Maki.Api.Localization;
using Maki.Api.Services.Upscaling;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Maki.Api.Services;

/// <summary>What the Maki process is costing the host right now.</summary>
/// <param name="CpuPercent">Share of the cores the process may use, 0 to 100, averaged since the previous read.</param>
public sealed record RunningProcess(double CpuPercent, long MemoryBytes, int Cores);

/// <summary>
/// One piece of background work in progress. <c>Kind</c> says how the page words it; <c>Label</c> is set
/// only for a scheduled job, already in the reader's language. <c>Control</c> is what the page may offer:
/// <c>stop</c> (with <c>StopId</c>), <c>pause</c> or <c>resume</c>, or null for work nobody should cut short.
/// </summary>
public sealed record RunningItem(
    string Kind,
    string? Label = null,
    DateTime? StartedAtUtc = null,
    int? Active = null,
    int? Done = null,
    int? Total = null,
    int? Waiting = null,
    string? Detail = null,
    string? Control = null,
    string? StopId = null);

public sealed record RunningWork(RunningProcess Process, IReadOnlyList<RunningItem> Items);

/// <summary>
/// One registered Quartz job. <c>IntervalSeconds</c> is the repeat of its trigger, null for a job that
/// only runs when something starts it. <c>LastRunUtc</c> falls back to the job's health row, which
/// survives a restart but is restamped at most every five minutes. <c>LastFailed</c> is that row's verdict.
/// </summary>
public sealed record ScheduledJob(
    string Group,
    string Name,
    string Label,
    DateTime? NextRunUtc,
    DateTime? LastRunUtc,
    long? IntervalSeconds,
    bool Running,
    bool Paused,
    bool LastFailed);

/// <summary>What starting a job by hand found.</summary>
public enum ManualRunResult
{
    Started,
    NotFound,
    AlreadyRunning
}

/// <summary>
/// Samples the process's CPU time between reads, so the figure covers the last poll interval rather
/// than the process's whole life. With no recent read it measures over a short wait instead.
/// </summary>
public sealed class ProcessCpuSampler(TimeProvider? time = null)
{
    private static readonly TimeSpan MinWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ColdWindow = TimeSpan.FromMilliseconds(500);

    private readonly Lock _gate = new();
    private (long Ticks, TimeSpan Cpu)? _last;
    private double _percent;

    public async Task<double> ReadAsync(CancellationToken ct)
    {
        var clock = time ?? TimeProvider.System;
        lock (_gate)
        {
            if (_last is { } last && clock.GetElapsedTime(last.Ticks) is var since && since < MinWindow)
            {
                return _percent;
            }
        }

        var (startTicks, startCpu) = Sample(clock);
        lock (_gate)
        {
            if (_last is { } last && clock.GetElapsedTime(last.Ticks) <= MaxWindow)
            {
                (startTicks, startCpu) = last;
            }
        }

        if (clock.GetElapsedTime(startTicks) < MinWindow)
        {
            await Task.Delay(ColdWindow, ct);
        }

        var (endTicks, endCpu) = Sample(clock);
        var wall = clock.GetElapsedTime(startTicks, endTicks);
        var percent = wall <= TimeSpan.Zero
            ? 0
            : Math.Clamp((endCpu - startCpu) / wall / Environment.ProcessorCount * 100, 0, 100);
        lock (_gate)
        {
            _last = (endTicks, endCpu);
            _percent = percent;
        }

        return percent;
    }

    private static (long Ticks, TimeSpan Cpu) Sample(TimeProvider clock)
    {
        using var process = Process.GetCurrentProcess();
        return (clock.GetTimestamp(), process.TotalProcessorTime);
    }
}

/// <summary>
/// Collects what Maki is busy with for the Health page: scheduled jobs mid-run, the server enhance
/// queue and its prepare-ahead list, page conversion, scraper downloads in flight (a torrent client's
/// work is not Maki's), bulk series edits and source matching. Health scans are left out because the page already shows them with a cancel button.
/// Read only; every source here is an in-memory snapshot apart from three counts.
/// </summary>
public sealed class HealthRunningWork(
    ISchedulerFactory schedulers,
    UpscaleQueue upscale,
    PageConversionWorker conversion,
    SeriesBulkOperations bulk,
    ImageCacheRebuildStatus imageCache,
    SourceMatchQueue matches,
    DownloadPauseService pauses,
    ProcessCpuSampler cpu,
    MakiDbContext db,
    ILocalizer localizer)
{
    private static readonly QueueStatus[] InFlight =
    [
        QueueStatus.FetchingPages, QueueStatus.Downloading, QueueStatus.Validating, QueueStatus.Packaging,
        QueueStatus.Importing, QueueStatus.Resolving
    ];

    /// <summary>
    /// Jobs that never read their cancellation token. They finish in milliseconds, so a stop button
    /// on one would only ever do nothing.
    /// </summary>
    private static readonly HashSet<Type> Unstoppable =
    [
        typeof(ArtifactIdleUnloadJob), typeof(BrowserIdleShutdownJob), typeof(EmbedderIdleUnloadJob),
        typeof(UpscalerIdleUnloadJob)
    ];

    /// <summary>
    /// Data a by-hand run carries, matching the button that already starts the job elsewhere. Every other
    /// job runs exactly as its schedule would start it, so one that checks its own daily hour or switch may
    /// find nothing to do.
    /// </summary>
    private static readonly Dictionary<Type, Func<JobDataMap>> ManualData = new()
    {
        [typeof(ScrobbleJob)] = () => new JobDataMap { { ScrobbleJob.ForceKey, true } },
        [typeof(EmbeddingIndexJob)] = () => new JobDataMap { { EmbeddingIndexJob.ManualTriggerKey, true } },
    };

    public async Task<IReadOnlyList<ScheduledJob>> ScheduledAsync(CancellationToken ct)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        var running = (await scheduler.GetCurrentlyExecutingJobs(ct)).Select(c => c.JobDetail.Key).ToHashSet();
        var rows = await db.HealthChecks.AsNoTracking()
            .Where(c => c.Category == "job")
            .Select(c => new { c.Id, c.Status, c.CheckedAt })
            .ToDictionaryAsync(c => c.Id, ct);
        var jobs = new List<ScheduledJob>();
        foreach (var key in await scheduler.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.AnyGroup(), ct))
        {
            var detail = await scheduler.GetJobDetail(key, ct);
            if (detail is null) continue;
            var triggers = await scheduler.GetTriggersOfJob(key, ct);
            var paused = triggers.Count > 0;
            foreach (var trigger in triggers)
            {
                paused &= await scheduler.GetTriggerState(trigger.Key, ct) == TriggerState.Paused;
            }

            var labelKey = JobLabels.For(detail.JobType, key.Name);
            var row = rows.GetValueOrDefault($"job:{key}");
            jobs.Add(new ScheduledJob(
                key.Group,
                key.Name,
                JobLabels.IsLabel(labelKey) ? localizer.Get(labelKey) : labelKey,
                triggers.Select(t => t.GetNextFireTimeUtc()).Where(t => t is not null).Min()?.UtcDateTime,
                triggers.Select(t => t.GetPreviousFireTimeUtc()).Where(t => t is not null).Max()?.UtcDateTime
                    ?? (row is { Status: not "unchecked" } ? DateTime.SpecifyKind(row.CheckedAt, DateTimeKind.Utc) : null),
                triggers.OfType<ISimpleTrigger>().Where(t => t.RepeatCount != 0)
                    .Select(t => (long?)(long)t.RepeatInterval.TotalSeconds).Min(),
                running.Contains(key),
                paused,
                row?.Status == "error"));
        }

        return jobs;
    }

    /// <summary>Starts a job now, unless a run of it is already going.</summary>
    public async Task<ManualRunResult> RunNowAsync(string group, string name, CancellationToken ct)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        var key = new JobKey(name, group);
        var detail = await scheduler.GetJobDetail(key, ct);
        if (detail is null)
        {
            return ManualRunResult.NotFound;
        }

        if ((await scheduler.GetCurrentlyExecutingJobs(ct)).Any(c => c.JobDetail.Key.Equals(key)))
        {
            return ManualRunResult.AlreadyRunning;
        }

        if (ManualData.TryGetValue(detail.JobType, out var data))
        {
            await scheduler.TriggerJob(key, data(), ct);
        }
        else
        {
            await scheduler.TriggerJob(key, ct);
        }

        return ManualRunResult.Started;
    }

    public async Task<RunningWork> ReadAsync(CancellationToken ct)
    {
        var items = new List<RunningItem>();
        await AddJobsAsync(items, ct);
        AddEnhance(items);
        await AddConversionAsync(items, ct);
        await AddDownloadsAsync(items, ct);
        AddBulk(items);
        if (matches.Matching is > 0 and var matching)
        {
            items.Add(new RunningItem("sourceMatch", Active: matching));
        }

        long memory;
        using (var process = Process.GetCurrentProcess())
        {
            memory = process.WorkingSet64;
        }

        var percent = await cpu.ReadAsync(ct);
        return new RunningWork(new RunningProcess(Math.Round(percent, 1), memory, Environment.ProcessorCount), items);
    }

    private async Task AddJobsAsync(List<RunningItem> items, CancellationToken ct)
    {
        var scheduler = await schedulers.GetScheduler(ct);
        var running = await scheduler.GetCurrentlyExecutingJobs(ct);
        foreach (var context in running.OrderBy(c => c.FireTimeUtc))
        {
            var type = context.JobDetail.JobType;
            var key = JobLabels.For(type, context.JobDetail.Key.Name);
            var label = JobLabels.IsLabel(key) ? localizer.Get(key) : key;
            var item = new RunningItem("job", label, context.FireTimeUtc.UtcDateTime,
                Control: Unstoppable.Contains(type) ? null : "stop", StopId: context.FireInstanceId);
            if (type == typeof(ImageCacheRebuildJob) && imageCache.Snapshot() is { Running: true } rebuild)
            {
                item = item with { Done = rebuild.Processed, Total = rebuild.Total };
            }

            items.Add(item);
        }
    }

    private void AddEnhance(List<RunningItem> items)
    {
        var snapshot = upscale.Snapshot();
        var forReaders = snapshot.Working - snapshot.BackgroundWorking;
        if (forReaders > 0 || snapshot.Pending > 0)
        {
            items.Add(new RunningItem("enhance", Active: forReaders, Waiting: snapshot.Pending));
        }

        if (snapshot.BackgroundWorking > 0 || snapshot.Background > 0)
        {
            items.Add(new RunningItem("prepareAhead", Active: snapshot.BackgroundWorking, Waiting: snapshot.Background));
        }
    }

    private async Task AddConversionAsync(List<RunningItem> items, CancellationToken ct)
    {
        if (conversion.Working is not { } working)
        {
            return;
        }

        var pass = conversion.CurrentPass;
        var waiting = await PageConversionWorker.QueuedQuery(db).CountAsync(ct);
        if (pass is not null)
        {
            waiting += await PageConversionWorker.PendingQuery(db).CountAsync(ct);
        }

        var passRunning = await conversion.StateAsync(ct) == PageConversionPassState.Running;
        items.Add(new RunningItem("pageConversion",
            StartedAtUtc: pass?.StartedAtUtc ?? working.StartedAtUtc,
            Done: pass is null ? null : pass.Converted + pass.Unchanged + pass.Hardlinked + pass.Failed,
            Waiting: waiting,
            Detail: working.Path,
            Control: passRunning ? "pause" : null));
    }

    private async Task AddDownloadsAsync(List<RunningItem> items, CancellationToken ct)
    {
        var counts = await db.DownloadQueue.IgnoreQueryFilters()
            .Where(q => q.Protocol == AcquisitionProtocol.Scraper &&
                        (InFlight.Contains(q.Status) || q.Status == QueueStatus.Queued))
            .GroupBy(q => q.Status == QueueStatus.Queued)
            .Select(g => new { Queued = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var active = counts.FirstOrDefault(c => !c.Queued)?.Count ?? 0;
        if (active > 0)
        {
            var paused = (await pauses.ActiveAsync(ct)).All is not null;
            items.Add(new RunningItem("downloads", Active: active,
                Waiting: counts.FirstOrDefault(c => c.Queued)?.Count ?? 0, Control: paused ? "resume" : "pause"));
        }
    }

    private void AddBulk(List<RunningItem> items)
    {
        foreach (var operation in bulk.Active())
        {
            var status = operation.Snapshot(maxFailures: 0);
            if (status.State != "running") continue;
            items.Add(new RunningItem("bulk", Done: status.Done, Total: status.Total,
                Detail: char.ToLowerInvariant(status.Action[0]) + status.Action[1..],
                Control: operation.Cancel.IsCancellationRequested ? null : "stop", StopId: operation.Id.ToString()));
        }
    }
}
