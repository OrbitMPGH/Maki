using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
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
        db,
        new DownloadQueueService(_db.ScopeFactory(), TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
        new StatsEventService(db),
        new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
        new TestCurrentUser(1),
        NullLogger<ChapterController>.Instance);

    /// <summary>A series rooted at the temp directory, plus one chapter, returning both ids.</summary>
    private (int SeriesId, int ChapterId) SeedSeriesWithChapter(string title = "Series")
    {
        using var db = _db.NewContext();
        var root = new RootFolder { Path = _root };
        db.RootFolders.Add(root);
        db.SaveChanges();

        var series = new Series
        {
            Title = title,
            SortTitle = title.ToLowerInvariant(),
            RootFolderId = root.Id,
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

    [Fact]
    public async Task Delete_refuses_a_batch_spanning_two_series()
    {
        var (_, first) = SeedSeriesWithChapter("First");
        var (_, second) = SeedSeriesWithChapter("Second");

        using var db = _db.NewContext();
        var result = await Controller(db).Delete([first, second], default);

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
            var result = await Controller(db).Delete([chapterId], default);

            Assert.IsType<OkObjectResult>(result);
            Assert.True(File.Exists(outside));
            Assert.Empty(db.Chapters);
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
