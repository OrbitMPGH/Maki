using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>The torrent search for wanted chapters no source lists, and the minimum seeders for unattended grabs.</summary>
[Collection(OneRunPerProcessCollection.Name)]
public class TorrentMissingSearchTests : IDisposable
{
    private readonly UpgradeWorld _world = new();
    private readonly FakeReleases _releases = new();
    private readonly SettableClock _clock = SettableClock.LocalNoon();

    public TorrentMissingSearchTests()
    {
        _world.Seed(p => p.Cutoff = QualityTier.Volume, s => s.Title = "Kaguya");
        _world.Settings.Set(SettingKeys.ProwlarrUrl, "http://prowlarr.test");
        _world.Settings.Set(SettingKeys.ProwlarrApiKey, "key");
        _world.Settings.Set(SettingKeys.DownloadClientUrl, "http://qbt.test");
    }

    public void Dispose() => _world.Dispose();

    private TorrentUpgradeService Service(MakiDbContext db) => new(db,
        new UpgradeEvaluationService(db, TestQuality.Create(_world.Registry)), _releases, _world.Inbox, _world.Settings,
        _clock, new FakeRelatedTitles().Provider(), NullLogger<TorrentUpgradeService>.Instance);

    /// <summary>A chapter in a volume; <paramref name="listed"/> false drops the source links the kit gives it.</summary>
    private int Chapter(decimal number, int? volume, bool withFile = false, bool listed = false)
    {
        var (chapterId, _) = _world.Chapter(number, withFile: withFile);
        using var db = _world.Db.NewContext();
        db.Chapters.Single(c => c.Id == chapterId).Volume = volume;
        if (!listed)
        {
            db.ChapterSourceLinks.RemoveRange(db.ChapterSourceLinks.Where(l => l.ChapterId == chapterId));
        }

        db.SaveChanges();
        return chapterId;
    }

    private async Task<SeriesVolumeSearchResult> MissingSearchAsync(int? seriesId = null)
    {
        using var db = _world.Db.NewContext();
        return await Service(db).SearchSeriesAsync(seriesId ?? _world.SeriesId, CancellationToken.None,
            respectInterval: true, missing: true);
    }

    private async Task<List<int>> CandidatesAsync()
    {
        using var db = _world.Db.NewContext();
        return await Service(db).MissingCandidatesAsync(CancellationToken.None);
    }

    private void Series(Action<Series> change)
    {
        using var db = _world.Db.NewContext();
        change(db.Series.Single(s => s.Id == _world.SeriesId));
        db.SaveChanges();
    }

    [Fact]
    public async Task A_series_whose_chapters_no_source_lists_gets_a_volume_grabbed_with_upgrades_off()
    {
        Chapter(1, 1);
        Chapter(2, 1);
        _releases.Results.Add(FakeReleases.Release("Kaguya v01 (Digital) (1r0n)"));

        var result = await MissingSearchAsync();

        Assert.True(result.Searched);
        var grab = Assert.Single(_releases.Grabs);
        Assert.Equal(DownloadOrigin.Upgrade, grab.Origin);
        Assert.Equal(1001, result.Grabbed);
        using var db = _world.Db.NewContext();
        Assert.NotNull(db.Series.Single().LastVolumeSearchUtc);
    }

    [Fact]
    public async Task Chapters_a_source_lists_stay_with_the_scrapers()
    {
        Chapter(1, 1, listed: true);
        Chapter(2, 1, listed: true);
        _releases.Results.Add(FakeReleases.Release("Kaguya v01 (Digital) (1r0n)"));

        Assert.Equal(VolumeSearchReasons.Disabled, (await MissingSearchAsync()).Reason);
        Assert.Empty(await CandidatesAsync());

        _world.Settings.Set(SettingKeys.UpgradesEnabled, "true");
        Assert.Equal(VolumeSearchReasons.NothingBelowCutoff, (await MissingSearchAsync()).Reason);
        Assert.Empty(_releases.Queries);
    }

    [Fact]
    public async Task A_chapter_listed_only_by_a_switched_off_mapping_counts_as_unlisted()
    {
        Chapter(1, 1, listed: true);
        using (var db = _world.Db.NewContext())
        {
            foreach (var mapping in db.SourceMappings)
            {
                mapping.Enabled = false;
            }

            db.SaveChanges();
        }

        Assert.Equal([_world.SeriesId], await CandidatesAsync());
    }

