using Maki.Core.Entities;

namespace Maki.Core.Metadata;

/// <summary>
/// Builds the metadata-derived half of a fresh <see cref="Series"/> row. Shared by manual add
/// (<c>SeriesCreationService</c>) and folder import (<c>LibraryImportService</c>) so the fields
/// pulled from a provider can't drift between the two creation paths — that drift is how
/// <see cref="Series.Type"/> went unset on imported series while manually-added ones got it fine.
/// <para>
/// Callers still set folder/root/monitor fields themselves (<see cref="Series.RootFolderId"/>,
/// <see cref="Series.FolderName"/>, <see cref="Series.MonitorNewItems"/>,
/// <see cref="Series.SourceMatchPending"/>) — those come from the request, not the provider.
/// </para>
/// </summary>
public static class SeriesMetadataMapper
{
    public static Series NewFromMetadata(SeriesMetadata metadata)
    {
        var now = DateTime.UtcNow;
        return new Series
        {
            Title = metadata.Title,
            SortTitle = SortTitleFor(metadata.Title),
            OriginalTitle = metadata.OriginalTitle,
            AltTitles = [.. metadata.AltTitles],
            Status = metadata.Status,
            Type = metadata.Type,
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
            TotalChapters = metadata.TotalChapters,
            TotalVolumes = metadata.TotalVolumes,
            AuthorStory = metadata.AuthorStory,
            AuthorArt = metadata.AuthorArt,
            HasAnime = metadata.HasAnime,
            AnimeName = metadata.AnimeName,
            AnimeStart = metadata.AnimeStart,
            AnimeEnd = metadata.AnimeEnd,
            Added = now,
            LastMetadataRefresh = now,
        };
    }

    /// <summary>Lowercased title with a leading english article dropped, so "The ..." sorts under its real letter.</summary>
    public static string SortTitleFor(string title)
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
