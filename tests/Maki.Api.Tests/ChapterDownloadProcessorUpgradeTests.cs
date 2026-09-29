using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Quality;
using Maki.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

/// <summary>
/// Runs <see cref="DownloadOrigin.Upgrade"/> items through the real processor against a file on disk.
/// The existing file is 4 pages from "agg" at 80px; the profile's width format sits at 150px.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public class ChapterDownloadProcessorUpgradeTests : IDisposable
{
    private readonly UpgradeWorld _world = new();
    private int _chapterId;
    private int _fileId;
    private string _path = "";
    private byte[] _original = [];

    public ChapterDownloadProcessorUpgradeTests()
    {
        _world.Seed(minWidth: 150);
        var (chapterId, fileId) = _world.Chapter(1, onDisk: true, pages: 4, width: 80);
        _chapterId = chapterId;
        _fileId = fileId!.Value;
        _path = Path.Combine(_world.Library, "Series", "Series 001.cbz");
        _original = File.ReadAllBytes(_path);
    }

    public void Dispose() => _world.Dispose();

    private int QueueUpgrade() => _world.QueueUpgrade(_chapterId, _fileId);

    private async Task RunAsync(int itemId) =>
        Assert.Equal(DownloadOutcome.Settled, await _world.ProcessAsync(itemId));

    private string TmpPath(int itemId) => Path.Combine(_world.Library, ".maki", "tmp", $"{itemId}.cbz");

    private string TrashDir => Path.Combine(_world.Library, ".maki-trash", _world.SeriesId.ToString());

    [Fact]
    public async Task A_copy_that_loses_on_full_measurement_leaves_the_library_untouched()
    {
        // The probe said official at 160px; the full chapter turns out to be missing half its pages.
        _world.OfficialPages = UpgradeWorld.InlinePages(2, 160);
        var writeTime = File.GetLastWriteTimeUtc(_path);
        var itemId = QueueUpgrade();

        await RunAsync(itemId);

        Assert.Equal(_original, File.ReadAllBytes(_path));
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(_path));
        Assert.False(File.Exists(TmpPath(itemId)));
        Assert.False(Directory.Exists(TrashDir));

        using var db = _world.Db.NewContext();
        var item = db.DownloadQueue.Single(q => q.Id == itemId);
        Assert.Equal(QueueStatus.Completed, item.Status);
        Assert.Null(item.ErrorKey);
        var info = UpgradeInfo.Parse(item.UpgradeInfoJson)!;
        Assert.Equal(UpgradeOutcomes.Rejected, info.Outcome);
        Assert.Equal(UpgradeReasons.FewerPages, info.Reason);
        Assert.Equal(2, info.After!.PageCount);
        var attempt = db.UpgradeAttempts.Single();
        Assert.Equal(UpgradeReasons.UpgradeRejected, attempt.Reason);
        Assert.Equal("o1", attempt.SourceChapterId);
        Assert.Empty(db.UpgradeHistory);
        var file = db.ChapterFiles.Single(f => f.Id == _fileId);
        Assert.Equal(QualityTier.Aggregator, file.Tier);
        Assert.Null(file.ReplacedAtUtc);
        Assert.Empty(_world.Inbox.RaisedForSeries);
    }

    [Fact]
    public async Task A_winning_copy_replaces_the_file_at_the_same_path_and_trashes_the_old_one()
    {
        _world.OfficialPages = UpgradeWorld.InlinePages(4, 160);
        await _world.Archives.GetAsync(_fileId, _original.Length, _path);
        var loadsBefore = _world.Archives.Loads;
        DateTime dateAdded;
        using (var db = _world.Db.NewContext())
        {
            dateAdded = db.ChapterFiles.Single(f => f.Id == _fileId).DateAdded;
        }

        var itemId = QueueUpgrade();

        await RunAsync(itemId);

        var trashed = Path.Combine(TrashDir, $"{_fileId}-Series 001.cbz");
        Assert.Equal(_original, File.ReadAllBytes(trashed));
        Assert.NotEqual(_original, File.ReadAllBytes(_path));
        Assert.Equal(160, ChapterFileMeasurer.MeasureArchive(_path, 0, CancellationToken.None).MedianWidth);
        Assert.False(File.Exists(TmpPath(itemId)));

        await _world.Archives.GetAsync(_fileId, _original.Length, _path);
        Assert.Equal(loadsBefore + 1, _world.Archives.Loads);

        using var check = _world.Db.NewContext();
        var file = check.ChapterFiles.Single(f => f.Id == _fileId);
        Assert.Equal("Series/Series 001.cbz", file.RelativePath);
        Assert.Equal(QualityTier.Official, file.Tier);
        Assert.Equal(UpgradeWorld.Official, file.SourceName);
        Assert.Equal("o1", file.SourceChapterId);
        Assert.Equal(160, file.MedianWidth);
        Assert.Equal(new FileInfo(_path).Length, file.Size);
        Assert.NotNull(file.ReplacedAtUtc);
        Assert.Equal(dateAdded, file.DateAdded);
        Assert.Single(check.ChapterFiles);
        Assert.Equal(_fileId, check.Chapters.Single(c => c.Id == _chapterId).ChapterFileId);

        var history = check.UpgradeHistory.Single();
        Assert.Equal($".maki-trash/{_world.SeriesId}/{_fileId}-Series 001.cbz", history.TrashPath);
        Assert.Equal(_original.Length, history.TrashBytes);
        Assert.Equal("aggregator", QualitySnapshot.Parse(history.BeforeJson)!.Tier);
        Assert.Equal(80, QualitySnapshot.Parse(history.BeforeJson)!.MedianWidth);
        Assert.Equal("official", QualitySnapshot.Parse(history.AfterJson)!.Tier);
        Assert.Equal(itemId, history.QueueItemId);

        var item = check.DownloadQueue.Single(q => q.Id == itemId);
        Assert.Equal(QueueStatus.Completed, item.Status);
        var info = UpgradeInfo.Parse(item.UpgradeInfoJson)!;
        Assert.Equal(UpgradeOutcomes.Applied, info.Outcome);
        Assert.Equal(history.Id, info.HistoryId);

        Assert.Empty(check.StatsEvents);
        var raised = Assert.Single(_world.Inbox.RaisedForSeries);
        Assert.Equal(InboxEventType.ChapterUpgraded, raised.Type);
        Assert.Equal("inbox.upgrade.chapter", raised.Message.Key);
    }

    [Fact]
    public async Task A_hardlinked_old_file_is_moved_aside_and_its_other_link_keeps_its_bytes()
    {
        var seeding = Path.Combine(_world.Root, "torrents", "seeding.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(seeding)!);
        File.Delete(_path);
        File.WriteAllBytes(seeding, _original);
        var placement = FileLinker.Place(seeding, _path, preferHardlink: true);
        _world.OfficialPages = UpgradeWorld.InlinePages(4, 160);
        var itemId = QueueUpgrade();

        await RunAsync(itemId);

        Assert.Equal(_original, File.ReadAllBytes(seeding));
        Assert.Equal(_original, File.ReadAllBytes(Path.Combine(TrashDir, $"{_fileId}-Series 001.cbz")));
        Assert.NotEqual(_original, File.ReadAllBytes(_path));
        Assert.Equal(FilePlacement.Hardlinked, placement);
    }

    [Fact]
    public async Task A_target_that_is_gone_fails_cleanly()
    {
        _world.OfficialPages = UpgradeWorld.InlinePages(4, 160);
        File.Delete(_path);
        var itemId = QueueUpgrade();

        await RunAsync(itemId);

        Assert.False(File.Exists(_path));
        Assert.False(File.Exists(TmpPath(itemId)));
        Assert.False(Directory.Exists(TrashDir));
        using (var db = _world.Db.NewContext())
        {
            var item = db.DownloadQueue.Single(q => q.Id == itemId);
            Assert.Equal(QueueStatus.Failed, item.Status);
            Assert.Equal("error.download.upgradeTargetGone", item.ErrorKey);
            Assert.Null(item.NextAttempt);
            Assert.Equal(0, item.RetryCount);
            Assert.Empty(db.UpgradeHistory);
        }

        Assert.Equal(0, await _world.Queue.RequeueEligibleFailuresAsync(5));
        using var after = _world.Db.NewContext();
        Assert.Equal(QueueStatus.Failed, after.DownloadQueue.Single(q => q.Id == itemId).Status);
    }

    [Fact]
    public async Task A_file_entering_the_trash_ages_from_that_moment_not_from_its_old_mtime()
    {
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddDays(-90));
        _world.OfficialPages = UpgradeWorld.InlinePages(4, 160);

        await RunAsync(QueueUpgrade());

        var trashed = Path.Combine(TrashDir, $"{_fileId}-Series 001.cbz");
        Assert.True(File.GetLastWriteTimeUtc(trashed) > DateTime.UtcNow.AddMinutes(-5));

        // Orphan it, so only the sweep of unreferenced files could remove it.
        using (var db = _world.Db.NewContext())
        {
            db.UpgradeHistory.RemoveRange(db.UpgradeHistory);
            db.SaveChanges();
        }

        async Task PurgeAsync()
        {
            using var db = _world.Db.NewContext();
            await new UpgradeTrashService(db, _world.Settings,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<UpgradeTrashService>.Instance).PurgeAsync(default);
        }

        await PurgeAsync();
        Assert.True(File.Exists(trashed));

        File.SetLastWriteTimeUtc(trashed, DateTime.UtcNow.AddDays(-20));
        await PurgeAsync();
        Assert.False(File.Exists(trashed));
    }

    [Fact]
    public async Task A_pdf_target_is_rejected_and_left_alone()
    {
        var (chapterId, fileId) = _world.Chapter(5, onDisk: true, pages: 4, width: 80, extension: "pdf");
        var pdf = Path.Combine(_world.Library, "Series", "Series 005.pdf");
        var bytes = File.ReadAllBytes(pdf);
        _world.OfficialPages = UpgradeWorld.InlinePages(4, 160);
        var itemId = _world.QueueUpgrade(chapterId, fileId!.Value, "o5");

        await RunAsync(itemId);

        Assert.Equal(bytes, File.ReadAllBytes(pdf));
        Assert.False(File.Exists(TmpPath(itemId)));
        Assert.False(Directory.Exists(TrashDir));
        using var db = _world.Db.NewContext();
        var item = db.DownloadQueue.Single(q => q.Id == itemId);
        Assert.Equal(QueueStatus.Completed, item.Status);
        var info = UpgradeInfo.Parse(item.UpgradeInfoJson)!;
        Assert.Equal(UpgradeOutcomes.Rejected, info.Outcome);
        Assert.Equal(UpgradeReasons.UnsupportedFile, info.Reason);
        Assert.Empty(db.UpgradeHistory);
    }

    [Fact]
    public async Task An_upgrade_failure_sends_nothing_outbound()
    {
        _world.OfficialPages = _ => new Maki.Core.Sources.ChapterPages([]);
        var itemId = QueueUpgrade();

        await RunAsync(itemId);

        using var db = _world.Db.NewContext();
        Assert.Equal("error.download.noPages", db.DownloadQueue.Single(q => q.Id == itemId).ErrorKey);
        Assert.Empty(_world.Notifications.Sent);
        Assert.Equal(_original, File.ReadAllBytes(_path));
    }

    [Fact]
    public async Task An_upgrade_batch_sends_nothing_outbound_in_any_branch()
    {
        var clock = new StoppedClock(DateTimeOffset.UtcNow);
        var notifications = new RecordingNotifications();
        var inbox = new RecordingInbox();
        using var batches = new DownloadBatchNotifier(notifications, inbox, new TestLocalizer(),
            new TestUserLocaleResolver(), clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DownloadBatchNotifier>.Instance);

        await batches.QueuedAsync(1, "Series", [1, 2], DownloadOrigin.Upgrade);
        Assert.True(await batches.CompletedAsync(1, 1));
        Assert.True(await batches.FailedAsync(1, 2, "error.download.noPages"));

        await batches.QueuedAsync(2, "Other", [3, 4], DownloadOrigin.Upgrade);
        await batches.CompletedAsync(2, 3);
        clock.Now = clock.Now.AddHours(2);
        await batches.SweepStaleAsync();

        await batches.QueuedAsync(3, "Third", [5, 6], DownloadOrigin.Upgrade);
        await batches.CompletedAsync(3, 5);
        await batches.CompletedAsync(3, 6);

        Assert.Empty(notifications.Sent);
        Assert.Contains(inbox.RaisedForSeries, r => r.SeriesId == 3 && r.Type == InboxEventType.ChapterUpgraded);
        Assert.Contains(inbox.RaisedForSeries, r => r.SeriesId == 1 && r.Type == InboxEventType.DownloadFailed);
    }

    private int QueueForced(bool ignoreGuards = false, int? mappingId = null, string sourceChapterId = "o1") =>
        _world.QueueForced(_chapterId, _fileId, mappingId ?? _world.OfficialMappingId, sourceChapterId, ignoreGuards);

    /// <summary>Pages that pass validation but carry no dimensions the measurer can read.</summary>
    private static Func<Maki.Core.Sources.SourceChapter, Maki.Core.Sources.ChapterPages> AvifPages(int count)
    {
        var avif = new byte[256];
        avif[3] = 0x1C;
        "ftypavif"u8.CopyTo(avif.AsSpan(4));
        return c => new Maki.Core.Sources.ChapterPages([.. Enumerable.Range(0, count)
            .Select(i => new Maki.Core.Sources.PageRequest($"https://{c.SourceName}.test/{i}.avif", Data: avif))]);
    }

    private UpgradeInfo Info(int itemId)
    {
        using var db = _world.Db.NewContext();
        return UpgradeInfo.Parse(db.DownloadQueue.Single(q => q.Id == itemId).UpgradeInfoJson)!;
    }

    private void AssertUntouched(int itemId)
    {
        Assert.Equal(_original, File.ReadAllBytes(_path));
        Assert.False(File.Exists(TmpPath(itemId)));
        Assert.False(Directory.Exists(TrashDir));
        using var db = _world.Db.NewContext();
        Assert.Empty(db.UpgradeHistory);
        Assert.Empty(db.UpgradeAttempts);
    }

    [Fact]
    public async Task A_forced_pick_that_scores_lower_still_replaces_the_file_without_a_memo_or_inbox_row()
    {
        // Same tier as the file and below the profile's width format, so the upgrader would never take it.
        _world.AggPages = UpgradeWorld.InlinePages(4, 60);
        var itemId = QueueForced(mappingId: _world.AggMappingId, sourceChapterId: "a1");

        await RunAsync(itemId);

        Assert.Equal(_original, File.ReadAllBytes(Path.Combine(TrashDir, $"{_fileId}-Series 001.cbz")));
        Assert.Equal(60, ChapterFileMeasurer.MeasureArchive(_path, 0, CancellationToken.None).MedianWidth);
        Assert.False(File.Exists(TmpPath(itemId)));
        using var db = _world.Db.NewContext();
        var history = db.UpgradeHistory.Single();
        Assert.Equal(itemId, history.QueueItemId);
        var info = Info(itemId);
        Assert.Equal(UpgradeOutcomes.Applied, info.Outcome);
        Assert.True(info.Force);
        Assert.Equal(history.Id, info.HistoryId);
        Assert.Equal(QueueStatus.Completed, db.DownloadQueue.Single(q => q.Id == itemId).Status);
        Assert.NotNull(db.ChapterFiles.Single(f => f.Id == _fileId).ReplacedAtUtc);
        Assert.Empty(db.UpgradeAttempts);
        Assert.Empty(db.StatsEvents);
        Assert.Empty(_world.Inbox.RaisedForSeries);
        Assert.Empty(_world.Inbox.Raised);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_forced_pick_that_cannot_be_measured_is_rejected_unless_guards_are_ignored(bool ignoreGuards)
    {
        _world.OfficialPages = AvifPages(4);
        var itemId = QueueForced(ignoreGuards);

        await RunAsync(itemId);

        var info = Info(itemId);
        if (ignoreGuards)
        {
            Assert.Equal(UpgradeOutcomes.Applied, info.Outcome);
            Assert.NotEqual(_original, File.ReadAllBytes(_path));
            return;
        }

        Assert.Equal(UpgradeOutcomes.Rejected, info.Outcome);
        Assert.Equal(UpgradeReasons.Unmeasurable, info.Reason);
        AssertUntouched(itemId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_forced_pick_on_a_protected_file_is_rejected_unless_guards_are_ignored(bool ignoreGuards)
    {
        using (var db = _world.Db.NewContext())
        {
            db.ChapterFiles.Single(f => f.Id == _fileId).Trusted = true;
            db.SaveChanges();
        }

        _world.OfficialPages = UpgradeWorld.InlinePages(4, 160);
        var itemId = QueueForced(ignoreGuards);

        await RunAsync(itemId);

        var info = Info(itemId);
        if (ignoreGuards)
        {
            Assert.Equal(UpgradeOutcomes.Applied, info.Outcome);
            return;
        }

        Assert.Equal(UpgradeOutcomes.Rejected, info.Outcome);
        Assert.Equal("trusted", info.Reason);
        AssertUntouched(itemId);
    }

    [Fact]
    public async Task A_forced_pick_with_too_few_pages_is_rejected()
    {
        _world.OfficialPages = UpgradeWorld.InlinePages(2, 160);
        var itemId = QueueForced();

        await RunAsync(itemId);

        Assert.Equal(UpgradeReasons.FewerPages, Info(itemId).Reason);
        AssertUntouched(itemId);
    }

    [Fact]
    public async Task A_forced_pick_on_a_pdf_is_rejected_even_when_guards_are_ignored()
    {
        var (chapterId, fileId) = _world.Chapter(5, onDisk: true, pages: 4, width: 80, extension: "pdf");
        var pdf = Path.Combine(_world.Library, "Series", "Series 005.pdf");
        var bytes = File.ReadAllBytes(pdf);
        _world.OfficialPages = UpgradeWorld.InlinePages(4, 160);
        var itemId = _world.QueueForced(chapterId, fileId!.Value, _world.OfficialMappingId, "o5", ignoreGuards: true);

        await RunAsync(itemId);

        Assert.Equal(bytes, File.ReadAllBytes(pdf));
        var info = Info(itemId);
        Assert.Equal(UpgradeOutcomes.Rejected, info.Outcome);
        Assert.Equal(UpgradeReasons.UnsupportedFile, info.Reason);
        AssertUntouched(itemId);
    }

    [Fact]
    public async Task A_series_without_a_profile_can_still_be_force_replaced()
    {
        using (var db = _world.Db.NewContext())
        {
            db.Series.Single(s => s.Id == _world.SeriesId).UpgradeProfileId = null;
            db.SaveChanges();
        }

        _world.OfficialPages = UpgradeWorld.InlinePages(4, 160);
        var itemId = QueueForced();

        await RunAsync(itemId);

        Assert.Equal(UpgradeOutcomes.Applied, Info(itemId).Outcome);
        Assert.NotEqual(_original, File.ReadAllBytes(_path));
        using var check = _world.Db.NewContext();
        var history = check.UpgradeHistory.Single();
        Assert.Equal(0, history.ProfileId);
        Assert.Equal(QualityTier.Official, check.ChapterFiles.Single(f => f.Id == _fileId).Tier);
    }

    [Fact]
    public async Task A_forced_pick_whose_target_is_gone_sends_nothing_outbound()
    {
        _world.OfficialPages = UpgradeWorld.InlinePages(4, 160);
        File.Delete(_path);
        var itemId = QueueForced();

        await RunAsync(itemId);

        using var db = _world.Db.NewContext();
        Assert.Equal("error.download.upgradeTargetGone", db.DownloadQueue.Single(q => q.Id == itemId).ErrorKey);
        Assert.Empty(_world.Notifications.Sent);
    }

    [Fact]
    public async Task A_forced_pick_on_a_file_shared_with_another_chapter_is_rejected_even_when_guards_are_ignored()
    {
        var (otherId, _) = _world.Chapter(2, withFile: false);
        using (var db = _world.Db.NewContext())
        {
            db.Chapters.Single(c => c.Id == otherId).ChapterFileId = _fileId;
            db.SaveChanges();
        }

        _world.OfficialPages = UpgradeWorld.InlinePages(4, 160);
        var itemId = QueueForced(ignoreGuards: true);

        await RunAsync(itemId);

        var info = Info(itemId);
        Assert.Equal(UpgradeOutcomes.Rejected, info.Outcome);
        Assert.Equal("shared_file", info.Reason);
        AssertUntouched(itemId);
    }

    [Fact]
    public async Task An_upgrade_a_user_asked_for_stays_out_of_the_inbox_and_keeps_their_id_on_the_history()
    {
        _world.OfficialPages = UpgradeWorld.InlinePages(4, 160);
        var itemId = QueueUpgrade();
        using (var db = _world.Db.NewContext())
        {
            db.DownloadQueue.Single(q => q.Id == itemId).QueuedByUserId = 5;
            db.SaveChanges();
        }

        await RunAsync(itemId);

        Assert.Equal(UpgradeOutcomes.Applied, Info(itemId).Outcome);
        Assert.Empty(_world.Inbox.RaisedForSeries);
        using var check = _world.Db.NewContext();
        Assert.Equal(5, check.UpgradeHistory.Single().QueuedByUserId);
    }

    [Fact]
    public async Task A_normal_download_records_the_source_chapter_id()
    {
        _world.OfficialPages = UpgradeWorld.InlinePages(3, 40);
        var (chapterId, _) = _world.Chapter(2, withFile: false);
        int itemId;
        using (var db = _world.Db.NewContext())
        {
            var item = new DownloadQueueItem
            {
                SeriesId = _world.SeriesId, ChapterId = chapterId, SourceMappingId = _world.OfficialMappingId,
                SourceChapterId = "o2", QueuedAt = DateTime.UtcNow, Origin = DownloadOrigin.Manual
            };
            db.DownloadQueue.Add(item);
            db.SaveChanges();
            itemId = item.Id;
        }

        await RunAsync(itemId);

        using var check = _world.Db.NewContext();
        var file = check.Chapters.Include(c => c.ChapterFile).Single(c => c.Id == chapterId).ChapterFile!;
        Assert.Equal("o2", file.SourceChapterId);
        Assert.Null(file.ReplacedAtUtc);
        Assert.Single(check.StatsEvents);
        Assert.Empty(check.UpgradeHistory);
    }
}
