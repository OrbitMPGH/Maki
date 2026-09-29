using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Http;
using Maki.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Resolution picking the chapter's own language, a rate-limited listing surfacing as a rate limit,
/// Smart skipping chapters still backing off, and the set-based bulk enqueue.
/// </summary>
public class DownloadPipelineGuardTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private static SourceMapping Mapping(string source, int priority = 1) => new()
    {
        SourceName = source, SourceSeriesId = "s", Url = $"https://{source}.test", Priority = priority, Enabled = true,
    };

    private Chapter SeedChapter(int seriesId, decimal? number, string language = "en", string? title = null)
    {
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = number, Language = language, Title = title };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter;
    }

    [Theory]
    [InlineData("es", "es-1")]
    [InlineData("en", "en-1")]
    public async Task Resolve_picks_the_listing_in_the_chapter_language(string language, string expected)
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("multi")]);
        var chapter = SeedChapter(seriesId, 1m, language);
        var resolver = Sources.Resolver(new SourceRegistry([new FakeSource
        {
            Name = "multi",
            OnListChapters = _ =>
            [
                new("multi", "s", "en-1", "1", 1m, 2, null, "en", null),
                new("multi", "s", "es-1", "1", 1m, 2, null, "es", null),
            ],
        }]));

        using var db = _db.NewContext();
        var resolved = await resolver.ResolveAsync(db, chapter, null, CancellationToken.None);

        Assert.Equal(expected, resolved.SourceChapterId);
    }

    [Fact]
    public async Task Resolve_does_not_stand_in_another_language_for_a_missing_one()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("multi")]);
        var oneShot = SeedChapter(seriesId, null, "es", "Extra");
        var resolver = Sources.Resolver(new SourceRegistry([new FakeSource
        {
            Name = "multi",
            OnListChapters = _ => [new("multi", "s", "en-extra", null, null, null, "Extra", "en", null)],
        }]));

        using var db = _db.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync(db, oneShot, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_rate_limited_listing_surfaces_as_a_rate_limit_naming_the_source()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("busy")]);
        var chapter = SeedChapter(seriesId, 1m);
        var resolver = Sources.Resolver(new SourceRegistry([new FakeSource
        {
            Name = "busy",
            ListThrows = new RateLimitException("429", TimeSpan.FromMinutes(2)),
        }]));

        using var db = _db.NewContext();
        var ex = await Assert.ThrowsAsync<SourceRateLimitedException>(
            () => resolver.ResolveAsync(db, chapter, null, CancellationToken.None));

        Assert.Equal("busy", ex.SourceName);
        Assert.Equal(TimeSpan.FromMinutes(2), ex.RetryAfter);
    }

    private void SeedRow(int seriesId, int chapterId, QueueStatus status, int retries, DateTime? nextAttempt)
    {
        using var db = _db.NewContext();
        db.DownloadQueue.Add(new DownloadQueueItem
        {
            SeriesId = seriesId, ChapterId = chapterId, Protocol = AcquisitionProtocol.Scraper, Status = status,
            RetryCount = retries, NextAttempt = nextAttempt, QueuedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Smart_skips_chapters_whose_last_attempt_failed_and_is_not_due()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var seriesId = _db.SeedSeries();
        var backingOff = SeedChapter(seriesId, 1m);
        var exhausted = SeedChapter(seriesId, 2m);
        var due = SeedChapter(seriesId, 3m);
        var retriedSince = SeedChapter(seriesId, 4m);
        SeedRow(seriesId, backingOff.Id, QueueStatus.Failed, 1, now.AddMinutes(5));
        SeedRow(seriesId, exhausted.Id, QueueStatus.Failed, 5, null);
        SeedRow(seriesId, due.Id, QueueStatus.Failed, 1, now.AddMinutes(-1));
        SeedRow(seriesId, retriedSince.Id, QueueStatus.Failed, 5, null);
        SeedRow(seriesId, retriedSince.Id, QueueStatus.Cancelled, 0, null);

        using var db = _db.NewContext();
        var chapters = db.Chapters.Where(c => c.SeriesId == seriesId).ToList();
        var skipped = await SmartDownloadJob.BackingOffAsync(db, chapters, maxAttempts: 5, now, CancellationToken.None);

        Assert.Equal(new HashSet<int> { backingOff.Id, exhausted.Id }, skipped);
        Assert.Equal([due.Id, retriedSince.Id], Chapter.NextWanted(chapters, 2, skipped));
    }

    [Fact]
    public async Task Bulk_enqueue_queues_in_order_with_one_sort_order_each_and_skips_active_chapters()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("fake")]);
        var first = SeedChapter(seriesId, 1m);
        var active = SeedChapter(seriesId, 2m);
        var third = SeedChapter(seriesId, 3m);
        SeedRow(seriesId, active.Id, QueueStatus.Queued, 0, null);
        var queue = new DownloadQueueService(
            _db.ScopeFactory(), TimeProvider.System, Sources.SingleChapterResolver(null, "fake"),
            NullLogger<DownloadQueueService>.Instance);

        var result = await queue.EnqueueChaptersAsync(
            [third.Id, active.Id, first.Id], DownloadOrigin.Manual, queuedByUserId: 1);

        Assert.Null(result.Error);
        Assert.Equal([third.Id, first.Id], result.Queued.Select(q => q.ChapterId!.Value));
        Assert.True(result.Queued[0].SortOrder < result.Queued[1].SortOrder);
        Assert.All(result.Queued, q => Assert.Equal(DownloadOrigin.Manual, q.Origin));
    }

    [Fact]
    public async Task Bulk_enqueue_skips_an_unmapped_series_and_says_why_but_queues_the_rest()
    {
        var mapped = _db.SeedSeries(title: "Mapped", mappings: [Mapping("fake")]);
        var unmapped = _db.SeedSeries(title: "Unmapped");
        var ok = SeedChapter(mapped, 1m);
        var orphan = SeedChapter(unmapped, 1m);
        var queue = new DownloadQueueService(
            _db.ScopeFactory(), TimeProvider.System, Sources.SingleChapterResolver(null, "fake"),
            NullLogger<DownloadQueueService>.Instance);

        var result = await queue.EnqueueChaptersAsync([orphan.Id, ok.Id], DownloadOrigin.Manual, null);

        Assert.Equal("error.download.noEnabledMapping", result.Error);
        Assert.Equal(ok.Id, Assert.Single(result.Queued).ChapterId);
    }
}
