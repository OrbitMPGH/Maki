using Maki.Api.Services;
using Maki.Core.Quality;

namespace Maki.Api.Tests;

/// <summary>
/// The quality line <see cref="Maki.Api.Controllers.SourceMappingController.Compare"/> adds to each
/// panel of a snapshot. The file on disk is "agg" at 800px over 20 pages; the profile's width format
/// (worth 10) starts at 1400px.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public class SourceCompareQualityTests : IDisposable
{
    private readonly UpgradeWorld _world = new();

    public void Dispose() => _world.Dispose();

    private CompareSnapshot Snapshot(int officialPages = 20) => new(
        _world.SeriesId, false, false, true, 1m, [1m],
        [
            new ComparePanel(_world.OfficialMappingId, UpgradeWorld.Official, "Official", "ready", null, "1",
                officialPages, true, [new ComparePage("a", 1600, 2400, 10), null, new ComparePage("b", 1600, 2400, 10)]),
            new ComparePanel(_world.AggMappingId, UpgradeWorld.Agg, "Agg", "ready", null, "1", 20, true,
                [new ComparePage("c", null, null, 10)])
        ]);

    private async Task<CompareSnapshot> FillAsync(CompareSnapshot snapshot)
    {
        using var db = _world.Db.NewContext();
        return await SourceCompareQuality.FillAsync(db, new UpgradeEvaluationService(db, TestQuality.Create(_world.Registry)),
            _world.Registry, snapshot, CancellationToken.None);
    }

    [Fact]
    public async Task A_panel_with_known_widths_is_scored_against_the_file_on_disk()
    {
        _world.Seed();
        _world.Chapter(1);

        var filled = await FillAsync(Snapshot());

        var official = filled.Panels[0].Quality!;
        Assert.Equal("official", official.Tier);
        Assert.Equal(10, official.Score);
        Assert.Equal(["High resolution"], official.MatchedFormats);
        Assert.True(official.IsUpgrade);
        Assert.Null(official.Reason);
        Assert.Equal(1600, official.MedianWidth);
        Assert.Equal(20, official.PageCount);
        Assert.Equal("aggregator", official.CurrentTier);
        Assert.Equal(0, official.CurrentScore);
        Assert.Equal(800, official.CurrentWidth);
        Assert.Null(filled.Panels[1].Quality);
    }

    [Fact]
    public async Task A_panel_that_would_not_win_carries_the_reason()
    {
        _world.Seed();
        _world.Chapter(1);

        var official = (await FillAsync(Snapshot(officialPages: 5))).Panels[0].Quality!;

        Assert.False(official.IsUpgrade);
        Assert.Equal(UpgradeReasons.FewerPages, official.Reason);
    }

    [Fact]
    public async Task A_series_without_a_profile_reports_no_profile()
    {
        _world.Seed(series: s => s.UpgradeProfileId = null);
        _world.Chapter(1);

        var official = (await FillAsync(Snapshot())).Panels[0].Quality!;

        Assert.Equal("official", official.Tier);
        Assert.Equal(0, official.Score);
        Assert.Empty(official.MatchedFormats);
        Assert.False(official.IsUpgrade);
        Assert.Equal(SourceCompareQuality.NoProfile, official.Reason);
        Assert.Equal(1600, official.MedianWidth);
        Assert.Equal("aggregator", official.CurrentTier);
        Assert.Null(official.CurrentScore);
        Assert.Equal(800, official.CurrentWidth);
    }

    [Fact]
    public async Task The_chapter_in_the_language_that_has_a_file_is_the_one_compared()
    {
        _world.Seed();
        var (englishId, _) = _world.Chapter(1, withFile: false);
        var (spanishId, _) = _world.Chapter(1);
        using (var db = _world.Db.NewContext())
        {
            db.Chapters.Single(c => c.Id == spanishId).Language = "es";
            db.SaveChanges();
        }

        Assert.True(englishId < spanishId);
        var official = (await FillAsync(Snapshot())).Panels[0].Quality!;

        Assert.Equal("aggregator", official.CurrentTier);
        Assert.Equal(800, official.CurrentWidth);
        Assert.True(official.IsUpgrade);
    }

    [Fact]
    public async Task A_snapshot_without_a_chapter_number_is_left_as_it_is()
    {
        _world.Seed();
        _world.Chapter(1);
        var snapshot = Snapshot() with { ChapterNumber = null };

        var filled = await FillAsync(snapshot);

        Assert.All(filled.Panels, p => Assert.Null(p.Quality));
    }

    [Fact]
    public async Task A_file_backing_several_chapters_is_never_called_an_upgrade()
    {
        _world.Seed();
        var (_, fileId) = _world.Chapter(1);
        var (second, _) = _world.Chapter(2, withFile: false);
        using (var db = _world.Db.NewContext())
        {
            db.Chapters.Single(c => c.Id == second).ChapterFileId = fileId;
            db.SaveChanges();
        }

        var official = (await FillAsync(Snapshot())).Panels[0].Quality!;

        Assert.False(official.IsUpgrade);
        Assert.Equal(UpgradeReasons.SharedFile, official.Reason);
    }

    [Fact]
    public async Task The_copy_already_on_disk_is_never_called_an_upgrade()
    {
        _world.Seed();
        _world.Chapter(1, file: f => { f.SourceName = UpgradeWorld.Official; f.SourceChapterId = "o1"; });

        var official = (await FillAsync(Snapshot())).Panels[0].Quality!;

        Assert.False(official.IsUpgrade);
        Assert.Equal(UpgradeReasons.SameCopy, official.Reason);
    }
}
