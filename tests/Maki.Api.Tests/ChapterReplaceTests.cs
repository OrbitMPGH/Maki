using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Core.Security;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="ChapterController.DownloadFrom"/> and <see cref="ChapterController.Redownload"/> on chapters
/// that already have a file: the row carries a forced <see cref="UpgradeInfo"/> so the processor
/// replaces the file through the upgrade gate instead of overwriting it.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public class ChapterReplaceTests : IDisposable
{
    private readonly UpgradeWorld _world = new();

    public ChapterReplaceTests() => _world.Seed();

    public void Dispose() => _world.Dispose();

    private ChapterController Controller(MakiDbContext db, MakiPermission permissions) => new(
        new TestLocalizer(), db, _world.Queue, new StatsEventService(db), _world.Archives, _world.Registry,
        _world.Availability,
        new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
        _world.Batches(), new TestCurrentUser(1, permissions: permissions), NullLogger<ChapterController>.Instance);

    private UpgradeEvaluationService Evaluation(MakiDbContext db) => new(db, TestQuality.Create(_world.Registry));

    private DownloadQueueItem Row(int chapterId)
    {
        using var db = _world.Db.NewContext();
        return db.DownloadQueue.AsNoTracking().Single(q => q.ChapterId == chapterId);
    }

    [Theory]
    [InlineData(MakiPermission.DownloadChapters, false)]
    [InlineData(MakiPermission.DownloadChapters | MakiPermission.ManageDownloadQueue, true)]
    public async Task Download_from_on_a_chapter_with_a_file_queues_a_forced_replacement(
        MakiPermission permissions, bool ignoreGuards)
    {
        var (chapterId, fileId) = _world.Chapter(1);
        using var db = _world.Db.NewContext();

        var result = await Controller(db, permissions).DownloadFrom(
            chapterId, new DownloadChapterFromRequest(_world.OfficialMappingId), Evaluation(db), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var row = Row(chapterId);
        Assert.Equal(DownloadOrigin.Manual, row.Origin);
        var info = UpgradeInfo.Parse(row.UpgradeInfoJson)!;
        Assert.True(info.Force);
        Assert.Equal(ignoreGuards, info.IgnoreGuards);
        Assert.Equal(fileId, info.ChapterFileId);
        Assert.Equal(_world.ProfileId, info.ProfileId);
        Assert.Equal("aggregator", info.Before.Tier);
        Assert.Equal(800, info.Before.MedianWidth);
        Assert.Equal(UpgradeWorld.Official, info.Predicted.SourceName);
        Assert.Equal("official", info.Predicted.Tier);
        Assert.Equal(UpgradeOutcomes.Pending, info.Outcome);
    }

    [Fact]
    public async Task Download_from_a_globally_switched_off_source_is_refused()
    {
        var (chapterId, _) = _world.Chapter(1, withFile: false);
        _world.Settings.Set(SettingKeys.SourcesDisabled, UpgradeWorld.Official);
        using var db = _world.Db.NewContext();

        var result = await Controller(db, MakiPermission.Admin).DownloadFrom(
            chapterId, new DownloadChapterFromRequest(_world.OfficialMappingId), Evaluation(db), CancellationToken.None);

        var refused = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("error.sourceMapping.sourceDisabled", refused.Value!.GetType().GetProperty("code")!.GetValue(refused.Value));
        Assert.False(_world.Db.NewContext().DownloadQueue.Any());
    }

    [Fact]
    public async Task Download_from_on_a_chapter_without_a_file_queues_a_plain_download()
    {
        var (chapterId, _) = _world.Chapter(1, withFile: false);
        using var db = _world.Db.NewContext();

        var result = await Controller(db, MakiPermission.Admin).DownloadFrom(
            chapterId, new DownloadChapterFromRequest(_world.OfficialMappingId), Evaluation(db), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(Row(chapterId).UpgradeInfoJson);
    }

    [Fact]
    public async Task Download_from_on_a_file_shared_with_another_chapter_queues_a_plain_download()
    {
        var (chapterId, fileId) = _world.Chapter(1);
        var (otherId, _) = _world.Chapter(2, withFile: false);
        using (var seed = _world.Db.NewContext())
        {
            seed.Chapters.Single(c => c.Id == otherId).ChapterFileId = fileId;
            seed.SaveChanges();
        }

        using var db = _world.Db.NewContext();
        await Controller(db, MakiPermission.Admin).DownloadFrom(
            chapterId, new DownloadChapterFromRequest(_world.OfficialMappingId), Evaluation(db), CancellationToken.None);

        Assert.Null(Row(chapterId).UpgradeInfoJson);
    }

    [Fact]
    public async Task Download_from_on_a_pdf_queues_a_plain_download()
    {
        var (chapterId, fileId) = _world.Chapter(1);
        using (var seed = _world.Db.NewContext())
        {
            var file = seed.ChapterFiles.Single(f => f.Id == fileId);
            file.RelativePath = Path.ChangeExtension(file.RelativePath, ".pdf");
            seed.SaveChanges();
        }

        using var db = _world.Db.NewContext();
        await Controller(db, MakiPermission.Admin).DownloadFrom(
            chapterId, new DownloadChapterFromRequest(_world.OfficialMappingId), Evaluation(db), CancellationToken.None);

        Assert.Null(Row(chapterId).UpgradeInfoJson);
    }

    [Fact]
    public async Task Download_from_stores_the_replacement_on_a_row_it_re_pins()
    {
        var (chapterId, fileId) = _world.Chapter(1);
        using (var seed = _world.Db.NewContext())
        {
            seed.DownloadQueue.Add(new DownloadQueueItem
            {
                SeriesId = _world.SeriesId, ChapterId = chapterId, Status = QueueStatus.Queued,
                QueuedAt = DateTime.UtcNow, Origin = DownloadOrigin.Manual
            });
            seed.SaveChanges();
        }

        using var db = _world.Db.NewContext();
        await Controller(db, MakiPermission.DownloadChapters).DownloadFrom(
            chapterId, new DownloadChapterFromRequest(_world.OfficialMappingId), Evaluation(db), CancellationToken.None);

        var info = UpgradeInfo.Parse(Row(chapterId).UpgradeInfoJson)!;
        Assert.True(info.Force);
        Assert.Equal(fileId, info.ChapterFileId);
    }

    [Fact]
    public async Task A_pick_over_a_queued_automatic_upgrade_makes_it_the_users_forced_replacement()
    {
        var (chapterId, fileId) = _world.Chapter(1);
        using (var scanDb = _world.Db.NewContext())
        using (var batches = _world.Batches())
        {
            Assert.Equal(1, (await _world.Scanner(scanDb, batches).ScanSeriesAsync(_world.SeriesId, default)).Enqueued);
        }

        var queued = Row(chapterId);
        Assert.Equal(DownloadOrigin.Upgrade, queued.Origin);
        Assert.Equal(QueueStatus.Queued, queued.Status);
        using (var check = _world.Db.NewContext())
        {
            Assert.Single(check.UpgradeAttempts);
        }

        using var db = _world.Db.NewContext();
        var controller = new ChapterController(
            new TestLocalizer(), db, _world.Queue, new StatsEventService(db), _world.Archives, _world.Registry,
            _world.Availability,
            new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
            _world.Batches(), new TestCurrentUser(7, permissions: MakiPermission.DownloadChapters),
            NullLogger<ChapterController>.Instance);
        var result = await controller.DownloadFrom(
            chapterId, new DownloadChapterFromRequest(_world.AggMappingId), Evaluation(db), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var row = Row(chapterId);
        Assert.Equal(queued.Id, row.Id);
        Assert.Equal(DownloadOrigin.Manual, row.Origin);
        Assert.Equal(7, row.QueuedByUserId);
        Assert.Equal(_world.AggMappingId, row.PreferredMappingId);
        var info = UpgradeInfo.Parse(row.UpgradeInfoJson)!;
        Assert.True(info.Force);
        Assert.Equal(fileId, info.ChapterFileId);
        using var after = _world.Db.NewContext();
        Assert.Empty(after.UpgradeAttempts);
    }

    [Theory]
    [InlineData(MakiPermission.DownloadChapters, false)]
    [InlineData(MakiPermission.Admin, true)]
    public async Task Redownload_queues_a_forced_replacement_for_every_chapter(MakiPermission permissions, bool ignoreGuards)
    {
        var (first, firstFile) = _world.Chapter(1);
        var (second, secondFile) = _world.Chapter(2);
        _world.OfficialListing = id =>
        [
            new SourceChapter(UpgradeWorld.Official, id, "o1", "1", 1m, null, null, "en", null),
            new SourceChapter(UpgradeWorld.Official, id, "o2", "2", 2m, null, null, "en", null)
        ];
        using var db = _world.Db.NewContext();

        var result = await Controller(db, permissions).Redownload(
            new RedownloadRequest(_world.SeriesId, UpgradeWorld.Official), Evaluation(db), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        foreach (var (chapterId, fileId) in new[] { (first, firstFile), (second, secondFile) })
        {
            var info = UpgradeInfo.Parse(Row(chapterId).UpgradeInfoJson)!;
            Assert.True(info.Force);
            Assert.Equal(ignoreGuards, info.IgnoreGuards);
            Assert.Equal(fileId, info.ChapterFileId);
            Assert.Equal(UpgradeWorld.Official, info.Predicted.SourceName);
        }
    }
}
