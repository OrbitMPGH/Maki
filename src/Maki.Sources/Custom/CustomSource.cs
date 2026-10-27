using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Maki.Core.Http;
using Maki.Core.Parsing;
using Maki.Core.Sources;
using Maki.Core.Sources.Custom;
using Maki.Sources.Common;

namespace Maki.Sources.Custom;

/// <summary>
/// A site an admin described with a <see cref="CustomSourceDefinition"/>. Every HTML fetch goes
/// through the guarded <c>custom-sources</c> fetcher and is held to the definition's host; page
/// images go out through <see cref="PagesHttpClientName"/> at download time, behind the same
/// <see cref="SpacingGate"/>, because a small site's heaviest traffic is its images.
/// </summary>
public sealed partial class CustomSource : ISource
{
    public const string PagesHttpClientName = "custom-source-pages";

    public const int MaxListPages = 50;
    public const int MaxChapters = 5000;
    public const int MaxPages = 1000;

    private readonly IHtmlFetcher _fetcher;
    private readonly IHttpClientFactory _http;
    private readonly SpacingGate _gate;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>>? _sessionHeaders;
    private readonly Uri _base;
    private readonly SourceCatalog _catalogue = new(TimeSpan.FromHours(6));
    private readonly CustomEngineOptions? _engine;

    /// <summary>HeanCMS's API origin, the one host besides the site's own that HTML and JSON may come from.</summary>
    private readonly Uri? _api;

    /// <param name="sessionHeaders">
    /// Clearance cookies and UA for a host the fetcher solved a challenge for
    /// (<see cref="ChallengeAwareFetcher.SessionHeadersFor"/>), so page images reuse them.
    /// </param>
    public CustomSource(
        CustomSourceDefinition definition,
        IHtmlFetcher fetcher,
        IHttpClientFactory http,
        SpacingGate gate,
        Func<string, string, IReadOnlyDictionary<string, string>>? sessionHeaders = null)
    {
        Definition = definition;
        _fetcher = fetcher;
        _http = http;
        _gate = gate;
        _sessionHeaders = sessionHeaders;
        _base = new Uri(definition.BaseUrl.TrimEnd('/') + "/");

        if (definition.Engine is { } engine)
        {
            _engine = definition.EngineOptions ?? new CustomEngineOptions();
            var path = _engine.SeriesPath ?? CustomEngines.DefaultSeriesPath(engine);
            SeriesTemplate = engine == CustomEngines.HeanCms
                ? $"/{path}/{CustomTemplate.SeriesId}"
                : $"/{path}/{CustomTemplate.SeriesId}/";
            ChapterTemplate = engine switch
            {
                CustomEngines.MangaThemesia => $"/{CustomTemplate.ChapterId}/",
                CustomEngines.Keyoapp => $"/chapter/{CustomTemplate.ChapterId}/",
                CustomEngines.HeanCms => $"/{path}/{CustomTemplate.SeriesId}/{CustomTemplate.ChapterId}",
                _ => $"/{path}/{CustomTemplate.SeriesId}/{CustomTemplate.ChapterId}/"
            };
            _api = engine == CustomEngines.HeanCms ? HeanApi(_engine, _base) : null;
        }
        else
        {
            SeriesTemplate = definition.Series.Url;
            ChapterTemplate = definition.Chapters.Url;
        }
    }

    /// <summary>The definition's own templates, or the ones its engine implies.</summary>
    public string SeriesTemplate { get; }
    public string ChapterTemplate { get; }

    public CustomSourceDefinition Definition { get; }

    public string Name => Definition.SourceName;
    public string DisplayName => Definition.Name;
    public string BaseUrl => _base.GetLeftPart(UriPartial.Authority);
    public SourceCapabilities Capabilities => Definition.FlareSolverr ? SourceCapabilities.NeedsFlareSolverr : SourceCapabilities.None;
    public SourceKind Kind => CustomSourceEnums.Kind(Definition.Kind) ?? SourceKind.Scanlator;
    public SourceContent Content => CustomSourceEnums.Content(Definition.Content) ?? SourceContent.Manga;
    public SourceRating Rating => CustomSourceEnums.Rating(Definition.Rating) ?? SourceRating.General;
    public IReadOnlyList<string> SupportedLanguages => [SourceLanguages.Canonical(Definition.Language)];
    public bool ReleaseDatesAreReliable => Definition.Engine is null
        ? Definition.Chapters.Date is not { Format: "relative" }
        : Definition.Engine != CustomEngines.Madara;
    public IReadOnlyList<string> CoverHosts => Definition.CoverHosts;

