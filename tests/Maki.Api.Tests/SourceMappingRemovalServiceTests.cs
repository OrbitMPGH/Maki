using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class SourceMappingRemovalServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SourceMappingRemovalService BuildService(
        SourceAvailability? availability = null,
        DownloadQueueService? queue = null,
        DownloadBatchNotifier? batches = null) =>
        new(
            _db.NewContext(),
            availability ?? Sources.AllEnabled,
            queue ?? new DownloadQueueService(null!, TimeProvider.System, null!,
                NullLogger<DownloadQueueService>.Instance),
            batches!,
            new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
            NullLogger<SourceMappingRemovalService>.Instance);

    private static SourceMapping Mapping(string name, bool enabled = true) => new()
    {
        SourceName = name,
        SourceSeriesId = $"{name}-series",
        Url = $"https://{name}.test/series",
        Enabled = enabled
    };

    [Fact]
    public async Task Partial_cleanup_requires_snapshots_for_remaining_live_mappings()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("wrong"), Mapping("good")]);
        int wrongId;
        using (var db = _db.NewContext())
        {
            wrongId = db.SourceMappings.Single(m => m.SourceName == "wrong").Id;
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 1, Language = "en" });
            db.SaveChanges();
        }

        var error = await Assert.ThrowsAsync<MissingChapterSnapshotsException>(
            () => BuildService().RemoveAsync(wrongId, deleteFiles: false));

        Assert.Equal("good", Assert.Single(error.Mappings).SourceName);
        using var check = _db.NewContext();
        Assert.Equal(2, check.SourceMappings.Count(m => m.SeriesId == seriesId));
        Assert.Single(check.Chapters.Where(c => c.SeriesId == seriesId));
    }

    [Fact]
    public async Task Removing_the_last_source_needs_no_snapshot_and_keeps_files_unlinked()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("wrong"));
        int wrongId;
        using (var db = _db.NewContext())
        {
            wrongId = db.SourceMappings.Single().Id;
            var file = new ChapterFile
            {
                SeriesId = seriesId,
                RelativePath = "Test Series/wrong.cbz",
                SourceName = "wrong",
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            db.Chapters.Add(new Chapter
            {
                SeriesId = seriesId, Number = 1, Language = "en", ChapterFileId = file.Id
            });
            db.SaveChanges();
        }

        var result = await BuildService().RemoveAsync(wrongId, deleteFiles: false);

        Assert.NotNull(result);
        Assert.Equal(1, result.RemovedChapters);
        Assert.Equal(1, result.DetachedFiles);
        using var check = _db.NewContext();
        Assert.Empty(check.SourceMappings.Where(m => m.SeriesId == seriesId));
        Assert.Empty(check.Chapters.Where(c => c.SeriesId == seriesId));
        Assert.Single(check.ChapterFiles.Where(f => f.SeriesId == seriesId));
    }

    [Fact]
    public async Task Shared_chapter_survives_with_good_metadata_and_wrong_file_detached()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("wrong"), Mapping("good")]);
        int wrongId;
        int goodId;
        int sharedId;
        using (var db = _db.NewContext())
        {
            var mappings = db.SourceMappings.Where(m => m.SeriesId == seriesId).ToList();
            var wrong = mappings.Single(m => m.SourceName == "wrong");
            var good = mappings.Single(m => m.SourceName == "good");
            wrongId = wrong.Id;
            goodId = good.Id;
            wrong.ChapterSnapshotAt = DateTime.UtcNow;
            good.ChapterSnapshotAt = DateTime.UtcNow;

            var sharedFile = new ChapterFile
            {
                SeriesId = seriesId,
                RelativePath = "Test Series/shared.cbz",
                SourceName = "wrong",
                DateAdded = DateTime.UtcNow
            };
            var staleFile = new ChapterFile
            {
                SeriesId = seriesId,
                RelativePath = "Test Series/stale.cbz",
                SourceName = "wrong",
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.AddRange(sharedFile, staleFile);
            db.SaveChanges();

            var shared = new Chapter
            {
                SeriesId = seriesId, Number = 1, NumberRaw = "wrong-1", Volume = 9,
                Title = "Wrong title", Language = "en", ChapterFileId = sharedFile.Id
            };
            var stale = new Chapter
            {
                SeriesId = seriesId, Number = 50, Title = "Other show", Language = "en",
                ChapterFileId = staleFile.Id
            };
            db.Chapters.AddRange(shared, stale);
            db.SaveChanges();
            sharedId = shared.Id;

            db.ChapterSourceLinks.AddRange(
                Link(shared.Id, wrongId, "wrong-shared", "Wrong title", 9, "wrong-1"),
                Link(shared.Id, goodId, "good-shared", "Correct title", 1, "1"),
                Link(stale.Id, wrongId, "wrong-stale", "Other show", null, "50"));
            db.SaveChanges();
        }

        var result = await BuildService().RemoveAsync(wrongId, deleteFiles: false);

        Assert.NotNull(result);
        Assert.Equal(1, result.RemovedChapters);
        Assert.Equal(1, result.RetainedChapters);
        Assert.Equal(2, result.DetachedFiles);
        using var check = _db.NewContext();
        var chapter = Assert.Single(check.Chapters.Where(c => c.SeriesId == seriesId));
        Assert.Equal(sharedId, chapter.Id);
        Assert.Equal("Correct title", chapter.Title);
        Assert.Equal("1", chapter.NumberRaw);
        Assert.Equal(1, chapter.Volume);
        Assert.Null(chapter.ChapterFileId);
        Assert.Equal(2, check.ChapterFiles.Count(f => f.SeriesId == seriesId));
    }

    [Fact]
    public async Task Disabled_remaining_mapping_does_not_preserve_chapters()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("wrong"), Mapping("disabled", enabled: false)]);
        int wrongId;
        using (var db = _db.NewContext())
        {
            var mappings = db.SourceMappings.Where(m => m.SeriesId == seriesId).ToList();
            wrongId = mappings.Single(m => m.SourceName == "wrong").Id;
            var disabled = mappings.Single(m => m.SourceName == "disabled");
            disabled.ChapterSnapshotAt = DateTime.UtcNow;
            var chapter = new Chapter { SeriesId = seriesId, Number = 1, Language = "en" };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            db.ChapterSourceLinks.Add(Link(chapter.Id, disabled.Id, "disabled-1"));
            db.SaveChanges();
        }

        var result = await BuildService().RemoveAsync(wrongId, deleteFiles: false);

        Assert.NotNull(result);
        Assert.Equal(1, result.RemovedChapters);
        using var check = _db.NewContext();
        Assert.Empty(check.Chapters.Where(c => c.SeriesId == seriesId));
    }

    [Fact]
    public async Task Active_work_using_removed_mapping_is_cancelled_without_requeue()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("wrong"), Mapping("good")]);
        int wrongId;
        int queueItemId;
        using (var db = _db.NewContext())
        {
            var mappings = db.SourceMappings.Where(m => m.SeriesId == seriesId).ToList();
            var wrong = mappings.Single(m => m.SourceName == "wrong");
            var good = mappings.Single(m => m.SourceName == "good");
            wrongId = wrong.Id;
            good.ChapterSnapshotAt = DateTime.UtcNow;
            var chapter = new Chapter { SeriesId = seriesId, Number = 1, Language = "en" };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            db.ChapterSourceLinks.Add(Link(chapter.Id, good.Id, "good-1"));
            var item = new DownloadQueueItem
            {
                SeriesId = seriesId,
                ChapterId = chapter.Id,
                SourceMappingId = wrongId,
                Status = QueueStatus.Queued,
                QueuedAt = DateTime.UtcNow
            };
            db.DownloadQueue.Add(item);
            db.SaveChanges();
            queueItemId = item.Id;
        }

        var queue = new DownloadQueueService(null!, TimeProvider.System, null!,
            NullLogger<DownloadQueueService>.Instance);
        var cancellation = queue.WorkCancellationToken(queueItemId);
        using var batches = new DownloadBatchNotifier(
            null!, null!, TimeProvider.System, NullLogger<DownloadBatchNotifier>.Instance);

        var result = await BuildService(queue: queue, batches: batches)
            .RemoveAsync(wrongId, deleteFiles: false);

        Assert.NotNull(result);
        Assert.True(cancellation.IsCancellationRequested);
        using var check = _db.NewContext();
        Assert.Empty(check.DownloadQueue.Where(q => q.Id == queueItemId));
        Assert.Single(check.Chapters.Where(c => c.SeriesId == seriesId));
    }

    [Fact]
    public async Task Optional_file_deletion_removes_disk_file_and_record()
    {
        var root = Path.Combine(Path.GetTempPath(), $"maki-source-remove-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Test Series"));
        var relativePath = Path.Combine("Test Series", "wrong.cbz");
        var absolutePath = Path.Combine(root, relativePath);
        await File.WriteAllBytesAsync(absolutePath, [1, 2, 3]);

        try
        {
            var seriesId = _db.SeedSeries(mappings: Mapping("wrong"));
            int wrongId;
            using (var db = _db.NewContext())
            {
                var series = db.Series.Include(s => s.RootFolder).Single(s => s.Id == seriesId);
                series.RootFolder!.Path = root;
                wrongId = db.SourceMappings.Single(m => m.SeriesId == seriesId).Id;
                var file = new ChapterFile
                {
                    SeriesId = seriesId,
                    RelativePath = relativePath,
                    SourceName = "wrong",
                    DateAdded = DateTime.UtcNow
                };
                db.ChapterFiles.Add(file);
                db.SaveChanges();
                db.Chapters.Add(new Chapter
                {
                    SeriesId = seriesId, Number = 1, Language = "en", ChapterFileId = file.Id
                });
                db.SaveChanges();
            }

            var result = await BuildService().RemoveAsync(wrongId, deleteFiles: true);

            Assert.NotNull(result);
            Assert.Equal(1, result.DeletedFiles);
            Assert.Equal(0, result.FailedFileDeletions);
            Assert.Empty(result.FailedFileDeletionPaths);
            Assert.False(File.Exists(absolutePath));
            using var check = _db.NewContext();
            Assert.Empty(check.ChapterFiles.Where(f => f.SeriesId == seriesId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task File_shared_with_a_retained_chapter_is_not_detached_or_deleted()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("wrong"), Mapping("good")]);
        int wrongId;
        using (var db = _db.NewContext())
        {
            var mappings = db.SourceMappings.Where(m => m.SeriesId == seriesId).ToList();
            var wrong = mappings.Single(m => m.SourceName == "wrong");
            var good = mappings.Single(m => m.SourceName == "good");
            wrongId = wrong.Id;
            good.ChapterSnapshotAt = DateTime.UtcNow;
            var file = new ChapterFile
            {
                SeriesId = seriesId,
                RelativePath = "Test Series/volume.cbz",
                SourceName = "good",
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            var retained = new Chapter
            {
                SeriesId = seriesId, Number = 1, Language = "en", ChapterFileId = file.Id
            };
            var removed = new Chapter
            {
                SeriesId = seriesId, Number = 50, Language = "en", ChapterFileId = file.Id
            };
            db.Chapters.AddRange(retained, removed);
            db.SaveChanges();
            db.ChapterSourceLinks.AddRange(
                Link(retained.Id, good.Id, "good-1"),
                Link(removed.Id, wrong.Id, "wrong-50"));
            db.SaveChanges();
        }

        var result = await BuildService().RemoveAsync(wrongId, deleteFiles: true);

        Assert.NotNull(result);
        Assert.Equal(0, result.DetachedFiles);
        Assert.Equal(0, result.DeletedFiles);
        using var check = _db.NewContext();
        var chapter = Assert.Single(check.Chapters.Where(c => c.SeriesId == seriesId));
        Assert.NotNull(chapter.ChapterFileId);
        Assert.Single(check.ChapterFiles.Where(f => f.SeriesId == seriesId));
    }

    [Fact]
    public async Task Unsafe_file_path_is_left_as_an_unlinked_record()
    {
        var seriesId = _db.SeedSeries(mappings: Mapping("wrong"));
        int wrongId;
        using (var db = _db.NewContext())
        {
            wrongId = db.SourceMappings.Single(m => m.SeriesId == seriesId).Id;
            var file = new ChapterFile
            {
                SeriesId = seriesId,
                RelativePath = "../outside.cbz",
                SourceName = "wrong",
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            db.Chapters.Add(new Chapter
            {
                SeriesId = seriesId, Number = 1, Language = "en", ChapterFileId = file.Id
            });
            db.SaveChanges();
        }

        var result = await BuildService().RemoveAsync(wrongId, deleteFiles: true);

        Assert.NotNull(result);
        Assert.Equal(1, result.FailedFileDeletions);
        Assert.Equal("../outside.cbz", Assert.Single(result.FailedFileDeletionPaths));
        using var check = _db.NewContext();
        Assert.Single(check.ChapterFiles.Where(f => f.SeriesId == seriesId));
        Assert.Empty(check.Chapters.Where(c => c.SeriesId == seriesId));
    }

    [Fact]
    public async Task Consecutive_removals_use_existing_snapshots()
    {
        var seriesId = _db.SeedSeries(mappings: [Mapping("first"), Mapping("second")]);
        int firstId;
        int secondId;
        using (var db = _db.NewContext())
        {
            var mappings = db.SourceMappings.Where(m => m.SeriesId == seriesId).ToList();
            var first = mappings.Single(m => m.SourceName == "first");
            var second = mappings.Single(m => m.SourceName == "second");
            firstId = first.Id;
            secondId = second.Id;
            first.ChapterSnapshotAt = DateTime.UtcNow;
            second.ChapterSnapshotAt = DateTime.UtcNow;
            var chapter = new Chapter { SeriesId = seriesId, Number = 1, Language = "en" };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            db.ChapterSourceLinks.AddRange(
                Link(chapter.Id, firstId, "first-1"),
                Link(chapter.Id, secondId, "second-1"));
            db.SaveChanges();
        }

        var firstResult = await BuildService().RemoveAsync(firstId, deleteFiles: false);
        var secondResult = await BuildService().RemoveAsync(secondId, deleteFiles: false);

        Assert.NotNull(firstResult);
        Assert.Equal(1, firstResult.RetainedChapters);
        Assert.NotNull(secondResult);
        Assert.Equal(1, secondResult.RemovedChapters);
        using var check = _db.NewContext();
        Assert.Empty(check.SourceMappings.Where(m => m.SeriesId == seriesId));
        Assert.Empty(check.Chapters.Where(c => c.SeriesId == seriesId));
    }

    private static ChapterSourceLink Link(
        int chapterId,
        int mappingId,
        string sourceChapterId,
        string? title = null,
        int? volume = null,
        string? numberRaw = null) =>
        new()
        {
            ChapterId = chapterId,
            SourceMappingId = mappingId,
            SourceChapterId = sourceChapterId,
            Title = title,
            Volume = volume,
            NumberRaw = numberRaw
        };
}
