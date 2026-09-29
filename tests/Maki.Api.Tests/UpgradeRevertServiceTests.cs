using Maki.Api.Services;
using Maki.Core.Quality;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class UpgradeRevertServiceTests : IDisposable
{
    private readonly UpgradeWorld _world = new();
    private readonly int _fileId;
    private readonly string _path;
    private readonly byte[] _original;
    private readonly int _historyId;
    private readonly byte[] _upgraded;

    public UpgradeRevertServiceTests()
    {
        _world.Seed(minWidth: 150);
        var (chapterId, fileId) = _world.Chapter(1, onDisk: true, pages: 4, width: 80,
            file: f => { f.ReleaseName = "[Group] Series 001"; f.ReleaseHash = "abc123"; });
        _fileId = fileId!.Value;
        _path = Path.Combine(_world.Library, "Series", "Series 001.cbz");
        _original = File.ReadAllBytes(_path);
        _world.OfficialPages = UpgradeWorld.InlinePages(4, 160);
        _world.ProcessAsync(_world.QueueUpgrade(chapterId, _fileId)).GetAwaiter().GetResult();
        _upgraded = File.ReadAllBytes(_path);
        using var db = _world.Db.NewContext();
        _historyId = db.UpgradeHistory.Single().Id;
    }

    public void Dispose() => _world.Dispose();

    private async Task<UpgradeRevertError> RevertAsync()
    {
        using var db = _world.Db.NewContext();
        var (_, error) = await new UpgradeRevertService(db, _world.Archives, NullLogger<UpgradeRevertService>.Instance)
            .RevertAsync(_historyId, null, CancellationToken.None);
        return error;
    }

    [Fact]
    public async Task Round_trip_restores_the_bytes_and_the_columns()
    {
        Assert.NotEqual(_original, _upgraded);
        await _world.Archives.GetAsync(_fileId, _upgraded.Length, _path);
        var loads = _world.Archives.Loads;

        Assert.Equal(UpgradeRevertError.None, await RevertAsync());

        Assert.Equal(_original, File.ReadAllBytes(_path));
        await _world.Archives.GetAsync(_fileId, _upgraded.Length, _path);
        Assert.Equal(loads + 1, _world.Archives.Loads);

        using var db = _world.Db.NewContext();
        var file = db.ChapterFiles.Single(f => f.Id == _fileId);
        Assert.Equal(QualityTier.Aggregator, file.Tier);
        Assert.Equal(UpgradeWorld.Agg, file.SourceName);
        Assert.Equal("a1", file.SourceChapterId);
        Assert.Equal(80, file.MedianWidth);
        Assert.Equal(4, file.PageCount);
        Assert.Equal(_original.Length, file.Size);
        Assert.NotNull(file.ReplacedAtUtc);
        Assert.Equal("[Group] Series 001", file.ReleaseName);
        Assert.Equal("abc123", file.ReleaseHash);

        var history = db.UpgradeHistory.Single();
        Assert.NotNull(history.RevertedAtUtc);
        Assert.Equal($".maki-trash/{_world.SeriesId}/{_fileId}-reverted-Series 001.cbz", history.TrashPath);
        Assert.Equal(_upgraded.Length, history.TrashBytes);
        Assert.Equal(_upgraded, File.ReadAllBytes(Path.Combine(_world.Library, history.TrashPath!)));

        var attempt = db.UpgradeAttempts.Single(a => a.SourceChapterId == "o1");
        Assert.Equal(UpgradeReasons.RevertedByUser, attempt.Reason);
        Assert.Equal(_world.OfficialMappingId, attempt.SourceMappingId);
    }

    [Fact]
    public void The_upgrade_clears_the_release_and_keeps_it_in_the_before_snapshot()
    {
        using var db = _world.Db.NewContext();
        var file = db.ChapterFiles.Single(f => f.Id == _fileId);
        Assert.Null(file.ReleaseName);
        Assert.Null(file.ReleaseHash);
        var before = QualitySnapshot.Parse(db.UpgradeHistory.Single().BeforeJson)!;
        Assert.Equal("[Group] Series 001", before.ReleaseName);
        Assert.Equal("abc123", before.ReleaseHash);
    }

    [Fact]
    public async Task Only_the_newest_standing_upgrade_of_a_file_can_be_reverted()
    {
        int newer;
        using (var db = _world.Db.NewContext())
        {
            var first = db.UpgradeHistory.Single();
            var row = new Maki.Core.Entities.UpgradeHistory
            {
                SeriesId = first.SeriesId, ChapterId = first.ChapterId, ChapterFileId = first.ChapterFileId,
                ProfileId = first.ProfileId, ProfileVersion = 1, BeforeJson = first.AfterJson, AfterJson = first.AfterJson,
                TrashPath = first.TrashPath, TrashBytes = first.TrashBytes, CreatedAtUtc = DateTime.UtcNow
            };
            db.UpgradeHistory.Add(row);
            db.SaveChanges();
            newer = row.Id;
        }

        Assert.Equal(UpgradeRevertError.NotLatest, await RevertAsync());
        Assert.Equal(_upgraded, File.ReadAllBytes(_path));

        using (var db = _world.Db.NewContext())
        {
            db.UpgradeHistory.Single(h => h.Id == newer).RevertedAtUtc = DateTime.UtcNow;
            db.SaveChanges();
        }

        Assert.Equal(UpgradeRevertError.None, await RevertAsync());
    }

    [Fact]
    public async Task A_cancel_after_the_first_move_still_leaves_the_rows_matching_the_disk()
    {
        using var cts = new CancellationTokenSource();
        using var db = _world.Db.NewContext();
        var service = new UpgradeRevertService(db, _world.Archives, new CancelOnLog(cts));

        var (_, error) = await service.RevertAsync(_historyId, null, cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(UpgradeRevertError.None, error);
        Assert.Equal(_original, File.ReadAllBytes(_path));
        using var check = _world.Db.NewContext();
        Assert.NotNull(check.UpgradeHistory.Single().RevertedAtUtc);
        Assert.Equal(80, check.ChapterFiles.Single(f => f.Id == _fileId).MedianWidth);
        Assert.Single(check.UpgradeAttempts, a => a.Reason == UpgradeReasons.RevertedByUser);
    }

    /// <summary>Cancels the revert the moment it logs, which it first does right after moving a file.</summary>
    private sealed class CancelOnLog(CancellationTokenSource cts) : Microsoft.Extensions.Logging.ILogger<UpgradeRevertService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => cts.Cancel();
    }

    [Fact]
    public async Task Queue_history_carries_the_upgrades_revert_state()
    {
        async Task<Maki.Api.Dtos.UpgradeQueueInfoDto> HistoryUpgradeAsync()
        {
            using var db = _world.Db.NewContext();
            using var batches = _world.Batches();
            var controller = new Maki.Api.Controllers.QueueController(new TestLocalizer(), db, _world.Queue, batches,
                null!, new Maki.Api.Hubs.EventBroadcaster(new NoopHubContext(), _world.Db.ScopeFactory()), null!,
                NullLogger<Maki.Api.Controllers.QueueController>.Instance);
            var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.History(1, 25, default));
            var page = Assert.IsType<Maki.Api.Dtos.QueueHistoryDto>(ok.Value);
            var row = Assert.Single(page.Items);
            Assert.Equal("upgrade", row.Origin);
            return row.Upgrade!;
        }

        var applied = await HistoryUpgradeAsync();
        Assert.Equal(_historyId, applied.HistoryId);
        Assert.False(applied.Reverted);
        Assert.True(applied.TrashAvailable);

        await RevertAsync();
        var reverted = await HistoryUpgradeAsync();
        Assert.True(reverted.Reverted);
        Assert.False(reverted.TrashAvailable);
    }

    [Fact]
    public async Task A_second_revert_is_refused()
    {
        Assert.Equal(UpgradeRevertError.None, await RevertAsync());
        Assert.Equal(UpgradeRevertError.AlreadyReverted, await RevertAsync());
        Assert.Equal(_original, File.ReadAllBytes(_path));
    }

    [Fact]
    public async Task Refused_when_the_trashed_file_is_gone()
    {
        File.Delete(Path.Combine(_world.Library, ".maki-trash", _world.SeriesId.ToString(), $"{_fileId}-Series 001.cbz"));

        Assert.Equal(UpgradeRevertError.TrashGone, await RevertAsync());
        Assert.Equal(_upgraded, File.ReadAllBytes(_path));
    }

    [Fact]
    public async Task The_controller_answers_409_for_both_refusals()
    {
        using var db = _world.Db.NewContext();
        var controller = new Maki.Api.Controllers.UpgradesController(
            new UpgradeEvaluationService(db, TestQuality.Create(_world.Registry)), db, new TestLocalizer(),
            NullLogger<Maki.Api.Controllers.UpgradesController>.Instance);
        var reverts = new UpgradeRevertService(db, _world.Archives, NullLogger<UpgradeRevertService>.Instance);
        var user = new TestCurrentUser(1);

        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Revert(_historyId, reverts, user, default));
        var again = Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(
            await controller.Revert(_historyId, reverts, user, default));
        Assert.Equal(409, again.StatusCode);
    }
}

