using System.Collections.Concurrent;
using Mangarr.Core.Configuration;
using Mangarr.Core.Entities;
using Mangarr.Core.Kavita;
using Mangarr.Data;

namespace Mangarr.Api.Services;

/// <summary>
/// Debounced Kavita notifications. Callers queue a series folder whenever files
/// land or change; a background loop flushes the distinct folders to Kavita's
/// scan-folder endpoint (so a 100-chapter batch triggers one scan per flush, not
/// 100). After each scan request it also pushes series metadata Kavita can't get
/// from ComicInfo.xml — the MangaBaka poster as the series cover, web links, and
/// publication status — retrying until Kavita's delayed scan has actually created
/// the series. No-op (pending work is dropped) while Kavita isn't configured.
/// </summary>
public class KavitaScanService(
    KavitaClient kavita,
    IAppSettings settings,
    IServiceScopeFactory scopeFactory,
    ILogger<KavitaScanService> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);
    /// <summary>Kavita runs scan-folder scans ~1 minute after they're requested.</summary>
    private static readonly TimeSpan PushDelay = TimeSpan.FromSeconds(60);
    private const int MaxPushAttempts = 8;

    private sealed record PushWork(string SeriesFolder, DateTime NotBefore, int AttemptsLeft);

    private readonly ConcurrentDictionary<string, int> _pendingScans = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, PushWork> _pendingPushes = new();

    /// <summary>Queue a series folder (Mangarr-side absolute path) for a Kavita scan + metadata push on the next flush.</summary>
    public void QueueScan(string seriesFolderPath, int seriesId) => _pendingScans[seriesFolderPath] = seriesId;

    /// <summary>
    /// Queue only the metadata/cover push (no scan) — for when Mangarr-side
    /// metadata changed but the files didn't.
    /// </summary>
    public void QueuePush(string seriesFolderPath, int seriesId) =>
        _pendingPushes[seriesId] = new PushWork(seriesFolderPath, DateTime.UtcNow, MaxPushAttempts);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (_pendingScans.IsEmpty && _pendingPushes.IsEmpty)
            {
                continue;
            }

            try
            {
                await FlushAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Kavita notification flush failed");
            }
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        var url = await settings.GetAsync(SettingKeys.KavitaUrl, ct);
        var apiKey = await settings.GetAsync(SettingKeys.KavitaApiKey, ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
        {
            _pendingScans.Clear();
            _pendingPushes.Clear();
            return;
        }

        var mapFrom = await settings.GetAsync(SettingKeys.KavitaPathMapFrom, ct);
        var mapTo = await settings.GetAsync(SettingKeys.KavitaPathMapTo, ct);

        foreach (var (folder, seriesId) in _pendingScans.ToList())
        {
            _pendingScans.TryRemove(folder, out _);
            var mapped = KavitaPathMapper.Map(folder, mapFrom, mapTo);
            try
            {
                await kavita.ScanFolderAsync(url, apiKey, mapped, ct);
                logger.LogInformation("Asked Kavita to scan {Folder}", mapped);
                _pendingPushes[seriesId] = new PushWork(folder, DateTime.UtcNow + PushDelay, MaxPushAttempts);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Kavita scan-folder failed for {Folder}", mapped);
            }
        }

        foreach (var (seriesId, work) in _pendingPushes.ToList())
        {
            if (work.NotBefore > DateTime.UtcNow)
            {
                continue;
            }

            bool done;
            try
            {
                done = await PushSeriesAsync(seriesId, work.SeriesFolder, url, apiKey, mapFrom, mapTo, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Kavita metadata push failed for series {SeriesId}", seriesId);
                done = true; // permanent error — don't retry into the same failure every tick
            }

            if (done || work.AttemptsLeft <= 1)
            {
                if (!done)
                {
                    logger.LogInformation(
                        "Series {SeriesId} never appeared in Kavita — skipping metadata push (folder outside its libraries?)",
                        seriesId);
                }

                _pendingPushes.TryRemove(seriesId, out _);
            }
            else
            {
                _pendingPushes[seriesId] = work with { AttemptsLeft = work.AttemptsLeft - 1 };
            }
        }
    }

    /// <summary>
    /// Pushes cover + metadata for one series. Returns false when the series
    /// isn't in Kavita yet (its scan may still be pending) so the caller retries.
    /// </summary>
    private async Task<bool> PushSeriesAsync(
        int seriesId, string folder, string url, string apiKey, string? mapFrom, string? mapTo, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
        var series = await db.Series.FindAsync([seriesId], ct);
        if (series is null)
        {
            return true; // deleted in the meantime
        }

        var mapped = KavitaPathMapper.Map(folder, mapFrom, mapTo);
        var kavitaSeries = await kavita.FindSeriesAsync(url, apiKey, [series.Title, series.FolderName], mapped, ct);
        if (kavitaSeries is null)
        {
            return false;
        }

        // Cover: upload the MangaBaka poster unless a cover is already locked —
        // either set by the user (respect it) or by an earlier push (no-op anyway).
        if (series.CoverPath is { } coverPath && File.Exists(coverPath) && !kavitaSeries.CoverImageLocked)
        {
            await kavita.UploadSeriesCoverAsync(url, apiKey, kavitaSeries.Id,
                await File.ReadAllBytesAsync(coverPath, ct), ct);
            logger.LogInformation("Pushed cover to Kavita for {Series}", series.Title);
        }

        var metadata = await kavita.GetSeriesMetadataAsync(url, apiKey, kavitaSeries.Id, ct);
        if (metadata is null)
        {
            return true;
        }

        if (SeriesWebLinks.Joined(series) is { } webLinks)
        {
            metadata["webLinks"] = webLinks;
        }

        if (MapPublicationStatus(series.Status) is { } status)
        {
            metadata["publicationStatus"] = status.Value;
            // Lock only what ComicInfo can't express (scans would reset Hiatus /
            // Cancelled to Ongoing). Ongoing/Ended stay unlocked so Kavita can
            // upgrade Ended → Completed itself once every chapter is present.
            metadata["publicationStatusLocked"] = status.Lock;
        }

        if (series.Year is > 0)
        {
            metadata["releaseYear"] = series.Year;
            metadata["releaseYearLocked"] = true;
        }

        await kavita.UpdateSeriesMetadataAsync(url, apiKey, metadata, ct);
        logger.LogInformation("Pushed metadata to Kavita for {Series} (Kavita series {Id})",
            series.Title, kavitaSeries.Id);
        return true;
    }

    /// <summary>Kavita PublicationStatus: OnGoing=0, Hiatus=1, Completed=2, Cancelled=3, Ended=4.</summary>
    private static (int Value, bool Lock)? MapPublicationStatus(SeriesStatus status) => status switch
    {
        SeriesStatus.Ongoing => (0, false),
        SeriesStatus.Hiatus => (1, true),
        SeriesStatus.Cancelled => (3, true),
        SeriesStatus.Completed => (4, false),
        _ => null
    };
}
