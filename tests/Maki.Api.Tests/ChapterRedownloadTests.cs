using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Re-downloading a series' chapters from the source that won a comparison. What it must leave
/// alone matters more than what it queues: the user asked to prefer a source, not to replace files.
/// </summary>
public class ChapterRedownloadTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>A source that lists exactly the chapter numbers given.</summary>
    private static FakeSource Source(string name, params decimal[] chapters) => new()
    {
        Name = name,
        OnListChapters = seriesId =>
            [.. chapters.Select(n => new SourceChapter(
                name, seriesId, n.ToString(), n.ToString(), n, null, null, "en", null))]
    };

    private static SourceMapping Mapping(string name, int priority) => new()
    {
        SourceName = name,
        SourceSeriesId = "s",
        Url = $"https://{name}.test/s",
        Priority = priority
    };

    private ChapterController BuildController(params ISource[] sources)
    {
        var registry = new SourceRegistry(sources);
        var queue = new DownloadQueueService(
            _db.ScopeFactory(), TimeProvider.System,
            Sources.Resolver(registry), NullLogger<DownloadQueueService>.Instance);

        return new ChapterController(
            _db.NewContext(), queue, null!, null!, registry,
            new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
            new DownloadBatchNotifier(
                new RecordingNotifications(), new RecordingInbox(), TimeProvider.System,
                NullLogger<DownloadBatchNotifier>.Instance),
            new TestCurrentUser(1), NullLogger<ChapterController>.Instance);
    }

    /// <summary>Seeds a series whose chapters already have files from the named sources.</summary>
    private int SeedDownloaded(params (decimal Number, string From)[] chapters)
    {
        var seriesId = _db.SeedSeries("Test Series", mappings: [Mapping("good", 1), Mapping("bad", 2)]);

        using var db = _db.NewContext();
        foreach (var (number, from) in chapters)
        {
            var file = new ChapterFile
            {
                SeriesId = seriesId,
                RelativePath = $"Test Series/ch{number}.cbz",
                Size = 1,
                SourceName = from,
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(file);
            db.SaveChanges();

            db.Chapters.Add(new Chapter
            {
                SeriesId = seriesId,
                Number = number,
                Language = "en",
                Wanted = true,
                ChapterFileId = file.Id
            });
        }

        db.SaveChanges();
        return seriesId;
    }

    private static (int Queued, int Unavailable) Result(IActionResult result)
    {
        var value = Assert.IsType<OkObjectResult>(result).Value!;
        return ((int)value.GetType().GetProperty("queued")!.GetValue(value)!,
                (int)value.GetType().GetProperty("unavailable")!.GetValue(value)!);
    }

    [Fact]
    public async Task Queues_only_the_chapters_that_came_from_another_source()
    {
        var seriesId = SeedDownloaded((1m, "good"), (2m, "bad"), (3m, "bad"));
        var controller = BuildController(Source("good", 1m, 2m, 3m), Source("bad", 1m, 2m, 3m));

        var (queued, unavailable) = Result(
            await controller.Redownload(new(seriesId, "good"), default));

        // Chapter 1 already came from the winner and is left alone.
        Assert.Equal(2, queued);
        Assert.Equal(0, unavailable);
    }

    [Theory]
    // The user's own file, brought in from disk.
    [InlineData("import")]
    // A grabbed release. Its "source" is an indexer, not something this can re-fetch from.
    [InlineData("torrent:Nyaa")]
    public async Task Files_that_did_not_come_from_a_scrape_source_are_never_replaced(string from)
    {
        // Neither name resolves in the source registry, which is what keeps them out without this
        // having to know the sentinels by heart.
        var seriesId = SeedDownloaded((1m, from), (2m, "bad"));
        var controller = BuildController(Source("good", 1m, 2m), Source("bad", 1m, 2m));

        var (queued, unavailable) = Result(await controller.Redownload(new(seriesId, "good"), default));

        Assert.Equal(1, queued);
        Assert.Equal(0, unavailable);
    }

    [Fact]
    public async Task Chapters_the_winner_does_not_carry_are_reported_not_queued()
    {
        // Re-fetching these would resolve straight back to the source they already came from, which
        // is work for no change.
        var seriesId = SeedDownloaded((1m, "bad"), (2m, "bad"), (50m, "bad"));
        var controller = BuildController(Source("good", 1m, 2m), Source("bad", 1m, 2m, 50m));

        var (queued, unavailable) = Result(await controller.Redownload(new(seriesId, "good"), default));

        Assert.Equal(2, queued);
        Assert.Equal(1, unavailable);
    }

    [Fact]
    public async Task An_unmapped_source_is_a_404()
    {
        var seriesId = SeedDownloaded((1m, "bad"));
        var controller = BuildController(Source("good", 1m), Source("bad", 1m));

        Assert.IsType<NotFoundResult>(await controller.Redownload(new(seriesId, "elsewhere"), default));
    }

    [Fact]
    public async Task Nothing_to_do_lists_no_chapters_and_makes_no_request()
    {
        var seriesId = SeedDownloaded((1m, "good"), (2m, "good"));
        var good = Source("good", 1m, 2m);
        var controller = BuildController(good, Source("bad", 1m, 2m));

        var (queued, unavailable) = Result(await controller.Redownload(new(seriesId, "good"), default));

        Assert.Equal(0, queued);
        Assert.Equal(0, unavailable);
        // The listing costs a network round trip against a rate-limited source; there is nothing to
        // check it against when no chapter is a candidate.
        Assert.Equal(0, good.ListCalls);
    }
}
