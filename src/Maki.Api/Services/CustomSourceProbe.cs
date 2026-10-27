using System.Text.RegularExpressions;
using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Core.Sources.Custom;
using Maki.Sources.Custom;
using Maki.Sources.Custom.Inference;

namespace Maki.Api.Services;

/// <summary>
/// The wizard's reads of a site it doesn't have a definition for yet: fetch the pasted pages
/// through the guarded client, run the guessers, and hand back a draft. Nothing is saved. Every
/// fetch is held to the pasted series URL's host and spaced like a source with the default interval.
/// </summary>
public sealed partial class CustomSourceProbe(CustomSourceFetcher fetcher, CustomSourceFactory factory, CustomSourceThrottle throttle)
{
    /// <summary>A probe that can't go on, as a catalogue key and its arguments.</summary>
    public sealed class ProbeFailed(string key, object? args = null, Exception? inner = null) : Exception(key, inner)
    {
        public string Key { get; } = key;
        public object? Args { get; } = args;
    }

    /// <param name="ViaFlareSolverr">Either page needed FlareSolverr, so the draft sets <c>flareSolverr</c>.</param>
    /// <param name="ChapterError">Why the pasted chapter page couldn't be read, in the site's or the fetcher's words.</param>
    public sealed record SiteResult(
        CustomSourceDefinition Draft,
        bool ViaFlareSolverr,
        string? Title,
        string SeriesId,
        EngineDetector.Match? Engine,
        IReadOnlyList<ChapterListGuesser.Guess> Chapters,
        PagesResult? Pages,
        string? ChapterError);

    public sealed record PagesResult(CustomPages Pages, int Count, IReadOnlyList<string> Preview, ImageResult Image);

    /// <param name="Error">What failed, in the site's or the fetcher's words; null when the image loaded.</param>
    public sealed record ImageResult(bool Ok, string? Format, int Bytes, string? Error);

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex NotIdChar();

    public async Task<SiteResult> ProbeSiteAsync(string? seriesUrl, string? chapterUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(seriesUrl?.Trim(), UriKind.Absolute, out var series)
            || CustomSourceValidator.BaseUri(series.GetLeftPart(UriPartial.Authority)) is not { } baseUri)
        {
            throw new ProbeFailed("error.customSource.probe.urlInvalid");
        }

        Uri? chapter = null;
        if (!string.IsNullOrWhiteSpace(chapterUrl)
            && (!Uri.TryCreate(chapterUrl.Trim(), UriKind.Absolute, out chapter) || !CustomHost.Same(chapter.Host, baseUri.Host)))
        {
            throw new ProbeFailed("error.customSource.probe.otherHost", new { host = baseUri.Host });
        }

        if (UrlShape.Series(series) is not { } shape)
        {
            throw new ProbeFailed("error.customSource.probe.seriesUrlShape");
        }

        var gate = Gate(baseUri);
        var (seriesPage, viaFlare) = await FetchAsync(series, baseUri, gate, ct);

        CustomPage? chapterPage = null;
        string? chapterError = null;
        if (chapter is not null)
        {
            try
            {
                (chapterPage, var chapterVia) = await FetchAsync(chapter, baseUri, gate, ct);
                viaFlare |= chapterVia;
            }
            catch (ProbeFailed ex)
            {
                chapterError = ex.InnerException?.Message ?? ex.Message;
            }
        }

        // SilentQuill links the root form of a chapter while the pasted URL may be another one.
        var canonical = chapterPage is null
            ? null
            : ChapterListGuesser.Resolve(chapter!, chapterPage.Document.QuerySelector("link[rel='canonical']")?.GetAttribute("href"));
        var engine = EngineDetector.Run(seriesPage) ?? await DetectHeanCmsAsync(baseUri, series, gate, ct);
        var chapters = ChapterListGuesser.Run(seriesPage, shape.SeriesId, chapter, canonical);
        var pages = chapterPage is null ? null : PageListGuesser.Run(chapterPage, seriesPage);
        var sampleChapter = chapters.FirstOrDefault()?.Rows
            .FirstOrDefault(r => r.Link is not null && new[] { chapter, canonical }.Any(u => u is not null && ChapterListGuesser.SamePath(new Uri(r.Link), u)))?.Link
            ?? canonical?.AbsoluteUri ?? chapter?.AbsoluteUri ?? "";

