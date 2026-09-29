using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class SourceQualityTests : IDisposable
{
    private const long ReferenceSize = 5_625_000; // 1.5 bits per pixel over 20 pages of 1000x1500

    private readonly UpgradeWorld _world = new();

    public void Dispose() => _world.Dispose();

    /// <summary>
    /// The file and the official mapping are the same tier, so only measured points can make the
    /// official copy an upgrade.
    /// </summary>
    private int SeedSameTier()
    {
        _world.Seed(profile: p =>
        {
            p.Cutoff = QualityTier.Volume;
            p.FormatScores = [];
            p.ResolutionWeight = 10;
            p.CompressionWeight = 10;
        });
        var (chapterId, _) = _world.Chapter(1, file: f =>
        {
            f.Tier = QualityTier.Official;
            f.MedianWidth = 1000;
            f.MedianHeight = 1500;
            f.ImageFormat = "jpg";
            f.Size = ReferenceSize;
        });
        return chapterId;
    }

    private void AddSamples(int mappingId, DateTime measuredAt, params int[] chapterIds)
    {
        using var db = _world.Db.NewContext();
        foreach (var chapterId in chapterIds)
        {
            db.SourceQualitySamples.Add(new SourceQualitySample
            {
                SourceMappingId = mappingId, SeriesId = _world.SeriesId, ChapterId = chapterId,
                Origin = SourceQualityOrigin.Download, PageCount = 20, MedianWidth = 1000, MedianHeight = 1500,
                SizeBytes = ReferenceSize, ImageFormat = "jpg", MeasuredAtUtc = measuredAt
            });
        }

        db.SaveChanges();
    }

    private async Task<UpgradeScanResult> ScanAsync()
    {
        using var db = _world.Db.NewContext();
        using var batches = _world.Batches();
        return await _world.Scanner(db, batches).ScanSeriesAsync(_world.SeriesId, CancellationToken.None);
    }

    private List<SourceQualitySample> Samples()
    {
        using var db = _world.Db.NewContext();
        return db.SourceQualitySamples.AsNoTracking().ToList();
    }

    [Fact]
    public async Task A_source_whose_recent_chapters_would_not_win_is_not_probed()
    {
        var chapterId = SeedSameTier();
        AddSamples(_world.OfficialMappingId, DateTime.UtcNow.AddDays(-1), 1001, 1002);

        var result = await ScanAsync();

        Assert.Equal(0, result.CandidatesProbed);
        Assert.Empty(_world.Http.Requested);
        using var db = _world.Db.NewContext();
        var attempt = Assert.Single(db.UpgradeAttempts.AsNoTracking().ToList(), a => a.SourceMappingId == _world.OfficialMappingId);
        Assert.Equal(chapterId, attempt.ChapterId);
        Assert.Equal(UpgradeReasons.EstimateNotHigher, attempt.Reason);
        Assert.False(attempt.Probed);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 70)]
    public async Task Too_few_or_too_old_samples_still_probe_and_the_probe_is_recorded(int samples, int daysOld)
    {
        var chapterId = SeedSameTier();
        AddSamples(_world.OfficialMappingId, DateTime.UtcNow.AddDays(-daysOld), [.. Enumerable.Range(1001, samples)]);

        var result = await ScanAsync();

        Assert.Equal(1, result.CandidatesProbed);
        var probe = Assert.Single(Samples(), s => s.ChapterId == chapterId);
        Assert.Equal(SourceQualityOrigin.Probe, probe.Origin);
        Assert.Equal(_world.OfficialMappingId, probe.SourceMappingId);
        Assert.True(probe.MedianWidth > 0);
    }

    [Fact]
    public async Task An_estimate_rejection_is_looked_at_again_once_it_expires()
    {
        var chapterId = SeedSameTier();
        using (var db = _world.Db.NewContext())
        {
            db.UpgradeAttempts.Add(new UpgradeAttempt
            {
                ChapterId = chapterId, SeriesId = _world.SeriesId, SourceMappingId = _world.OfficialMappingId,
                SourceChapterId = "o1", ProfileId = _world.ProfileId, ProfileVersion = 1,
                Reason = UpgradeReasons.EstimateNotHigher,
                CreatedAtUtc = DateTime.UtcNow - UpgradeReasons.EstimateMemoLifetime - TimeSpan.FromDays(1)
            });
            db.SaveChanges();
        }

        var result = await ScanAsync();

        Assert.Equal(1, result.CandidatesProbed);
    }

    [Fact]
    public async Task Recording_replaces_the_same_chapter_and_keeps_only_the_newest_samples()
    {
        _world.Seed();
        using var db = _world.Db.NewContext();
        var mapping = db.SourceMappings.Single(m => m.Id == _world.AggMappingId);
        var start = DateTime.UtcNow.AddDays(-20);
        for (var i = 0; i < SourceQualitySamples.Keep + 3; i++)
        {
            await SourceQualitySamples.RecordAsync(db, mapping, 100 + i, SourceQualityOrigin.Download, 20, 1000, 1500,
                ReferenceSize, "jpg", start.AddDays(i), CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await SourceQualitySamples.RecordAsync(db, mapping, 100 + SourceQualitySamples.Keep + 2, SourceQualityOrigin.Probe,
            20, 1600, 2400, ReferenceSize, "png", DateTime.UtcNow, CancellationToken.None);
        await SourceQualitySamples.RecordAsync(db, mapping, 999, SourceQualityOrigin.Probe, 20, null, 1500,
            ReferenceSize, "jpg", DateTime.UtcNow, CancellationToken.None);
        await db.SaveChangesAsync();

        var rows = Samples();
        Assert.Equal(SourceQualitySamples.Keep, rows.Count);
        Assert.Equal(Enumerable.Range(103, SourceQualitySamples.Keep), rows.Select(r => r.ChapterId).Order());
        var replaced = Assert.Single(rows, r => r.Origin == SourceQualityOrigin.Probe);
        Assert.Equal((1600, "png"), (replaced.MedianWidth, replaced.ImageFormat));

        var estimate = (await SourceQualitySamples.EstimatesAsync(db, _world.SeriesId, CancellationToken.None))[mapping.Id];
        Assert.Equal(SourceQualitySamples.Keep, estimate.Samples);
        Assert.Equal(1000, estimate.MedianWidth);
        Assert.Equal("jpg", estimate.ImageFormat);
    }

    [Fact]
    public async Task Seeding_gives_each_mapping_its_measured_library_files_once()
    {
        _world.Seed();
        var (ch1, _) = _world.Chapter(1, file: f => f.Size = ReferenceSize);
        _world.Chapter(2, file: f => f.MeasuredAtUtc = null);
        _world.Chapter(3, file: f => f.SourceName = "import");

        using (var db = _world.Db.NewContext())
        {
            await new SourceQualitySeeder(db, NullLogger<SourceQualitySeeder>.Instance).RunOnceAsync();
        }

        var sample = Assert.Single(Samples());
        Assert.Equal((_world.AggMappingId, ch1, SourceQualityOrigin.Library), (sample.SourceMappingId, sample.ChapterId, sample.Origin));
        Assert.Equal(800, sample.MedianWidth);

        using (var db = _world.Db.NewContext())
        {
            db.SourceQualitySamples.RemoveRange(db.SourceQualitySamples);
            db.SaveChanges();
            await new SourceQualitySeeder(db, NullLogger<SourceQualitySeeder>.Instance).RunOnceAsync();
        }

        Assert.Empty(Samples());
    }

    [Fact]
    public async Task An_upgrade_download_records_what_it_measured()
    {
        _world.Seed();
        _world.OfficialPages = UpgradeWorld.InlinePages(20, 600);
        var (chapterId, fileId) = _world.Chapter(1, onDisk: true, width: 800);
        var item = _world.QueueUpgrade(chapterId, fileId!.Value);

        await _world.ProcessAsync(item);

        var sample = Assert.Single(Samples());
        Assert.Equal((_world.OfficialMappingId, chapterId, SourceQualityOrigin.Download),
            (sample.SourceMappingId, sample.ChapterId, sample.Origin));
        Assert.Equal(600, sample.MedianWidth);
    }
}