    [Fact]
    public async Task Only_the_unlisted_chapters_a_release_covers_count_as_missing()
    {
        Chapter(1, 1, listed: true);
        var unlisted = Chapter(2, 1);

        using var db = _world.Db.NewContext();
        var service = Service(db);
        var (judge, scope) = await service.AdditionsAsync(_world.SeriesId, volumeFirst: false, CancellationToken.None);
        var view = (await service.EvaluateAsync(_world.SeriesId, [FakeReleases.Release("Kaguya v01 (Digital) (1r0n)")],
            CancellationToken.None, judge, scope)).Single();

        Assert.True(judge);
        Assert.Equal(unlisted, Assert.Single(scope!.ChapterIds));
        Assert.Equal(1, view.Verdict.MissingCount);
        Assert.Equal(1, view.Verdict.SkippedCount);
    }

    [Fact]
    public async Task With_upgrades_off_the_missing_search_never_takes_a_release_that_only_upgrades()
    {
        Chapter(1, 1, withFile: true, listed: true);
        Chapter(2, 2);
        _releases.Results.Add(FakeReleases.Release("Kaguya v01 (Digital) (1r0n)", guid: "upgrade"));

        var result = await MissingSearchAsync();

        Assert.True(result.Searched);
        Assert.Empty(_releases.Grabs);
        Assert.Null(result.ProposalId);

        _world.Settings.Set(SettingKeys.UpgradesEnabled, "true");
        Series(s => s.LastVolumeSearchUtc = null);
        await MissingSearchAsync();

        Assert.Equal("upgrade", Assert.Single(_releases.Grabs).Release.Guid);
    }

    [Fact]
    public async Task The_switch_turns_the_missing_search_off()
    {
        Chapter(1, 1);
        _world.Settings.Set(SettingKeys.UpgradesMissingSearch, "false");

        Assert.Equal(VolumeSearchReasons.Disabled, (await MissingSearchAsync()).Reason);
        Assert.Empty(_releases.Queries);
    }

    [Fact]
    public async Task A_series_with_no_chapters_and_no_source_gets_a_proposal_to_approve_by_hand()
    {
        using (var db = _world.Db.NewContext())
        {
            db.SourceMappings.RemoveRange(db.SourceMappings);
            db.SaveChanges();
        }

        _releases.Results.Add(FakeReleases.Release("Kaguya v01 (Digital) (1r0n)"));

        Assert.Equal([_world.SeriesId], await CandidatesAsync());
        var result = await MissingSearchAsync();

        Assert.Empty(_releases.Grabs);
        Assert.NotNull(result.ProposalId);
        using var check = _world.Db.NewContext();
        Assert.Contains(SpanVerdictReasons.VolumeSpanUnknown, check.TorrentProposals.Single().ReasonsJson);
    }

    [Fact]
    public async Task A_series_with_sources_still_to_list_its_chapters_is_left_to_them()
    {
        Assert.Empty(await CandidatesAsync());
        Assert.Equal(VolumeSearchReasons.Disabled, (await MissingSearchAsync()).Reason);
    }

    [Fact]
    public async Task Candidates_are_monitored_series_outside_the_interval_without_a_pending_proposal()
    {
        Chapter(1, 1);
        Assert.Equal([_world.SeriesId], await CandidatesAsync());

        Series(s => s.MonitorNewItems = NewChapterMonitorMode.None);
        Assert.Empty(await CandidatesAsync());
        Assert.Equal(VolumeSearchReasons.Disabled, (await MissingSearchAsync()).Reason);

        Series(s =>
        {
            s.MonitorNewItems = NewChapterMonitorMode.All;
            s.LastVolumeSearchUtc = _clock.GetUtcNow().UtcDateTime.AddDays(-2);
        });
        Assert.Empty(await CandidatesAsync());

        Series(s => s.LastVolumeSearchUtc = _clock.GetUtcNow().UtcDateTime.AddDays(-8));
        Assert.Equal([_world.SeriesId], await CandidatesAsync());

        Series(s => s.SourceMatchPending = true);
        Assert.Empty(await CandidatesAsync());
        Series(s => s.SourceMatchPending = false);

        using (var db = _world.Db.NewContext())
        {
            db.TorrentProposals.Add(new TorrentProposal
            {
                SeriesId = _world.SeriesId, ReleaseGuid = "p", Status = TorrentProposalStatus.Pending,
                CreatedAtUtc = _clock.GetUtcNow().UtcDateTime
            });
            db.SaveChanges();
        }

        Assert.Empty(await CandidatesAsync());
    }

    [Fact]
    public async Task A_failed_search_comes_back_after_a_day_and_a_searched_one_after_a_week()
    {
        Chapter(1, 1);
        _releases.SearchError = new HttpRequestException("indexer down");

        Assert.Equal(VolumeSearchReasons.SearchFailed, (await MissingSearchAsync()).Reason);
        _clock.Now = _clock.Now.AddDays(1).AddMinutes(1);
        Assert.Equal([_world.SeriesId], await CandidatesAsync());

        _releases.SearchError = null;
        Assert.True((await MissingSearchAsync()).Searched);
        _clock.Now = _clock.Now.AddDays(6);
        Assert.Empty(await CandidatesAsync());
        _clock.Now = _clock.Now.AddDays(1).AddMinutes(1);
        Assert.Equal([_world.SeriesId], await CandidatesAsync());
    }