        var title = Title(seriesPage);
        var draft = new CustomSourceDefinition
        {
            Id = IdFor(baseUri),
            Name = NameFor(seriesPage, baseUri),
            BaseUrl = baseUri.GetLeftPart(UriPartial.Authority),
            Language = LanguageOf(seriesPage),
            FlareSolverr = viaFlare,
            Engine = engine?.Engine,
            EngineOptions = engine?.Options,
            Series = engine is null ? new CustomSeries { Url = shape.Template } : new CustomSeries(),
            Chapters = engine is null ? chapters.FirstOrDefault()?.Chapters ?? new CustomChapters() : new CustomChapters(),
            Pages = engine is null ? pages?.Pages ?? new CustomPages() : new CustomPages(),
            Sample = new CustomSample { SeriesUrl = series.AbsoluteUri, ChapterUrl = sampleChapter, Query = title ?? (engine is null ? null : shape.SeriesId) }
        };

        draft = draft with { CoverHosts = await CoverHostsAsync(draft, shape.SeriesId, baseUri, ct) };

        PagesResult? pagesResult = null;
        if (pages is not null)
        {
            var image = await CheckImageAsync(draft, pages.Images[0], ct);
            pagesResult = new PagesResult(pages.Pages, pages.Images.Count, pages.Images.Take(3).Append(pages.Images[^1]).Distinct().ToList(), image);
        }

