using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

public sealed class SourceOrderTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly SourceRegistry Registry = new(
        new[] { "poor", "good", "fresh" }.Select(name => new FakeSource
        {
            Name = name,
            OnListChapters = _ => [new(name, "s", "1", "1", 1m, null, null, "en", null)]
        }));

    /// <summary>
    /// Three aggregator sources, priority poor, good, fresh. "poor" measures well under the reference
    /// quality, "good" well over it, "fresh" has never been measured on this series.
    /// </summary>
    private (int SeriesId, int Poor, int Good, int Fresh) Seed(SourceOrderMode? seriesMode, string? instanceDefault)
    {
        using var db = _db.NewContext();
        var root = new RootFolder { Path = Path.GetTempPath() };
        db.RootFolders.Add(root);
        db.SaveChanges();
        var series = new Series
        {
            Title = "S", SortTitle = "s", FolderName = "S", RootFolderId = root.Id, SourceOrderMode = seriesMode
        };
        var poor = new SourceMapping { SourceName = "poor", SourceSeriesId = "s", Priority = 1 };
        var good = new SourceMapping { SourceName = "good", SourceSeriesId = "s", Priority = 2 };
        var fresh = new SourceMapping { SourceName = "fresh", SourceSeriesId = "s", Priority = 3 };
        series.SourceMappings.AddRange([poor, good, fresh]);
        db.Series.Add(series);
        if (instanceDefault is not null)
        {
            db.AppConfig.Add(new AppConfigEntry { Key = SettingKeys.DownloadSourceOrder, Value = instanceDefault });
        }

        db.SaveChanges();
        foreach (var (mapping, bitsPerPixel) in new[] { (poor, 0.5), (good, 3.0) })
        {
            for (var chapter = 1; chapter <= 2; chapter++)
            {
                db.SourceQualitySamples.Add(new SourceQualitySample
                {
                    SourceMappingId = mapping.Id, SeriesId = series.Id, ChapterId = chapter, PageCount = 20,
                    MedianWidth = 1000, MedianHeight = 1500, SizeBytes = (long)(bitsPerPixel * 1000 * 1500 / 8) * 20,
                    ImageFormat = "jpg", MeasuredAtUtc = DateTime.UtcNow
                });
            }
        }

        db.SaveChanges();
        return (series.Id, poor.Id, good.Id, fresh.Id);
    }

    private static async Task<int[]> OrderAsync(MakiDbContext db, int seriesId)
    {
        var mappings = await db.SourceMappings.Where(m => m.SeriesId == seriesId).ToListAsync();
        var result = await new SourceOrderService(Registry, TestQuality.Create(Registry)).OrderAsync(db, seriesId, mappings, CancellationToken.None);
        return [.. result.Ordered.Select(m => m.Id)];
    }

    [Fact]
    public async Task Manual_order_is_the_priority_order_and_is_the_default()
    {
        var (seriesId, poor, good, fresh) = Seed(null, null);
        using var db = _db.NewContext();

        Assert.Equal([poor, good, fresh], await OrderAsync(db, seriesId));
    }

    [Theory]
    [InlineData(SourceOrderMode.Quality, null)]
    [InlineData(null, "quality")]
    public async Task Quality_order_puts_the_better_measured_source_first_and_an_unmeasured_one_last(
        SourceOrderMode? seriesMode, string? instanceDefault)
    {
        var (seriesId, poor, good, fresh) = Seed(seriesMode, instanceDefault);
        using var db = _db.NewContext();

        Assert.Equal([good, poor, fresh], await OrderAsync(db, seriesId));
    }

    [Fact]
    public async Task A_source_unmeasured_here_ranks_after_measured_ones_whatever_its_record_elsewhere()
    {
        var (seriesId, poor, good, fresh) = Seed(SourceOrderMode.Quality, null);
        using (var seed = _db.NewContext())
        {
            var root = seed.RootFolders.First();
            var other = new Series { Title = "O", SortTitle = "o", FolderName = "O", RootFolderId = root.Id };
            var mapping = new SourceMapping { SourceName = "fresh", SourceSeriesId = "o", Priority = 1 };
            other.SourceMappings.Add(mapping);
            seed.Series.Add(other);
            seed.SaveChanges();
            for (var chapter = 1; chapter <= 2; chapter++)
            {
                seed.SourceQualitySamples.Add(new SourceQualitySample
                {
                    SourceMappingId = mapping.Id, SeriesId = other.Id, ChapterId = 100 + chapter, PageCount = 20,
                    MedianWidth = 1500, MedianHeight = 2250, SizeBytes = (long)(3.0 * 1500 * 2250 / 8) * 20,
                    ImageFormat = "jpg", MeasuredAtUtc = DateTime.UtcNow
                });
            }

            seed.SaveChanges();
        }

        using var db = _db.NewContext();

        Assert.Equal([good, poor, fresh], await OrderAsync(db, seriesId));
    }

    private void SeedDownloads(
        int seriesId, int mappingId, int completed, int failed, string errorKey = "error.download.sourceError",
        DateTime? queuedAt = null)
    {
        using var db = _db.NewContext();
        for (var i = 0; i < completed + failed; i++)
        {
            var ok = i < completed;
            db.DownloadQueue.Add(new DownloadQueueItem
            {
                SeriesId = seriesId, SourceMappingId = mappingId, QueuedAt = queuedAt ?? DateTime.UtcNow,
                Status = ok ? QueueStatus.Completed : QueueStatus.Failed, ErrorKey = ok ? null : errorKey
            });
        }

        db.SaveChanges();
    }

    [Fact]
    public async Task A_source_that_keeps_failing_is_tried_last_whatever_its_quality()
    {
        var (seriesId, poor, good, fresh) = Seed(SourceOrderMode.Quality, null);
        SeedDownloads(seriesId, good, completed: 0, failed: 4);
        using var db = _db.NewContext();

        Assert.Equal([poor, fresh, good], await OrderAsync(db, seriesId));
    }

    [Fact]
    public async Task Failures_that_do_not_blame_the_source_do_not_demote_it()
    {
        var (seriesId, poor, good, fresh) = Seed(SourceOrderMode.Quality, null);
        SeedDownloads(seriesId, good, completed: 0, failed: 4, errorKey: "error.download.disk");
        using var db = _db.NewContext();

        Assert.Equal([good, poor, fresh], await OrderAsync(db, seriesId));
    }

    [Fact]
    public async Task Failures_inside_the_reliability_window_demote_a_source()
    {
        var (seriesId, poor, good, fresh) = Seed(SourceOrderMode.Quality, null);
        SeedDownloads(seriesId, good, completed: 0, failed: 4, queuedAt: DateTime.UtcNow - LibraryCompositionService.ReliabilityWindow + TimeSpan.FromHours(1));
        using var db = _db.NewContext();

        Assert.Equal([poor, fresh, good], await OrderAsync(db, seriesId));
    }

    [Fact]
    public async Task Failures_older_than_the_reliability_window_do_not_demote_a_source()
    {
        var (seriesId, poor, good, fresh) = Seed(SourceOrderMode.Quality, null);
        SeedDownloads(seriesId, good, completed: 0, failed: 4, queuedAt: DateTime.UtcNow - LibraryCompositionService.ReliabilityWindow - TimeSpan.FromHours(1));
        using var db = _db.NewContext();

        Assert.Equal([good, poor, fresh], await OrderAsync(db, seriesId));
    }

    [Fact]
    public async Task Completions_older_than_the_window_do_not_keep_a_failing_source_in_place()
    {
        var (seriesId, poor, good, fresh) = Seed(SourceOrderMode.Quality, null);
        SeedDownloads(seriesId, good, completed: 20, failed: 0, queuedAt: DateTime.UtcNow - LibraryCompositionService.ReliabilityWindow - TimeSpan.FromDays(1));
        SeedDownloads(seriesId, good, completed: 0, failed: 4);
        using var db = _db.NewContext();

        Assert.Equal([poor, fresh, good], await OrderAsync(db, seriesId));
    }

    [Fact]
    public async Task A_source_that_fails_elsewhere_is_not_trusted_on_a_series_it_has_not_been_tried_on()
    {
        var (seriesId, poor, good, fresh) = Seed(SourceOrderMode.Quality, null);
        using (var seed = _db.NewContext())
        {
            var root = seed.RootFolders.First();
            var other = new Series { Title = "O", SortTitle = "o", FolderName = "O", RootFolderId = root.Id };
            var mapping = new SourceMapping { SourceName = "good", SourceSeriesId = "o", Priority = 1 };
            other.SourceMappings.Add(mapping);
            seed.Series.Add(other);
            seed.SaveChanges();
            SeedDownloads(other.Id, mapping.Id, completed: 0, failed: 5);
        }

        using var db = _db.NewContext();

        Assert.Equal([poor, fresh, good], await OrderAsync(db, seriesId));
    }

    [Fact]
    public async Task Manual_order_ignores_download_failures()
    {
        var (seriesId, poor, good, fresh) = Seed(SourceOrderMode.Manual, null);
        SeedDownloads(seriesId, poor, completed: 0, failed: 9);
        using var db = _db.NewContext();

        Assert.Equal([poor, good, fresh], await OrderAsync(db, seriesId));
    }

    [Fact]
    public async Task A_series_set_to_manual_ignores_a_quality_default()
    {
        var (seriesId, poor, good, fresh) = Seed(SourceOrderMode.Manual, "quality");
        using var db = _db.NewContext();

        Assert.Equal([poor, good, fresh], await OrderAsync(db, seriesId));
    }

    [Fact]
    public async Task Downloads_resolve_to_the_best_source_unless_one_is_preferred()
    {
        var (seriesId, poor, good, _) = Seed(SourceOrderMode.Quality, null);
        var resolver = Sources.Resolver(Registry);
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = 1, NumberRaw = "1" };

        var best = await resolver.ResolveAsync(db, chapter, null, CancellationToken.None);
        var pinned = await resolver.ResolveAsync(db, chapter, poor, CancellationToken.None);

        Assert.Equal(good, best.Mapping.Id);
        Assert.Equal(poor, pinned.Mapping.Id);
    }

    [Fact]
    public async Task A_held_source_is_resolved_only_after_every_other_one()
    {
        var (seriesId, poor, _, _) = Seed(SourceOrderMode.Quality, null);
        var resolver = Sources.Resolver(Registry);
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = 1, NumberRaw = "1" };

        var resolved = await resolver.ResolveAsync(db, chapter, null, CancellationToken.None,
            lastResortSources: new HashSet<string> { "good" });

        Assert.Equal(poor, resolved.Mapping.Id);
    }
}
