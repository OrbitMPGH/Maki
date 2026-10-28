using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Maki.Core.Parsing;
using Maki.Core.Sources;
using Maki.Core.Sources.Custom;

namespace Maki.Sources.Custom;

/// <summary>
/// The <c>keyoapp</c> engine. Series at <c>/series/{slug}/</c>, chapters at <c>/chapter/{id}/</c>.
/// The series list page carries every series, so search is a catalogue matched by title. Page
/// images are <c>img[uid]</c> placeholders that a script turns into <c>{cdn}{uid}</c>; the CDN is
/// read from that script. Upcoming and paid chapters carry a clock, coin or lock icon and are left out.
/// </summary>
public sealed partial class CustomSource
{
    [GeneratedRegex(@"realUrl\s*=\s*[`'""](https?://[^`'""$]+?)\$\{uid\}")]
    private static partial Regex KeyoappImageBase();

    private Task<IReadOnlyList<SourceSeriesResult>> KeyoappSearchAsync(string title, CancellationToken ct) =>
        _catalogue.SearchAsync(title, KeyoappCatalogueAsync, ct);

    private async Task<List<SourceSeriesResult>> KeyoappCatalogueAsync(CancellationToken ct)
    {
        var page = await FetchAsync(new Uri(_base, "series/"), ct);
        var results = new List<SourceSeriesResult>();
        foreach (var card in page.Document.QuerySelectorAll("#searched_series_page > button, #searched_series_page > div"))
        {
            if (Resolve(page.Url, card.QuerySelector("a[href]")?.GetAttribute("href")) is not { } href
                || ResolveSeriesIdFromUrl(href) is not { } id)
            {
                continue;
            }

            var name = Clean(card.QuerySelector("a[href][title]")?.GetAttribute("title")) ?? Clean(card.GetAttribute("alt")) ?? id;
            var style = card.QuerySelector("[style*='background-image']")?.GetAttribute("style");
            results.Add(new SourceSeriesResult(id, name, SeriesUrl(id).AbsoluteUri, KeyoappCover(page.Url, style)));
        }

        if (results.Count == 0)
        {
            throw new SourceErrorException($"{DisplayName}: the series page listed no Keyoapp series");
        }

        return results.DistinctBy(r => r.SourceSeriesId).ToList();
    }

    private async Task<SourceSeriesDetail> KeyoappSeriesAsync(string seriesId, CancellationToken ct)
    {
        var url = SeriesUrl(seriesId);
        var page = await FetchAsync(url, ct);
        var doc = page.Document;
        var status = doc.QuerySelectorAll("span")
            .FirstOrDefault(s => string.Equals(Clean(s.TextContent), "Status", StringComparison.OrdinalIgnoreCase))
            ?.ParentElement?.NextElementSibling?.TextContent;
        return new SourceSeriesDetail(
            seriesId,
            Clean(doc.QuerySelector("h1")?.TextContent) ?? Meta(page, "og:title") ?? seriesId,
            url.AbsoluteUri,
            Unproxied(Meta(page, "og:image")),
            Meta(page, "og:description"),
            CustomSourceEnums.Status(Clean(status)));
    }

    private async Task<IReadOnlyList<ListedChapter>> KeyoappEntriesAsync(string seriesId, CancellationToken ct)
    {
        var page = await FetchAsync(SeriesUrl(seriesId), ct);
        var items = page.Document.QuerySelectorAll("#chapters > a[href]").ToList();
        if (items.Count == 0)
        {
            throw new SourceErrorException($"{DisplayName}: no Keyoapp chapter list on {page.Url.AbsolutePath}");
        }

        var entries = new List<ListedChapter>();
        foreach (var item in items.Take(MaxChapters))
        {
            if (Unreleased(item)
                || Resolve(page.Url, item.GetAttribute("href")) is not { } href
                || CustomTemplate.Match(_base, ChapterTemplate, href, CustomTemplate.ChapterId) is not { } chapterId)
            {
                continue;
            }

            var label = Clean(item.GetAttribute("title")) ?? Clean(item.QuerySelector("span.truncate, span")?.TextContent);
            var parsed = ChapterNumberParser.Parse(label);
            var title = parsed.Number is null ? label : null;
            parsed = parsed.OrSlugNumber(href.AbsoluteUri);
            entries.Add(new ListedChapter(
                new SourceChapter(
                    Name, seriesId, chapterId, label, parsed.Number, parsed.Volume, title, Definition.Language,
                    MadaraDate(item.GetAttribute("d")), ChapterUrl(seriesId, chapterId).AbsoluteUri),
                href.AbsoluteUri));
        }

        return entries;
    }

    private async Task<List<string>> KeyoappPagesAsync(Uri chapterUrl, CancellationToken ct)
    {
        var page = await FetchAsync(chapterUrl, ct);
        var uids = page.Document.QuerySelectorAll("#pages img[uid]").Select(i => i.GetAttribute("uid")).OfType<string>().ToList();
        if (uids.Count == 0)
        {
            throw new ChapterLockedException($"{DisplayName}: chapter {chapterUrl.AbsolutePath} shows no pages (locked or not out yet)");
        }

        var imageBase = KeyoappImageBase().Match(page.Body) is { Success: true } m
            ? m.Groups[1].Value
            : throw new SourceErrorException($"{DisplayName}: the chapter page names no image address for its pages");
        return ImageUrls(page, uids.Select(uid => imageBase + uid));
    }

    /// <summary>An upcoming, coin or locked chapter: the icon is the same on every Keyoapp site whatever its language.</summary>
    private static bool Unreleased(IElement item) =>
        item.QuerySelector("img[src*='clock'], img[src*='Coin'], img[src*='coin'], img[src*='lock']") is not null;

    /// <summary>Keyoapp serves covers through the wsrv.nl image proxy; the image itself is in its <c>url</c> parameter.</summary>
    private static string? Unproxied(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.EndsWith("wsrv.nl", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var inner = System.Web.HttpUtility.ParseQueryString(uri.Query)["url"];
        if (string.IsNullOrEmpty(inner))
        {
            return url;
        }

        return inner.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? inner : "https://" + inner;
    }

    private static string? KeyoappCover(Uri pageUrl, string? style)
    {
        var raw = style is null ? null : Regex.Match(style, @"url\(['""]?([^'"")]+)").Groups[1].Value;
        return string.IsNullOrEmpty(raw) ? null : Unproxied(Resolve(pageUrl, raw)?.AbsoluteUri);
    }
}