        return new SiteResult(draft, viaFlare, title, shape.SeriesId, engine, chapters, pagesResult, chapterError);
    }

    /// <summary>Tries the site's own search form and the common search shapes with the sample series' title.</summary>
    public async Task<SearchGuesser.Guess> ProbeSearchAsync(string? baseUrl, string? seriesTemplate, string? seriesUrl, string? query, CancellationToken ct)
    {
        if (CustomSourceValidator.BaseUri(baseUrl) is not { } baseUri
            || string.IsNullOrWhiteSpace(seriesTemplate) || !CustomTemplate.Mentions(seriesTemplate, CustomTemplate.SeriesId)
            || !Uri.TryCreate(seriesUrl, UriKind.Absolute, out var series))
        {
            throw new ProbeFailed("error.customSource.probe.urlInvalid");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ProbeFailed("error.customSource.sampleQueryRequired");
        }

        var seriesId = CustomTemplate.Match(baseUri, seriesTemplate, series, CustomTemplate.SeriesId)
            ?? throw new ProbeFailed("error.customSource.test.sampleSeriesUnresolved");
        var gate = Gate(baseUri);
        return await SearchGuesser.RunAsync(baseUri, seriesTemplate, seriesId, query.Trim(), async (url, token) =>
        {
            try
            {
                return ((await FetchAsync(url, baseUri, gate, token)).Page, null);
            }
            catch (ProbeFailed ex)
            {
                return (null, ex.InnerException?.Message ?? ex.Message);
            }
        }, ct);
    }

    /// <summary>
    /// HeanCMS renders its pages in the browser, so its series page says nothing; its API answering
    /// <c>series/{slug}</c> with a <c>series_slug</c> does.
    /// </summary>
    private async Task<EngineDetector.Match?> DetectHeanCmsAsync(Uri baseUri, Uri series, SpacingGate gate, CancellationToken ct)
    {
        var segments = series.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || CustomSource.HeanApi(new CustomEngineOptions(), baseUri) is not { } api)
        {
            return null;
        }

        await gate.WaitAsync(ct);
        try
        {
            var body = (await fetcher.FetchDetailedAsync(new HtmlFetchRequest(new Uri(api, "series/" + segments[1]).AbsoluteUri), ct)).Html;
            return body.Contains("\"series_slug\"", StringComparison.Ordinal)
                ? new EngineDetector.Match(CustomEngines.HeanCms, new CustomEngineOptions { SeriesPath = segments[0] })
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The sample series' cover host when it isn't the site's own, so the cover proxy will fetch it.</summary>
    private async Task<List<string>> CoverHostsAsync(CustomSourceDefinition draft, string seriesId, Uri baseUri, CancellationToken ct)
    {
        try
        {
            var cover = (await factory.Create(draft).GetSeriesAsync(seriesId, ct)).CoverUrl;
            return Uri.TryCreate(cover, UriKind.Absolute, out var uri) && !CustomHost.Same(uri.Host, baseUri.Host)
                ? [CustomHost.Strip(uri.Host).ToLowerInvariant()]
                : [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    private SpacingGate Gate(Uri baseUri) =>
        throttle.For("probe:" + CustomHost.Strip(baseUri.Host), CustomSourceDefinition.DefaultIntervalMs);

    private async Task<(CustomPage Page, bool ViaFlareSolverr)> FetchAsync(Uri url, Uri baseUri, SpacingGate gate, CancellationToken ct)
    {
        if (!CustomHost.Same(url.Host, baseUri.Host))
        {
            throw new ProbeFailed("error.customSource.probe.otherHost", new { host = baseUri.Host });
        }

        await gate.WaitAsync(ct);
        try
        {
            var result = await fetcher.FetchDetailedAsync(
                new HtmlFetchRequest(url.AbsoluteUri, Headers: new Dictionary<string, string> { ["Referer"] = baseUri.AbsoluteUri }), ct);
            return (new CustomPage(result.Html, url), result.ViaFlareSolverr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new ProbeFailed("error.customSource.probe.fetchFailed", new { url = url.AbsoluteUri, reason = ex.Message }, ex);
        }
    }

    /// <summary>One image with the headers a download would send, so "image did not load" shows up here and not on the first download.</summary>
    private async Task<ImageResult> CheckImageAsync(CustomSourceDefinition draft, string url, CancellationToken ct)
    {
        try
        {
            var bytes = await factory.Create(draft).FetchImageAsync(url, ct);
            var format = CustomSourceTester.Sniff(bytes);
            return new ImageResult(format is not null, format, bytes.Length, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ImageResult(false, null, 0, ex.Message);
        }
    }

    private static string? Title(CustomPage page) =>
        Text(page.Document.QuerySelector("h1")?.TextContent)
        ?? Text(page.Document.QuerySelector("meta[property='og:title']")?.GetAttribute("content"))
        ?? Text(page.Document.Title);

    private static string IdFor(Uri baseUri)
    {
        var label = CustomHost.Strip(baseUri.Host).Split('.')[0].ToLowerInvariant();
        var id = NotIdChar().Replace(label, "-").Trim('-');
        return id.Length < 2 ? "site-" + id : id.Length > 40 ? id[..40] : id;
    }

    private static string NameFor(CustomPage page, Uri baseUri)
    {
        if (Text(page.Document.QuerySelector("meta[property='og:site_name']")?.GetAttribute("content")) is { Length: <= 60 } siteName)
        {
            return siteName;
        }

        var label = CustomHost.Strip(baseUri.Host).Split('.')[0];
        return char.ToUpperInvariant(label[0]) + label[1..];
    }

    private static string LanguageOf(CustomPage page)
    {
        var lang = page.Document.DocumentElement.GetAttribute("lang");
        var canonical = string.IsNullOrWhiteSpace(lang) ? "" : SourceLanguages.Canonical(lang.Split('-', '_')[0]);
        return canonical.Length is 2 or 3 ? canonical : "en";
    }

    private static string? Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