/// <summary>
/// A torrent volume that replaced two single-chapter files, as <c>TorrentImportService</c> leaves it:
/// both old files in the trash, their rows gone, one grouped history row each, and both chapters on
/// the volume file.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public class UpgradeGroupRevertTests : IDisposable
{
    private readonly UpgradeWorld _world = new();
    private readonly Guid _group = Guid.NewGuid();
    private readonly int _volumeId;
    private readonly int[] _chapterIds;
    private readonly string _volumePath;
    private readonly byte[] _volumeBytes;
    private readonly Dictionary<int, byte[]> _originals = [];

    public UpgradeGroupRevertTests()
    {
        _world.Seed();
        var one = _world.Chapter(1, withFile: false).ChapterId;
        var two = _world.Chapter(2, withFile: false).ChapterId;
        _chapterIds = [one, two];
        _volumePath = Path.Combine(_world.Library, "Series", "Series v01.cbz");
        TestQuality.WriteCbz(_volumePath, 8, 120, 180);
        _volumeBytes = File.ReadAllBytes(_volumePath);

        using var db = _world.Db.NewContext();
        var volume = new Maki.Core.Entities.ChapterFile
        {
            SeriesId = _world.SeriesId, RelativePath = "Series/Series v01.cbz", SourceName = "torrent:Nyaa",
            Tier = QualityTier.Volume, Size = _volumeBytes.Length, DateAdded = DateTime.UtcNow
        };
        db.ChapterFiles.Add(volume);
        db.SaveChanges();
        _volumeId = volume.Id;

        var trashDir = Path.Combine(_world.Library, ".maki-trash", _world.SeriesId.ToString());
        Directory.CreateDirectory(trashDir);
        for (var i = 0; i < 2; i++)
        {
            var oldId = 900 + i;
            var name = $"Series {i + 1:000}.cbz";
            var trash = Path.Combine(trashDir, $"{oldId}-{name}");
            TestQuality.WriteCbz(trash, 4, 80 + i, 120);
            _originals[_chapterIds[i]] = File.ReadAllBytes(trash);
            db.Chapters.Single(c => c.Id == _chapterIds[i]).ChapterFileId = volume.Id;
            db.UpgradeHistory.Add(new Maki.Core.Entities.UpgradeHistory
            {
                SeriesId = _world.SeriesId,
                ChapterId = _chapterIds[i],
                ChapterFileId = oldId,
                BeforeJson = new QualitySnapshot { Tier = "aggregator", SourceName = UpgradeWorld.Agg, PageCount = 4, MedianWidth = 80 + i }.Serialize(),
                AfterJson = new QualitySnapshot { Tier = "volume", SourceName = "torrent:Nyaa", PageCount = 8 }.Serialize(),
                TrashPath = $".maki-trash/{_world.SeriesId}/{oldId}-{name}",
                TrashBytes = _originals[_chapterIds[i]].Length,
                CreatedAtUtc = DateTime.UtcNow,
                GroupId = _group,
                DetailJson = new VolumeReplacementDetail
                {
                    RelativePath = $"Series/{name}", ChapterIds = [_chapterIds[i]], ReplacementFileId = volume.Id,
                    DateAdded = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                }.Serialize()
            });
        }

        db.SaveChanges();
    }

    public void Dispose() => _world.Dispose();

    private async Task<UpgradeRevertError> RevertAsync()
    {
        using var db = _world.Db.NewContext();
        var (_, error) = await new UpgradeRevertService(db, _world.Archives, NullLogger<UpgradeRevertService>.Instance)
            .RevertGroupAsync(_group, null, CancellationToken.None);
        return error;
    }

    private string Original(int index) => Path.Combine(_world.Library, "Series", $"Series {index + 1:000}.cbz");

    [Fact]
    public async Task A_group_revert_puts_every_file_back_and_the_volume_in_the_trash()
    {
        Assert.Equal(UpgradeRevertError.None, await RevertAsync());

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(_originals[_chapterIds[i]], File.ReadAllBytes(Original(i)));
        }

        Assert.False(File.Exists(_volumePath));
        using var db = _world.Db.NewContext();
        Assert.False(db.ChapterFiles.Any(f => f.Id == _volumeId));
        foreach (var (chapterId, index) in _chapterIds.Select((id, i) => (id, i)))
        {
            var fileId = db.Chapters.Single(c => c.Id == chapterId).ChapterFileId;
            var file = db.ChapterFiles.Single(f => f.Id == fileId);
            Assert.Equal($"Series/Series {index + 1:000}.cbz", file.RelativePath);
            Assert.Equal(QualityTier.Aggregator, file.Tier);
            Assert.Equal(80 + index, file.MedianWidth);
        }

        var rows = db.UpgradeHistory.Where(h => h.GroupId == _group).ToList();
        Assert.All(rows, r => Assert.NotNull(r.RevertedAtUtc));
        var aside = Assert.Single(rows.Select(r => r.TrashPath).Distinct());
        Assert.Equal($".maki-trash/{_world.SeriesId}/{_volumeId}-reverted-Series v01.cbz", aside);
        Assert.Equal(_volumeBytes, File.ReadAllBytes(Path.Combine(_world.Library, aside!)));

        using var trashDb = _world.Db.NewContext();
        var trash = new UpgradeTrashService(trashDb, _world.Settings, NullLogger<UpgradeTrashService>.Instance);
        Assert.Equal((_volumeBytes.LongLength, 1), await trash.SizeAsync(CancellationToken.None));
        Assert.Equal(UpgradeRevertError.AlreadyReverted, await RevertAsync());
    }

    [Fact]
    public async Task A_chapter_the_volume_brought_that_no_row_names_is_unlinked()
    {
        var extra = _world.Chapter(3, withFile: false).ChapterId;
        using (var db = _world.Db.NewContext())
        {
            db.Chapters.Single(c => c.Id == extra).ChapterFileId = _volumeId;
            db.SaveChanges();
        }

        Assert.Equal(UpgradeRevertError.None, await RevertAsync());

        using var check = _world.Db.NewContext();
        Assert.Null(check.Chapters.Single(c => c.Id == extra).ChapterFileId);
    }

    [Fact]
    public async Task A_file_split_across_two_volumes_still_reverts()
    {
        var three = _world.Chapter(3, withFile: false).ChapterId;
        var four = _world.Chapter(4, withFile: false).ChapterId;
        var secondPath = Path.Combine(_world.Library, "Series", "Series v02.cbz");
        TestQuality.WriteCbz(secondPath, 8, 120, 180);
        var trash = Path.Combine(_world.Library, ".maki-trash", _world.SeriesId.ToString(), "902-Series 003-004.cbz");
        TestQuality.WriteCbz(trash, 8, 90, 120);
        var original = File.ReadAllBytes(trash);
        int secondId;
        using (var db = _world.Db.NewContext())
        {
            var second = new Maki.Core.Entities.ChapterFile
            {
                SeriesId = _world.SeriesId, RelativePath = "Series/Series v02.cbz", SourceName = "torrent:Nyaa",
                Tier = QualityTier.Volume, DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(second);
            db.SaveChanges();
            secondId = second.Id;
            db.Chapters.Single(c => c.Id == three).ChapterFileId = _volumeId;
            db.Chapters.Single(c => c.Id == four).ChapterFileId = second.Id;
            db.UpgradeHistory.Add(new Maki.Core.Entities.UpgradeHistory
            {
                SeriesId = _world.SeriesId, ChapterId = three, ChapterFileId = 902,
                BeforeJson = new QualitySnapshot { Tier = "aggregator", PageCount = 8, MedianWidth = 90 }.Serialize(),
                AfterJson = new QualitySnapshot { Tier = "volume" }.Serialize(),
                TrashPath = $".maki-trash/{_world.SeriesId}/902-Series 003-004.cbz", TrashBytes = original.Length,
                CreatedAtUtc = DateTime.UtcNow, GroupId = _group,
                DetailJson = new VolumeReplacementDetail
                {
                    RelativePath = "Series/Series 003-004.cbz", ChapterIds = [three, four],
                    ReplacementFileId = _volumeId, ReplacementFileIds = [_volumeId, second.Id]
                }.Serialize()
            });
            db.SaveChanges();
        }

        Assert.Equal(UpgradeRevertError.None, await RevertAsync());

        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_world.Library, "Series", "Series 003-004.cbz")));
        Assert.False(File.Exists(secondPath));
        using var check = _world.Db.NewContext();
        Assert.False(check.ChapterFiles.Any(f => f.Id == secondId || f.Id == _volumeId));
        var restored = check.ChapterFiles.Single(f => f.RelativePath == "Series/Series 003-004.cbz");
        Assert.Equal(restored.Id, check.Chapters.Single(c => c.Id == three).ChapterFileId);
        Assert.Equal(restored.Id, check.Chapters.Single(c => c.Id == four).ChapterFileId);
    }

    [Fact]
    public async Task A_group_whose_trash_is_gone_is_refused_untouched()
    {
        File.Delete(Path.Combine(_world.Library, ".maki-trash", _world.SeriesId.ToString(), "901-Series 002.cbz"));

        Assert.Equal(UpgradeRevertError.TrashGone, await RevertAsync());
        Assert.True(File.Exists(_volumePath));
        Assert.False(File.Exists(Original(0)));
    }

    [Fact]
    public async Task A_group_whose_chapters_moved_on_is_refused_untouched()
    {
        using (var db = _world.Db.NewContext())
        {
            var other = new Maki.Core.Entities.ChapterFile
            {
                SeriesId = _world.SeriesId, RelativePath = "Series/newer.cbz", SourceName = "agg", DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(other);
            db.SaveChanges();
            db.Chapters.Single(c => c.Id == _chapterIds[1]).ChapterFileId = other.Id;
            db.SaveChanges();
        }

        Assert.Equal(UpgradeRevertError.NotLatest, await RevertAsync());
        Assert.True(File.Exists(_volumePath));
    }

    [Fact]
    public async Task A_collision_rolls_back_what_already_moved()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Original(1))!);
        File.WriteAllText(Original(1), "someone else's file");

        Assert.Equal(UpgradeRevertError.MoveFailed, await RevertAsync());

        Assert.Equal(_volumeBytes, File.ReadAllBytes(_volumePath));
        Assert.False(File.Exists(Original(0)));
        Assert.True(File.Exists(Path.Combine(_world.Library, ".maki-trash", _world.SeriesId.ToString(), "900-Series 001.cbz")));
        Assert.Equal("someone else's file", File.ReadAllText(Original(1)));
        using var db = _world.Db.NewContext();
        Assert.All(db.Chapters.Where(c => _chapterIds.Contains(c.Id)).ToList(), c => Assert.Equal(_volumeId, c.ChapterFileId));
        Assert.All(db.UpgradeHistory.ToList(), h => Assert.Null(h.RevertedAtUtc));
    }
}
