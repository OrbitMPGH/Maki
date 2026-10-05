using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>Which chapter the Discover preview opens on a source's listing, and how a run ends.</summary>
[Collection(ConfigDirCollection.Name)]
public class SeriesPreviewTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "maki-preview-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static SourceChapter Chapter(decimal? number, string id = "") =>
        new("fake", "s", id == "" ? number?.ToString() ?? "x" : id, number?.ToString(), number, null, null, "en", null);

    [Fact]
    public void Picks_chapter_one_even_when_a_prologue_is_listed()
    {
        var picked = SeriesPreviewService.PickFirstChapter([Chapter(3), Chapter(0), Chapter(1), Chapter(2)]);

        Assert.Equal(1m, picked?.Number);
    }

    [Fact]
    public void Falls_back_to_the_lowest_numbered_chapter()
    {
        // MangaDex drops old scans, so a listing can start well past chapter 1.
        var picked = SeriesPreviewService.PickFirstChapter([Chapter(52), Chapter(48), Chapter(50)]);

        Assert.Equal(48m, picked?.Number);
    }

    [Fact]
    public void Unnumbered_extras_are_never_picked()
    {
        Assert.Equal(7m, SeriesPreviewService.PickFirstChapter([Chapter(null, "oneshot"), Chapter(7)])?.Number);
        Assert.Null(SeriesPreviewService.PickFirstChapter([Chapter(null, "oneshot")]));
    }

    [Fact]
    public void An_empty_listing_has_no_first_chapter() =>
        Assert.Null(SeriesPreviewService.PickFirstChapter([]));

    private static SeriesPreviewService.ListedCandidate Listed(string name, decimal? first) =>
        new(new SourceCandidate(new FakeSource { Name = name }, "s", null), first is null ? null : Chapter(first), null);

    [Fact]
    public void A_source_with_chapter_one_beats_a_higher_ranked_one_that_starts_later()
    {
        // Official sites tend to list only the newest free chapters, and a preview of chapter 3 is
        // not what anyone opened it for.
        var order = SeriesPreviewService.FetchOrder(
            [Listed("webtoons", 3), Listed("empty", null), Listed("mangadex", 1), Listed("weebcentral", 1)]);

        Assert.Equal(["mangadex", "weebcentral", "webtoons"], order.Select(l => l.Candidate.Source.Name));
    }

    private SeriesPreviewService Service(params FakeSource[] sources)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddScoped(sp => new SourceMatchService(
            sp.GetRequiredService<MakiDbContext>(), new SourceRegistry(sources),
            new FakeAppSettings().Set(SettingKeys.SourcePriorityOrder, string.Join(",", sources.Select(s => s.Name))),
            Sources.AllEnabled, new SourceExternalIdCache(TimeProvider.System),
            new SourceMatchSearchCache(TimeProvider.System), NullLogger<SourceMatchService>.Instance));

        var queue = new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance);
        var prior = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        AppPaths paths;
        try
        {
            Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
            paths = new AppPaths();
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", prior);
        }

        return new SeriesPreviewService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
            new PageDownloader(new StubHttpClientFactory(""), queue, TimeProvider.System, NullLogger<PageDownloader>.Instance),
            queue, paths, NullLogger<SeriesPreviewService>.Instance);
    }

    private static readonly Series Ippo = new() { Title = "Hajime no Ippo" };

    private static SourceSeriesResult Hit() => new(SourceSeriesId: "series", Title: "Hajime no Ippo", Url: "https://x.test/s");

    private static async Task<SeriesPreviewSnapshot> Settled(SeriesPreviewService service, long providerId, int userId)
    {
        for (var i = 0; i < 500; i++)
        {
            var snapshot = service.Snapshot(providerId, userId, new TestLocalizer());
            if (snapshot is { Status: "failed" or "ready" })
            {
                return snapshot;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Preview never settled");
    }

    [Fact]
    public async Task A_source_whose_pages_time_out_hands_over_to_the_next_one()
    {
        // An HttpClient timeout is a TaskCanceledException while the job's own token is live. It
        // used to end the whole preview as "timed out" without asking the next source.
        var asked = 0;
        var slow = new FakeSource
        {
            Name = "slow",
            OnSearch = _ => [Hit()],
            OnListChapters = _ => [Chapter(1)],
            OnGetPages = _ => throw new TaskCanceledException("HttpClient.Timeout", new TimeoutException()),
        };
        var next = new FakeSource
        {
            Name = "next",
            OnSearch = _ => [Hit()],
            OnListChapters = _ => [Chapter(1)],
            OnGetPages = _ =>
            {
                Interlocked.Increment(ref asked);
                return new ChapterPages([]);
            },
        };
        var service = Service(slow, next);

        service.Start(1, Ippo, 1, new TestLocalizer());
        var snapshot = await Settled(service, 1, 1);

        Assert.Equal(1, asked);
        Assert.Equal("error.preview.noChapter", snapshot.Error);
    }

    [Fact]
    public async Task A_source_whose_listing_times_out_does_not_end_the_preview()
    {
        var asked = 0;
        var slow = new FakeSource
        {
            Name = "slow",
            OnSearch = _ => [Hit()],
            ListThrows = new TaskCanceledException("HttpClient.Timeout", new TimeoutException()),
        };
        var next = new FakeSource
        {
            Name = "next",
            OnSearch = _ => [Hit()],
            OnListChapters = _ => [Chapter(1)],
            OnGetPages = _ =>
            {
                Interlocked.Increment(ref asked);
                return new ChapterPages([]);
            },
        };
        var service = Service(slow, next);

        service.Start(1, Ippo, 1, new TestLocalizer());
        var snapshot = await Settled(service, 1, 1);

        Assert.Equal(1, asked);
        Assert.Equal("error.preview.noChapter", snapshot.Error);
    }

    [Fact]
    public async Task Finished_previews_past_the_cap_are_dropped_oldest_first()
    {
        var service = Service();
        var count = SeriesPreviewService.MaxFinishedJobs + 1;

        for (var id = 1; id <= count; id++)
        {
            service.Start(id, Ippo, id, new TestLocalizer());
            await Settled(service, id, id);
        }

        for (var i = 0; i < 500 && service.Snapshot(1, 1, new TestLocalizer()) is not null; i++)
        {
            await Task.Delay(10);
        }

        Assert.Null(service.Snapshot(1, 1, new TestLocalizer()));
        for (var id = 2; id <= count; id++)
        {
            Assert.NotNull(service.Snapshot(id, id, new TestLocalizer()));
        }
    }
}