    /// <summary>False for a site with neither search nor a catalogue page: series are linked by URL only.</summary>
    public bool HasSearch => Definition.HasSearch;

    public string? ResolveSeriesIdFromUrl(Uri url) =>
        CustomTemplate.Match(_base, SeriesTemplate, url, CustomTemplate.SeriesId);

    // ── Search ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        switch (Definition.Engine)
        {
            case CustomEngines.Madara: return await MadaraSearchAsync(title, ct);
            case CustomEngines.MangaThemesia: return await ThemesiaSearchAsync(title, ct);
            case CustomEngines.Keyoapp: return await KeyoappSearchAsync(title, ct);
            case CustomEngines.HeanCms: return await HeanSearchAsync(title, ct);
        }

        if (Definition.Search is { } search)
        {
            var page = await FetchAsync(CustomTemplate.Expand(_base, search.Url, query: title), ct);
            return Extract(() => SeriesResults(search.Item, search.Id, search.Title, search.Cover, page));
        }

        if (Definition.Catalogue is { } catalogue)
        {
            return await _catalogue.SearchAsync(title, token => LoadCatalogueAsync(catalogue, token), ct);
        }

        return [];
    }

    private async Task<List<SourceSeriesResult>> LoadCatalogueAsync(CustomCatalogue catalogue, CancellationToken ct)
    {
        var results = new List<SourceSeriesResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Uri? next = CustomTemplate.Expand(_base, catalogue.Url);
        for (var i = 0; i < MaxListPages && next is not null && seen.Add(next.AbsoluteUri); i++)
        {
            var page = await FetchAsync(next, ct);
            results.AddRange(Extract(() => SeriesResults(catalogue.Item, catalogue.Id, catalogue.Title, catalogue.Cover, page)));
            next = Resolve(page.Url, Extract(() => CustomExtractor.PageField(catalogue.NextPage, page)));
        }

        if (results.Count == 0)
        {
            throw new SourceErrorException($"{DisplayName}: the catalogue page listed no series ({Describe(catalogue.Item)})");
        }

        return results.DistinctBy(r => r.SourceSeriesId).ToList();
    }

    private List<SourceSeriesResult> SeriesResults(
        ListExtractor item, FieldExtractor id, FieldExtractor? title, FieldExtractor? cover, CustomPage page)
    {
        var results = new List<SourceSeriesResult>();
        foreach (var entry in CustomExtractor.List(item, page))
        {
            if (IdOf(id, entry, page.Url) is not { } seriesId)
            {
                continue;
            }

            results.Add(new SourceSeriesResult(
                seriesId,
                CustomExtractor.Field(title, entry) ?? seriesId,
                SeriesUrl(seriesId).AbsoluteUri,
                Resolve(page.Url, CustomExtractor.Field(cover, entry))?.AbsoluteUri));
        }

        return results.DistinctBy(r => r.SourceSeriesId).ToList();
    }

    // ── Series ────────────────────────────────────────────────────────

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        switch (Definition.Engine)
        {
            case CustomEngines.Madara: return await MadaraSeriesAsync(sourceSeriesId, ct);
            case CustomEngines.MangaThemesia: return await ThemesiaSeriesAsync(sourceSeriesId, ct);
            case CustomEngines.Keyoapp: return await KeyoappSeriesAsync(sourceSeriesId, ct);
            case CustomEngines.HeanCms: return await HeanSeriesAsync(sourceSeriesId, ct);
        }

        var series = Definition.Series;
        var url = SeriesUrl(sourceSeriesId);
        var page = await FetchAsync(url, ct);
        return Extract(() =>
        {
            var title = CustomExtractor.PageField(series.Title, page)
                ?? Meta(page, "og:title")
                ?? page.Document.QuerySelector("h1")?.TextContent.Trim()
                ?? sourceSeriesId;
            var cover = CustomExtractor.PageField(series.Cover, page) ?? Meta(page, "og:image");
            var description = CustomExtractor.PageField(series.Description, page) ?? Meta(page, "og:description");
            return new SourceSeriesDetail(
                sourceSeriesId, title, url.AbsoluteUri, Resolve(page.Url, cover)?.AbsoluteUri, description,
                CustomSourceEnums.Status(CustomExtractor.PageField(series.Status, page)));
        });
    }

    // ── Chapters ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default) =>
        SourceChapterList.Normalize((await ListEntriesAsync(sourceSeriesId, ct)).Select(e => e.Chapter));

    /// <summary>A listed chapter and the link its row carried, for the round-trip check in the test run.</summary>
    public sealed record ListedChapter(SourceChapter Chapter, string? ItemHref);

    public async Task<IReadOnlyList<ListedChapter>> ListEntriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        switch (Definition.Engine)
        {
            case CustomEngines.Madara: return await MadaraEntriesAsync(sourceSeriesId, _engine!, ct);
            case CustomEngines.MangaThemesia: return await ThemesiaEntriesAsync(sourceSeriesId, ct);
            case CustomEngines.Keyoapp: return await KeyoappEntriesAsync(sourceSeriesId, ct);
            case CustomEngines.HeanCms: return await HeanEntriesAsync(sourceSeriesId, ct);
        }

        var chapters = Definition.Chapters;
        var entries = new List<ListedChapter>();
        var items = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Uri? next = CustomTemplate.Expand(_base, chapters.ListUrl ?? SeriesTemplate, sourceSeriesId);
        for (var i = 0; i < MaxListPages && next is not null && seen.Add(next.AbsoluteUri) && entries.Count < MaxChapters; i++)
        {
            var page = await FetchAsync(next, ct);
            var listed = Extract(() => CustomExtractor.List(chapters.Item, page));
            items += listed.Count;
            foreach (var item in listed)
            {
                if (Extract(() => ChapterOf(sourceSeriesId, item, page)) is { } chapter)
                {
                    entries.Add(chapter);
                }
            }

            next = Resolve(page.Url, Extract(() => CustomExtractor.PageField(chapters.NextPage, page)));
        }

        // A page that was fetched fine but matched nothing is a layout change, not a series with no
        // chapters: an empty listing would stall a monitored series without anyone noticing.
        if (items == 0)
        {
            throw new SourceErrorException($"{DisplayName}: no chapters matched {Describe(chapters.Item)}");
        }

        return entries.Take(MaxChapters).ToList();
    }

    private ListedChapter? ChapterOf(string seriesId, CustomItem item, CustomPage page)
    {
        var chapters = Definition.Chapters;
        if (IdOf(chapters.Id, item, page.Url) is not { } chapterId)
        {
            return null;
        }

        var href = Resolve(page.Url, item.Href)?.AbsoluteUri;
        var numberText = CustomExtractor.Field(chapters.Number, item)
            ?? (item.Element is not null ? CustomExtractor.Field(new FieldExtractor(), item) : null);
        var volumeText = CustomExtractor.Field(chapters.Volume, item);
        var parsed = ChapterNumberParser.Parse(numberText, volumeText).OrSlugNumber(href ?? chapterId);
        var url = ChapterUrl(seriesId, chapterId);

        return new ListedChapter(
            new SourceChapter(
                Name, seriesId, chapterId, numberText, parsed.Number, parsed.Volume,
                CustomExtractor.Field(chapters.Title, item), Definition.Language,
                ParseDate(CustomExtractor.Field(chapters.Date, item), chapters.Date), url.AbsoluteUri),
            href);
    }

    // ── Pages ─────────────────────────────────────────────────────────

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var chapterUrl = ChapterUrl(chapter.SourceSeriesId, chapter.SourceChapterId);
        switch (Definition.Engine)
        {
            case CustomEngines.Madara: return Pages(await MadaraPagesAsync(chapterUrl, ct));
            case CustomEngines.MangaThemesia: return Pages(await ThemesiaPagesAsync(chapterUrl, ct));
            case CustomEngines.Keyoapp: return Pages(await KeyoappPagesAsync(chapterUrl, ct));
            case CustomEngines.HeanCms: return Pages(await HeanPagesAsync(chapter, ct));
        }

        var pages = Definition.Pages;
        var pageUrl = pages.Url is null
            ? chapterUrl
            : CustomTemplate.Expand(_base, pages.Url, chapter.SourceSeriesId, chapter.SourceChapterId);
        var page = await FetchAsync(pageUrl, ct);

        var urls = ImageUrls(page, Extract(() => CustomExtractor.List(pages.List, page)
            .Select(item => pages.Image is null ? CustomExtractor.DefaultImage(item) : CustomExtractor.Field(pages.Image, item))
            .ToList()));

        if (urls.Count == 0)
        {
            throw new SourceErrorException($"{DisplayName}: no page images matched {Describe(pages.List)}");
        }

        return Pages(urls);
    }

    private static List<string> ImageUrls(CustomPage page, IEnumerable<string?> raw) => raw
        .Select(r => Resolve(page.Url, r))
        .Where(u => u is not null && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
        .Select(u => u!.AbsoluteUri)
        .Distinct(StringComparer.Ordinal)
        .Take(MaxPages)
        .ToList();

    private ChapterPages Pages(List<string> urls)
    {
        return new ChapterPages(urls
            .Select(url =>
            {
                var headers = ImageHeaders(new Uri(url));
                return new PageRequest(url, headers, Fetch: token => FetchImageAsync(url, headers, token));
            })
            .ToList());
    }

    private IReadOnlyDictionary<string, string> ImageHeaders(Uri image)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserUserAgent.Value,
            ["Referer"] = _base.AbsoluteUri
        };

        if (_sessionHeaders is not null && CustomHost.Same(image.Host, _base.Host))
        {
            foreach (var (key, value) in _sessionHeaders(image.Host, _base.AbsoluteUri))
            {
                headers[key] = value;
            }
        }

        foreach (var (key, value) in Definition.Headers)
        {
            headers[key] = value;
        }

        return headers;
    }

    /// <summary>One image with the headers a download would send, for the wizard's image check.</summary>
    public Task<byte[]> FetchImageAsync(string url, CancellationToken ct) => FetchImageAsync(url, ImageHeaders(new Uri(url)), ct);

    public async Task<byte[]> FetchImageAsync(string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        PublicAddressGuard.EnsureAllowed(url);
        await _gate.WaitAsync(ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        using var response = await _http.CreateClient(PagesHttpClientName).SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            throw new RateLimitException(
                $"Rate limited by {request.RequestUri?.Host} (HTTP {(int)response.StatusCode})", response.Headers.RetryAfter?.Delta);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    public Uri SeriesUrl(string seriesId) => CustomTemplate.Expand(_base, SeriesTemplate, seriesId);

    public Uri ChapterUrl(string seriesId, string chapterId) =>
        CustomTemplate.Expand(_base, ChapterTemplate, seriesId, chapterId);

    private async Task<CustomPage> FetchAsync(Uri url, CancellationToken ct, string? formBody = null)
    {
        if (!CustomHost.Same(url.Host, _base.Host) && !string.Equals(url.Host, _api?.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new SourceErrorException($"{DisplayName} only reads pages on {_base.Host}, not {url.Host}");
        }

        await _gate.WaitAsync(ct);
        var headers = new Dictionary<string, string>(Definition.Headers, StringComparer.OrdinalIgnoreCase);
        headers.TryAdd("Referer", _base.AbsoluteUri);
        if (formBody is not null)
        {
            headers.TryAdd("X-Requested-With", "XMLHttpRequest");
        }

        var body = await _fetcher.FetchAsync(new HtmlFetchRequest(url.AbsoluteUri, FormBody: formBody, Headers: headers), ct);
        return new CustomPage(body, url);
    }

    /// <summary>
    /// An id read from a link is matched against the link's resolved path, since sites mix absolute
    /// and relative hrefs. A link to another host yields no id.
    /// </summary>
    private string? IdOf(FieldExtractor field, CustomItem item, Uri pageUrl)
    {
        var raw = CustomExtractor.Field(field with { Regex = null }, item);
        if (raw is null)
        {
            return null;
        }

        if (field.Attr is "href" or "src" || raw.StartsWith('/') || raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(pageUrl, raw, out var resolved) || !CustomHost.Same(resolved.Host, _base.Host))
            {
                return null;
            }

            raw = resolved.AbsolutePath;
        }

        return CustomExtractor.Narrow(raw, field.Regex);
    }

    private static Uri? Resolve(Uri pageUrl, string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(pageUrl, raw.Trim(), out var uri) ? uri : null;

    private static string? Meta(CustomPage page, string property) =>
        page.Document.QuerySelector($"meta[property='{property}'], meta[name='{property}']")?.GetAttribute("content")?.Trim() is { Length: > 0 } value
            ? value
            : null;

    private static DateTime? ParseDate(string? text, DateExtractor? date)
    {
        if (string.IsNullOrWhiteSpace(text) || date is null)
        {
            return null;
        }

        return date.Format switch
        {
            "relative" => RelativeDate.Parse(text),
            "iso" => DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var iso) ? iso : null,
            var format => DateTime.TryParseExact(text.Trim(), format, CustomSourceEnums.Culture(date.Culture),
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var exact) ? exact : null
        };
    }

    private static string Describe(ListExtractor list) =>
        list.Css is { } css ? $"css '{css}'" : list.Regex is { } regex ? $"regex '{regex}'" : $"json path '{list.Json?.Path}'";

    /// <summary>A regex that ran out of time fails the source like a layout change would, not the whole sync.</summary>
    private T Extract<T>(Func<T> extract)
    {
        try
        {
            return extract();
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new SourceErrorException($"{DisplayName}: the pattern '{ex.Pattern}' took too long and was stopped");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new SourceErrorException($"{DisplayName}: the JSON the definition points at did not parse ({ex.Message})");
        }
    }
}
