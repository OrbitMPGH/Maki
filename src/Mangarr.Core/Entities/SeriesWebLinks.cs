namespace Mangarr.Core.Entities;

/// <summary>
/// External site links for a series, built from its cross-provider ids. Used for
/// the ComicInfo.xml Web field (comma-separated, Kavita-compatible) and for the
/// Kavita metadata push.
/// </summary>
public static class SeriesWebLinks
{
    public static List<string> For(Series series)
    {
        List<string> links = [];
        if (series.MangaBakaId is int mangaBaka)
        {
            links.Add($"https://mangabaka.org/{mangaBaka}");
        }

        if (series.AniListId is int aniList)
        {
            links.Add($"https://anilist.co/manga/{aniList}");
        }

        if (series.MalId is int mal)
        {
            links.Add($"https://myanimelist.net/manga/{mal}");
        }

        if (!string.IsNullOrWhiteSpace(series.MangaUpdatesId))
        {
            links.Add($"https://www.mangaupdates.com/series/{series.MangaUpdatesId}");
        }

        if (!string.IsNullOrWhiteSpace(series.MangaDexUuid))
        {
            links.Add($"https://mangadex.org/title/{series.MangaDexUuid}");
        }

        return links;
    }

    /// <summary>Comma-separated form for ComicInfo Web / Kavita webLinks; null when there are none.</summary>
    public static string? Joined(Series series) =>
        For(series) is { Count: > 0 } links ? string.Join(",", links) : null;
}
