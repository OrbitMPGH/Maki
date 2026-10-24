using Maki.Api.Services;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class ChapterDownloadProcessorRenameRaceTests : IDisposable
{
    private readonly UpgradeWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task A_download_waiting_on_a_rename_lands_in_the_renamed_folder()
    {
        _world.Seed();
        var (chapterId, _) = _world.Chapter(1, withFile: false);
        _world.OfficialPages = UpgradeWorld.InlinePages(3, 100);
        Directory.CreateDirectory(_world.Library);

        int itemId;
        using (var db = _world.Db.NewContext())
        {
            var item = new DownloadQueueItem
            {
                SeriesId = _world.SeriesId, ChapterId = chapterId, SourceMappingId = _world.OfficialMappingId,
                PreferredMappingId = _world.OfficialMappingId, SourceChapterId = "o1",
                QueuedAt = DateTime.UtcNow, Origin = DownloadOrigin.Manual
            };
            db.DownloadQueue.Add(item);
            db.SaveChanges();
            itemId = item.Id;
        }

        Task<DownloadOutcome> run;
        using (await SeriesLocks.SeriesAsync(_world.SeriesId, CancellationToken.None))
        {
            run = _world.ProcessAsync(itemId);
            // Downloaded and waiting to import: the folder it would have written to is about to change.
            await Wait.UntilAsync(() => run.IsCompleted || SeriesLocks.SeriesUsers(_world.SeriesId) == 2,
                "the download never asked for the series lock to import");
            Assert.False(run.IsCompleted);

            using var db = _world.Db.NewContext();
            db.Series.Single(s => s.Id == _world.SeriesId).FolderName = "Renamed";
            db.SaveChanges();
        }

        Assert.Equal(DownloadOutcome.Settled, await run);
        using var check = _world.Db.NewContext();
        var done = check.DownloadQueue.Single(q => q.Id == itemId);
        Assert.True(done.Status == QueueStatus.Completed, $"{done.Status} {done.ErrorKey}");
        var file = Assert.Single(check.ChapterFiles);
        Assert.StartsWith("Renamed", file.RelativePath);
        Assert.True(File.Exists(Path.Combine(_world.Library, file.RelativePath)));
    }
}
