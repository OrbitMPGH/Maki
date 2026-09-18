using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.SenManga;

/// <summary>
/// Sen Manga scraper — Japanese raw (untranslated) scans. The site is a client-rendered SPA
/// (the SSR shell ships an empty &lt;div id="root"&gt;), so nothing here parses HTML: every call
/// goes through the same JSON API the SPA itself calls (found by reading its bundled JS, since
/// none of it is documented). Search/series id is the slug from <c>/manga/{slug}</c>; a chapter
/// id is its "url" field ("1193.407873"), not the bare chapter number "slug" field — the pages
/// endpoint needs the former.
/// </summary>
public class SenMangaSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-senmanga";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => "senmanga";
    public string DisplayName => "Sen Manga";
    public string BaseUrl => "https://raw.senmanga.com";
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public IReadOnlyList<string> SupportedLanguages => ["ja"];

    /// <summary>The API's own covers and page images are served off a different site's CDN.</summary>
    public IReadOnlyList<string> CoverHosts => ["rawkuma.net", "kuma.kyut.dev"];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url) =>
        SourceUrl.PathTail(url, BaseUrl, "/manga/", firstSegmentOnly: true);

    // ── Search ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var encoded = Uri.EscapeDataString(title);
        var response = await Client.GetFromJsonAsync<SearchResponse>($"api/search?q={encoded}", JsonOptions, ct);
        if (response?.Series is null)
        {
            return [];
        }

        return response.Series
            .Select(s => new SourceSeriesResult(s.Slug, s.Title, $"{BaseUrl}/manga/{s.Slug}", s.Cover))
            .ToList();
    }

    // ── Series detail ─────────────────────────────────────────────────

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var detail = await Client.GetFromJsonAsync<SeriesDetail>($"api/manga/{sourceSeriesId}", JsonOptions, ct)
            ?? throw new InvalidOperationException($"Sen Manga returned no data for '{sourceSeriesId}'");

        return new SourceSeriesDetail(
            sourceSeriesId,
            string.IsNullOrEmpty(detail.Title) ? sourceSeriesId : detail.Title,
            $"{BaseUrl}/manga/{sourceSeriesId}",
            detail.Cover,
            detail.Description,
            detail.Status);
    }

    // ── Chapters ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var detail = await Client.GetFromJsonAsync<SeriesDetail>($"api/manga/{sourceSeriesId}", JsonOptions, ct);
        if (detail?.ChapterList is null)
        {
            return [];
        }

        var chapters = new List<SourceChapter>();
        foreach (var entry in detail.ChapterList)
        {
            if (string.IsNullOrEmpty(entry.Url))
            {
                continue;
            }

            // "number" is the authoritative field ("37.3" for a .3 chapter); the title
            // ("Chapter 1193") is only a fallback for the rare entry missing it.
            decimal? number = decimal.TryParse(entry.Number, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var direct)
                ? direct
                : ChapterNumberParser.Parse(entry.Title).Number;

            DateTime? releaseDate = DateTime.TryParse(
                entry.Datetime, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
                ? d
                : null;

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                entry.Url,
                entry.Title,
                number,
                Volume: null,
                Title: null,
                Language: "ja",
                releaseDate,
                Url: $"{BaseUrl}{entry.FullUrl}"));
        }

        return SourceChapterList.Normalize(chapters);
    }

    // ── Page images ───────────────────────────────────────────────────

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var read = await Client.GetFromJsonAsync<ReadResponse>(
            $"api/read/{chapter.SourceSeriesId}/{chapter.SourceChapterId}", JsonOptions, ct);
        if (read?.Pages is null)
        {
            return new ChapterPages([]);
        }

        var pages = read.Pages
            .Where(url => !string.IsNullOrEmpty(url))
            .Select(url => new PageRequest(url))
            .ToList();

        return new ChapterPages(pages);
    }

    private sealed class SearchResponse
    {
        public List<SeriesSummary>? Series { get; set; }
    }

    private sealed class SeriesSummary
    {
        public string Title { get; set; } = "";
        public string Slug { get; set; } = "";
        public string? Cover { get; set; }
    }

    private sealed class SeriesDetail
    {
        public string Title { get; set; } = "";
        public string? Cover { get; set; }
        public string? Description { get; set; }
        public string? Status { get; set; }

        [JsonPropertyName("chapterList")]
        public List<ChapterEntry>? ChapterList { get; set; }
    }

    private sealed class ChapterEntry
    {
        public string Title { get; set; } = "";
        public string? Number { get; set; }
        public string Url { get; set; } = "";

        [JsonPropertyName("full_url")]
        public string FullUrl { get; set; } = "";

        public string? Datetime { get; set; }
    }

    private sealed class ReadResponse
    {
        public List<string>? Pages { get; set; }
    }
}
