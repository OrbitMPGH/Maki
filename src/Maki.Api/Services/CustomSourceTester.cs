using Maki.Core.Sources;
using Maki.Core.Sources.Custom;
using Maki.Sources.Custom;

namespace Maki.Api.Services;

/// <summary>
/// Runs an unsaved definition against its sample: search, series, chapter list, pages and one
/// image, each reported on its own so the admin sees which part broke. Errors are the engine's or
/// the site's own words and pass through as they are.
/// </summary>
public static class CustomSourceTester
{
    /// <param name="Status"><c>ok</c>, <c>failed</c> or <c>skipped</c>.</param>
    /// <param name="ErrorKey">Maki's own reason, a catalogue key the controller words.</param>
    /// <param name="Error">What the engine or the site said, passed through as it is.</param>
    public record Step(string Name, string Status, string? ErrorKey = null, string? Error = null, object? Detail = null);

    public record Report(bool Passed, IReadOnlyList<Step> Steps);

    public static async Task<Report> RunAsync(CustomSource source, CancellationToken ct)
    {
        var definition = source.Definition;
        var steps = new List<Step>();
        var seriesId = source.ResolveSeriesIdFromUrl(new Uri(definition.Sample.SeriesUrl));

        if (!definition.HasSearch)
        {
            steps.Add(new Step("search", "skipped"));
        }
        else
        {
            steps.Add(await RunStep("search", async () =>
            {
                var results = await source.SearchAsync(definition.Sample.Query ?? "", ct);
                var found = seriesId is not null && results.Any(r => r.SourceSeriesId == seriesId);
                var detail = new { count = results.Count, found, titles = results.Take(5).Select(r => r.Title).ToList() };
                return found ? (null, detail) : ("error.customSource.test.sampleNotInSearch", detail);
            }));
        }

        if (seriesId is null)
        {
            steps.Add(new Step("series", "failed", "error.customSource.test.sampleSeriesUnresolved"));
            return new Report(false, steps);
        }

        steps.Add(await RunStep("series", async () =>
        {
            var series = await source.GetSeriesAsync(seriesId, ct);
            return (null, new { id = seriesId, series.Title, series.CoverUrl, series.Status, description = series.Description?.Length > 200 ? series.Description[..200] : series.Description });
        }));

        IReadOnlyList<CustomSource.ListedChapter> listed = [];
        steps.Add(await RunStep("chapters", async () =>
        {
            listed = await source.ListEntriesAsync(seriesId, ct);
            var chapters = SourceChapterList.Normalize(listed.Select(e => e.Chapter));

            // Downloads rebuild a chapter's URL from its ids alone, so the template must give back
            // exactly the link the row carried.
            var mismatched = listed
                .Where(e => e.ItemHref is not null && e.Chapter.Url is not null && !CustomSourceService.SameUrl(e.ItemHref, e.Chapter.Url))
                .Select(e => new { link = e.ItemHref, built = e.Chapter.Url })
                .Take(5)
                .ToList();
            var detail = new
            {
                count = chapters.Count,
                unnumbered = chapters.Count(c => c.Number is null),
                first = chapters.Take(5).Select(Summary).ToList(),
                last = chapters.Count > 5 ? chapters.TakeLast(5).Select(Summary).ToList() : [],
                mismatched
            };
            return mismatched.Count > 0
                ? ("error.customSource.test.urlTemplateMismatch", detail)
                : (null, detail);
        }));

        var sample = CustomSourceService.SampleChapter(source, definition, listed.Select(e => e.Chapter));
        if (sample is null)
        {
            steps.Add(listed.Count == 0
                ? new Step("pages", "skipped")
                : new Step("pages", "failed", "error.customSource.test.sampleChapterNotListed"));
            steps.Add(new Step("image", "skipped"));
            return new Report(false, steps);
        }

        IReadOnlyList<PageRequest> pages = [];
        steps.Add(await RunStep("pages", async () =>
        {
            pages = (await source.GetPagesAsync(sample, ct)).Pages;
            return (null, new { count = pages.Count, chapter = Summary(sample), first = pages.Take(3).Select(p => p.Url).ToList() });
        }));

        if (pages.Count == 0)
        {
            steps.Add(new Step("image", "skipped"));
        }
        else
        {
            steps.Add(await RunStep("image", async () =>
            {
                var first = pages[0];
                var bytes = first.Fetch is { } fetch
                    ? await fetch(ct)
                    : await source.FetchImageAsync(first.Url, first.Headers ?? new Dictionary<string, string>(), ct);
                var format = Sniff(bytes);
                var detail = new { bytes = bytes.Length, format };
                return format is null ? ("error.customSource.test.notAnImage", detail) : (null, detail);
            }));
        }

        return new Report(steps.All(s => s.Status != "failed"), steps);
    }

    private static object Summary(SourceChapter c) => new { c.SourceChapterId, c.NumberRaw, c.Number, c.Volume, c.Title, c.ReleaseDate, c.Url };

    private static async Task<Step> RunStep(string name, Func<Task<(string? ErrorKey, object? Detail)>> run)
    {
        try
        {
            var (errorKey, detail) = await run();
            return new Step(name, errorKey is null ? "ok" : "failed", errorKey, Detail: detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Step(name, "failed", Error: ex.Message);
        }
    }

    private static string? Sniff(byte[] b) => b switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "jpeg",
        [0x89, 0x50, 0x4E, 0x47, ..] => "png",
        [0x47, 0x49, 0x46, 0x38, ..] => "gif",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "webp",
        [_, _, _, _, 0x66, 0x74, 0x79, 0x70, 0x61, 0x76, 0x69, ..] => "avif",
        _ => null
    };
}
