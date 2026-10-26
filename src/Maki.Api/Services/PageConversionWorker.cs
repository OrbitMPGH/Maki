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
/// Converts the library that was there before conversion was switched on, one file at a time, at low
/// priority: a short pause between files, and pages encoded through <c>ImageWorkGate</c> alongside the reader's own decodes. The
/// pass is started and paused from the settings card; its state survives a restart, and a file is done
/// once <see cref="ChapterFile.ConversionCheckedAtUtc"/> is set, so a resumed pass picks up where it
/// stopped. A file that is busy or unreadable is passed over until the next pass.
/// </summary>
public class PageConversionWorker(
    IServiceScopeFactory scopes, IAppSettings settings, ILogger<PageConversionWorker> logger) : BackgroundService
{
    /// <summary>Kept free on the library volume, on top of the file being converted.</summary>
    public const long DiskReserveBytes = 1L << 30;

    private const int BatchSize = 20;

    internal TimeSpan DelayBetweenFiles { get; set; } = TimeSpan.FromMilliseconds(250);

    private readonly SemaphoreSlim _wake = new(0);
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
        _wake.Release();
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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await StateAsync(stoppingToken) == PageConversionPassState.Running &&
                    await RunPassAsync(stoppingToken))
                {
                    continue;
                }

                await _wake.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken);
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

                    plan ??= await PageConversionOptions.LoadPlanAsync(db, ct);
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

    /// <summary>Archives conversion has not looked at yet. PDFs are never converted, and an import still owed its link is left alone.</summary>
    public static IQueryable<ChapterFile> PendingQuery(MakiDbContext db) =>
        db.ChapterFiles.IgnoreQueryFilters()
            .Where(f => f.ConversionCheckedAtUtc == null &&
                        (f.RelativePath.ToLower().EndsWith(".cbz") || f.RelativePath.ToLower().EndsWith(".zip")) &&
                        !db.Series.IgnoreQueryFilters().Any(s =>
                            s.Id == f.SeriesId && s.PendingImportLink != PendingImportLink.None));

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
