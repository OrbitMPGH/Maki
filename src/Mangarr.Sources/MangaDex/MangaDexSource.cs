using System.Net.Http.Json;
using Mangarr.Core.Parsing;
using Mangarr.Core.Sources;

namespace Mangarr.Sources.MangaDex;

/// <summary>
/// MangaDex source backed by the official JSON API (api.mangadex.org).
/// Page URLs come from the at-home network and expire after ~15 minutes,
/// so GetPagesAsync must be called at download time.
/// </summary>
public class MangaDexSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-mangadex";

    public string Name => "mangadex";
    public string DisplayName => "MangaDex";
    public string BaseUrl => "https://mangadex.org";
    public SourceCapabilities Capabilities => SourceCapabilities.SupportsLanguageFilter;

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var response = await Client.GetFromJsonAsync<MdCollectionResponse<MdManga>>(
            $"manga?title={Uri.EscapeDataString(title)}&limit=10&includes[]=cover_art" +
            "&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica",
            ct);

        return response?.Data.Select(m => new SourceSeriesResult(
            m.Id,
            PickTitle(m.Attributes),
            $"{BaseUrl}/title/{m.Id}",
            CoverUrlFor(m),
            PickLocalized(m.Attributes.Description))).ToList() ?? [];
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var response = await Client.GetFromJsonAsync<MdEntityResponse<MdManga>>(
            $"manga/{sourceSeriesId}?includes[]=cover_art", ct)
            ?? throw new InvalidOperationException($"MangaDex returned no data for {sourceSeriesId}");

        var m = response.Data ?? throw new InvalidOperationException($"MangaDex manga {sourceSeriesId} not found");
        return new SourceSeriesDetail(
            m.Id,
            PickTitle(m.Attributes),
            $"{BaseUrl}/title/{m.Id}",
            CoverUrlFor(m),
            PickLocalized(m.Attributes.Description),
            m.Attributes.Status);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var language = string.IsNullOrWhiteSpace(languageFilter) ? "en" : languageFilter;
        var chapters = new List<SourceChapter>();
        var offset = 0;

        while (true)
        {
            var response = await Client.GetFromJsonAsync<MdCollectionResponse<MdChapter>>(
                $"manga/{sourceSeriesId}/feed?limit=500&offset={offset}" +
                $"&translatedLanguage[]={Uri.EscapeDataString(language)}" +
                "&order[chapter]=asc&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica",
                ct);

            if (response is null)
            {
                break;
            }

            foreach (var c in response.Data)
            {
                // Skip chapters with no pages on MangaDex: hosted off-site (externalUrl)
                // or delisted (isUnavailable, common for licensed titles).
                if (!string.IsNullOrEmpty(c.Attributes.ExternalUrl) || c.Attributes.IsUnavailable)
                {
                    continue;
                }

                var parsed = ChapterNumberParser.Parse(c.Attributes.Chapter, c.Attributes.Volume);
                chapters.Add(new SourceChapter(
                    Name,
                    sourceSeriesId,
                    c.Id,
                    c.Attributes.Chapter,
                    parsed.Number,
                    parsed.Volume,
                    c.Attributes.Title,
                    c.Attributes.TranslatedLanguage ?? language,
                    c.Attributes.PublishAt,
                    $"{BaseUrl}/chapter/{c.Id}"));
            }

            offset += response.Limit;
            if (offset >= response.Total || response.Data.Count == 0)
            {
                break;
            }
        }

        // The same chapter number often exists from multiple scanlation groups;
        // keep the first occurrence per (number, volume) so the diff stays stable.
        return chapters
            .GroupBy(c => (c.Number, c.Volume, c.Language))
            .Select(g => g.OrderBy(c => c.ReleaseDate ?? DateTime.MaxValue).First())
            .OrderBy(c => c.Number)
            .ToList();
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var response = await Client.GetFromJsonAsync<MdAtHomeResponse>(
            $"at-home/server/{chapter.SourceChapterId}", ct)
            ?? throw new InvalidOperationException($"at-home returned no data for chapter {chapter.SourceChapterId}");

        var pages = response.Chapter.Data
            .Select(file => new PageRequest($"{response.BaseUrl}/data/{response.Chapter.Hash}/{file}"))
            .ToList();

        return new ChapterPages(pages);
    }

    private static string PickTitle(MdMangaAttributes attributes)
    {
        if (attributes.Title.TryGetValue("en", out var en))
        {
            return en;
        }

        var alt = attributes.AltTitles.FirstOrDefault(t => t.ContainsKey("en"));
        if (alt != null)
        {
            return alt["en"];
        }

        return attributes.Title.Values.FirstOrDefault() ?? "Unknown";
    }

    private static string? PickLocalized(Dictionary<string, string> localized)
    {
        return localized.TryGetValue("en", out var en) ? en : localized.Values.FirstOrDefault();
    }

    private string? CoverUrlFor(MdManga manga)
    {
        var cover = manga.Relationships.FirstOrDefault(r => r.Type == "cover_art")?.Attributes?.FileName;
        return cover != null ? $"https://uploads.mangadex.org/covers/{manga.Id}/{cover}.256.jpg" : null;
    }
}
