using System.Globalization;
using Maki.Core.Parsing;
using Maki.Core.Sources;
using Maki.Core.Sources.Custom;
using Maki.Sources.Common;

namespace Maki.Sources.Custom;

/// <summary>
/// The <c>madara</c> engine: <see cref="MadaraParser"/> in place of the definition's sections.
/// Search is the theme's GET search, and the chapter list is either rendered into the series page
/// or loaded by POST, from the newer <c>{series}/ajax/chapters/</c> endpoint or the older
/// <c>admin-ajax.php</c> one.
/// </summary>
public sealed partial class CustomSource
{
    private static readonly string[] MadaraDateFormats =
        ["MMMM d, yyyy", "MMM d, yyyy", "MMM d, yy", "d MMMM yyyy", "d MMM yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "dd.MM.yyyy"];

    private async Task<IReadOnlyList<SourceSeriesResult>> MadaraSearchAsync(string title, CancellationToken ct)
    {
        var page = await FetchAsync(new Uri(_base, $"?s={Uri.EscapeDataString(title)}&post_type=wp-manga"), ct);
        var results = new List<SourceSeriesResult>();
        foreach (var item in MadaraParser.ParseArchive(page.Document))
        {
            if (Resolve(page.Url, item.Href) is { } href && ResolveSeriesIdFromUrl(href) is { } id)
            {
                results.Add(new SourceSeriesResult(id, item.Title, SeriesUrl(id).AbsoluteUri, Resolve(page.Url, item.CoverUrl)?.AbsoluteUri));
            }
        }

        return results.DistinctBy(r => r.SourceSeriesId).ToList();
    }

    private async Task<SourceSeriesDetail> MadaraSeriesAsync(string seriesId, CancellationToken ct)
    {
        var url = SeriesUrl(seriesId);
        var page = await FetchAsync(url, ct);
        var info = MadaraParser.ParseSeries(page.Document);
        return new SourceSeriesDetail(
            seriesId,
            info.Title ?? Meta(page, "og:title") ?? seriesId,
            url.AbsoluteUri,
            Resolve(page.Url, info.CoverUrl ?? Meta(page, "og:image"))?.AbsoluteUri,
            info.Description ?? Meta(page, "og:description"),
            CustomSourceEnums.Status(info.Status));
    }

    private async Task<IReadOnlyList<ListedChapter>> MadaraEntriesAsync(string seriesId, CustomEngineOptions options, CancellationToken ct)
    {
        var seriesUrl = SeriesUrl(seriesId);
        var page = options.ChapterList == CustomEngineOptions.Ajax
            ? await MadaraAjaxChaptersAsync(seriesUrl, ct)
            : await FetchAsync(seriesUrl, ct);

        var items = MadaraParser.ParseChapters(page.Document);
        if (items.Count == 0)
        {
            throw new SourceErrorException($"{DisplayName}: no Madara chapter list on {page.Url.AbsolutePath}");
        }

        var seriesPath = seriesUrl.AbsolutePath;
        var entries = new List<ListedChapter>();
        foreach (var item in items.Take(MaxChapters))
        {
            if (Resolve(page.Url, item.Href) is not { } href
                || !href.AbsolutePath.StartsWith(seriesPath, StringComparison.Ordinal)
                || CustomTemplate.Match(_base, ChapterTemplate, href, CustomTemplate.ChapterId) is not { } chapterId)
            {
                continue;
            }

            // A name with no number is the chapter's title, and the number then comes from the URL.
            var parsed = ChapterNumberParser.Parse(item.Name, item.VolumeRaw);
            var title = parsed.Number is null ? item.Name : null;
            parsed = parsed.OrSlugNumber(href.AbsoluteUri);

            entries.Add(new ListedChapter(
                new SourceChapter(
                    Name, seriesId, chapterId, item.Name, parsed.Number, parsed.Volume, title, Definition.Language,
                    MadaraDate(item.DateText), ChapterUrl(seriesId, chapterId).AbsoluteUri),
                href.AbsoluteUri));
        }

        return entries;
    }

    private async Task<CustomPage> MadaraAjaxChaptersAsync(Uri seriesUrl, CancellationToken ct)
    {
        try
        {
            var page = await FetchAsync(new Uri(seriesUrl, "ajax/chapters/"), ct, formBody: "");
            if (MadaraParser.ParseChapters(page.Document).Count > 0)
            {
                return page;
            }
        }
        catch (HttpRequestException)
        {
            // Older installs answer 404 or 400 here and only have the admin-ajax endpoint.
        }

        var series = await FetchAsync(seriesUrl, ct);
        if (MadaraParser.ParseChapters(series.Document).Count > 0)
        {
            return series;
        }

        var postId = MadaraParser.PostId(series.Document)
            ?? throw new SourceErrorException($"{DisplayName}: the series page has no Madara post id to load chapters with");
        return await FetchAsync(new Uri(_base, "wp-admin/admin-ajax.php"), ct,
            $"action=manga_get_chapters&manga={Uri.EscapeDataString(postId)}");
    }

    private async Task<List<string>> MadaraPagesAsync(Uri chapterUrl, CancellationToken ct)
    {
        var page = await FetchAsync(chapterUrl, ct);
        if (page.Document.QuerySelector("#chapter-protector-data") is not null)
        {
            throw new SourceErrorException($"{DisplayName}: this chapter is behind Madara's chapter protector, which Maki can't read");
        }

        var urls = ImageUrls(page, MadaraParser.ParsePages(page.Document).Select(MadaraParser.ImageUrl));
        if (urls.Count == 0)
        {
            throw new SourceErrorException($"{DisplayName}: no page images in div.reading-content");
        }

        return urls;
    }

    /// <summary>Relative text ("3 days ago"), then the absolute shapes Madara sites print, in the source's language first.</summary>
    private DateTime? MadaraDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (RelativeDate.Parse(text) is { } relative)
        {
            return relative;
        }

        const DateTimeStyles styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        foreach (var culture in new[] { CustomSourceEnums.Culture(Definition.Language), CultureInfo.InvariantCulture })
        {
            if (DateTime.TryParseExact(text.Trim(), MadaraDateFormats, culture, styles, out var date))
            {
                return date;
            }
        }

        return null;
    }
}
