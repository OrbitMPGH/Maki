namespace Mangarr.Core.Entities;

/// <summary>A labeled external link for a series. <see cref="Site"/> is a stable lowercase key.</summary>
public record MetadataLink(string Site, string Url);

/// <summary>
/// External site links for a series, built from its cross-provider ids. Used for
/// the ComicInfo.xml Web field (comma-separated, Kavita-compatible), the Kavita
/// metadata push, and the clickable link buttons in the UI.
/// </summary>
public static class SeriesWebLinks
{
    /// <summary>Labeled links (site key + url) for every cross-id the series has.</summary>
    public static List<MetadataLink> Labeled(Series series)
    {
        List<MetadataLink> links = [];
        if (series.MangaBakaId is int mangaBaka)
        {
            links.Add(new("mangabaka", $"https://mangabaka.org/{mangaBaka}"));
        }

        if (series.AniListId is int aniList)
        {
            links.Add(new("anilist", $"https://anilist.co/manga/{aniList}"));
        }

        if (series.MalId is int mal)
        {
            links.Add(new("myanimelist", $"https://myanimelist.net/manga/{mal}"));
        }

        if (!string.IsNullOrWhiteSpace(series.MangaUpdatesId))
        {
            links.Add(new("mangaupdates", $"https://www.mangaupdates.com/series/{series.MangaUpdatesId}"));
        }

        if (!string.IsNullOrWhiteSpace(series.MangaDexUuid))
        {
            links.Add(new("mangadex", $"https://mangadex.org/title/{series.MangaDexUuid}"));
        }

        return links;
    }

    public static List<string> For(Series series) =>
        Labeled(series).Select(l => l.Url).ToList();

    /// <summary>Comma-separated form for ComicInfo Web / Kavita webLinks; null when there are none.</summary>
    public static string? Joined(Series series) =>
        For(series) is { Count: > 0 } links ? string.Join(",", links) : null;
}
