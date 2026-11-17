using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class ChapterPageCountServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static SourceMapping Mapping(string source) => new()
    {
        SourceName = source, SourceSeriesId = source + "-series", Url = $"https://{source}.test", Priority = 1, Enabled = true,
    };

    /// <summary>
    /// Floor 3 split into 3.1 and 3.2 on "parts" (with page counts), whole on "whole" (without), and a
    /// chapter 4 on both so floor 3 is not the last one.
    /// </summary>
    private int SeedSplitFloor(Action<Series>? configure = null)
    {
        var seriesId = _db.SeedSeries(configure: configure, mappings: [Mapping("parts"), Mapping("whole")]);
        using var db = _db.NewContext();
        var mappings = db.SourceMappings.Where(m => m.SeriesId == seriesId).ToDictionary(m => m.SourceName);
        void Link(string source, decimal number, int? pages)
        {
            var chapter = db.Chapters.Local.FirstOrDefault(c => c.Number == number)
                          ?? db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = number, Language = "en" }).Entity;
            db.ChapterSourceLinks.Add(new ChapterSourceLink
            {
                Chapter = chapter, SourceMappingId = mappings[source].Id,
                SourceChapterId = $"{source}-{number}", NumberRaw = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PageCount = pages,
            });
        }

        Link("parts", 3.1m, 13);
        Link("parts", 3.2m, 26);
        Link("parts", 4, 50);
        Link("whole", 3, null);
        Link("whole", 4, null);
        db.SaveChanges();
        return seriesId;
    }

    private ChapterPageCountService Counter(params ISource[] sources)
    {
        var db = _db.NewContext();
        var availability = Sources.AllEnabled;
        return new ChapterPageCountService(
            db,
            new ChapterFoldPreviewService(db, availability),
            new SourceRegistry(sources),
            new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
            new PageCountFailures(),
            TimeProvider.System,
            NullLogger<ChapterPageCountService>.Instance);
    }

    private static ChapterPages Pages(int count) =>
        new([.. Enumerable.Range(0, count).Select(i => new PageRequest($"https://whole.test/{i}.jpg"))]);

    [Fact]
    public async Task Fetches_the_count_a_pending_fold_waits_on_and_the_fold_then_settles()
    {
        var seriesId = SeedSplitFloor();
        var asked = new List<string>();
        var whole = new FakeSource
        {
            Name = "whole",
            OnGetPages = c =>
            {
                asked.Add(c.SourceChapterId);
                return Pages(36);
            },
        };

        var run = await Counter(new FakeSource { Name = "parts" }, whole).RunAsync(10, CancellationToken.None);

        Assert.Equal(1, run.Counted);
        Assert.Equal(["whole-3"], asked);
        using var db = _db.NewContext();
        var preview = await new ChapterFoldPreviewService(db, Sources.AllEnabled).ForSeriesAsync(seriesId, CancellationToken.None);
        var three = preview!.Floors.Single(f => f.Floor == 3);
        Assert.Equal("Merged", three.State);
        Assert.Empty(three.MissingCounts);
    }

    [Fact]
    public async Task A_source_that_needs_a_browser_is_not_counted()
    {
        SeedSplitFloor();
        var whole = new FakeSource
        {
            Name = "whole",
            Capabilities = SourceCapabilities.NeedsBrowser,
            OnGetPages = _ => throw new InvalidOperationException("must not be asked"),
        };

        var run = await Counter(new FakeSource { Name = "parts" }, whole).RunAsync(10, CancellationToken.None);

        Assert.Equal(0, run.Counted);
        Assert.Equal(1, run.Skipped);
    }

    [Fact]
    public async Task The_budget_caps_the_requests_of_a_run()
    {
        SeedSplitFloor();
        var whole = new FakeSource { Name = "whole", OnGetPages = _ => Pages(36) };

        var run = await Counter(new FakeSource { Name = "parts" }, whole).RunAsync(0, CancellationToken.None);

        Assert.Equal(0, run.Counted);
        Assert.Equal(1, run.Waiting);
    }

    [Fact]
    public async Task A_failed_count_is_not_retried_in_the_next_run()
    {
        SeedSplitFloor();
        var calls = 0;
        var whole = new FakeSource
        {
            Name = "whole",
            OnGetPages = _ =>
            {
                calls++;
                throw new InvalidOperationException("layout changed");
            },
        };
        var counter = Counter(new FakeSource { Name = "parts" }, whole);

        await counter.RunAsync(10, CancellationToken.None);
        var second = await counter.RunAsync(10, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(1, second.Skipped);
    }
}