    [Fact]
    public async Task An_archived_series_is_never_a_candidate()
    {
        Chapter(1, 1);
        Series(s => s.ArchivedAt = DateTime.UtcNow);

        Assert.Empty(await CandidatesAsync());
    }

    [Fact]
    public async Task An_unattended_grab_waits_for_the_minimum_seeders()
    {
        Chapter(1, 1);
        Chapter(2, 1);
        _world.Settings.Set(SettingKeys.UpgradesTorrentMinSeeders, "5");
        _releases.Results.Add(FakeReleases.Release("Kaguya v01 (Digital) (1r0n)", seeders: 4, guid: "thin"));

        await MissingSearchAsync();
        Assert.Empty(_releases.Grabs);

        _releases.Results.Add(FakeReleases.Release("Kaguya v01 (Digital) (LuCaZ)", seeders: 5, guid: "seeded"));
        Series(s => s.LastVolumeSearchUtc = null);
        await MissingSearchAsync();

        Assert.Equal("seeded", Assert.Single(_releases.Grabs).Release.Guid);
    }

    [Fact]
    public async Task Automatic_approval_waits_for_the_minimum_seeders_too()
    {
        Chapter(1, 1);
        Chapter(2, 1);
        Chapter(3, 1);
        Chapter(4, 1);
        _world.Settings.Set(SettingKeys.UpgradesAutoApprove, "true");
        _world.Settings.Set(SettingKeys.UpgradesTorrentMinSeeders, "10");
        _releases.Results.Add(FakeReleases.Release("Kaguya v01 (Digital) (1r0n)", seeders: 3));

        var result = await MissingSearchAsync();

        Assert.Empty(_releases.Grabs);
        Assert.NotNull(result.ProposalId);

        _world.Settings.Set(SettingKeys.UpgradesEnabled, "true");
        using (var db = _world.Db.NewContext())
        {
            Assert.Equal(0, await Service(db).AutoApprovePendingAsync(CancellationToken.None));
        }

        _world.Settings.Set(SettingKeys.UpgradesTorrentMinSeeders, "3");
        using (var db = _world.Db.NewContext())
        {
            Assert.Equal(1, await Service(db).AutoApprovePendingAsync(CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(null, 5, true)]
    [InlineData(0, 1, false)]
    [InlineData(0, 0, false)]
    [InlineData(4, 5, false)]
    [InlineData(5, 5, true)]
    public void Enough_seeders_lets_an_unknown_count_through_and_never_a_dead_release(int? seeders, int min, bool expected) =>
        Assert.Equal(expected, TorrentUpgradeRules.EnoughSeeders(seeders, min));

    private async Task RunJobAsync()
    {
        using var db = _world.Db.NewContext();
        var job = new UpgradeVolumeSearchJob(Service(db), _world.Settings, _clock, NullLogger<UpgradeVolumeSearchJob>.Instance);
        await job.Execute(new TestJobContext());
    }

    /// <summary>Another series of the same profile whose one wanted chapter no source lists.</summary>
    private void UnlistedSeries(string title)
    {
        using var db = _world.Db.NewContext();
        var series = new Series
        {
            Title = title, SortTitle = title.ToLowerInvariant(), FolderName = title,
            RootFolderId = db.RootFolders.Select(r => r.Id).First(), UpgradeProfileId = _world.ProfileId
        };
        db.Series.Add(series);
        db.SaveChanges();
        db.Chapters.Add(new Chapter { SeriesId = series.Id, Number = 1, Volume = 1 });
        db.SaveChanges();
    }

    [Fact]
    public async Task The_job_searches_a_few_series_a_tick_with_the_upgrade_switches_off()
    {
        Chapter(1, 1);
        foreach (var title in new[] { "Second", "Third", "Fourth" })
        {
            UnlistedSeries(title);
        }

        await RunJobAsync();

        Assert.Equal(TorrentUpgradeService.MissingSearchesPerRun, _releases.Queries.Count);
        Assert.Null(await _world.Settings.GetAsync(SettingKeys.UpgradesLastVolumeSearchDate));

        await RunJobAsync();
        Assert.Equal(4, _releases.Queries.Count);

        await RunJobAsync();
        Assert.Equal(4, _releases.Queries.Count);
    }

    [Fact]
    public async Task The_job_leaves_the_missing_search_alone_without_a_download_client()
    {
        Chapter(1, 1);
        _world.Settings.Set(SettingKeys.DownloadClientUrl, "");

        await RunJobAsync();

        Assert.Empty(_releases.Queries);
    }
}
