using System.Globalization;
using System.Text.Json;
using Maki.Core.Parsing;
using Maki.Core.Sources;
using Maki.Core.Sources.Custom;
using Maki.Sources.Common;

namespace Maki.Sources.Custom;

/// <summary>
/// The <c>heancms</c> engine: everything comes from the site's JSON API (<c>api.{host}</c> unless
/// <see cref="CustomEngineOptions.ApiUrl"/> says otherwise), and the website's own URLs are only
/// used as links. Paid chapters (<c>price</c> above 0) are left out of the list.
/// </summary>
public sealed partial class CustomSource
{
    private const int HeanPageSize = 100;

    /// <summary>The API origin, or null when the option names something that isn't the site's host or a subdomain of it.</summary>
    public static Uri? HeanApi(CustomEngineOptions options, Uri baseUri)
    {
        var raw = options.ApiUrl ?? $"https://api.{CustomHost.Strip(baseUri.Host)}";
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var api) || api.Scheme is not ("https" or "http"))
        {
            return null;
        }

        var site = CustomHost.Strip(baseUri.Host);
        var host = CustomHost.Strip(api.Host);
        return string.Equals(host, site, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase)
            ? new Uri(api.GetLeftPart(UriPartial.Authority) + api.AbsolutePath.TrimEnd('/') + "/")
            : null;
    }

    private Uri Api(string relative) =>
        new(_api ?? throw new SourceErrorException($"{DisplayName}: engineOptions.apiUrl is not on the site's host"), relative);

    private async Task<JsonDocument> HeanJsonAsync(string relative, CancellationToken ct)
    {
        var page = await FetchAsync(Api(relative), ct);
        try
        {
            return JsonDocument.Parse(PreUnwrap.Unwrap(page.Body, page.Url.AbsoluteUri));
        }
        catch (JsonException ex)
        {
            throw new SourceErrorException($"{DisplayName}: the API answered {relative} with something that isn't JSON ({ex.Message})");
        }
    }

    private async Task<IReadOnlyList<SourceSeriesResult>> HeanSearchAsync(string title, CancellationToken ct)
    {
        using var json = await HeanJsonAsync($"query?query_string={Uri.EscapeDataString(title)}&perPage=30&page=1", ct);
        var results = new List<SourceSeriesResult>();
        foreach (var item in Array(json.RootElement, "data"))
        {
            if (Str(item, "series_slug") is not { } slug || Str(item, "series_type") is "Novel")
            {
                continue;
            }

            results.Add(new SourceSeriesResult(slug, Str(item, "title") ?? slug, SeriesUrl(slug).AbsoluteUri, Str(item, "thumbnail")));
        }

        return results;
    }

    private async Task<SourceSeriesDetail> HeanSeriesAsync(string seriesId, CancellationToken ct)
    {
        using var json = await HeanJsonAsync($"series/{Uri.EscapeDataString(seriesId)}", ct);
        var root = json.RootElement;
        return new SourceSeriesDetail(
            seriesId,
            Str(root, "title") ?? seriesId,
            SeriesUrl(seriesId).AbsoluteUri,
            Str(root, "thumbnail"),
            Clean(Str(root, "description")),
            CustomSourceEnums.Status(Str(root, "status")));
    }

    private async Task<IReadOnlyList<ListedChapter>> HeanEntriesAsync(string seriesId, CancellationToken ct)
    {
        int seriesKey;
        using (var series = await HeanJsonAsync($"series/{Uri.EscapeDataString(seriesId)}", ct))
        {
            seriesKey = series.RootElement.TryGetProperty("id", out var id) && id.TryGetInt32(out var value)
                ? value
                : throw new SourceErrorException($"{DisplayName}: the API's series {seriesId} has no id");
        }

        var entries = new List<ListedChapter>();
        var seen = 0;
        for (var page = 1; page <= MaxListPages && entries.Count < MaxChapters; page++)
        {
            using var json = await HeanJsonAsync($"chapter/query?page={page}&perPage={HeanPageSize}&series_id={seriesKey}", ct);
            var items = Array(json.RootElement, "data").ToList();
            seen += items.Count;
            foreach (var item in items)
            {
                if (Str(item, "chapter_slug") is not { } chapterId
                    || item.TryGetProperty("price", out var price) && price.ValueKind == JsonValueKind.Number && price.GetDecimal() > 0)
                {
                    continue;
                }

                var label = Str(item, "chapter_name");
                var parsed = ChapterNumberParser.Parse(label);
                var title = parsed.Number is null ? label : Str(item, "chapter_title");
                parsed = parsed.OrSlugNumber(chapterId);
                var url = ChapterUrl(seriesId, chapterId).AbsoluteUri;
                entries.Add(new ListedChapter(
                    new SourceChapter(Name, seriesId, chapterId, label, parsed.Number, parsed.Volume, title, Definition.Language,
                        Date(Str(item, "created_at")), url),
                    url));
            }

            var last = json.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("last_page", out var lp) && lp.TryGetInt32(out var n) ? n : page;
            if (page >= last || items.Count == 0)
            {
                break;
            }
        }

        if (seen == 0)
        {
            throw new SourceErrorException($"{DisplayName}: the API listed no chapters for {seriesId}");
        }

        return entries;
    }

    private async Task<List<string>> HeanPagesAsync(SourceChapter chapter, CancellationToken ct)
    {
        using var json = await HeanJsonAsync(
            $"chapter/{Uri.EscapeDataString(chapter.SourceSeriesId)}/{Uri.EscapeDataString(chapter.SourceChapterId)}", ct);
        var root = json.RootElement.TryGetProperty("chapter", out var c) ? c : json.RootElement;
        var images = root.TryGetProperty("chapter_data", out var data) && data.ValueKind == JsonValueKind.Object
            ? Array(data, "images")
            : Array(json.RootElement, "data");
        var urls = images.Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : null).ToList();
        var page = new CustomPage("", Api(""));
        var resolved = ImageUrls(page, urls);
        if (resolved.Count == 0)
        {
            throw new ChapterLockedException($"{DisplayName}: chapter {chapter.SourceChapterId} has no pages (paid or not out yet)");
        }

        return resolved;
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static string? Str(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? Date(string? iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
}
