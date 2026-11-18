using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;

namespace Maki.Api.Tests;

/// <summary>A folded chapter a source lists only as parts downloads as every part's pages, in order, in one file.</summary>
[Collection(ConfigDirCollection.Name)]
public class ChapterDownloadProcessorFoldTests : IDisposable
{
    private readonly UpgradeWorld _world = new();

    public ChapterDownloadProcessorFoldTests() => _world.Seed();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task A_chapter_folded_from_parts_downloads_both_parts_as_one()
    {
        Directory.CreateDirectory(_world.Library);
        var (chapterId, _) = _world.Chapter(1, withFile: false);
        using (var db = _world.Db.NewContext())
        {
            db.SourceMappings.Single(m => m.Id == _world.OfficialMappingId).Enabled = false;
            var link = db.ChapterSourceLinks.Single(l => l.ChapterId == chapterId && l.SourceMappingId == _world.AggMappingId);
            link.SourceChapterId = "a1.1";
            link.Parts = ChapterFoldPart.Format([new ChapterFoldPart("a1.1", 1.1m, "1.1", 5), new ChapterFoldPart("a1.2", 1.2m, "1.2", 7)]);
            db.SaveChanges();
        }

        var asked = new List<string>();
        _world.AggListing = id =>
        [
            new SourceChapter(UpgradeWorld.Agg, id, "a1.1", "1.1", 1.1m, null, null, "en", null),
            new SourceChapter(UpgradeWorld.Agg, id, "a1.2", "1.2", 1.2m, null, null, "en", null),
        ];
        _world.AggPages = c =>
        {
            asked.Add(c.SourceChapterId);
            return UpgradeWorld.InlinePages(c.SourceChapterId == "a1.1" ? 5 : 7, 700)(c);
        };

        int id;
        using (var db = _world.Db.NewContext())
        {
            var item = new DownloadQueueItem
            {
                SeriesId = _world.SeriesId, ChapterId = chapterId, Protocol = AcquisitionProtocol.Scraper,
                Status = QueueStatus.Queued, QueuedAt = DateTime.UtcNow,
            };
            db.DownloadQueue.Add(item);
            db.SaveChanges();
            id = item.Id;
        }

        await _world.ProcessAsync(id);

        using var check = _world.Db.NewContext();
        var row = check.DownloadQueue.Single(q => q.Id == id);
        Assert.True(row.Status == QueueStatus.Completed, $"{row.Status} {row.ErrorKey} {row.ErrorMessage}");
        Assert.Equal(["a1.1", "a1.2"], asked);
        var fileId = check.Chapters.Single(c => c.Id == chapterId).ChapterFileId;
        Assert.Equal(12, check.ChapterFiles.Single(f => f.Id == fileId).PageCount);
    }

    [Fact]
    public async Task A_row_queued_before_its_fold_broke_never_downloads_the_first_part_as_the_whole()
    {
        Directory.CreateDirectory(_world.Library);
        var (chapterId, _) = _world.Chapter(1, withFile: false);
        var (partId, _) = _world.Chapter(1.1m, withFile: false);
        using (var db = _world.Db.NewContext())
        {
            db.ChapterSourceLinks.Single(l => l.ChapterId == partId && l.SourceMappingId == _world.AggMappingId).SourceChapterId = "a1.1";
            db.SourceMappings.Single(m => m.Id == _world.OfficialMappingId).Enabled = false;
            // The sync that broke the fold took the whole's link; 1.1 is a row of its own again.
            db.ChapterSourceLinks.Remove(db.ChapterSourceLinks.Single(l => l.ChapterId == chapterId && l.SourceMappingId == _world.AggMappingId));
            db.SaveChanges();
        }

        var asked = new List<string>();
        _world.AggListing = id => [new SourceChapter(UpgradeWorld.Agg, id, "a1.1", "1.1", 1.1m, null, null, "en", null)];
        _world.AggPages = c =>
        {
            asked.Add(c.SourceChapterId);
            return UpgradeWorld.InlinePages(5, 700)(c);
        };

        int id;
        using (var db = _world.Db.NewContext())
        {
            var item = new DownloadQueueItem
            {
                SeriesId = _world.SeriesId, ChapterId = chapterId, Protocol = AcquisitionProtocol.Scraper,
                Status = QueueStatus.Queued, QueuedAt = DateTime.UtcNow, SourceMappingId = _world.AggMappingId, SourceChapterId = "a1.1",
            };
            db.DownloadQueue.Add(item);
            db.SaveChanges();
            id = item.Id;
        }

        await _world.ProcessAsync(id);

        using var check = _world.Db.NewContext();
        Assert.Empty(asked);
        Assert.NotEqual(QueueStatus.Completed, check.DownloadQueue.Single(q => q.Id == id).Status);
        Assert.Null(check.Chapters.Single(c => c.Id == chapterId).ChapterFileId);
    }
}
