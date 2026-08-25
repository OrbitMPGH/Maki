using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Core.Naming;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Why adding a series failed, when it failed before the row was written. Once the row exists the
/// remaining steps degrade into <see cref="SeriesCreationResult.Warnings"/> instead — a series with
/// no folder on disk looks fine until a download lands, so it has to be said out loud, but it is not
/// worth throwing away a committed row over.
/// </summary>
public enum SeriesCreationError
{
    RootFolderNotFound,
    MetadataNotFound,
    AlreadyInLibrary,
}

public record SeriesCreationResult(Series? Series, SeriesCreationError? Error, List<string> Warnings)
{
    public static SeriesCreationResult Failed(SeriesCreationError error) => new(null, error, []);
}

/// <summary>
/// Creates a library series from a metadata provider id: the row, its folder, its cover, its source
/// mappings and the first chapter sync.
/// <para>
/// Extracted from <c>SeriesController.Add</c> because approving a
/// <see cref="SeriesRequestKind.NewSeries"/> request has to do exactly the same thing. Two copies
/// would drift, and the half that drifted would be the one nobody with an admin account exercises.
/// </para>
/// </summary>
public class SeriesCreationService(
    MakiDbContext db,
    IEnumerable<IMetadataProvider> metadataProviders,
    CoverService coverService,
    SourceMatchService sourceMatchService,
    ChapterSyncService chapterSyncService,
    SourceMatchQueue sourceMatchQueue,
    StatsEventService stats,
    SeriesIdentityService identity,
    IAppSettings appSettings,
    ILogger<SeriesCreationService> logger)
{
    /// <param name="deferSourceMatching">
    /// Hand auto-matching to <see cref="SourceMatchWorkerHostedService"/> instead of awaiting it.
    /// Searching every source plus the first chapter sync is tens of seconds, which is the whole
    /// cost of the Add button, so the interactive path defers and the series page renders the wait.
    /// Callers that need the chapter list to exist by the time this returns — approving a series
    /// request queues a chapter range straight afterwards — must leave it false.
    /// </param>
    /// <param name="incognito">
    /// An explicit <see cref="IncognitoMode"/> name from the caller, which always wins. Null means
    /// "decide from the content rating" — see <see cref="IncognitoRatingRules"/>, which is also what
    /// an API client that never heard of the field gets.
    /// </param>
    public async Task<SeriesCreationResult> CreateAsync(
        string metadataProviderId,
        int rootFolderId,
        bool monitored,
        string monitorNewItems,
        CancellationToken ct,
        bool deferSourceMatching = false,
        string? incognito = null)
    {
        var rootFolder = await db.RootFolders.FindAsync([rootFolderId], ct);
        if (rootFolder is null)
        {
            return SeriesCreationResult.Failed(SeriesCreationError.RootFolderNotFound);
        }

        var provider = metadataProviders.First();
        var metadata = await provider.GetAsync(metadataProviderId, ct);
        if (metadata is null)
        {
            return SeriesCreationResult.Failed(SeriesCreationError.MetadataNotFound);
        }

        if (metadata.MangaBakaId is int existingId &&
            await db.Series.AnyAsync(s => s.MangaBakaId == existingId, ct))
        {
            return SeriesCreationResult.Failed(SeriesCreationError.AlreadyInLibrary);
        }

        var series = SeriesMetadataMapper.NewFromMetadata(metadata);
        // Monitoring is only the mode now, so an unmonitored add is simply mode None —
        // there's no separate flag left for it to contradict.
        series.MonitorNewItems = await DefaultedMonitorMode(
            !monitored
                ? NewChapterMonitorMode.None
                : Enum.TryParse<NewChapterMonitorMode>(monitorNewItems, true, out var mode)
                    ? mode
                    : NewChapterMonitorMode.All, ct);
        // An explicit choice from the add form wins, including an explicit "Off" over a rule that
        // would have hidden it. Only an absent value consults the per-rating rules.
        series.Incognito = Enum.TryParse<IncognitoMode>(incognito, true, out var explicitMode)
            ? explicitMode
            : IncognitoRatingRules.Resolve(
                IncognitoRatingRules.Parse(
                    await appSettings.GetAsync(SettingKeys.LibraryIncognitoByRating, ct)),
                series.ContentRating);
        series.RootFolderId = rootFolder.Id;
        series.FolderName = FileNameSanitizer.Sanitize(metadata.Title);
        series.SourceMatchPending = deferSourceMatching;

        db.Series.Add(series);
        await db.SaveChangesAsync(ct);

        // Before the add event, so a series removed and put back reads as one continuous history
        // rather than a fresh one starting today.
        await identity.AdoptOrphansAsync(series, ct);
        await stats.RecordAsync(StatsEventType.SeriesAdded, series.Id, series.Title, ct: ct);

        // The series row is already committed, so these steps can't fail the request — but they
        // can't be swallowed either. Collect what went wrong and hand it back with the result.
        var warnings = new List<string>();

        var seriesFolder = Path.Combine(rootFolder.Path, series.FolderName);
        try
        {
            Directory.CreateDirectory(seriesFolder);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create series folder for {Title}", series.Title);
            warnings.Add($"Could not create the series folder ({seriesFolder}): {ex.Message}");
        }

        if (metadata.CoverUrl != null)
        {
            var coverPath = await coverService.DownloadCoverAsync(series.Id, metadata.CoverUrl, ct);
            if (coverPath != null)
            {
                series.CoverPath = coverPath;
                await db.SaveChangesAsync(ct);
            }
        }

        // Link site sources by title match, then pull the initial chapter list. Enqueued rather
        // than awaited when the caller can live without the chapter list being there on return:
        // the flag is already committed on the row above, so the worker picks it up even if it
        // only starts running after a restart.
        if (deferSourceMatching)
        {
            sourceMatchQueue.Enqueue(series.Id);
        }
        else
        {
            try
            {
                var mapped = await sourceMatchService.AutoMatchAsync(series, ct);
                if (mapped.Count > 0)
                {
                    await chapterSyncService.SyncSeriesAsync(series.Id, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto source matching failed for {Title}", series.Title);
                warnings.Add($"Could not match sources automatically: {ex.Message}. Link a source manually from the series page.");
            }
        }

        return new SeriesCreationResult(series, null, warnings);
    }

    private async Task<NewChapterMonitorMode> DefaultedMonitorMode(NewChapterMonitorMode requested, CancellationToken ct) =>
        requested == NewChapterMonitorMode.All &&
        await appSettings.GetAsync(SettingKeys.MonitoringUnmonitorSpecials, ct) == "true"
            ? NewChapterMonitorMode.MainOnly
            : requested;
}
