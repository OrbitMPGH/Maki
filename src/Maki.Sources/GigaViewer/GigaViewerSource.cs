using System.Globalization;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Maki.Core.Images;
using Maki.Core.Sources;

namespace Maki.Sources.GigaViewer;

/// <summary>
/// Shared implementation for every GigaViewer-powered site (Shonen Jump+, Comic Days, Sunday
/// Webry, MAGCOMI, Tonari no Young Jump, Comic Zenon, Kurage Bunch). One concrete sealed class
/// per site in <c>GigaViewerSources.cs</c> supplies only its <see cref="GigaViewerSite"/>.
/// <para>
/// A series is identified by one of its episode ids (its own <c>/series/{id}</c> page 404s), and
/// the episode page carries an "aggregate id" that the pagination API groups every episode of the
/// series under. Free episodes only: rentable/purchasable ones never unlock on a schedule, so
/// listing them would retry forever.
/// </para>
/// </summary>
public abstract class GigaViewerSource(IHttpClientFactory httpClientFactory, GigaViewerSite site) : ISource
{
    /// <summary>Shared, rate-limited client for the descrambled page-image CDN, registered once
    /// in Program.cs for every GigaViewer site (the images live on a `cdn-*-img.{host}` subdomain
    /// of whichever site is asking, not one fixed host).</summary>
    public const string ImageHttpClientName = "source-gigaviewer-img";

    private static readonly HtmlParser Parser = new();

    public string Name => site.Name;
    public string DisplayName => site.DisplayName;
    public string BaseUrl => site.BaseUrl;
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public IReadOnlyList<string> SupportedLanguages => ["ja"];

    /// <summary>Classic search thumbnails are on cdn-scissors.gigaviewer.com; og:image covers are
    /// on cdn-img.{host} or cdn-ak-img.{host}, both inside BaseUrl's own domain already.</summary>
    public IReadOnlyList<string> CoverHosts => ["gigaviewer.com"];

