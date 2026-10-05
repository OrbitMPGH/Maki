using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class UpgradeScanServiceTests : IDisposable
{
    private readonly UpgradeWorld _world = new();

    public void Dispose() => _world.Dispose();

    private async Task<UpgradeScanResult> ScanAsync()
    {
        using var db = _world.Db.NewContext();
        using var batches = _world.Batches();
        return await _world.Scanner(db, batches).ScanSeriesAsync(_world.SeriesId, CancellationToken.None);
    }

    /// <summary>The daily scan's rules: quiet period and memo both apply.</summary>
    private async Task<UpgradeScanResult> DailyScanAsync()
    {
        using var db = _world.Db.NewContext();
        using var batches = _world.Batches();
        return await _world.Scanner(db, batches).ScanAllAsync(CancellationToken.None, ignoreGlobalSwitch: true);
    }

    private List<DownloadQueueItem> Queue()
    {
        using var db = _world.Db.NewContext();
        return db.DownloadQueue.AsNoTracking().OrderBy(q => q.Id).ToList();
    }

    private List<UpgradeAttempt> Attempts()
    {
        using var db = _world.Db.NewContext();
        return db.UpgradeAttempts.AsNoTracking().ToList();
    }

    [Fact]
    public async Task Queues_a_better_copy_pinned_to_its_mapping_behind_everything_already_queued()
    {
        _world.Seed();
        var (chapterId, fileId) = _world.Chapter(1);
        var (missingId, _) = _world.Chapter(2, withFile: false, wanted: true);
        var (unwantedId, _) = _world.Chapter(3, wanted: false, file: f => f.Trusted = true);
        using (var db = _world.Db.NewContext())
        {
            db.DownloadQueue.Add(new DownloadQueueItem
            {
                SeriesId = _world.SeriesId, ChapterId = missingId, Status = QueueStatus.Completed,
                QueuedAt = DateTime.UtcNow, SortOrder = 7, Origin = DownloadOrigin.Manual
            });
            db.SaveChanges();
        }

        Dictionary<int, bool> WantedFlags()
        {
            using var db = _world.Db.NewContext();
            return db.Chapters.AsNoTracking().ToDictionary(c => c.Id, c => c.Wanted);
        }

        var wantedBefore = WantedFlags();

        var result = await ScanAsync();

        Assert.Equal(1, result.Enqueued);
        Assert.Equal(1, result.CandidatesProbed);
        var item = Assert.Single(Queue(), q => q.Origin == DownloadOrigin.Upgrade);
        Assert.Equal(chapterId, item.ChapterId);
        Assert.Equal(QueueStatus.Queued, item.Status);
        Assert.Equal(_world.OfficialMappingId, item.PreferredMappingId);
        Assert.Equal(_world.OfficialMappingId, item.SourceMappingId);
        Assert.Equal("o1", item.SourceChapterId);
        Assert.True(item.SortOrder > 7);
        var info = UpgradeInfo.Parse(item.UpgradeInfoJson)!;
        Assert.Equal(fileId, info.ChapterFileId);
        Assert.Equal("aggregator", info.Before.Tier);
        Assert.Equal("official", info.Predicted.Tier);
        Assert.Equal(1600, info.Predicted.MedianWidth);
        Assert.Equal(UpgradeOutcomes.Pending, info.Outcome);
        var attempt = Assert.Single(Attempts());
        Assert.Equal(UpgradeReasons.Enqueued, attempt.Reason);
        Assert.Equal(attempt.Id, info.AttemptId);

        Assert.DoesNotContain(Queue(), q => q.ChapterId == missingId && q.Origin == DownloadOrigin.Upgrade);
        Assert.DoesNotContain(Queue(), q => q.ChapterId == unwantedId);
        Assert.Equal(wantedBefore, WantedFlags());
    }

    [Fact]
    public async Task A_chapter_without_a_file_is_never_touched_even_when_wanted()
    {
        _world.Seed();
        _world.Chapter(1, withFile: false, wanted: true);

        var result = await ScanAsync();

        Assert.Equal(0, result.ChaptersChecked);
        Assert.Empty(Queue());
        Assert.Empty(Attempts());
        Assert.Empty(_world.Http.Requested);
    }

    public static TheoryData<string, string> SkippedFiles => new()
    {
        { "unmeasured", "unmeasured" },
        { "trusted", "trusted" },
        { "cutoff", "cutoff_met" },
        { "quiet", UpgradeReasons.QuietPeriod },
        { "replacedRecently", UpgradeReasons.QuietPeriod },
    };

    [Theory]
    [MemberData(nameof(SkippedFiles))]
    public async Task Leaves_files_alone_that_are_not_up_for_upgrade(string state, string reason)
    {
        _world.Seed();
        _world.Chapter(1, file: f =>
        {
            switch (state)
            {
                case "unmeasured": f.MeasuredAtUtc = null; break;
                case "trusted": f.Trusted = true; break;
                case "cutoff": f.Tier = QualityTier.Official; f.SourceName = UpgradeWorld.Official; break;
                case "quiet": f.DateAdded = DateTime.UtcNow.AddDays(-2); break;
                case "replacedRecently": f.ReplacedAtUtc = DateTime.UtcNow.AddDays(-1); break;
            }
        });

        var result = await DailyScanAsync();

        Assert.Equal(0, result.Enqueued);
        Assert.Equal(1, result.Skipped[reason]);
        Assert.Empty(_world.Http.Requested);
        Assert.Empty(Queue());
    }

    [Fact]
    public async Task Incognito_series_are_skipped_when_the_setting_is_off()
    {
        _world.Seed(series: s => s.Incognito = IncognitoMode.Full);
        _world.Chapter(1);
        _world.Settings.Set(SettingKeys.UpgradesScanIncognito, "false");

        var result = await ScanAsync();

        Assert.Equal(0, result.SeriesScanned);
        Assert.Empty(Queue());

        _world.Settings.Set(SettingKeys.UpgradesScanIncognito, "true");
        Assert.Equal(1, (await ScanAsync()).Enqueued);
    }

    [Fact]
    public async Task A_disabled_mapping_is_not_a_candidate()
    {
        _world.Seed();
        _world.Chapter(1);
        using (var db = _world.Db.NewContext())
        {
            db.SourceMappings.Where(m => m.Id == _world.OfficialMappingId)
                .ExecuteUpdate(s => s.SetProperty(m => m.Enabled, false));
        }

        var result = await ScanAsync();

        Assert.Equal(0, result.CandidatesProbed);
        Assert.Empty(_world.Http.Requested);
        Assert.Empty(Queue());
    }

    [Fact]
    public async Task A_globally_disabled_source_is_not_a_candidate()
    {
        _world.Seed();
        _world.Chapter(1);
        _world.Settings.Set(SettingKeys.SourcesDisabled, UpgradeWorld.Official);

        var result = await ScanAsync();

        Assert.Equal(0, result.CandidatesProbed);
        Assert.Empty(_world.Http.Requested);
        Assert.Empty(Queue());
    }

    [Fact]
    public async Task The_files_own_source_is_only_a_candidate_for_a_re_upload()
    {
        _world.Seed(p => p.Cutoff = QualityTier.Volume);
        _world.AggPages = UpgradeWorld.UrlPages(20);
        _world.Chapter(1, file: f => f.SourceChapterId = "a1");
        using (var db = _world.Db.NewContext())
        {
            db.SourceMappings.Where(m => m.Id == _world.OfficialMappingId)
                .ExecuteUpdate(s => s.SetProperty(m => m.Enabled, false));
        }

        Assert.Equal(0, (await ScanAsync()).CandidatesProbed);
        Assert.Empty(Queue());

        using (var db = _world.Db.NewContext())
        {
            db.ChapterSourceLinks.Where(l => l.SourceMappingId == _world.AggMappingId)
                .ExecuteUpdate(s => s.SetProperty(l => l.SourceChapterId, "a1-v2"));
        }

        var result = await ScanAsync();

        Assert.Equal(1, result.CandidatesProbed);
        var item = Assert.Single(Queue());
        Assert.Equal(_world.AggMappingId, item.PreferredMappingId);
        Assert.Equal("a1-v2", item.SourceChapterId);
    }

    [Fact]
    public async Task A_losing_candidate_is_memoised_and_not_probed_again()
    {
        _world.Seed();
        _world.OfficialPages = UpgradeWorld.UrlPages(5);
        _world.Chapter(1);

        var first = await DailyScanAsync();
        var requests = _world.Http.Requested.Count;
        var second = await DailyScanAsync();

        Assert.Equal(1, first.CandidatesProbed);
        Assert.Equal(1, first.Skipped[UpgradeReasons.FewerPages]);
        Assert.Equal(0, second.CandidatesProbed);
        Assert.Equal(1, second.Skipped["memoised"]);
        Assert.Equal(requests, _world.Http.Requested.Count);
        var attempt = Assert.Single(Attempts());
        Assert.Equal(UpgradeReasons.FewerPages, attempt.Reason);
        Assert.True(attempt.Probed);
        Assert.Empty(Queue());
    }

    [Fact]
    public async Task A_scan_asked_for_by_hand_looks_again_at_memoised_and_quiet_chapters()
    {
        _world.Seed();
        _world.OfficialPages = UpgradeWorld.UrlPages(5);
        _world.Chapter(1);
        _world.Chapter(2, file: f => f.DateAdded = DateTime.UtcNow.AddDays(-1));

        var daily = await DailyScanAsync();
        var manual = await ScanAsync();

        Assert.Equal(1, daily.CandidatesProbed);
        Assert.Equal(1, daily.Skipped[UpgradeReasons.QuietPeriod]);
        Assert.Equal(2, manual.CandidatesProbed);
        Assert.False(manual.Skipped.ContainsKey("memoised"));
        Assert.False(manual.Skipped.ContainsKey(UpgradeReasons.QuietPeriod));
    }

    [Fact]
    public async Task The_series_keeps_why_its_last_scan_passed_chapters_over()
    {
        _world.Seed();
        _world.Chapter(1, file: f => f.Trusted = true);
        var (onlyOwnSource, _) = _world.Chapter(2);
        using (var db = _world.Db.NewContext())
        {
            db.ChapterSourceLinks.RemoveRange(
                db.ChapterSourceLinks.Where(l => l.ChapterId == onlyOwnSource && l.SourceMappingId == _world.OfficialMappingId));
            db.SaveChanges();
        }

        var result = await ScanAsync();

        Assert.Equal(0, result.CandidatesProbed);
        Assert.Equal(1, result.Skipped[UpgradeReasons.NoOtherSource]);
        using var check = _world.Db.NewContext();
        var scan = SeriesDto.FromEntity(check.Series.Single()).LastUpgradeScan!;
        Assert.Equal(2, scan.Checked);
        Assert.Equal(new Dictionary<string, int> { ["trusted"] = 1, [UpgradeReasons.NoOtherSource] = 1 }, scan.Skipped);
    }

    [Fact]
    public async Task A_pdf_file_is_skipped_as_unsupported()
    {
        _world.Seed();
        _world.Chapter(1, extension: "pdf");

        var result = await ScanAsync();

        Assert.Equal(1, result.Skipped[UpgradeReasons.UnsupportedFile]);
        Assert.Empty(_world.Http.Requested);
        Assert.Empty(Queue());
    }

    [Fact]
    public async Task An_enqueued_memo_whose_download_was_cancelled_is_probed_again()
    {
        _world.Seed();
        _world.Chapter(1);
        Assert.Equal(1, (await ScanAsync()).Enqueued);

        using (var db = _world.Db.NewContext())
        {
            db.DownloadQueue.ExecuteUpdate(s => s.SetProperty(q => q.Status, QueueStatus.Cancelled));
        }

        var again = await ScanAsync();

        Assert.Equal(1, again.CandidatesProbed);
        Assert.Equal(1, again.Enqueued);
        Assert.Equal(2, Queue().Count);
        Assert.Single(Attempts());
    }

    [Fact]
    public async Task An_enqueued_memo_whose_download_is_still_live_blocks_the_chapter()
    {
        _world.Seed();
        _world.Chapter(1);
        await ScanAsync();

        var again = await ScanAsync();

        Assert.Equal(0, again.CandidatesProbed);
        Assert.Equal(1, again.Skipped["queued"]);
    }

    [Fact]
    public async Task A_full_scan_writes_the_marker_and_a_series_scan_does_not()
    {
        _world.Seed();
        _world.Chapter(1);

        await ScanAsync();
        Assert.Null(await _world.Settings.GetAsync(SettingKeys.UpgradesLastScanDate));

        _world.Settings.Set(SettingKeys.UpgradesEnabled, "true");
        using var db = _world.Db.NewContext();
        using var batches = _world.Batches();
        await _world.Scanner(db, batches).ScanAllAsync(CancellationToken.None);
        Assert.Equal(UpgradeOptions.MarkerDate(UpgradeOptions.LocalNow(TimeProvider.System)),
            await _world.Settings.GetAsync(SettingKeys.UpgradesLastScanDate));
    }

    [Fact]
    public async Task A_profile_edit_reopens_memoised_candidates()
    {
        _world.Seed();
        _world.OfficialPages = UpgradeWorld.UrlPages(5);
        _world.Chapter(1);
        await ScanAsync();

        using (var db = _world.Db.NewContext())
        {
            db.UpgradeProfiles.ExecuteUpdate(s => s.SetProperty(p => p.Version, p => p.Version + 1));
        }

        Assert.Equal(1, (await ScanAsync()).CandidatesProbed);
        Assert.Equal(2, Attempts().Count);
    }

    [Fact]
    public async Task Stops_probing_at_maxProbesPerRun()
    {
        _world.Seed();
        _world.Chapter(1);
        _world.Chapter(2);
        _world.Chapter(3);
        _world.Settings.Set(SettingKeys.UpgradesMaxProbesPerRun, "2");

        var result = await ScanAsync();

        Assert.Equal(2, result.CandidatesProbed);
        Assert.Equal(2, result.Enqueued);
        Assert.Equal(1, result.Skipped["probe_budget"]);
        Assert.Equal(2, Attempts().Count);
    }

    [Fact]
    public async Task Stops_queueing_at_maxPerDay_without_memoising_the_rest()
    {
        _world.Seed();
        _world.Chapter(1);
        _world.Chapter(2);
        _world.Chapter(3);
        _world.Settings.Set(SettingKeys.UpgradesMaxPerDay, "1");

        var result = await ScanAsync();

        Assert.Equal(3, result.CandidatesProbed);
        Assert.Equal(1, result.Enqueued);
        Assert.Equal(2, result.Skipped["daily_cap"]);
        Assert.Single(Queue());
        Assert.Single(Attempts());

        var again = await ScanAsync();
        Assert.Equal(0, again.Enqueued);
        Assert.Equal(2, again.CandidatesProbed);
    }

    [Fact]
    public async Task A_volume_file_backing_several_chapters_is_never_replaced()
    {
        _world.Seed();
        var (_, fileId) = _world.Chapter(1);
        var (second, _) = _world.Chapter(2, withFile: false);
        using (var db = _world.Db.NewContext())
        {
            db.Chapters.Where(c => c.Id == second).ExecuteUpdate(s => s.SetProperty(c => c.ChapterFileId, fileId));
        }

        var result = await ScanAsync();

        Assert.Equal(2, result.Skipped["shared_file"]);
        Assert.Empty(Queue());
    }

    [Fact]
    public async Task The_daily_scan_does_nothing_while_upgrades_are_switched_off()
    {
        _world.Seed();
        _world.Chapter(1);

        using var db = _world.Db.NewContext();
        using var batches = _world.Batches();
        var result = await _world.Scanner(db, batches).ScanAllAsync(CancellationToken.None);

        Assert.Equal(0, result.SeriesScanned);
        Assert.Empty(Queue());

        _world.Settings.Set(SettingKeys.UpgradesEnabled, "true");
        Assert.Equal(1, (await _world.Scanner(db, batches).ScanAllAsync(CancellationToken.None)).Enqueued);
    }

    [Fact]
    public async Task A_series_with_upgrades_off_in_its_profile_is_skipped()
    {
        _world.Seed(p => p.UpgradesEnabled = false);
        _world.Chapter(1);

        Assert.Equal(0, (await ScanAsync()).SeriesScanned);
        Assert.Empty(Queue());
    }

    private async Task<UpgradeScanResult> ScanChapterAsync(int chapterId, int? userId = null)
    {
        using var db = _world.Db.NewContext();
        using var batches = _world.Batches();
        return await _world.Scanner(db, batches).ScanChapterAsync(chapterId, userId, CancellationToken.None);
    }

    private Series SeriesRow()
    {
        using var db = _world.Db.NewContext();
        return db.Series.AsNoTracking().Single(s => s.Id == _world.SeriesId);
    }

    [Fact]
    public async Task A_chapter_scan_reports_every_candidate_and_ignores_the_quiet_period()
    {
        _world.Seed();
        // A different chapter id on the file's own source makes "agg" a candidate too (a re-upload);
        // its listing serves no pages, so its probe fails.
        var (chapterId, _) = _world.Chapter(1, file: f =>
        {
            f.SourceChapterId = "old";
            f.DateAdded = DateTime.UtcNow.AddDays(-1);
        });
        _world.Chapter(2);

        var result = await ScanChapterAsync(chapterId);

        Assert.Equal(1, result.Enqueued);
        Assert.Equal(1, result.ChaptersChecked);
        Assert.Equal(_world.OfficialMappingId, result.QueuedFromMappingId);
        Assert.Equal(2, result.Candidates.Count);
        var official = Assert.Single(result.Candidates, c => c.MappingId == _world.OfficialMappingId);
        Assert.Equal(UpgradeReasons.Enqueued, official.Reason);
        Assert.Equal(UpgradeWorld.Official, official.SourceName);
        Assert.Equal("o1", official.SourceChapterId);
        Assert.True(official.Probed);
        Assert.Equal(1600, official.MedianWidth);
        Assert.Equal(20, official.PageCount);
        Assert.Equal(10, official.Score);
        var agg = Assert.Single(result.Candidates, c => c.MappingId == _world.AggMappingId);
        Assert.Equal(UpgradeReasons.ProbeFailed, agg.Reason);
        Assert.Null(agg.MedianWidth);
        Assert.Equal(chapterId, Assert.Single(Queue()).ChapterId);

        var series = SeriesRow();
        Assert.NotNull(series.LastUpgradeScanUtc);
        Assert.Equal(2, series.LastUpgradeScanProbed);
        Assert.Equal(1, series.LastUpgradeScanQueued);
    }

    [Fact]
    public async Task A_chapter_scan_still_leaves_a_protected_file_alone()
    {
        _world.Seed();
        var (chapterId, _) = _world.Chapter(1, file: f => f.Trusted = true);

        var result = await ScanChapterAsync(chapterId);

        Assert.Equal(1, result.Skipped["trusted"]);
        Assert.Empty(result.Candidates);
        Assert.Null(result.QueuedFromMappingId);
        Assert.Empty(_world.Http.Requested);
        Assert.Empty(Queue());
    }

    [Fact]
    public async Task A_chapter_scan_reports_a_memoised_candidate_without_probing_it()
    {
        _world.Seed();
        var (chapterId, _) = _world.Chapter(1);
        using (var db = _world.Db.NewContext())
        {
            await UpgradeAttempts.UpsertAsync(db, chapterId, _world.SeriesId, _world.OfficialMappingId, "o1",
                _world.ProfileId, 1, UpgradeReasons.FewerPages, true, 3, 1600, 10, CancellationToken.None);
            db.SaveChanges();
        }

        var result = await ScanChapterAsync(chapterId);

        var outcome = Assert.Single(result.Candidates);
        Assert.Equal(UpgradeReasons.FewerPages, outcome.Reason);
        Assert.True(outcome.Probed);
        Assert.Equal(3, outcome.PageCount);
        Assert.Equal(1, result.Skipped["memoised"]);
        Assert.Empty(_world.Http.Requested);
        Assert.Null(result.QueuedFromMappingId);
        Assert.Equal(0, SeriesRow().LastUpgradeScanProbed);
    }

    [Fact]
    public async Task A_chapter_scan_of_a_series_without_a_profile_says_so()
    {
        _world.Seed(series: s => s.UpgradeProfileId = null);
        var (chapterId, _) = _world.Chapter(1);

        var result = await ScanChapterAsync(chapterId);

        Assert.Equal(1, result.Skipped["no_profile"]);
        Assert.Empty(result.Candidates);
        Assert.Null(SeriesRow().LastUpgradeScanUtc);
    }

    [Fact]
    public async Task A_chapter_scan_records_who_asked_on_the_upgrade_row()
    {
        _world.Seed();
        var (chapterId, _) = _world.Chapter(1);

        await ScanChapterAsync(chapterId, userId: 5);

        var item = Assert.Single(Queue());
        Assert.Equal(DownloadOrigin.Upgrade, item.Origin);
        Assert.Equal(5, item.QueuedByUserId);
    }

    [Fact]
    public async Task A_chapter_scan_ignores_the_daily_cap()
    {
        _world.Seed();
        var (chapterId, _) = _world.Chapter(1);
        var (earlierId, _) = _world.Chapter(2);
        _world.Settings.Set(SettingKeys.UpgradesMaxPerDay, "1");
        using (var db = _world.Db.NewContext())
        {
            db.DownloadQueue.Add(new DownloadQueueItem
            {
                SeriesId = _world.SeriesId, ChapterId = earlierId, Status = QueueStatus.Completed,
                QueuedAt = DateTime.UtcNow, Origin = DownloadOrigin.Upgrade
            });
            db.SaveChanges();
        }

        var result = await ScanChapterAsync(chapterId);

        Assert.Equal(1, result.Enqueued);
        Assert.False(result.Skipped.ContainsKey("daily_cap"));
        Assert.Equal(_world.OfficialMappingId, result.QueuedFromMappingId);
    }

    public static TheoryData<string, string> SkippedSeries => new()
    {
        { "noProfile", "no_profile" },
        { "upgradesOff", "upgrades_disabled" },
        { "incognito", "incognito" },
    };

    [Theory]
    [MemberData(nameof(SkippedSeries))]
    public async Task A_skipped_series_keeps_its_last_scan_empty(string state, string reason)
    {
        _world.Seed(p => p.UpgradesEnabled = state != "upgradesOff", s =>
        {
            if (state == "noProfile") s.UpgradeProfileId = null;
            if (state == "incognito") s.Incognito = IncognitoMode.Full;
        });
        _world.Chapter(1);
        _world.Settings.Set(SettingKeys.UpgradesScanIncognito, "false");

        var result = await ScanAsync();

        Assert.Equal(0, result.SeriesScanned);
        Assert.Equal(1, result.Skipped[reason]);
        var series = SeriesRow();
        Assert.Null(series.LastUpgradeScanUtc);
        Assert.Null(series.LastUpgradeScanProbed);
        Assert.Null(series.LastUpgradeScanQueued);
    }

    [Fact]
    public async Task A_series_scan_records_its_last_scan_on_the_series()
    {
        _world.Seed();
        _world.Chapter(1);
        Assert.Null(SeriesRow().LastUpgradeScanUtc);

        var result = await ScanAsync();

        Assert.Empty(result.Candidates);
        Assert.Null(result.QueuedFromMappingId);
        var series = SeriesRow();
        Assert.NotNull(series.LastUpgradeScanUtc);
        Assert.Equal(1, series.LastUpgradeScanProbed);
        Assert.Equal(1, series.LastUpgradeScanQueued);
        var dto = Maki.Api.Dtos.SeriesDto.FromEntity(series).LastUpgradeScan!;
        Assert.Equal((1, 1), (dto.Probed, dto.Queued));
    }

    [Fact]
    public async Task A_reverted_copy_stays_out_after_a_profile_edit_and_in_a_scan_by_hand()
    {
        _world.Seed();
        var (chapterId, _) = _world.Chapter(1);
        using (var db = _world.Db.NewContext())
        {
            db.UpgradeAttempts.Add(new UpgradeAttempt
            {
                ChapterId = chapterId, SeriesId = _world.SeriesId, SourceMappingId = _world.OfficialMappingId,
                SourceChapterId = "o1", ProfileId = _world.ProfileId, ProfileVersion = 1,
                Reason = UpgradeReasons.RevertedByUser, Probed = true, CreatedAtUtc = DateTime.UtcNow
            });
            db.SaveChanges();
            db.UpgradeProfiles.ExecuteUpdate(s => s.SetProperty(p => p.Version, p => p.Version + 1));
        }

        var manual = await ScanAsync();
        var daily = await DailyScanAsync();

        Assert.Equal(0, manual.CandidatesProbed);
        Assert.Equal(1, manual.Skipped["memoised"]);
        Assert.Equal(0, daily.CandidatesProbed);
        Assert.Empty(Queue());
        var outcome = Assert.Single((await ScanChapterAsync(chapterId)).Candidates);
        Assert.Equal(UpgradeReasons.RevertedByUser, outcome.Reason);
    }
}
