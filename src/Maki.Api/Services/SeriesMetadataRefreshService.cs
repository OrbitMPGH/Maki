using Maki.Core.Entities;
using Maki.Core.Metadata;

namespace Maki.Api.Services;

/// <summary>
/// Re-pulls a series' metadata from the provider and applies it to the entity.
/// Shared by the daily MetadataRefreshJob (no cover) and the on-demand
/// refresh endpoint (with cover). Does not save changes.
/// </summary>
public class SeriesMetadataRefreshService(
    IEnumerable<IMetadataProvider> metadataProviders,
    CoverService coverService)
{
    /// <returns>false when the series has no provider id or the lookup returned nothing.</returns>
    public async Task<bool> RefreshAsync(Series series, bool includeCover, CancellationToken ct = default)
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

        series.Status = metadata.Status;
        series.Overview = metadata.Description ?? series.Overview;
        series.Genres = [.. metadata.Genres];
        series.Tags = [.. metadata.Tags];
        series.TotalChapters = metadata.TotalChapters ?? series.TotalChapters;
        series.TotalVolumes = metadata.TotalVolumes ?? series.TotalVolumes;
        series.AuthorStory = metadata.AuthorStory ?? series.AuthorStory;
        series.AuthorArt = metadata.AuthorArt ?? series.AuthorArt;
        series.HasAnime = metadata.HasAnime;
        series.AnimeName = metadata.AnimeName ?? series.AnimeName;
        series.AnimeStart = metadata.AnimeStart ?? series.AnimeStart;
        series.AnimeEnd = metadata.AnimeEnd ?? series.AnimeEnd;
        series.MalId = metadata.MalId ?? series.MalId;
        series.AniListId = metadata.AniListId ?? series.AniListId;
        series.KitsuId = metadata.KitsuId ?? series.KitsuId;
        series.MangaBakaId = metadata.MangaBakaId ?? series.MangaBakaId;
        series.LastMetadataRefresh = DateTime.UtcNow;

        if (includeCover && metadata.CoverUrl != null)
        {
            var coverPath = await coverService.DownloadCoverAsync(series.Id, metadata.CoverUrl, ct);
            if (coverPath != null)
            {
                series.CoverPath = coverPath;
            }
        }

        return true;
    }
}