    public string HttpClientName => $"source-{site.Name}";

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);
    private HttpClient ImageClient => httpClientFactory.CreateClient(ImageHttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        var tail = SourceUrl.PathTail(url, BaseUrl, "/episode/", firstSegmentOnly: true);
        return tail is { Length: > 0 } && tail.All(char.IsAsciiDigit) ? tail : null;
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return [];
        }

        var html = await Client.GetStringAsync($"search?q={Uri.EscapeDataString(title)}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var results = new List<SourceSeriesResult>();
        var seen = new HashSet<string>();

        // Classic markup (ul.search-series-list on Jump+/Zenon, ul.series-list on Days/Webry/
        // Tonari) and the Next.js markup (MAGCOMI and most follow-ups) both land in this one
        // query; their class names don't overlap so there's no risk of double-matching an item.
        foreach (var item in doc.QuerySelectorAll(
                     "ul.search-series-list > li, ul.series-list > li, li[class^='SearchResultItem_li__']"))
        {
            var link = item.QuerySelector("a[href*='/episode/']");
            var href = link?.GetAttribute("href");
            if (string.IsNullOrEmpty(href))
            {
                // Some results (a colour-version edition) link only /volume/{id}; skip them,
                // there is no episode entry point to build a series id from.
                continue;
            }

            var index = href.IndexOf("/episode/", StringComparison.OrdinalIgnoreCase);
            var seriesId = EpisodeIdFromTail(href[(index + "/episode/".Length)..]);
            if (seriesId is null || !seen.Add(seriesId))
            {
                continue;
            }

            var resultTitle = ExtractSearchTitle(item);

            if (string.IsNullOrEmpty(resultTitle))
            {
                continue;
            }

            var url = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : $"{BaseUrl}{href}";
            var cover = item.QuerySelector("img")?.GetAttribute("src");

            results.Add(new SourceSeriesResult(seriesId, resultTitle, url, cover));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var doc = await GetEpisodeDocumentAsync(sourceSeriesId, ct);

        var title = doc.QuerySelector("h1.series-header-title")?.TextContent.Trim() ?? sourceSeriesId;
        var description = doc.QuerySelector("p.series-header-description")?.TextContent.Trim();
        if (string.IsNullOrEmpty(description))
        {
            description = doc.QuerySelector("meta[property='og:description']")?.GetAttribute("content")?.Trim();
        }

        var cover = doc.QuerySelector("meta[property='og:image']")?.GetAttribute("content");

        return new SourceSeriesDetail(sourceSeriesId, title, $"{BaseUrl}/episode/{sourceSeriesId}", cover, description);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var doc = await GetEpisodeDocumentAsync(sourceSeriesId, ct);
        var aggregateId = ReadAggregateId(doc, sourceSeriesId);

        var chapters = new List<SourceChapter>();
        var offset = 0;
        var firstPageCount = -1;

        for (var page = 0; page < 100; page++)
        {
            var url = $"api/viewer/pagination_readable_products?type=episode&aggregate_id={aggregateId}" +
                       $"&sort_order=desc&offset={offset}";
            var json = await Client.GetStringAsync(url, ct);
            using var parsed = JsonDocument.Parse(json);
            var items = parsed.RootElement;
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            {
                break;
            }

            var count = items.GetArrayLength();
            if (firstPageCount < 0)
            {
                firstPageCount = count;
            }

            foreach (var item in items.EnumerateArray())
            {
                var chapter = ParseChapterItem(item, sourceSeriesId);
                if (chapter is not null)
                {
                    chapters.Add(chapter);
                }
            }

            offset += count;
            if (count < firstPageCount)
            {
                break;
            }
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var episodeUrl = chapter.Url ?? $"{BaseUrl}/episode/{chapter.SourceChapterId}";
        var html = await Client.GetStringAsync($"episode/{chapter.SourceChapterId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var raw = doc.QuerySelector("script#episode-json")?.GetAttribute("data-value");
        if (string.IsNullOrEmpty(raw))
        {
            // Missing entirely means the markup changed under us, not that the episode is
            // locked (a locked episode still ships #episode-json, just with pageStructure null).
            throw new InvalidOperationException(
                $"{Name}: episode {chapter.SourceChapterId} has no episode-json");
        }

        using var json = JsonDocument.Parse(raw);
        if (!json.RootElement.TryGetProperty("readableProduct", out var readableProduct) ||
            !readableProduct.TryGetProperty("pageStructure", out var pageStructure) ||
            pageStructure.ValueKind != JsonValueKind.Object ||
            !pageStructure.TryGetProperty("pages", out var pageArray) ||
            pageArray.ValueKind != JsonValueKind.Array)
        {
            throw new ChapterLockedException($"{Name}: episode {chapter.SourceChapterId} is locked");
        }

        var choJuGiga = pageStructure.TryGetProperty("choJuGiga", out var choJuGigaEl)
            ? choJuGigaEl.GetString()
            : null;
        var headers = new Dictionary<string, string> { ["Referer"] = episodeUrl };

        var pages = new List<PageRequest>();
        foreach (var pageEl in pageArray.EnumerateArray())
        {
            if (pageEl.TryGetProperty("type", out var typeEl) && typeEl.GetString() != "main")
            {
                continue;
            }

            var src = pageEl.TryGetProperty("src", out var srcEl) ? srcEl.GetString() : null;
            if (string.IsNullOrEmpty(src))
            {
                continue;
            }

            if (choJuGiga == "baku")
            {
                var bytes = await FetchAndDescrambleAsync(src, episodeUrl, ct);
                pages.Add(new PageRequest(src, headers, Data: bytes));
            }
            else
            {
                pages.Add(new PageRequest(src, headers));
            }
        }

        return new ChapterPages(pages);
    }

    // A search result's href is raw scraped text, not a parsed Uri, so it can carry a trailing
    // slash, query string or fragment that ResolveSeriesIdFromUrl never sees (Uri.AbsolutePath
    // already strips those). Same digits-only rule either way: an id is the episode's numeric
    // readable-product id, nothing else.
    private static string? EpisodeIdFromTail(string tail)
    {
        var end = tail.IndexOfAny(['/', '?', '#']);
        var id = end < 0 ? tail : tail[..end];
        return id.Length > 0 && id.All(char.IsAsciiDigit) ? id : null;
    }

    // Title: .series-title (classic), then the Next.js title paragraph, then the <li>'s own
    // data-title, then the thumbnail's alt text, in the order the plan lists them.
    private static string? ExtractSearchTitle(AngleSharp.Dom.IElement item)
    {
        var classic = item.QuerySelector(".series-title")?.TextContent.Trim();
        if (!string.IsNullOrEmpty(classic))
        {
            return classic;
        }

        var next = item.QuerySelector("p[class^='SearchResultItem_series_title__']")?.TextContent.Trim();
        if (!string.IsNullOrEmpty(next))
        {
            return next;
        }

        var dataTitle = item.GetAttribute("data-title");
        return !string.IsNullOrEmpty(dataTitle) ? dataTitle : item.QuerySelector("img")?.GetAttribute("alt");
    }

    private async Task<AngleSharp.Dom.IDocument> GetEpisodeDocumentAsync(string episodeId, CancellationToken ct)
    {
        var html = await Client.GetStringAsync($"episode/{episodeId}", ct);
        return await Parser.ParseDocumentAsync(html, ct);
    }

    private string ReadAggregateId(AngleSharp.Dom.IDocument doc, string episodeId)
    {
        var aggregateId = doc.QuerySelector("script.js-valve")?.GetAttribute("data-giga_series");
        if (string.IsNullOrEmpty(aggregateId))
        {
            aggregateId = doc.QuerySelector(".js-readable-products-pagination")?.GetAttribute("data-aggregate-id");
        }

        if (string.IsNullOrEmpty(aggregateId))
        {
            throw new InvalidOperationException($"{Name}: no aggregate id found on episode {episodeId}");
        }

        return aggregateId;
    }

    private SourceChapter? ParseChapterItem(JsonElement item, string sourceSeriesId)
    {
        var id = item.TryGetProperty("readable_product_id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var isFree = item.TryGetProperty("purchase_info", out var purchaseInfo) &&
                     purchaseInfo.ValueKind == JsonValueKind.Object &&
                     purchaseInfo.TryGetProperty("is_free", out var isFreeEl) &&
                     isFreeEl.ValueKind == JsonValueKind.True;

        if (!isFree)
        {
            isFree = item.TryGetProperty("status", out var status) &&
                     status.ValueKind == JsonValueKind.Object &&
                     status.TryGetProperty("label", out var labelEl) &&
                     labelEl.GetString() == "is_free";
        }

        if (!isFree)
        {
            // Rentable/purchasable episodes never unlock on a schedule, so listing one would
            // just retry forever; drop it here instead of surfacing it as permanently locked.
            return null;
        }

        var title = item.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
        DateTime? releaseDate = null;
        if (item.TryGetProperty("display_open_at", out var dateEl) &&
            dateEl.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(
                dateEl.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsedDate))
        {
            releaseDate = parsedDate;
        }

        var number = GigaViewerChapterNumber.Parse(title);

        return new SourceChapter(
            Name,
            sourceSeriesId,
            id,
            NumberRaw: title,
            Number: number,
            Volume: null,
            Title: title,
            Language: "ja",
            ReleaseDate: releaseDate,
            Url: $"{BaseUrl}/episode/{id}");
    }

    private async Task<byte[]> FetchAndDescrambleAsync(string src, string episodeUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, src);
        request.Headers.Referrer = new Uri(episodeUrl);
        using var response = await ImageClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return await ImageWorkGate.RunAsync(() => Task.FromResult(GigaViewerDescrambler.Descramble(bytes)), ct);
    }
}
