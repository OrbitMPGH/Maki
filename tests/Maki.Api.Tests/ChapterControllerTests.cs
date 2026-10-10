using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Tests;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Covers the path-containment rules on the two actions that turn a stored
/// <c>ChapterFile.RelativePath</c> into a filesystem operation. <c>EditMetadata</c> and
/// <c>DeleteSeries</c> are ordinary non-admin permissions, so "the caller could do this anyway"
/// does not hold here: neither implies access to anything outside the library.
/// </summary>
public class ChapterControllerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"maki-chapters-{Guid.NewGuid():N}");

    public ChapterControllerTests() => Directory.CreateDirectory(Path.Combine(_root, "Series"));

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private ChapterController Controller(MakiDbContext db) => new(
        new TestLocalizer(),
        db,
        new DownloadQueueService(_db.ScopeFactory(), TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
        new StatsEventService(db),
        new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
        // Only the re-download action reaches these; nothing here exercises it.
        new SourceRegistry([]),
        new SourceAvailability(new FakeAppSettings(), new SourceRegistry([])),
        new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
        new DownloadBatchNotifier(
            new RecordingNotifications(), new RecordingInbox(), new TestLocalizer(),
            new TestUserLocaleResolver(), TimeProvider.System,
            NullLogger<DownloadBatchNotifier>.Instance),
        new TestCurrentUser(1),
        NullLogger<ChapterController>.Instance);

    private static ChapterFileDeletion Deletion(MakiDbContext db) => new(
        db, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), TimeProvider.System,
        NullLogger<ChapterFileDeletion>.Instance);

    /// <summary>
    /// A series rooted at the temp directory, plus one chapter, returning both ids. Passing
    /// <paramref name="rootFolderId"/> puts a second series in the same root instead of creating
    /// one, for tests where two series need to share a root the way a manual link can point one
    /// series' file at another's folder.
    /// </summary>
    private (int SeriesId, int ChapterId) SeedSeriesWithChapter(string title = "Series", int? rootFolderId = null)
    {
        using var db = _db.NewContext();
        var rootId = rootFolderId;
        if (rootId is null)
        {
            var root = new RootFolder { Path = _root };
            db.RootFolders.Add(root);
            db.SaveChanges();
            rootId = root.Id;
        }

        var series = new Series
        {
            Title = title,
            SortTitle = title.ToLowerInvariant(),
            RootFolderId = rootId.Value,
            FolderName = title,
            Added = DateTime.UtcNow
        };
        db.Series.Add(series);
        db.SaveChanges();

        var chapter = new Chapter { SeriesId = series.Id, Number = 1, NumberRaw = "1" };
        db.Chapters.Add(chapter);
        db.SaveChanges();

        return (series.Id, chapter.Id);
    }

    [Theory]
    [InlineData(@"..\outside.cbz")]
    [InlineData("../outside.cbz")]
    [InlineData(@"Series\..\..\outside.cbz")]
    public async Task Link_refuses_a_path_that_escapes_the_root_folder(string relativePath)
    {
        var (_, chapterId) = SeedSeriesWithChapter();

        // The file exists, so the "does it exist on disk" check cannot be what rejects this — the
        // containment check has to. Without it an EditMetadata holder can point a ChapterFile row at
        // anything on the filesystem, and the delete action below then removes it.
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "outside.cbz");
        await File.WriteAllTextAsync(outside, "not really a cbz");

        try
        {
            using var db = _db.NewContext();
            var result = await Controller(db).Link(
                new LinkChaptersRequest([chapterId], relativePath), default);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Empty(db.ChapterFiles);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task SetWantedBulk_flips_only_the_named_chapters_and_reports_the_count()
    {
        var (seriesId, first) = SeedSeriesWithChapter();
        int second;
        int untouched;
        using (var seed = _db.NewContext())
        {
            var two = seed.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 2, Wanted = true }).Entity;
            var three = seed.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 3, Wanted = true }).Entity;
            seed.SaveChanges();
            (second, untouched) = (two.Id, three.Id);
        }

        using (var db = _db.NewContext())
        {
            var result = Assert.IsType<OkObjectResult>(
                await Controller(db).SetWantedBulk(new SetChaptersWantedRequest([first, second, 999_999], false), default));
            Assert.Equal(2, result.Value!.GetType().GetProperty("updated")!.GetValue(result.Value));
        }

        using var check = _db.NewContext();
        Assert.False(check.Chapters.Single(c => c.Id == first).Wanted);
        Assert.False(check.Chapters.Single(c => c.Id == second).Wanted);
        Assert.True(check.Chapters.Single(c => c.Id == untouched).Wanted);
    }

    [Fact]
    public async Task Link_accepts_a_path_inside_the_root_folder()
    {
        var (seriesId, chapterId) = SeedSeriesWithChapter();
        var relativePath = Path.Combine("Series", "ch1.cbz");
        await File.WriteAllTextAsync(Path.Combine(_root, relativePath), "cbz");

        using var db = _db.NewContext();
        var result = await Controller(db).Link(
            new LinkChaptersRequest([chapterId], relativePath), default);

        Assert.IsType<OkObjectResult>(result);
        var file = Assert.Single(db.ChapterFiles);
        Assert.Equal(seriesId, file.SeriesId);
        Assert.Equal(relativePath, file.RelativePath);
    }

    // Regression: Resolve only proves the request text resolves *inside* the root; it says nothing
    // about whether the text itself is a sane relative path to store. "./X/a.cbz" and "../<root
    // name>/X/a.cbz" both resolve inside the root and used to be stored verbatim. LibraryPaths.TopFolder
    // then read the raw first segment ("." or "..") as if it were a real folder name, and
    // SeriesFolders.ForAsync handed that straight to rescan/relink, which enumerated the whole root
    // (".") or its parent ("..") as this series' own folder.
    [Fact]
    public async Task Link_canonicalizes_a_path_with_a_leading_dot_segment()
    {
        var (seriesId, chapterId) = SeedSeriesWithChapter();
        var onDisk = Path.Combine("Series", "ch1.cbz");
        await File.WriteAllTextAsync(Path.Combine(_root, onDisk), "cbz");
        var requestPath = "." + Path.DirectorySeparatorChar + onDisk;

        using var db = _db.NewContext();
        var result = await Controller(db).Link(new LinkChaptersRequest([chapterId], requestPath), default);

        Assert.IsType<OkObjectResult>(result);
        var file = Assert.Single(db.ChapterFiles);
        Assert.Equal(seriesId, file.SeriesId);
        Assert.Equal(onDisk, file.RelativePath);
    }

    [Fact]
    public async Task Link_canonicalizes_a_path_that_leaves_and_reenters_the_root_by_name()
    {
        var (_, chapterId) = SeedSeriesWithChapter();
        var onDisk = Path.Combine("Series", "ch1.cbz");
        await File.WriteAllTextAsync(Path.Combine(_root, onDisk), "cbz");
        var requestPath = Path.Combine("..", Path.GetFileName(_root)!, onDisk);

        using var db = _db.NewContext();
        var result = await Controller(db).Link(new LinkChaptersRequest([chapterId], requestPath), default);

        Assert.IsType<OkObjectResult>(result);
        var file = Assert.Single(db.ChapterFiles);
        Assert.Equal(onDisk, file.RelativePath);
    }

    [Fact]
    public async Task Link_canonicalizes_a_path_with_an_internal_dot_dot_segment()
    {
        var (_, chapterId) = SeedSeriesWithChapter();
        Directory.CreateDirectory(Path.Combine(_root, "Series", "Sub"));
        var onDisk = Path.Combine("Series", "ch2.cbz");
        await File.WriteAllTextAsync(Path.Combine(_root, onDisk), "cbz");
        var requestPath = Path.Combine("Series", "Sub", "..", "ch2.cbz");

        using var db = _db.NewContext();
        var result = await Controller(db).Link(new LinkChaptersRequest([chapterId], requestPath), default);

        Assert.IsType<OkObjectResult>(result);
        var file = Assert.Single(db.ChapterFiles);
        Assert.Equal(onDisk, file.RelativePath);
    }

    [Fact]
    public async Task Link_refuses_a_path_inside_another_series_folder()
    {
        var (seriesId, chapterId) = SeedSeriesWithChapter();
        var rootFolderId = await RootFolderIdOf(seriesId);
        SeedSeriesWithChapter("Other", rootFolderId);
        Directory.CreateDirectory(Path.Combine(_root, "Other"));
        var relativePath = Path.Combine("Other", "ch1.cbz");
        await File.WriteAllTextAsync(Path.Combine(_root, relativePath), "cbz");

        using var db = _db.NewContext();
        var result = await Controller(db).Link(new LinkChaptersRequest([chapterId], relativePath), default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(db.ChapterFiles);
    }

    private async Task<int> RootFolderIdOf(int seriesId)
    {
        using var db = _db.NewContext();
        return (await db.Series.FindAsync(seriesId))!.RootFolderId;
    }

    [Fact]
    public async Task Delete_refuses_a_batch_spanning_two_series()
    {
        var (_, first) = SeedSeriesWithChapter("First");
        var (_, second) = SeedSeriesWithChapter("Second");

        using var db = _db.NewContext();
        var result = await Controller(db).Delete([first, second], Deletion(db), default);

        // The root folder used to build the delete path comes from chapters[0]'s series, so a mixed
        // batch would delete the second series' file from under the first series' root.
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(2, await db.Chapters.CountAsync());
    }

    [Fact]
    public async Task Delete_leaves_a_file_that_resolves_outside_the_root_alone()
    {
        var (seriesId, chapterId) = SeedSeriesWithChapter();
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "keepme.cbz");
        await File.WriteAllTextAsync(outside, "not really a cbz");

        try
        {
            // Written straight to the database, as a row predating the check in Link would be. The
            // records still go, because the point of the endpoint is removing bad rows; what must
            // not happen is the file outside the library being deleted with them.
            using (var seed = _db.NewContext())
            {
                var file = new ChapterFile
                {
                    SeriesId = seriesId,
                    RelativePath = Path.Combine("..", "keepme.cbz"),
                    Size = 1,
                    SourceName = "Manual",
                    DateAdded = DateTime.UtcNow
                };
                seed.ChapterFiles.Add(file);
                seed.SaveChanges();

                var chapter = await seed.Chapters.FirstAsync(c => c.Id == chapterId);
                chapter.ChapterFileId = file.Id;
                seed.SaveChanges();
            }

            using var db = _db.NewContext();
            var result = await Controller(db).Delete([chapterId], Deletion(db), default);

            Assert.IsType<OkObjectResult>(result);
            Assert.True(File.Exists(outside));
            Assert.Empty(db.Chapters);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Link_and_Delete_do_not_follow_a_directory_symlink_out_of_the_root()
    {
        var (seriesId, chapterId) = SeedSeriesWithChapter();
        var outside = Path.Combine(Path.GetTempPath(), $"maki-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        var external = Path.Combine(outside, "ch1.cbz");
        await File.WriteAllTextAsync(external, "not really a cbz");
        var link = Path.Combine(_root, "Series", "linked");
        var throughLink = Path.Combine("Series", "linked", "ch1.cbz");

        try
        {
            if (!TestLinks.TryLinkDirectory(link, outside))
            {
                return;
            }

            using (var db = _db.NewContext())
            {
                var result = await Controller(db).Link(new LinkChaptersRequest([chapterId], throughLink), default);

                Assert.IsType<BadRequestObjectResult>(result);
                Assert.Empty(db.ChapterFiles);
            }

            using (var seed = _db.NewContext())
            {
                var file = new ChapterFile
                {
                    SeriesId = seriesId,
                    RelativePath = throughLink,
                    Size = 1,
                    SourceName = "Manual",
                    DateAdded = DateTime.UtcNow
                };
                seed.ChapterFiles.Add(file);
                seed.SaveChanges();
                (await seed.Chapters.FirstAsync(c => c.Id == chapterId)).ChapterFileId = file.Id;
                seed.SaveChanges();
            }

            using (var db = _db.NewContext())
            {
                var result = await Controller(db).Delete([chapterId], Deletion(db), default);

                Assert.IsType<OkObjectResult>(result);
                Assert.Empty(db.Chapters);
            }

            Assert.True(File.Exists(external));
        }
        finally
        {
            TestLinks.UnlinkDirectory(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Delete_removes_a_chapter_file_symlink_and_its_row_but_not_the_target()
    {
        var (seriesId, chapterId) = SeedSeriesWithChapter();
        var outside = Path.Combine(Path.GetTempPath(), $"maki-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        var external = Path.Combine(outside, "ch1.cbz");
        await File.WriteAllTextAsync(external, "not really a cbz");
        var link = Path.Combine(_root, "Series", "ch1.cbz");

        try
        {
            if (!TestLinks.TryLinkFile(link, external))
            {
                return;
            }

            using (var seed = _db.NewContext())
            {
                var file = new ChapterFile
                {
                    SeriesId = seriesId,
                    RelativePath = Path.Combine("Series", "ch1.cbz"),
                    Size = 1,
                    SourceName = "Manual",
                    DateAdded = DateTime.UtcNow
                };
                seed.ChapterFiles.Add(file);
                seed.SaveChanges();
                (await seed.Chapters.FirstAsync(c => c.Id == chapterId)).ChapterFileId = file.Id;
                seed.SaveChanges();
            }

            using (var db = _db.NewContext())
            {
                Assert.IsType<OkObjectResult>(await Controller(db).Delete([chapterId], Deletion(db), default));
                Assert.Empty(db.ChapterFiles);
            }

            Assert.False(File.Exists(link));
            Assert.True(File.Exists(external));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task Delete_keeps_the_file_when_another_series_row_still_points_at_it()
    {
        // A cross-series ChapterFile pair, as a row written before Link's own-folder check (above)
        // existed would look like: two different rows, same root, same physical path. Deleting the
        // chapter on one row must not take the file out from under the other row.
        var (seriesId, chapterId) = SeedSeriesWithChapter();
        var rootFolderId = await RootFolderIdOf(seriesId);
        var (otherSeriesId, otherChapterId) = SeedSeriesWithChapter("Other", rootFolderId);

        var sharedPath = Path.Combine("Series", "ch1.cbz");
        await File.WriteAllTextAsync(Path.Combine(_root, sharedPath), "cbz");

        int otherFileId;
        using (var seed = _db.NewContext())
        {
            var file = new ChapterFile
            {
                SeriesId = seriesId,
                RelativePath = sharedPath,
                Size = 1,
                SourceName = "Manual",
                DateAdded = DateTime.UtcNow
            };
            seed.ChapterFiles.Add(file);
            seed.SaveChanges();
            (await seed.Chapters.FirstAsync(c => c.Id == chapterId)).ChapterFileId = file.Id;

            var otherFile = new ChapterFile
            {
                SeriesId = otherSeriesId,
                RelativePath = sharedPath,
                Size = 1,
                SourceName = "Manual",
                DateAdded = DateTime.UtcNow
            };
            seed.ChapterFiles.Add(otherFile);
            seed.SaveChanges();
            (await seed.Chapters.FirstAsync(c => c.Id == otherChapterId)).ChapterFileId = otherFile.Id;
            seed.SaveChanges();
            otherFileId = otherFile.Id;
        }

        using (var db = _db.NewContext())
        {
            var result = await Controller(db).Delete([chapterId], Deletion(db), default);

            Assert.IsType<OkObjectResult>(result);
            Assert.Null(await db.Chapters.FindAsync(chapterId));
            var remaining = Assert.Single(db.ChapterFiles);
            Assert.Equal(otherFileId, remaining.Id);
        }

        Assert.True(File.Exists(Path.Combine(_root, sharedPath)));
    }

    [Fact]
    public async Task Delete_keeps_the_file_when_a_row_in_the_batch_stays_for_another_chapter()
    {
        // Two rows on one path, both in the batch, but the second still backs a chapter that is not
        // being deleted. That row stays, so the file under it must too.
        var (seriesId, chapterId) = SeedSeriesWithChapter();
        var sharedPath = Path.Combine("Series", "v01.cbz");
        await File.WriteAllTextAsync(Path.Combine(_root, sharedPath), "cbz");

        int secondChapterId, keptFileId;
        using (var seed = _db.NewContext())
        {
            ChapterFile NewFile() => new()
            {
                SeriesId = seriesId, RelativePath = sharedPath, Size = 1, SourceName = "Manual", DateAdded = DateTime.UtcNow
            };
            var first = NewFile();
            var second = NewFile();
            seed.ChapterFiles.AddRange(first, second);
            seed.SaveChanges();
            (await seed.Chapters.FirstAsync(c => c.Id == chapterId)).ChapterFileId = first.Id;
            var secondChapter = new Chapter { SeriesId = seriesId, Number = 2, NumberRaw = "2", ChapterFileId = second.Id };
            seed.Chapters.AddRange(secondChapter,
                new Chapter { SeriesId = seriesId, Number = 3, NumberRaw = "3", ChapterFileId = second.Id });
            seed.SaveChanges();
            secondChapterId = secondChapter.Id;
            keptFileId = second.Id;
        }

        using (var db = _db.NewContext())
        {
            Assert.IsType<OkObjectResult>(await Controller(db).Delete([chapterId, secondChapterId], Deletion(db), default));
            Assert.Equal(keptFileId, Assert.Single(db.ChapterFiles).Id);
        }

        Assert.True(File.Exists(Path.Combine(_root, sharedPath)));
    }

    /// <summary>Throws from <see cref="SaveChangesAsync"/> so a test can force a mid-operation failure.</summary>
    private sealed class FailingSaveDbContext(DbContextOptions<MakiDbContext> options) : MakiDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated save failure");
    }

    [Fact]
    public async Task Delete_leaves_the_file_and_rows_intact_when_the_save_fails()
    {
        var (seriesId, chapterId) = SeedSeriesWithChapter();
        var relativePath = Path.Combine("Series", "ch1.cbz");
        await File.WriteAllTextAsync(Path.Combine(_root, relativePath), "cbz");

        using (var seed = _db.NewContext())
        {
            var file = new ChapterFile
            {
                SeriesId = seriesId,
                RelativePath = relativePath,
                Size = 1,
                SourceName = "Manual",
                DateAdded = DateTime.UtcNow
            };
            seed.ChapterFiles.Add(file);
            seed.SaveChanges();
            (await seed.Chapters.FirstAsync(c => c.Id == chapterId)).ChapterFileId = file.Id;
            seed.SaveChanges();
        }

        using (var failing = new FailingSaveDbContext(_db.Options))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Controller(failing).Delete([chapterId], Deletion(failing), default));
        }

        using var db = _db.NewContext();
        Assert.True(File.Exists(Path.Combine(_root, relativePath)));
        Assert.NotNull(await db.Chapters.FindAsync(chapterId));
        Assert.Single(db.ChapterFiles);
    }

    /// <summary>Links a new file at <paramref name="relativePath"/> to the given chapters, writing it to disk under the root.</summary>
    private async Task<int> LinkFile(int seriesId, string relativePath, params int[] chapterIds)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, relativePath), "cbz");
        using var seed = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId, RelativePath = relativePath, Size = 1, SourceName = "Manual", DateAdded = DateTime.UtcNow
        };
        seed.ChapterFiles.Add(file);
        seed.SaveChanges();
        foreach (var chapter in seed.Chapters.Where(c => chapterIds.Contains(c.Id)))
        {
            chapter.ChapterFileId = file.Id;
        }

        seed.SaveChanges();
        return file.Id;
    }

    [Fact]
    public async Task DeleteFiles_keeps_the_chapters_and_their_reads_and_marks_them_removed()
    {
        var (seriesId, chapterId) = SeedSeriesWithChapter();
        int secondChapterId;
        using (var seed = _db.NewContext())
        {
            var second = new Chapter { SeriesId = seriesId, Number = 2, NumberRaw = "2" };
            seed.Chapters.Add(second);
            seed.SaveChanges();
            secondChapterId = second.Id;
            seed.ChapterProgress.Add(new ChapterProgress
            {
                UserId = 1, SeriesId = seriesId, ChapterId = chapterId, Completed = true,
                StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            seed.SaveChanges();
        }

        var relativePath = Path.Combine("Series", "v01.cbz");
        await LinkFile(seriesId, relativePath, chapterId, secondChapterId);

        using (var db = _db.NewContext())
        {
            // Only the first chapter is named, but the volume file backs both, so both lose it.
            var result = Assert.IsType<OkObjectResult>(await Controller(db).DeleteFiles([chapterId], Deletion(db), default));
            var counts = Assert.IsType<ChapterFileDeletion.Result>(result.Value);
            Assert.Equal(1, counts.Deleted);
            Assert.Equal(2, counts.ChaptersRemoved);
        }

        Assert.False(File.Exists(Path.Combine(_root, relativePath)));
        using var check = _db.NewContext();
        Assert.Empty(check.ChapterFiles);
        Assert.All(check.Chapters, c =>
        {
            Assert.Null(c.ChapterFileId);
            Assert.NotNull(c.FileRemovedAt);
        });
        Assert.True(Assert.Single(check.ChapterProgress).Completed);
    }

    [Fact]
    public async Task Both_deletes_keep_a_file_another_series_reaches_through_a_nested_root_folder()
    {
        // Root B sits inside root A, so one file on disk has a different relative path under each.
        var (seriesId, chapterId) = SeedSeriesWithChapter();
        int otherChapterId;
        using (var seed = _db.NewContext())
        {
            var nested = new RootFolder { Path = Path.Combine(_root, "Series") };
            seed.RootFolders.Add(nested);
            seed.SaveChanges();
            var other = new Series
            {
                Title = "Other", SortTitle = "other", RootFolderId = nested.Id, FolderName = "Other", Added = DateTime.UtcNow
            };
            seed.Series.Add(other);
            seed.SaveChanges();
            var otherChapter = new Chapter { SeriesId = other.Id, Number = 1, NumberRaw = "1" };
            seed.Chapters.Add(otherChapter);
            seed.SaveChanges();
            otherChapterId = otherChapter.Id;
            var otherFile = new ChapterFile
            {
                SeriesId = other.Id, RelativePath = "ch1.cbz", Size = 1, SourceName = "Manual", DateAdded = DateTime.UtcNow
            };
            seed.ChapterFiles.Add(otherFile);
            seed.SaveChanges();
            otherChapter.ChapterFileId = otherFile.Id;
            seed.SaveChanges();
        }

        var path = Path.Combine("Series", "ch1.cbz");
        await LinkFile(seriesId, path, chapterId);

        using (var db = _db.NewContext())
        {
            var result = Assert.IsType<OkObjectResult>(await Controller(db).DeleteFiles([chapterId], Deletion(db), default));
            Assert.Equal(1, Assert.IsType<ChapterFileDeletion.Result>(result.Value).Kept);
        }

        Assert.True(File.Exists(Path.Combine(_root, path)));
        using (var check = _db.NewContext())
        {
            Assert.NotNull((await check.Chapters.FindAsync(chapterId))!.FileRemovedAt);
            Assert.NotNull((await check.Chapters.FindAsync(otherChapterId))!.ChapterFileId);
        }

        // The same file linked again, then removed outright: the other series' row still holds it.
        await LinkFile(seriesId, path, chapterId);
        using (var db = _db.NewContext())
        {
            Assert.IsType<OkObjectResult>(await Controller(db).Delete([chapterId], Deletion(db), default));
        }

        Assert.True(File.Exists(Path.Combine(_root, path)));
    }
}
