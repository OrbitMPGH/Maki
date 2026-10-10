using Maki.Core.Entities;
using Maki.Core.Metadata;

namespace Maki.Api.Services;

/// <summary>
/// Re-pulls a series' metadata from the provider and applies it to the entity.
/// Shared by the daily MetadataRefreshJob (no cover) and the on-demand
/// refresh endpoint (with cover). Does not save changes.
/// <para>
/// With a <see cref="SeriesMetadataChangeLog"/>, changes to the title, status and totals are added
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

    /// <returns>false when the series has no provider id or the lookup returned nothing.</returns>
    public Task<bool> RefreshAsync(Series series, bool includeCover, CancellationToken ct = default) =>
        RefreshAsync(series, includeCover, SeriesMetadataField.None, ct);

    /// <param name="restore">
    /// Fields just reset to the provider. These take the provider's value even when it is empty,
    /// where an ordinary refresh keeps the current value, so a reset never leaves the user's text behind.
    /// </param>
    public async Task<bool> RefreshAsync(
        Series series, bool includeCover, SeriesMetadataField restore, CancellationToken ct = default)
    {
        if (series.MangaBakaId is null)
        {
            return false;
        }

        var provider = metadataProviders.First();
        var metadata = await provider.GetAsync(series.MangaBakaId.Value.ToString(), ct);
        if (metadata is null)
        {
            return false;
        }

        var before = SeriesMetadataChangeLog.Snapshot.Of(series);
        bool Open(SeriesMetadataField field) => !series.IsLocked(field);
        bool Restoring(SeriesMetadataField field) => (restore & field) == field;

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

        // A partial result's tags are unfiltered for spoilers, so they only fill an empty list.
        if (!metadata.Partial || series.Tags.Count == 0)
        {
            series.Tags = [.. metadata.Tags];
        }

        series.ContentRating = metadata.ContentRating ?? series.ContentRating;
        if (!metadata.Partial || metadata.AltTitles.Count > 0)
        {
            series.AltTitles = [.. metadata.AltTitles];
        }

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
        if (!metadata.Partial)
        {
            series.HasAnime = metadata.HasAnime;
        }

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

        if (includeCover && Open(SeriesMetadataField.Cover) && metadata.CoverUrl != null)
        {
            var coverPath = await coverService.DownloadCoverAsync(series.Id, metadata.CoverUrl, ct);
            if (coverPath != null)
            {
                series.CoverPath = coverPath;
                await coverService.WriteLibraryCoverAsync(series, ct);
            }
        }

        return true;
    }
}
