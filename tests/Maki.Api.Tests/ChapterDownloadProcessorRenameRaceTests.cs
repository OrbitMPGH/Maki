using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.Data.Sqlite;

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
            for (var i = 0; i < 400; i++)
            {
                try
                {
                    using var db = _world.Db.NewContext();
                    if (db.DownloadQueue.Single(q => q.Id == itemId).Status == QueueStatus.Importing)
                    {
                        break;
                    }
                }
                catch (SqliteException)
                {
                }

                await Task.Delay(25);
            }

            for (var i = 0; ; i++)
            {
                try
                {
                    using var db = _world.Db.NewContext();
                    db.Series.Single(s => s.Id == _world.SeriesId).FolderName = "Renamed";
                    db.SaveChanges();
                    break;
                }
                catch (Exception ex) when (i < 50 && (ex is SqliteException || ex.InnerException is SqliteException))
                {
                    await Task.Delay(50);
                }
            }
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
