using System.Net.Http.Json;
using System.Text.Json;
using Maki.Core.Parsing;
using Maki.Core.Sources;
using Maki.Sources.Common;

namespace Maki.Sources.MangaDex;

/// <summary>
/// MangaDex source backed by the official JSON API (api.mangadex.org).
/// Page URLs come from the at-home network and expire after ~15 minutes,
/// so GetPagesAsync must be called at download time.
/// </summary>
public class MangaDexSource(IHttpClientFactory httpClientFactory) : IScanlationGroupSource, IChapterVolumeSource
{
    public const string HttpClientName = "source-mangadex";

    private const int FeedPageSize = 500;
    private const int FeedOffsetCap = 10000;

    public string Name => "mangadex";
    public string DisplayName => "MangaDex";
    public string BaseUrl => "https://mangadex.org";
    public SourceCapabilities Capabilities => SourceCapabilities.SupportsLanguageFilter;

    /// <summary>Groups upload their own releases here, under their own name, rather than the site re-hosting them.</summary>
    public SourceKind Kind => SourceKind.Scanlator;
    public SourceContent Content =>
        SourceContent.Manga | SourceContent.Manhwa | SourceContent.Manhua | SourceContent.Doujinshi;
    // Every scanlation group posts in whatever language it works in; there's no fixed catalogue,
    // so this stands in for "essentially all of them" against Maki's own 14-language UI set.
    public IReadOnlyList<string> SupportedLanguages => Core.Localization.SupportedLanguages.All;

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
            "&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica&contentRating[]=pornographic",
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

    /// <summary>
    /// The same chapter number often exists from several scanlation groups; with no group preference
    /// the earliest-published upload is kept, so the diff stays stable across refreshes.
    /// </summary>
    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default) =>
        ScanlationGroupChoice.Pick(await ListUploadsAsync(sourceSeriesId, languageFilter, ct), ScanlationGroupPreference.None);

    public async Task<IReadOnlyList<SourceChapter>> ListUploadsAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var languages = SiteLanguageCodes.Parse(languageFilter);
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
                "&order[chapter]=asc&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica&contentRating[]=pornographic" +
                "&includes[]=scanlation_group",
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
                    $"{BaseUrl}/chapter/{c.Id}",
                    GroupFor(c)));
            }

            offset += response.Limit;
            if (offset >= response.Total || response.Data.Count == 0)
            {
                break;
            }
        }

        return chapters;
    }

    /// <summary>
    /// Chapter number → volume map from the full feed with includeUnavailable=1: unlike
    /// the aggregate endpoint (and the default feed), this still lists delisted chapters
    /// of licensed titles, whose volume assignment is exactly what we're after.
    /// English answers first; the feed of all languages fills the chapters English leaves out, as
    /// long as the volume it gives fits between the English volumes around it. A translated
    /// edition can split volumes differently (an omnibus numbers them lower), and that is the
    /// check that catches it.
    /// </summary>
    public async Task<IReadOnlyDictionary<decimal, int>> GetChapterVolumesAsync(
        string sourceSeriesId, CancellationToken ct = default)
    {
        var english = await WalkVolumesAsync(sourceSeriesId, "&translatedLanguage[]=en", ct);
        var every = await WalkVolumesAsync(sourceSeriesId, string.Empty, ct);
        if (english.Count == 0)
        {
            return every;
        }

        var numbers = english.Keys.Order().ToArray();
        var merged = new Dictionary<decimal, int>(english);
        foreach (var (number, volume) in every)
        {
            if (english.ContainsKey(number))
            {
                continue;
            }

            var next = ~Array.BinarySearch(numbers, number);
            if ((next > 0 && volume < english[numbers[next - 1]]) ||
                (next < numbers.Length && volume > english[numbers[next]]))
            {
                continue;
            }

            merged[number] = volume;
        }

        return merged;
    }

    private async Task<IReadOnlyDictionary<decimal, int>> WalkVolumesAsync(
        string sourceSeriesId, string languageQuery, CancellationToken ct)
    {
        var map = new Dictionary<decimal, int>();
        var conflicted = new HashSet<decimal>();
        var offset = 0;

        // MangaDex rejects any list request whose offset + limit passes 10000, so a longer feed is
        // read as far as it can be rather than failing the whole walk with a 400.
        while (offset + FeedPageSize <= FeedOffsetCap)
        {
            var response = await Client.GetFromJsonAsync<MdCollectionResponse<MdChapter>>(
                $"manga/{sourceSeriesId}/feed?limit={FeedPageSize}&offset={offset}&includeUnavailable=1" +
                languageQuery +
                "&order[chapter]=asc&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica&contentRating[]=pornographic",
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
                    if (map.TryGetValue(number, out var existing))
                    {
                        if (existing != volume)
                        {
                            conflicted.Add(number);
                        }
                    }
                    else
                    {
                        map[number] = volume;
                    }
                }
            }

            offset += response.Limit > 0 ? response.Limit : FeedPageSize;
            if (offset >= response.Total || response.Data.Count == 0)
            {
                break;
            }
        }

        foreach (var number in conflicted)
        {
            map.Remove(number);
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

    /// <summary>
    /// Scanlation group name(s) of the upload, joined with ", " when a chapter carries more than
    /// one (a joint release). Null when the chapter carries no scanlation_group relationship at all.
    /// </summary>
    private static string? GroupFor(MdChapter chapter)
    {
        var names = chapter.Relationships
            .Where(r => r.Type == "scanlation_group")
            .Select(r => r.Attributes?.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();

        return names.Count > 0 ? string.Join(", ", names) : null;
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
