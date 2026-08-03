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
    StatsEventService stats,
    IAppSettings appSettings,
    ILogger<SeriesCreationService> logger)
{
    public async Task<SeriesCreationResult> CreateAsync(
        string metadataProviderId,
        int rootFolderId,
        bool monitored,
        string monitorNewItems,
        CancellationToken ct)
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

        var series = new Series
        {
            Title = metadata.Title,
            SortTitle = SortTitleFor(metadata.Title),
            OriginalTitle = metadata.OriginalTitle,
            Status = metadata.Status,
            Overview = metadata.Description,
            Year = metadata.Year,
            Genres = [.. metadata.Genres],
            Tags = [.. metadata.Tags],
            MangaBakaId = metadata.MangaBakaId,
            AniListId = metadata.AniListId,
            MalId = metadata.MalId,
            KitsuId = metadata.KitsuId,
            MangaUpdatesId = metadata.MangaUpdatesId,
            MangaDexUuid = metadata.MangaDexUuid,
            // Monitoring is only the mode now, so an unmonitored add is simply mode None —
            // there's no separate flag left for it to contradict.
            MonitorNewItems = await DefaultedMonitorMode(
                !monitored
                    ? NewChapterMonitorMode.None
                    : Enum.TryParse<NewChapterMonitorMode>(monitorNewItems, true, out var mode)
                        ? mode
                        : NewChapterMonitorMode.All, ct),
            RootFolderId = rootFolder.Id,
            FolderName = FileNameSanitizer.Sanitize(metadata.Title),
            TotalChapters = metadata.TotalChapters,
            TotalVolumes = metadata.TotalVolumes,
            AuthorStory = metadata.AuthorStory,
            AuthorArt = metadata.AuthorArt,
            HasAnime = metadata.HasAnime,
            AnimeName = metadata.AnimeName,
            AnimeStart = metadata.AnimeStart,
            AnimeEnd = metadata.AnimeEnd,
            Added = DateTime.UtcNow,
            LastMetadataRefresh = DateTime.UtcNow
        };

        db.Series.Add(series);
        await db.SaveChangesAsync(ct);
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

        // Link site sources by title match, then pull the initial chapter list.
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

        return new SeriesCreationResult(series, null, warnings);
    }

    private async Task<NewChapterMonitorMode> DefaultedMonitorMode(NewChapterMonitorMode requested, CancellationToken ct) =>
        requested == NewChapterMonitorMode.All &&
        await appSettings.GetAsync(SettingKeys.MonitoringUnmonitorSpecials, ct) == "true"
            ? NewChapterMonitorMode.MainOnly
            : requested;

    private static string SortTitleFor(string title)
    {
        var lowered = title.ToLowerInvariant();
        foreach (var article in (string[])["the ", "a ", "an "])
        {
            if (lowered.StartsWith(article, StringComparison.Ordinal))
            {
                return lowered[article.Length..];
            }
        }

        return lowered;
    }
}
