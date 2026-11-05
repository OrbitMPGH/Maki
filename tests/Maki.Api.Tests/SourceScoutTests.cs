using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class SourceScoutTests : IDisposable
{
    private readonly UpgradeWorld _world = new();

    public void Dispose() => _world.Dispose();

    private SourceScoutService Scout() => new(
        _world.Db.ScopeFactory(), _world.Registry, _world.Availability, _world.Probes(), _world.Queue,
        NullLogger<SourceScoutService>.Instance);

    private List<SourceQualitySample> Samples()
    {
        using var db = _world.Db.NewContext();
        return db.SourceQualitySamples.AsNoTracking().ToList();
    }

    [Fact]
    public async Task Every_source_is_sampled_on_the_same_chapters_spread_across_the_series()
    {
        _world.Seed();
        _world.AggPages = UpgradeWorld.InlinePages(20, 700);
        _world.OfficialPages = UpgradeWorld.InlinePages(20, 1600);
        var chapters = Enumerable.Range(1, 9).Select(n => _world.Chapter(n, withFile: false).ChapterId).ToList();
        var scout = Scout();

        await scout.RunNowAsync(_world.SeriesId, CancellationToken.None);

        var samples = Samples();
        Assert.Equal(6, samples.Count);
        Assert.All(samples, s => Assert.Equal(SourceQualityOrigin.Probe, s.Origin));
        var aggChapters = samples.Where(s => s.SourceMappingId == _world.AggMappingId).Select(s => s.ChapterId).Order().ToList();
        var officialChapters = samples.Where(s => s.SourceMappingId == _world.OfficialMappingId).Select(s => s.ChapterId).Order().ToList();
        Assert.Equal(aggChapters, officialChapters);
        Assert.Equal(3, aggChapters.Select(id => chapters.IndexOf(id) / 3).Distinct().Count());
        Assert.All(samples.Where(s => s.SourceMappingId == _world.AggMappingId), s => Assert.Equal(700, s.MedianWidth));
        Assert.All(samples.Where(s => s.SourceMappingId == _world.OfficialMappingId), s => Assert.Equal(1600, s.MedianWidth));
        var snapshot = scout.Snapshot(_world.SeriesId)!;
        Assert.Equal(new ScoutSnapshotCounts(6, 6, 6), Counts(snapshot));
        Assert.Equal(3, snapshot.Chapters.Count);
        Assert.All(snapshot.Sources, s => Assert.Equal(("done", 3, 3), (s.State, s.Planned, s.Measured)));
    }

    [Fact]
    public async Task A_source_that_serves_nothing_is_counted_but_not_recorded()
    {
        _world.Seed();
        _world.OfficialPages = UpgradeWorld.InlinePages(20, 1600);
        _world.Chapter(1, withFile: false);
        var scout = Scout();

        await scout.RunNowAsync(_world.SeriesId, CancellationToken.None);

        var sample = Assert.Single(Samples());
        Assert.Equal(_world.OfficialMappingId, sample.SourceMappingId);
        var snapshot = scout.Snapshot(_world.SeriesId)!;
        Assert.Equal(new ScoutSnapshotCounts(2, 2, 1), Counts(snapshot));
        var agg = Assert.Single(snapshot.Sources, s => s.MappingId == _world.AggMappingId);
        Assert.Equal(("failed", "failed"), (agg.State, agg.Problem));
        Assert.Equal("done", Assert.Single(snapshot.Sources, s => s.MappingId == _world.OfficialMappingId).State);
    }

    [Fact]
    public async Task A_source_listing_none_of_the_picked_chapters_is_reported_as_skipped()
    {
        _world.Seed();
        _world.OfficialPages = UpgradeWorld.InlinePages(20, 1600);
        var (chapterId, _) = _world.Chapter(1, withFile: false);
        using (var db = _world.Db.NewContext())
        {
            db.ChapterSourceLinks.RemoveRange(db.ChapterSourceLinks.Where(l => l.SourceMappingId == _world.AggMappingId));
            db.SaveChanges();
        }

        var scout = Scout();
        await scout.RunNowAsync(_world.SeriesId, CancellationToken.None);

        var agg = Assert.Single(scout.Snapshot(_world.SeriesId)!.Sources, s => s.MappingId == _world.AggMappingId);
        Assert.Equal(("skipped", 0), (agg.State, agg.Planned));
        Assert.Equal(chapterId, Assert.Single(Samples()).ChapterId);
    }

    [Fact]
    public async Task A_source_sharing_no_chapter_with_the_others_is_measured_on_its_own_chapters()
    {
        _world.Seed();
        _world.AggPages = UpgradeWorld.InlinePages(12, 700);
        _world.OfficialPages = UpgradeWorld.InlinePages(40, 1600);
        var chapters = Enumerable.Range(1, 4).Select(n => _world.Chapter(n, withFile: false).ChapterId).ToList();
        using (var db = _world.Db.NewContext())
        {
            db.ChapterSourceLinks.RemoveRange(db.ChapterSourceLinks.Where(l => l.SourceMappingId == _world.AggMappingId && l.ChapterId != chapters[3]));
            db.ChapterSourceLinks.RemoveRange(db.ChapterSourceLinks.Where(l => l.SourceMappingId == _world.OfficialMappingId && l.ChapterId == chapters[3]));
            db.SaveChanges();
        }

        await Scout().RunNowAsync(_world.SeriesId, CancellationToken.None);

        var agg = Assert.Single(Samples(), s => s.SourceMappingId == _world.AggMappingId);
        Assert.Equal(chapters[3], agg.ChapterId);
        Assert.Equal(12, agg.PageCount);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public async Task Matching_only_starts_a_run_when_the_setting_is_on(string? value, bool expected)
    {
        _world.Seed();
        using var db = _world.Db.NewContext();
        if (value is not null)
        {
            db.AppConfig.Add(new AppConfigEntry { Key = SettingKeys.SourcesScoutOnMatch, Value = value });
            db.SaveChanges();
        }

        var scout = Scout();
        Assert.Equal(expected, await scout.StartIfEnabledAsync(db, _world.SeriesId, CancellationToken.None));

        Assert.Equal(expected, scout.Snapshot(_world.SeriesId) is not null);
    }

    [Fact]
    public async Task Smart_download_starts_a_missing_run_and_waits_for_it_to_finish()
    {
        _world.Seed();
        _world.OfficialPages = UpgradeWorld.InlinePages(20, 1600);
        _world.Chapter(1, withFile: false);
        var scout = Scout();
        using var db = _world.Db.NewContext();
        var chapters = db.Chapters.AsNoTracking().Where(c => c.SeriesId == _world.SeriesId).ToList();

        Assert.True(await SmartDownloadJob.WaitsForMeasurementAsync(
            db, scout, _world.SeriesId, chapters, scoutOnMatch: true, CancellationToken.None));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (scout.Snapshot(_world.SeriesId)!.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.NotEmpty(Samples());
        Assert.False(await SmartDownloadJob.WaitsForMeasurementAsync(
            db, scout, _world.SeriesId, chapters, scoutOnMatch: true, CancellationToken.None));
    }

    [Fact]
    public async Task A_finished_run_that_measured_nothing_does_not_hold_smart_download()
    {
        _world.Seed();
        _world.OfficialPages = _ => new ChapterPages([]);
        _world.Chapter(1, withFile: false);
        var scout = Scout();
        await scout.RunNowAsync(_world.SeriesId, CancellationToken.None);
        using var db = _world.Db.NewContext();
        var chapters = db.Chapters.AsNoTracking().Where(c => c.SeriesId == _world.SeriesId).ToList();

        Assert.Empty(Samples());
        Assert.False(await SmartDownloadJob.WaitsForMeasurementAsync(
            db, scout, _world.SeriesId, chapters, scoutOnMatch: true, CancellationToken.None));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Smart_download_starts_no_run_when_the_setting_is_off_or_a_file_is_on_disk(
        bool scoutOnMatch, bool withFile)
    {
        _world.Seed();
        _world.Chapter(1, withFile: withFile);
        var scout = Scout();
        using var db = _world.Db.NewContext();
        var chapters = db.Chapters.AsNoTracking().Where(c => c.SeriesId == _world.SeriesId).ToList();

        Assert.False(await SmartDownloadJob.WaitsForMeasurementAsync(
            db, scout, _world.SeriesId, chapters, scoutOnMatch, CancellationToken.None));
        Assert.Null(scout.Snapshot(_world.SeriesId));
    }

    private sealed record ScoutSnapshotCounts(int Probes, int Done, int Measured);

    private static ScoutSnapshotCounts Counts(ScoutSnapshot s) => new(s.Probes, s.Done, s.Measured);
}
