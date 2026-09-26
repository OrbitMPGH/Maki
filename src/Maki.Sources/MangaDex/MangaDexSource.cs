using System.Net.Http.Json;
using System.Text.Json;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.MangaDex;

/// <summary>
/// MangaDex source backed by the official JSON API (api.mangadex.org).
/// Page URLs come from the at-home network and expire after ~15 minutes,
/// so GetPagesAsync must be called at download time.
/// </summary>
public class MangaDexSource(IHttpClientFactory httpClientFactory) : ISource, IChapterVolumeSource
{
    public const string HttpClientName = "source-mangadex";

    public string Name => "mangadex";
    public string DisplayName => "MangaDex";
    public string BaseUrl => "https://mangadex.org";
    public SourceCapabilities Capabilities => SourceCapabilities.SupportsLanguageFilter;
    public SourceContent Content =>
        SourceContent.Manga | SourceContent.Manhwa | SourceContent.Manhua | SourceContent.Doujinshi;
    // Every scanlation group posts in whatever language it works in; there's no fixed catalogue,
    // so this stands in for "essentially all of them" against Maki's own 14-language UI set.
    public IReadOnlyList<string> SupportedLanguages => Core.Localization.SupportedLanguages.All;

    /// <summary>
    /// A stored <c>LanguageFilter</c> code as MangaDex spells it.
    /// <para>
    /// The filter is seeded from Maki's own UI locales (<c>SourceLanguagePreference.SeedFilter</c>)
    /// and MangaDex tags chapters with plain ISO 639-1, so the two agree on thirteen of the fourteen
    /// by luck rather than by design. Simplified Chinese is the exception: Maki writes
    /// <c>zh-Hans</c>, MangaDex files it under <c>zh</c> (<c>zh-hk</c> being the traditional one),
    /// and a feed asked for <c>zh-hans</c> matches nothing at all, so the mapping listed zero
    /// chapters while every screen reported it enabled and matched.
    /// </para>
    /// <para>
    /// Anything not named here is passed through as it was stored. An unknown code is answered with
    /// an empty feed rather than an error either way, and inventing a translation for a code no
    /// picker can produce would only hide the next mismatch.
    /// </para>
    /// </summary>
    private static string ToMangaDex(string code) => code switch
    {
        "zh-hans" => "zh",
        _ => code,
    };

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        // https://mangadex.org/title/{uuid}[/{slug}]
        var id = SourceUrl.PathTail(url, BaseUrl, "/title/", firstSegmentOnly: true);
        return id != null && Guid.TryParse(id, out _) ? id : null;
    }

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
            PickLocalized(m.Attributes.Description),
            ExternalIdsFor(m))).ToList() ?? [];
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
        var languages = SourceLanguages.Parse(languageFilter).Select(ToMangaDex).Distinct().ToList();
        // translatedLanguage[] is a repeated parameter, so several languages cost one request per
        // page rather than one pass per language — and the feed stays ordered by chapter across all
        // of them, which is what SourceChapterList.Normalize's per-(Number, Volume, Language) group
        // expects.
        var languageQuery = string.Concat(
            languages.Select(l => $"&translatedLanguage[]={Uri.EscapeDataString(l)}"));
        var chapters = new List<SourceChapter>();
        var offset = 0;

        while (true)
        {
            var response = await Client.GetFromJsonAsync<MdCollectionResponse<MdChapter>>(
                $"manga/{sourceSeriesId}/feed?limit=500&offset={offset}" +
                languageQuery +
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
                    c.Attributes.TranslatedLanguage ?? languages[0],
                    c.Attributes.PublishAt,
                    $"{BaseUrl}/chapter/{c.Id}"));
            }

            offset += response.Limit;
            if (offset >= response.Total || response.Data.Count == 0)
            {
                break;
            }
        }

        // The same chapter number often exists from multiple scanlation groups; keep the
        // earliest-published one so the diff stays stable across refreshes.
        return SourceChapterList.Normalize(
            chapters, g => g.OrderBy(c => c.ReleaseDate ?? DateTime.MaxValue).First());
    }

    /// <summary>
    /// Chapter number → volume map from the full feed with includeUnavailable=1: unlike
    /// the aggregate endpoint (and the default feed), this still lists delisted chapters
    /// of licensed titles, whose volume assignment is exactly what we're after. No
    /// language filter — volume boundaries are language-independent, and the EN feed
    /// of a licensed title is empty.
    /// </summary>
    public async Task<IReadOnlyDictionary<decimal, int>> GetChapterVolumesAsync(
        string sourceSeriesId, CancellationToken ct = default)
    {
        var map = new Dictionary<decimal, int>();
        var offset = 0;

        while (true)
        {
            var response = await Client.GetFromJsonAsync<MdCollectionResponse<MdChapter>>(
                $"manga/{sourceSeriesId}/feed?limit=500&offset={offset}&includeUnavailable=1" +
                "&order[chapter]=asc&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica",
                ct);

            if (response is null)
            {
                break;
            }

            foreach (var c in response.Data)
            {
                var parsed = ChapterNumberParser.Parse(c.Attributes.Chapter, c.Attributes.Volume);
                if (parsed is { Number: { } number, Volume: { } volume })
                {
                    map.TryAdd(number, volume);
                }
            }

            offset += response.Limit;
            if (offset >= response.Total || response.Data.Count == 0)
            {
                break;
            }
        }

        return map;
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

    /// <summary>
    /// Tracker ids for a search hit, taken from the <c>links</c> the API already returns with every
    /// manga object — so MangaDex needs no <see cref="ISource.GetExternalIdsAsync"/> override and its
    /// ids cost nothing on top of the search. The manga's own uuid is included under
    /// <see cref="ExternalIdService.MangaDex"/> so a series whose metadata already names a MangaDex
    /// title matches on that alone.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ExternalIdsFor(MdManga manga)
    {
        var ids = SourceExternalIds.From((ExternalIdService.MangaDex, manga.Id));

        if (manga.Attributes.Links is { ValueKind: JsonValueKind.Object } links)
        {
            // The site's own short codes. "mu"/"kt" are the same id forms MangaBaka stores; the rest
            // of the map is store and raw-scan links with no tracker identity in them.
            foreach (var (code, service) in MdLinkServices)
            {
                if (links.TryGetProperty(code, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    // Set drops the id forms MangaBaka doesn't use — MangaDex still carries legacy
                    // numeric "mu" ids and slug "kt" ids on older entries.
                    SourceExternalIds.Set(ids, service, value.GetString());
                }
            }
        }

        return ids;
    }

    private static readonly (string Code, string Service)[] MdLinkServices =
    [
        ("mal", ExternalIdService.Mal),
        ("al", ExternalIdService.AniList),
        ("mu", ExternalIdService.MangaUpdates),
        ("kt", ExternalIdService.Kitsu),
    ];

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
