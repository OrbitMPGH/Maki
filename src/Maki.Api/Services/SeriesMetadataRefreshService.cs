using Maki.Core.Entities;
using Maki.Core.Metadata;

namespace Maki.Api.Services;

/// <summary>
/// Re-pulls a series' metadata from the provider and applies it to the entity.
/// Shared by the daily MetadataRefreshJob (no cover) and the on-demand
/// refresh endpoint (with cover). Does not save changes.
/// <para>
/// With a <see cref="SeriesMetadataChangeLog"/>, changes to the title, status, totals, genres, synopsis and poster are added
/// to the change history on the same context; the caller saves, then calls
/// <see cref="PublishChangesAsync"/> to announce them.
/// </para>
/// </summary>
public class SeriesMetadataRefreshService(
    IEnumerable<IMetadataProvider> metadataProviders,
    CoverService coverService,
    SeriesMetadataChangeLog? changeLog = null)
{
    /// <summary>Announces the status changes the last refreshes recorded. Call after saving them.</summary>
    public Task PublishChangesAsync(CancellationToken ct = default) =>
        changeLog?.PublishAsync(ct) ?? Task.CompletedTask;

    /// <summary>
    /// Re-downloads only the poster, leaving every metadata field alone. The image-cache rebuild
    /// uses this rather than <see cref="RefreshAsync"/> with <c>includeCover: true</c>: rebuilding
    /// artwork should not quietly rewrite overviews, genres and titles across the whole library,
    /// which is what a full refresh over every series would do.
    /// </summary>
    /// <returns>false when the series has no provider id, carries a user-set cover, or the lookup returned no cover.</returns>
    public async Task<bool> RefreshCoverAsync(Series series, CancellationToken ct = default)
    {
        if (series.MangaBakaId is null || series.IsLocked(SeriesMetadataField.Cover))
        {
            return false;
        }

        var metadata = await metadataProviders.First().GetAsync(series.MangaBakaId.Value.ToString(), ct);
        if (metadata?.CoverUrl is null)
        {
            return false;
        }

        var coverPath = await coverService.DownloadCoverAsync(series.Id, metadata.CoverUrl, ct);
        if (coverPath is null)
        {
            return false;
        }

        series.CoverPath = coverPath;
        await coverService.WriteLibraryCoverAsync(series, ct);
        series.LastMetadataRefresh = DateTime.UtcNow;
        return true;
    }

    /// <param name="Refreshed">Whether the provider answered and its fields were applied.</param>
    /// <param name="CoverReplaced">
    /// Whether the poster was replaced by the provider's. Only meaningful when a cover was requested
    /// and the cover is not locked: false then means the provider had no poster or it did not download.
    /// </param>
    public readonly record struct RefreshResult(bool Refreshed, bool CoverReplaced);

    /// <returns>false when the series has no provider id or the lookup returned nothing.</returns>
    public Task<bool> RefreshAsync(Series series, bool includeCover, CancellationToken ct = default) =>
        RefreshAsync(series, includeCover, SeriesMetadataField.None, ct);

    /// <param name="restore">
    /// Fields just reset to the provider. These take the provider's value even when it is empty,
    /// where an ordinary refresh keeps the current value, so a reset never leaves the user's text behind.
    /// </param>
    public async Task<bool> RefreshAsync(
        Series series, bool includeCover, SeriesMetadataField restore, CancellationToken ct = default) =>
        (await RefreshWithResultAsync(series, includeCover, restore, ct)).Refreshed;

    /// <summary>As <see cref="RefreshAsync(Series, bool, SeriesMetadataField, CancellationToken)"/>, also saying whether the poster was replaced.</summary>
    public async Task<RefreshResult> RefreshWithResultAsync(
        Series series, bool includeCover, SeriesMetadataField restore, CancellationToken ct = default)
    {
        var metadata = await LookupAsync(series, ct);
        return metadata is null
            ? new RefreshResult(false, false)
            : await ApplyAsync(series, metadata, includeCover, restore, ct);
    }

    /// <summary>
    /// The provider's answer for a series, or null when it has no provider id or the lookup returned
    /// nothing. Split from <see cref="ApplyAsync"/> so a caller that holds the series across this slow
    /// call can reload it first and apply against what is stored now.
    /// </summary>
    public async Task<SeriesMetadata?> LookupAsync(Series series, CancellationToken ct = default) =>
        series.MangaBakaId is null
            ? null
            : await metadataProviders.First().GetAsync(series.MangaBakaId.Value.ToString(), ct);

    /// <summary>Applies <paramref name="metadata"/> to the fields the series has not locked. Does not save.</summary>
    /// <param name="reloadSeries">
    /// Runs after the poster download, the slow step, and before any field is written. A caller that
    /// holds the series across the lookup passes a reload here, so a hand edit and lock saved meanwhile
    /// are seen before the provider's values go on top of them.
    /// </param>
    public async Task<RefreshResult> ApplyAsync(
        Series series, SeriesMetadata metadata, bool includeCover, SeriesMetadataField restore,
        CancellationToken ct = default, Func<Task>? reloadSeries = null)
    {
        bool Open(SeriesMetadataField field) => !series.IsLocked(field);
        bool Restoring(SeriesMetadataField field) => (restore & field) == field;

        string? coverPath = null;
        var stamp = default(DateTime);
        if (includeCover && Open(SeriesMetadataField.Cover) && metadata.CoverUrl != null)
        {
            stamp = File.GetLastWriteTimeUtc(coverService.CoverPathFor(series.Id));
            coverPath = await coverService.DownloadCoverAsync(series.Id, metadata.CoverUrl, ct);
        }

        if (reloadSeries is not null)
        {
            await reloadSeries();
        }

        var before = SeriesMetadataChangeLog.Snapshot.Of(series);

        if (Open(SeriesMetadataField.Status))
        {
            series.Status = metadata.Status;
        }

        // Not ??-coalesced: a provider that stops reporting a type should clear it rather than
        // pin a reading profile onto a series it no longer classifies.
        series.Type = metadata.Type;
        if (Open(SeriesMetadataField.Overview))
        {
            series.Overview = Restoring(SeriesMetadataField.Overview)
                ? metadata.Description
                : metadata.Description ?? series.Overview;
        }

        if (Open(SeriesMetadataField.Genres))
        {
            series.Genres = [.. metadata.Genres];
        }

        series.Tags = [.. metadata.Tags];
        series.ContentRating = metadata.ContentRating ?? series.ContentRating;
        series.AltTitles = [.. metadata.AltTitles];

        series.Year = metadata.Year ?? series.Year;
        if (Open(SeriesMetadataField.TotalChapters))
        {
            series.TotalChapters = Restoring(SeriesMetadataField.TotalChapters)
                ? metadata.TotalChapters
                : metadata.TotalChapters ?? series.TotalChapters;
        }

        if (Open(SeriesMetadataField.TotalVolumes))
        {
            series.TotalVolumes = Restoring(SeriesMetadataField.TotalVolumes)
                ? metadata.TotalVolumes
                : metadata.TotalVolumes ?? series.TotalVolumes;
        }

        series.AuthorStory = metadata.AuthorStory ?? series.AuthorStory;
        series.AuthorArt = metadata.AuthorArt ?? series.AuthorArt;
        series.Publisher = metadata.Publisher ?? series.Publisher;
        series.HasAnime = metadata.HasAnime;

        series.AnimeName = metadata.AnimeName ?? series.AnimeName;
        series.AnimeStart = metadata.AnimeStart ?? series.AnimeStart;
        series.AnimeEnd = metadata.AnimeEnd ?? series.AnimeEnd;
        series.MalId = metadata.MalId ?? series.MalId;
        series.AniListId = metadata.AniListId ?? series.AniListId;
        series.KitsuId = metadata.KitsuId ?? series.KitsuId;
        series.MangaBakaId = metadata.MangaBakaId ?? series.MangaBakaId;
        series.LastMetadataRefresh = DateTime.UtcNow;
        if (Open(SeriesMetadataField.Title))
        {
            series.Title = metadata.Title;
            series.SortTitle = SeriesMetadataMapper.SortTitleFor(metadata.Title);
        }

        series.OriginalTitle = metadata.OriginalTitle ?? series.OriginalTitle;
        changeLog?.RecordRefresh(series, before);

        var coverReplaced = false;
        if (coverPath != null && Open(SeriesMetadataField.Cover))
        {
            series.CoverPath = coverPath;
            await coverService.WriteLibraryCoverAsync(series, ct);
            coverReplaced = true;
            if (File.GetLastWriteTimeUtc(coverPath) != stamp)
            {
                changeLog?.RecordReplaced(series, SeriesMetadataField.Cover, null, MetadataChangeSource.Refresh);
            }
        }

        return new RefreshResult(true, coverReplaced);
    }
}
