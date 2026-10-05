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
    /// quality, "good" well over it, "fresh" has never been measured.
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
    public async Task Quality_order_puts_the_better_measured_source_first_and_an_unmeasured_one_in_between(
        SourceOrderMode? seriesMode, string? instanceDefault)
    {
        var (seriesId, poor, good, fresh) = Seed(seriesMode, instanceDefault);
        using var db = _db.NewContext();

        Assert.Equal([good, fresh, poor], await OrderAsync(db, seriesId));
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
}
