using System.Globalization;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Storage;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public enum PageConversionPassState
{
    Idle,
    Running,
    Paused
}

/// <summary>Why a pass stopped without anybody pausing it.</summary>
public enum PageConversionHold
{
    None,
    LowDisk,
    ConversionOff,

    /// <summary>The settings or rules were saved while it ran; a restart runs under the new plan.</summary>
    SettingsChanged
}

/// <summary>Totals of one pass over the library, kept after it ends.</summary>
public sealed record PageConversionPassTotals(
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc,
    int Converted,
    int Unchanged,
    int Hardlinked,
    int Failed,
    long SavedBytes);

/// <summary>
/// Converts library files one at a time, at low priority: a short pause between files, and pages encoded
/// through <c>ImageWorkGate</c> alongside the reader's own decodes. Two kinds of work share the loop:
/// <list type="bullet">
/// <item>The queue: files a download or import placed while conversion was on
/// (<see cref="ChapterFile.ConversionQueuedAtUtc"/>), so the download itself never waits on an encode.
/// It runs whenever conversion is on, ahead of the pass and between its files. A file being read, or with a
/// download of one of its chapters in flight, waits and is tried again; one that keeps failing is left to
/// the pass after <see cref="MaxQueueAttempts"/> tries.</item>
/// <item>The pass over the library that was there before, started and paused from the settings card. Its
/// state survives a restart, and a file is done once <see cref="ChapterFile.ConversionCheckedAtUtc"/> is
/// set, so a resumed pass picks up where it stopped. A file that is busy or unreadable is passed over until
/// the next pass.</item>
/// </list>
/// </summary>
public class PageConversionWorker(
    IServiceScopeFactory scopes, IAppSettings settings, ILogger<PageConversionWorker> logger,
    Maki.Api.Jobs.ArtifactBuildGate? buildGate = null) : BackgroundService
{
    /// <summary>Kept free on the library volume, on top of the file being converted.</summary>
    public const long DiskReserveBytes = 1L << 30;

    public const int MaxQueueAttempts = 5;

    internal TimeSpan BusyRetry { get; set; } = TimeSpan.FromMinutes(2);
    internal TimeSpan FailedRetry { get; set; } = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LowDiskRetry = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The longest the worker sleeps with nothing due. Every source of new work signals it: a queued file,
    /// a start, a settings save. This only covers a signal lost to a restart race.
    /// </summary>
    internal static readonly TimeSpan IdleFallback = TimeSpan.FromMinutes(30);

    private const int BatchSize = 20;

    internal TimeSpan DelayBetweenFiles { get; set; } = TimeSpan.FromMilliseconds(250);

    // Static so a download or import can wake the worker without holding a reference to it.
    private static readonly SemaphoreSlim Wake = new(0, 1);

    /// <summary>Per queued file, when it may be tried again and how many tries failed. In memory: a restart retries at once.</summary>
    private readonly Dictionary<int, (DateTime RetryAt, int Failures)> _queueRetry = [];
    private DateTime _queueHeldUntil;

    // The earliest retry among the files the last queue run saw; null while conversion is off.
    private DateTime? _queueNextRetry;

    private readonly Lock _sync = new();
    private PageConversionPassTotals? _current;
    private string? _currentFile;
    private PageConversionHold _hold;

    // Bumped by every start and pause, so a run that outlives one (busy on a file while the wizard
    // pauses, saves and starts again) stops after that file instead of finishing under the old plan.
    private int _generation;

    // The file a run was busy with when it was stopped, decided by the old plan after the save's recheck ran.
    private int? _recheckFileId;

    public sealed record Snapshot(
        PageConversionPassState State, PageConversionHold Hold, string? CurrentFile, PageConversionPassTotals? Pass);

    public async Task<Snapshot> SnapshotAsync(CancellationToken ct)
    {
        var state = await StateAsync(ct);
        var last = LastPass(await settings.GetAsync(SettingKeys.PageConversionLastPass, ct));
        lock (_sync)
        {
            return new Snapshot(state, state == PageConversionPassState.Paused ? _hold : PageConversionHold.None,
                state == PageConversionPassState.Running ? _currentFile : null, _current ?? last);
        }
    }

    public async Task StartPassAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            _hold = PageConversionHold.None;
        }

        Interlocked.Increment(ref _generation);
        await settings.SetAsync(SettingKeys.PageConversionPass, "running", ct);
        Signal();
    }

    /// <summary>Wakes the worker for new work, such as a file just queued.</summary>
    public static void Signal()
    {
        try
        {
            Wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public async Task PauseAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _generation);
        if (await StateAsync(ct) == PageConversionPassState.Running)
        {
            await settings.SetAsync(SettingKeys.PageConversionPass, "paused", ct);
        }
    }

    /// <summary>
    /// A running pass decides every file by the plan it started with, so new settings stop it until it
    /// is restarted: <see cref="PageConversionHold.SettingsChanged"/>, or <see cref="PageConversionHold.ConversionOff"/>
    /// when conversion was turned off.
    /// </summary>
    public async Task PauseForSettingsAsync(PageConversionHold hold, CancellationToken ct)
    {
        Interlocked.Increment(ref _generation);
        if (await StateAsync(ct) == PageConversionPassState.Running)
        {
            await HoldAsync(hold, ct);
        }
    }

    public async Task<PageConversionPassState> StateAsync(CancellationToken ct) =>
        await settings.GetAsync(SettingKeys.PageConversionPass, ct) switch
        {
            "running" => PageConversionPassState.Running,
            "paused" => PageConversionPassState.Paused,
            _ => PageConversionPassState.Idle
        };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ImageWorkGate.SetConversionLimit(
            PageConversionOptions.ParseParallel(await settings.GetAsync(SettingKeys.PageConversionParallel, stoppingToken)));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunQueueAsync(stoppingToken);
                if (await StateAsync(stoppingToken) == PageConversionPassState.Running &&
                    await RunPassAsync(stoppingToken))
                {
                    continue;
                }

                await Wake.WaitAsync(IdleWait(DateTime.UtcNow), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Page conversion pass failed");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Until the earliest file the last queue run left waiting may be tried again, or the low-disk hold
    /// ends, at most <see cref="IdleFallback"/>. Zero when one came due while the run was busy.
    /// </summary>
    internal TimeSpan IdleWait(DateTime now)
    {
        var next = now + IdleFallback;
        if (_queueNextRetry is { } retry && retry < next)
        {
            next = retry;
        }

        if (_queueHeldUntil > now && _queueHeldUntil < next)
        {
            next = _queueHeldUntil;
        }

        return next > now ? next - now : TimeSpan.Zero;
    }

    private void RetryNoLaterThan(DateTime retryAt)
    {
        if (_queueNextRetry is not { } earliest || retryAt < earliest)
        {
            _queueNextRetry = retryAt;
        }
    }

    /// <summary>
    /// Runs until the library has nothing left to look at, or the pass is paused. True when a start or
    /// pause came in meanwhile: the run stopped after its current file, and a fresh one has to look again.
    /// </summary>
    internal async Task<bool> RunPassAsync(CancellationToken ct)
    {
        var generation = Volatile.Read(ref _generation);
        DateTime started;
        lock (_sync)
        {
            _current ??= new PageConversionPassTotals(DateTime.UtcNow, null, 0, 0, 0, 0, 0);
            started = _current.StartedAtUtc;
        }

        var lastId = 0;
        var cache = new PageConversionPassCache { PassId = started.ToString("O", CultureInfo.InvariantCulture) };
        PageConversionPlan? plan = null;
        try
        {
            while (true)
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
                int? stopped;
                lock (_sync)
                {
                    (stopped, _recheckFileId) = (_recheckFileId, null);
                }

                if (stopped is not null)
                {
                    await new PageConversionPlanService(db).RecheckFileAsync(
                        await PageConversionOptions.LoadPlanAsync(db, ct), stopped.Value, ct);
                }

                var batch = await PendingQuery(db).Where(f => f.Id > lastId)
                    .OrderBy(f => f.Id)
                    .Select(f => new
                    {
                        f.Id,
                        f.RelativePath,
                        f.Size,
                        Root = db.Series.IgnoreQueryFilters().Where(s => s.Id == f.SeriesId).Select(s => s.RootFolder!.Path).FirstOrDefault()
                    })
                    .Take(BatchSize)
                    .ToListAsync(ct);
                if (Volatile.Read(ref _generation) != generation)
                {
                    return true;
                }

                if (batch.Count == 0)
                {
                    await FinishAsync(ct);
                    return false;
                }

                foreach (var file in batch)
                {
                    lastId = file.Id;
                    if (Volatile.Read(ref _generation) != generation)
                    {
                        return true;
                    }

                    if (await StateAsync(ct) != PageConversionPassState.Running)
                    {
                        return false;
                    }

                    // New downloads go first, so a pass over a large library does not hold them up for hours.
                    await RunQueueAsync(ct);

                    plan ??=await PageConversionOptions.LoadPlanAsync(db, ct);
                    if (plan is null)
                    {
                        await HoldAsync(PageConversionHold.ConversionOff, ct);
                        return false;
                    }

                    if (file.Root is not null && DiskSpace.AvailableFor(file.Root) is { } free &&
                        free < file.Size + DiskReserveBytes)
                    {
                        await HoldAsync(PageConversionHold.LowDisk, ct);
                        return false;
                    }

                    lock (_sync)
                    {
                        _currentFile = file.RelativePath;
                    }

                    FileConversion result;
                    try
                    {
                        if (buildGate is not null) await buildGate.WaitUntilIdleAsync(ct);
                        using var fileScope = scopes.CreateScope();
                        result = await fileScope.ServiceProvider.GetRequiredService<PageConversionService>()
                            .ConvertAsync(file.Id, plan, cache, ct);
                    }
                    // One file that throws every time must not restart the pass on it forever.
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        logger.LogError(ex, "Could not convert the pages of {Path}", file.RelativePath);
                        result = new FileConversion(FileConversionStatus.Failed);
                    }

                    Count(result);
                    if (Volatile.Read(ref _generation) != generation)
                    {
                        lock (_sync)
                        {
                            _recheckFileId = file.Id;
                        }

                        return true;
                    }

                    if (DelayBetweenFiles > TimeSpan.Zero)
                    {
                        await Task.Delay(DelayBetweenFiles, ct);
                    }
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                _currentFile = null;
            }
        }
    }

    /// <summary>
    /// Archives conversion has not looked at yet, for the pass. PDFs are never converted, an import still
    /// owed its link is left alone, and a queued file belongs to <see cref="QueuedQuery"/>.
    /// </summary>
    public static IQueryable<ChapterFile> PendingQuery(MakiDbContext db) =>
        UncheckedQuery(db).Where(f => f.ConversionQueuedAtUtc == null);

    /// <summary>Files a download or import queued that the queue has not decided yet, with the same exclusions.</summary>
    public static IQueryable<ChapterFile> QueuedQuery(MakiDbContext db) =>
        UncheckedQuery(db).Where(f => f.ConversionQueuedAtUtc != null);

    private static IQueryable<ChapterFile> UncheckedQuery(MakiDbContext db) =>
        db.ChapterFiles.IgnoreQueryFilters()
            .Where(f => f.ConversionCheckedAtUtc == null &&
                        (f.RelativePath.ToLower().EndsWith(".cbz") || f.RelativePath.ToLower().EndsWith(".zip")) &&
                        !db.Series.IgnoreQueryFilters().Any(s =>
                            s.Id == f.SeriesId && s.PendingImportLink != PendingImportLink.None));

    /// <summary>
    /// Works through the queued files that are due, oldest first, under the plan as it is now. Returns how
    /// many were settled. Does nothing while conversion is off; the files stay queued.
    /// </summary>
    internal async Task<int> RunQueueAsync(CancellationToken ct)
    {
        _queueNextRetry = null;
        if (DateTime.UtcNow < _queueHeldUntil)
        {
            return 0;
        }

        var settled = 0;
        var lastId = 0;
        var cache = new PageConversionPassCache();
        PageConversionPlan? plan = null;
        while (true)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            var batch = await QueuedQuery(db).Where(f => f.Id > lastId)
                .OrderBy(f => f.Id)
                .Select(f => new
                {
                    f.Id,
                    f.RelativePath,
                    f.Size,
                    Root = db.Series.IgnoreQueryFilters().Where(s => s.Id == f.SeriesId).Select(s => s.RootFolder!.Path).FirstOrDefault()
                })
                .Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                return settled;
            }

            plan ??= await PageConversionOptions.LoadPlanAsync(db, ct);
            if (plan is null)
            {
                return settled;
            }

            foreach (var file in batch)
            {
                lastId = file.Id;
                var now = DateTime.UtcNow;
                if (_queueRetry.TryGetValue(file.Id, out var retry) && retry.RetryAt > now)
                {
                    RetryNoLaterThan(retry.RetryAt);
                    continue;
                }

                if (file.Root is not null && DiskSpace.AvailableFor(file.Root) is { } free &&
                    free < file.Size + DiskReserveBytes)
                {
                    logger.LogWarning("Page conversion queue held for {Minutes} minutes: {Free} bytes free on {Root}",
                        LowDiskRetry.TotalMinutes, free, file.Root);
                    _queueHeldUntil = now + LowDiskRetry;
                    return settled;
                }

                FileConversion result;
                try
                {
                    if (buildGate is not null) await buildGate.WaitUntilIdleAsync(ct);
                    using var fileScope = scopes.CreateScope();
                    result = await fileScope.ServiceProvider.GetRequiredService<PageConversionService>()
                        .ConvertAsync(file.Id, plan, cache, ct, queued: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogError(ex, "Could not convert the pages of {Path}", file.RelativePath);
                    result = new FileConversion(FileConversionStatus.Failed);
                }

                if (await SettleQueuedAsync(db, file.Id, file.RelativePath, result.Status, ct))
                {
                    settled++;
                }

                if (DelayBetweenFiles > TimeSpan.Zero)
                {
                    await Task.Delay(DelayBetweenFiles, ct);
                }
            }
        }
    }

    /// <summary>True when the file left the queue.</summary>
    private async Task<bool> SettleQueuedAsync(MakiDbContext db, int fileId, string path, FileConversionStatus status,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        switch (status)
        {
            case FileConversionStatus.Busy:
                _queueRetry[fileId] = (now + BusyRetry, _queueRetry.GetValueOrDefault(fileId).Failures);
                RetryNoLaterThan(now + BusyRetry);
                return false;
            case FileConversionStatus.Failed:
                var failures = _queueRetry.GetValueOrDefault(fileId).Failures + 1;
                if (failures < MaxQueueAttempts)
                {
                    _queueRetry[fileId] = (now + FailedRetry * failures, failures);
                    RetryNoLaterThan(now + FailedRetry * failures);
                    return false;
                }

                logger.LogWarning("Could not convert {Path} after {Attempts} tries; it is left for the library pass",
                    path, failures);
                break;
        }

        _queueRetry.Remove(fileId);
        await db.ChapterFiles.IgnoreQueryFilters()
            .Where(f => f.Id == fileId)
            .ExecuteUpdateAsync(set => set.SetProperty(f => f.ConversionQueuedAtUtc, (DateTime?)null), ct);
        return true;
    }

    private void Count(FileConversion result)
    {
        lock (_sync)
        {
            var c = _current!;
            _current = result.Status switch
            {
                FileConversionStatus.Converted => c with { Converted = c.Converted + 1, SavedBytes = c.SavedBytes + result.SavedBytes },
                FileConversionStatus.Unchanged => c with { Unchanged = c.Unchanged + 1 },
                FileConversionStatus.Hardlinked => c with { Hardlinked = c.Hardlinked + 1 },
                _ => c with { Failed = c.Failed + 1 }
            };
        }
    }

    private async Task HoldAsync(PageConversionHold hold, CancellationToken ct)
    {
        lock (_sync)
        {
            _hold = hold;
        }

        logger.LogInformation("Page conversion paused: {Reason}", hold);
        await settings.SetAsync(SettingKeys.PageConversionPass, "paused", ct);
    }

    private async Task FinishAsync(CancellationToken ct)
    {
        PageConversionPassTotals totals;
        lock (_sync)
        {
            totals = _current! with { FinishedAtUtc = DateTime.UtcNow };
            _current = null;
        }

        await settings.SetAsync(SettingKeys.PageConversionLastPass, JsonSerializer.Serialize(totals, Json), ct);
        await settings.SetAsync(SettingKeys.PageConversionPass, null, ct);
        logger.LogInformation(
            "Page conversion pass finished: {Converted} converted, {Unchanged} left as they were, {Hardlinked} hardlinked, {Failed} passed over, {Saved} bytes saved",
            totals.Converted, totals.Unchanged, totals.Hardlinked, totals.Failed, totals.SavedBytes);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static PageConversionPassTotals? LastPass(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<PageConversionPassTotals>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
