using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class UpgradeVolumeSearchJobTests : IDisposable
{
    private readonly UpgradeWorld _world = new();
    private readonly FakeReleases _releases = new();
    private readonly SettableClock _clock = SettableClock.LocalNoon();

    public UpgradeVolumeSearchJobTests()
    {
        _world.Seed(p => p.Cutoff = QualityTier.Volume);
        _world.Chapter(1);
        _world.Settings.Set(SettingKeys.UpgradesEnabled, "true");
        _world.Settings.Set(SettingKeys.UpgradesScanHour, "0");
        _world.Settings.Set(SettingKeys.ProwlarrUrl, "http://prowlarr.test");
        _world.Settings.Set(SettingKeys.ProwlarrApiKey, "key");
    }

    public void Dispose() => _world.Dispose();

    private async Task RunAsync(bool force = false, CancellationToken ct = default)
    {
        using var db = _world.Db.NewContext();
        var torrents = new TorrentUpgradeService(db, new UpgradeEvaluationService(db, TestQuality.Create(_world.Registry)),
            _releases, _world.Inbox, _world.Settings, _clock, NullLogger<TorrentUpgradeService>.Instance);
        var job = new UpgradeVolumeSearchJob(torrents, _world.Settings, _clock, NullLogger<UpgradeVolumeSearchJob>.Instance);
        await job.Execute(new TestJobContext(force ? new JobDataMap { [UpgradeVolumeSearchJob.ForceKey] = true } : null, ct));
    }

    [Fact]
    public async Task A_shutdown_mid_search_leaves_the_day_open()
    {
        using var cts = new CancellationTokenSource();
        _releases.OnSearch = () =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(ct: cts.Token));

        Assert.Null(await _world.Settings.GetAsync(SettingKeys.UpgradesLastVolumeSearchDate));
        Assert.False(UpgradeVolumeSearchJob.IsRunning);
    }

    private void ForgetLastSearch()
    {
        using var db = _world.Db.NewContext();
        foreach (var series in db.Series)
        {
            series.LastVolumeSearchUtc = null;
        }

        db.SaveChanges();
    }

    /// <summary>Another series on the same profile with one below-cutoff file.</summary>
    private void AnotherSeries(string title)
    {
        using var db = _world.Db.NewContext();
        var series = new Series
        {
            Title = title, SortTitle = title.ToLowerInvariant(), FolderName = title,
            RootFolderId = db.RootFolders.Select(r => r.Id).First(), UpgradeProfileId = _world.ProfileId
        };
        db.Series.Add(series);
        db.SaveChanges();
        var file = new ChapterFile
        {
            SeriesId = series.Id, RelativePath = $"{title}/{title} 001.cbz", SourceName = UpgradeWorld.Agg,
            Tier = QualityTier.Aggregator, MedianWidth = 800, PageCount = 20, MeasuredAtUtc = DateTime.UtcNow,
            DateAdded = DateTime.UtcNow
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        db.Chapters.Add(new Chapter { SeriesId = series.Id, Number = 1, ChapterFileId = file.Id });
        db.SaveChanges();
    }

    [Fact]
    public async Task It_runs_once_per_local_day_unless_forced()
    {
        await RunAsync();
        Assert.Single(_releases.Queries);
        Assert.Equal(UpgradeOptions.MarkerDate(UpgradeOptions.LocalNow(_clock)),
            await _world.Settings.GetAsync(SettingKeys.UpgradesLastVolumeSearchDate));

        ForgetLastSearch();
        await RunAsync();
        Assert.Single(_releases.Queries);

        await RunAsync(force: true);
        Assert.Equal(2, _releases.Queries.Count);
    }

    [Fact]
    public async Task The_volume_search_switch_keeps_the_timer_quiet()
    {
        _world.Settings.Set(SettingKeys.UpgradesVolumeSearch, "false");

        await RunAsync();

        Assert.Empty(_releases.Queries);
        Assert.Null(await _world.Settings.GetAsync(SettingKeys.UpgradesLastVolumeSearchDate));
    }

    [Fact]
    public async Task A_run_stops_at_the_per_run_cap()
    {
        AnotherSeries("Second");
        AnotherSeries("Third");
        _world.Settings.Set(SettingKeys.UpgradesVolumeSearchesPerRun, "2");

        await RunAsync();

        Assert.Equal(2, _releases.Queries.Count);
        using var db = _world.Db.NewContext();
        Assert.Equal(1, db.Series.Count(s => s.LastVolumeSearchUtc == null));
    }

    [Fact]
    public async Task Old_pending_proposals_expire_and_recent_ones_stay()
    {
        using (var db = _world.Db.NewContext())
        {
            db.TorrentProposals.AddRange(
                new TorrentProposal
                {
                    SeriesId = _world.SeriesId, ReleaseGuid = "old", Status = TorrentProposalStatus.Pending,
                    CreatedAtUtc = _clock.Now.UtcDateTime.AddDays(-40)
                },
                new TorrentProposal
                {
                    SeriesId = _world.SeriesId, ReleaseGuid = "new", Status = TorrentProposalStatus.Pending,
                    CreatedAtUtc = _clock.Now.UtcDateTime.AddDays(-5)
                });
            db.SaveChanges();
        }

        await RunAsync();

        using var check = _world.Db.NewContext();
        var rows = check.TorrentProposals.AsNoTracking().ToDictionary(p => p.ReleaseGuid, p => p.Status);
        Assert.Equal(TorrentProposalStatus.Expired, rows["old"]);
        Assert.Equal(TorrentProposalStatus.Pending, rows["new"]);
    }
}
