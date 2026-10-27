using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Maki.Core.Parsing;
using Maki.Core.Sources;
using Maki.Core.Sources.Custom;
using Maki.Sources.Common;

namespace Maki.Sources.Custom;

/// <summary>
/// The <c>mangathemesia</c> engine: the MangaReader WordPress theme. Series under
/// <c>/{seriesPath}/{slug}/</c>, chapters at the site root, the chapter list in <c>#chapterlist</c>,
/// and the page list in the <c>ts_reader.run({...})</c> call, with <c>#readerarea</c>'s images as
/// the fallback.
/// </summary>
public sealed partial class CustomSource
{
    private const string ThemesiaChapterItems = "#chapterlist li, div.eplister li, div.bxcl li, div.cl li";

    [GeneratedRegex(@"ts_reader\.run\((\{.*?\})\);", RegexOptions.Singleline)]
    private static partial Regex TsReader();

    private async Task<IReadOnlyList<SourceSeriesResult>> ThemesiaSearchAsync(string title, CancellationToken ct)
    {
        var page = await FetchAsync(new Uri(_base, $"?s={Uri.EscapeDataString(title)}"), ct);
        var results = new List<SourceSeriesResult>();
        foreach (var card in page.Document.QuerySelectorAll("div.bsx, div.listupd .bs"))
        {
            var link = card.QuerySelector("a[href]");
            if (Resolve(page.Url, link?.GetAttribute("href")) is not { } href || ResolveSeriesIdFromUrl(href) is not { } id)
            {
                continue;
            }

            var name = Clean(card.QuerySelector(".tt")?.TextContent) ?? Clean(link!.GetAttribute("title")) ?? id;
            var cover = Resolve(page.Url, MadaraParser.ImageUrl(card.QuerySelector("img")))?.AbsoluteUri;
            results.Add(new SourceSeriesResult(id, name, SeriesUrl(id).AbsoluteUri, cover));
        }

        return results.DistinctBy(r => r.SourceSeriesId).ToList();
    }

    private async Task<SourceSeriesDetail> ThemesiaSeriesAsync(string seriesId, CancellationToken ct)
    {
        var url = SeriesUrl(seriesId);
        var page = await FetchAsync(url, ct);
        var doc = page.Document;
        var status = doc.QuerySelectorAll(".imptdt, .tsinfo .imptdt, .infotable tr")
            .FirstOrDefault(e => e.TextContent.Contains("Status", StringComparison.OrdinalIgnoreCase))
            ?.QuerySelector("i, a, td:last-child")?.TextContent;
        return new SourceSeriesDetail(
            seriesId,
            Clean(doc.QuerySelector("h1.entry-title")?.TextContent) ?? Meta(page, "og:title") ?? seriesId,
            url.AbsoluteUri,
            Resolve(page.Url, MadaraParser.ImageUrl(doc.QuerySelector(".thumb img, .infomanga img")) ?? Meta(page, "og:image"))?.AbsoluteUri,
            Clean(doc.QuerySelector("div.entry-content[itemprop='description'], .desc, .entry-content")?.TextContent) ?? Meta(page, "og:description"),
            CustomSourceEnums.Status(Clean(status)));
    }

    private async Task<IReadOnlyList<ListedChapter>> ThemesiaEntriesAsync(string seriesId, CancellationToken ct)
    {
        var page = await FetchAsync(SeriesUrl(seriesId), ct);
        var items = page.Document.QuerySelectorAll(ThemesiaChapterItems).ToList();
        if (items.Count == 0)
        {
            throw new SourceErrorException($"{DisplayName}: no MangaThemesia chapter list on {page.Url.AbsolutePath}");
        }

        var entries = new List<ListedChapter>();
        foreach (var item in items.Take(MaxChapters))
        {
            if (Resolve(page.Url, item.QuerySelector("a[href]")?.GetAttribute("href")) is not { } href
                || CustomTemplate.Match(_base, ChapterTemplate, href, CustomTemplate.ChapterId) is not { } chapterId)
            {
                continue;
            }

            // One selector at a time: a group matches in document order, and the link wraps .chapternum and the date.
            var label = new[] { ".chapternum", ".lchx", ".eph-num a" }
                .Select(css => Clean(item.QuerySelector(css)?.TextContent))
                .FirstOrDefault(text => text is not null) ?? Clean(item.GetAttribute("data-num"));
            var parsed = ChapterNumberParser.Parse(label);
            var title = parsed.Number is null ? label : null;
            parsed = parsed.OrSlugNumber(href.AbsoluteUri);
            if (parsed.Number is null && decimal.TryParse(item.GetAttribute("data-num"), NumberStyles.Number, CultureInfo.InvariantCulture, out var num))
            {
                parsed = parsed with { Number = num, IsOneShot = false };
                title = null;
            }

            entries.Add(new ListedChapter(
                new SourceChapter(
                    Name, seriesId, chapterId, label, parsed.Number, parsed.Volume, title, Definition.Language,
                    MadaraDate(Clean(item.QuerySelector(".chapterdate, .dt")?.TextContent)), ChapterUrl(seriesId, chapterId).AbsoluteUri),
                href.AbsoluteUri));
        }

        return entries;
    }

    private async Task<List<string>> ThemesiaPagesAsync(Uri chapterUrl, CancellationToken ct)
    {
        var page = await FetchAsync(chapterUrl, ct);
        var urls = new List<string>();
        if (TsReader().Match(page.Body) is { Success: true } match)
        {
            try
            {
                using var json = JsonDocument.Parse(match.Groups[1].Value);
                if (json.RootElement.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
                {
                    var images = sources.EnumerateArray()
                        .Select(s => s.TryGetProperty("images", out var i) && i.ValueKind == JsonValueKind.Array ? i : default)
                        .FirstOrDefault(i => i.ValueKind == JsonValueKind.Array && i.GetArrayLength() > 0);
                    if (images.ValueKind == JsonValueKind.Array)
                    {
                        urls = ImageUrls(page, images.EnumerateArray().Select(i => i.GetString()));
                    }
                }
            }
            catch (JsonException)
            {
                // A site that tampers with the call gets the markup fallback below.
            }
        }

        if (urls.Count == 0)
        {
            urls = ImageUrls(page, page.Document.QuerySelectorAll("#readerarea img").Select(MadaraParser.ImageUrl));
        }

        if (urls.Count == 0)
        {
            throw new SourceErrorException($"{DisplayName}: no page images in ts_reader or #readerarea");
        }

        return urls;
    }

    private static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
