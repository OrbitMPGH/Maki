using Maki.Api.Services.Ocr;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Finds every chapter's credit pages ahead of time, so the reader opens with them already left out
/// (<see cref="CreditStore"/>). Nothing here holds up an import or a read: it runs one chapter at a
/// time with one decode, after downloads and imports have finished, and waits while anyone is being
/// served pages.
/// <para>
/// The unit is a series in one language, in reading order, so each chapter's neighbours were just
/// decoded. A series is due when a chapter with a file has no current answer (a new or replaced file,
/// a version bump, a failure a day old); its pass then also searches again every chapter whose
/// neighbours changed. Series read in the last two weeks come first, from the chapter after the last
/// one read, then series with new files, then the rest, series marked Reading first. A series with a
/// download in flight, an import link pending or a file queued for conversion waits, so a volume
/// download is searched once, whole.
/// </para>
/// <para>
/// Volumes are left out unless they are a scanlation pack: a volume file, or a chapter cut from one,
/// is only searched once the text on some volume file's first or last pages in its series reads as a
/// scanlator's (<see cref="ReaderCreditsCache.ProbeAsync"/>). Published volumes carry no credits, and
/// a volume download is where most of the work would be.
/// </para>
/// <para>
/// Off until someone asks for credit pages (<see cref="SettingKeys.CreditScanWanted"/>): an instance
/// where nobody hides them spends nothing.
/// </para>
/// </summary>
public sealed class CreditScanWorker(
    IServiceScopeFactory scopes, IAppSettings settings, ReaderCreditsCache credits, CreditStore store,
    ReaderArchiveCache archives, PageConversionWorker conversion, ILogger<CreditScanWorker> logger) : BackgroundService
{
    /// <summary>The longest the worker sleeps with nothing due; downloads, imports and the first request signal it.</summary>
    internal static readonly TimeSpan IdleFallback = TimeSpan.FromMinutes(30);

    /// <summary>A reader served a page this recently holds the scan.</summary>
    internal static readonly TimeSpan ReadingQuiet = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BetweenChapters = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FailedRetry = TimeSpan.FromDays(1);
    private static readonly TimeSpan ReadRecently = TimeSpan.FromDays(14);
    private static readonly TimeSpan AddedRecently = TimeSpan.FromDays(1);

    /// <summary>How long a series a pass could not settle stays out of the pick.</summary>
    private static readonly TimeSpan Backoff = TimeSpan.FromHours(1);

    private static readonly SemaphoreSlim Wake = new(0, 1);
    private static volatile bool _wanted;

    private readonly Lock _sync = new();
    private readonly Dictionary<int, DateTime> _backoff = [];
    private bool _paused;
    private int _done;
    private string? _working;
    private (DateTime At, int Count)? _waiting;

    /// <summary>What the Health page shows: whether the scan is on and paused, chapters searched since start, the series being searched.</summary>
    public sealed record Snapshot(bool Wanted, bool Paused, int Done, string? Working);

    public Snapshot Current
    {
        get
        {
            lock (_sync)
            {
                return new Snapshot(_wanted, _paused, _done, _working);
            }
        }
    }

    /// <summary>Wakes the worker for new work, such as a file just placed.</summary>
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

    /// <summary>Turns the scan on for good, the first time a reader asks for credit pages.</summary>
    public static async Task WantAsync(IAppSettings settings, CancellationToken ct)
    {
        if (_wanted)
        {
            return;
        }

        if (await settings.GetAsync(SettingKeys.CreditScanWanted, ct) != "true")
        {
            await settings.SetAsync(SettingKeys.CreditScanWanted, "true", ct);
        }

        _wanted = true;
        Signal();
    }

    /// <summary>Holds the scan after the chapter in hand, until <see cref="Resume"/> or a restart.</summary>
    public void Pause()
    {
        lock (_sync)
        {
            _paused = true;
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            _paused = false;
        }

        Signal();
    }

    private bool Paused
    {
        get
        {
            lock (_sync)
            {
                return _paused;
            }
        }
    }

    /// <summary>Chapters with a file and no current answer, counted at most every 20 seconds for the Health page.</summary>
    public async Task<int> WaitingAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_waiting is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(20))
            {
                return cached.Count;
            }
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var count = await Dirty(db, DateTime.UtcNow, store.OcrAvailable).CountAsync(ct);
        lock (_sync)
        {
            _waiting = (DateTime.UtcNow, count);
        }

        return count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            _wanted = _wanted || await settings.GetAsync(SettingKeys.CreditScanWanted, stoppingToken) == "true";
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_wanted && !Paused && await NextSeriesAsync(stoppingToken) is { } next)
                {
                    await ScanSeriesAsync(next.SeriesId, next.Language, stoppingToken);
                    continue;
                }

                SetWorking(null);
                await Wake.WaitAsync(IdleFallback, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Credit page scan failed");
                SetWorking(null);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Chapters with a file whose answer is missing or no longer current by what SQL can see: another
    /// file or size (a ComicInfo rewrite is only restamped), a version bump, or a failure a day old.
    /// Neighbour changes are found by the series pass itself.
    /// </summary>
    internal static IQueryable<Chapter> Dirty(MakiDbContext db, DateTime now, bool ocrAvailable)
    {
        var retry = now - FailedRetry;
        return db.Chapters.IgnoreQueryFilters()
            .Where(c => c.ChapterFileId != null && c.Series!.ArchivedAt == null)
            .Where(c => !db.ChapterCredits.IgnoreQueryFilters().Any(r =>
                r.ChapterId == c.Id && r.ChapterFileId == c.ChapterFileId && r.FileSize == c.ChapterFile!.Size &&
                r.DetectorVersion == CreditDetector.Version && r.TextVersion == CreditText.Version &&
                r.OcrVersion == PageTextReader.Version && (!r.Failed || r.ScannedAtUtc > retry) &&
                (r.Judge == CreditJudge.Ocr || !ocrAvailable)));
    }

    /// <summary>The series to scan next, in priority order, or null when nothing is due.</summary>
    internal async Task<(int SeriesId, string Language)?> NextSeriesAsync(CancellationToken ct)
    {
        if (await conversion.StateAsync(ct) == PageConversionPassState.Running)
        {
            // A pass rewrites files, which would only make this work again.
            return null;
        }

        var now = DateTime.UtcNow;
        List<int> held;
        lock (_sync)
        {
            foreach (var (id, until) in _backoff.ToList())
            {
                if (until < now)
                {
                    _backoff.Remove(id);
                }
            }

            held = [.. _backoff.Keys];
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var due = Dirty(db, now, store.OcrAvailable)
            .Where(c => c.Series!.PendingImportLink == PendingImportLink.None)
            .Where(c => c.ChapterFile!.ConversionQueuedAtUtc == null)
            .Where(c => !db.DownloadQueue.Any(q => q.SeriesId == c.SeriesId && q.ActiveChapterId != null))
            .Where(c => !held.Contains(c.SeriesId));

        var readSince = now - ReadRecently;
        var read = await db.ChapterProgress.IgnoreQueryFilters()
            .Where(p => p.UpdatedAt > readSince)
            .Join(db.Chapters.IgnoreQueryFilters(), p => p.ChapterId, c => c.Id, (p, c) => new { c.SeriesId, p.UpdatedAt })
            .GroupBy(x => x.SeriesId)
            .Select(g => new { SeriesId = g.Key, Last = g.Max(x => x.UpdatedAt) })
            .OrderByDescending(x => x.Last)
            .Select(x => x.SeriesId)
            .Take(50)
            .ToListAsync(ct);
        foreach (var seriesId in read)
        {
            if (await due.Where(c => c.SeriesId == seriesId).Select(c => c.Language).FirstOrDefaultAsync(ct) is { } language)
            {
                return (seriesId, language);
            }
        }

        var addedSince = now - AddedRecently;
        var added = await due
            .Where(c => c.ChapterFile!.DateAdded > addedSince)
            .OrderByDescending(c => c.ChapterFile!.DateAdded)
            .Select(c => new { c.SeriesId, c.Language })
            .FirstOrDefaultAsync(ct);
        if (added is not null)
        {
            return (added.SeriesId, added.Language);
        }

        var next = await due
            .Where(c => db.UserSeriesStates.IgnoreQueryFilters().Any(s => s.SeriesId == c.SeriesId && s.Status == ReadingStatus.Reading))
            .OrderBy(c => c.SeriesId)
            .Select(c => new { c.SeriesId, c.Language })
            .FirstOrDefaultAsync(ct)
            ?? await due
                .OrderBy(c => c.SeriesId)
                .Select(c => new { c.SeriesId, c.Language })
                .FirstOrDefaultAsync(ct);
        return next is null ? null : (next.SeriesId, next.Language);
    }

    /// <summary>
    /// One pass over a series in one language. A chapter the pass could not settle keeps the series out
    /// of the pick for <see cref="Backoff"/>, so it never spins the worker.
    /// </summary>
    internal async Task ScanSeriesAsync(int seriesId, string language, CancellationToken ct)
    {
        using (var scope = scopes.CreateScope())
        {
            await PassAsync(scope.ServiceProvider, seriesId, language, ct);
        }

        using var check = scopes.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<MakiDbContext>();
        if (!Paused && await Dirty(db, DateTime.UtcNow, store.OcrAvailable).AnyAsync(c => c.SeriesId == seriesId && c.Language == language, ct))
        {
            lock (_sync)
            {
                _backoff[seriesId] = DateTime.UtcNow + Backoff;
            }
        }
    }

    /// <summary>
    /// Searches every chapter without a current answer, or whose neighbours changed, and restamps a
    /// chapter whose file was only rewritten. Starts after the chapter last read and wraps round, so
    /// what is read next is ready first.
    /// </summary>
    private async Task PassAsync(IServiceProvider services, int seriesId, string language, CancellationToken ct)
    {
        var reader = services.GetRequiredService<ReaderService>();
        var db = services.GetRequiredService<MakiDbContext>();
        var order = await reader.ReadingOrderAsync(seriesId, language, ct);
        var slices = await reader.SlicesAsync(order, ct);
        var ordered = order.Select(id => slices.GetValueOrDefault(id)).ToList();
        SetWorking(ordered.OfType<ReaderService.ChapterSlice>().FirstOrDefault()?.Series.Title);

        var rows = await db.ChapterCredits.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.SeriesId == seriesId)
            .ToDictionaryAsync(r => r.ChapterId, ct);
        var files = await db.Chapters.IgnoreQueryFilters()
            .Where(c => c.SeriesId == seriesId && c.Language == language && c.ChapterFileId != null)
            .Select(c => new { c.Id, FileId = c.ChapterFileId!.Value, c.ChapterFile!.Size })
            .ToListAsync(ct);
        var shares = files.GroupBy(f => f.FileId).ToDictionary(g => g.Key, g => g.Count());
        bool IsVolume(ReaderService.ChapterSlice s) => Chapter.IsVolumeEntry(s.Chapter) || shares.GetValueOrDefault(s.ChapterFileId) > 1;

        // A file that could not be read gets a failed row, or its chapter would keep the series due.
        foreach (var file in files.Where(f => !slices.ContainsKey(f.Id)))
        {
            await store.SaveUnreadableAsync(file.Id, seriesId, file.FileId, file.Size, ct);
        }

        // Each volume file is probed once; a hit on any makes the whole series a scanlation pack.
        var probes = new Dictionary<int, bool>();
        foreach (var slice in ordered.OfType<ReaderService.ChapterSlice>().Where(IsVolume))
        {
            if (probes.ContainsKey(slice.ChapterFileId))
            {
                continue;
            }

            var known = ordered.OfType<ReaderService.ChapterSlice>()
                .Where(s => s.ChapterFileId == slice.ChapterFileId)
                .Select(s => (Slice: s, Row: rows.GetValueOrDefault(s.Chapter.Id)))
                .FirstOrDefault(x => x.Row is { ProbeHit: not null } && store.Current(x.Row, x.Slice.EdgeKey));
            probes[slice.ChapterFileId] = known.Row?.ProbeHit ?? await ProbeAsync(slice, ct);
        }

        var pack = probes.Values.Any(hit => hit);
        var lastRead = await db.ChapterProgress.IgnoreQueryFilters()
            .Where(p => order.Contains(p.ChapterId))
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => (int?)p.ChapterId)
            .FirstOrDefaultAsync(ct);
        var start = lastRead is { } readId && order.IndexOf(readId) is var at and >= 0 ? at + 1 : 0;

        for (var step = 0; step < ordered.Count && !Paused; step++)
        {
            var i = (start + step) % ordered.Count;
            if (ordered[i] is not { } slice)
            {
                continue;
            }

            var row = rows.GetValueOrDefault(slice.Chapter.Id);
            var current = row is not null && store.Current(row, slice.EdgeKey);
            if (!ReaderCreditsCache.Applies(slice) || slice.EdgeKey is null)
            {
                if (!current)
                {
                    await store.SaveAsync(slice, string.Empty, CreditPages.None, CreditJudge.Ocr, [], ct);
                }

                continue;
            }

            if (IsVolume(slice) && !pack)
            {
                if (current && row is { Skipped: true })
                {
                    await RestampAsync(row, slice, ct);
                }
                else
                {
                    await store.SaveSkippedAsync(slice, probes.GetValueOrDefault(slice.ChapterFileId), ct);
                }

                continue;
            }

            var (before, after) = ReaderService.NearbyOf(ordered, i, CreditDetector.References, ReaderCreditsCache.Usable);
            List<ReaderService.ChapterSlice> references = [.. before, .. after];
            var referencesKey = CreditStore.ReferencesKey(references);
            if (current && row is { Skipped: false } && row.ReferencesKey == referencesKey)
            {
                await RestampAsync(row, slice, ct);
                continue;
            }

            await QuietAsync(ct);
            var found = references.Count == 0
                ? CreditPages.None
                : await credits.FindAsync(slice, references, ct, parallelism: 1);
            if (await store.GetAsync(slice, ct) is null)
            {
                // A run the cache already held answers without writing; store it so the chapter is settled.
                await store.SaveAsync(slice, referencesKey, found, CreditJudge.Ocr, [], ct);
            }

            lock (_sync)
            {
                _done++;
            }

            await Task.Delay(BetweenChapters, ct);
        }
    }

    private async Task<bool> ProbeAsync(ReaderService.ChapterSlice slice, CancellationToken ct)
    {
        await QuietAsync(ct);
        try
        {
            return await credits.ProbeAsync(slice, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "Could not probe {Path} for scanlator pages", slice.ArchivePath);
            return false;
        }
    }

    private Task RestampAsync(ChapterCredits row, ReaderService.ChapterSlice slice, CancellationToken ct) =>
        row.ChapterFileId == slice.ChapterFileId && row.FileSize == slice.ArchiveSize
            ? Task.CompletedTask
            : store.RestampAsync(row.ChapterId, slice.ChapterFileId, slice.ArchiveSize, ct);

    /// <summary>Waits while a reader is being served pages, or the scan is paused.</summary>
    private async Task QuietAsync(CancellationToken ct)
    {
        while (archives.SinceLastServed < ReadingQuiet || Paused)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private void SetWorking(string? title)
    {
        lock (_sync)
        {
            _working = title;
        }
    }
}
