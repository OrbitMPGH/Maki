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
        Assert.Null(file.ReplacedAtUtc);
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
