using Maki.Api.Services;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>
/// A chapter whose file Maki removed on purpose keeps its reads, and the two timestamps behind that
/// are kept in step by <c>MakiDbContext</c> rather than by each writer.
/// </summary>
public sealed class ChapterFileRemovedTests : IDisposable
{
    private const int Owner = 1;
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private (int SeriesId, int OnDisk, int Removed, int Missing) Seed()
    {
        var seriesId = _db.SeedSeries();
        using var db = _db.NewContext();
        var file = new ChapterFile { SeriesId = seriesId, RelativePath = "c1.cbz", SourceName = "Test", DateAdded = DateTime.UtcNow };
        db.ChapterFiles.Add(file);
        db.SaveChanges();

        var onDisk = new Chapter { SeriesId = seriesId, Number = 1m, ChapterFileId = file.Id };
        var removed = new Chapter { SeriesId = seriesId, Number = 2m, FileRemovedAt = DateTime.UtcNow.AddDays(-1) };
        var missing = new Chapter { SeriesId = seriesId, Number = 3m };
        db.Chapters.AddRange(onDisk, removed, missing);
        db.SaveChanges();
        return (seriesId, onDisk.Id, removed.Id, missing.Id);
    }

    private static ChapterProgress Completed(int seriesId, int chapterId) => new()
    {
        UserId = Owner,
        SeriesId = seriesId,
        ChapterId = chapterId,
        Completed = true,
        StartedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public void A_read_chapter_whose_file_was_removed_still_counts_as_read()
    {
        var (seriesId, onDisk, removed, missing) = Seed();
        using (var db = _db.NewContext())
        {
            db.ChapterProgress.AddRange(
                Completed(seriesId, onDisk), Completed(seriesId, removed), Completed(seriesId, missing));
            db.SaveChanges();
        }

        using var scoped = _db.NewContext(Owner);
        // The missing chapter's read is a Kavita report for something never downloaded: not counted.
        Assert.Equal(2, ReadCounts.Read(scoped).Count(p => p.SeriesId == seriesId));
        Assert.Equal(2, ReadCounts.ReadFor(scoped, Owner).Count(p => p.SeriesId == seriesId));
    }

    [Fact]
    public void Completing_stamps_CompletedAt_once_and_unreading_clears_it()
    {
        var (seriesId, onDisk, _, _) = Seed();
        using (var db = _db.NewContext())
        {
            db.ChapterProgress.Add(Completed(seriesId, onDisk));
            db.SaveChanges();
        }

        DateTime first;
        using (var db = _db.NewContext())
        {
            var row = db.ChapterProgress.Single(p => p.ChapterId == onDisk);
            Assert.NotNull(row.CompletedAt);
            first = row.CompletedAt!.Value;

            // A re-read touches the row but is not a new completion.
            row.PageIndex = 3;
            row.UpdatedAt = DateTime.UtcNow.AddMinutes(5);
            db.SaveChanges();
        }

        using (var db = _db.NewContext())
        {
            var row = db.ChapterProgress.Single(p => p.ChapterId == onDisk);
            Assert.Equal(first, row.CompletedAt);

            row.Completed = false;
            db.SaveChanges();
        }

        using (var db = _db.NewContext())
        {
            Assert.Null(db.ChapterProgress.Single(p => p.ChapterId == onDisk).CompletedAt);
        }
    }

    [Fact]
    public void Linking_a_file_clears_the_removal_stamp()
    {
        var (seriesId, _, removed, _) = Seed();
        using (var db = _db.NewContext())
        {
            var chapter = db.Chapters.Single(c => c.Id == removed);
            chapter.ChapterFile = new ChapterFile
            {
                SeriesId = seriesId, RelativePath = "c2.cbz", SourceName = "Test", DateAdded = DateTime.UtcNow
            };
            db.SaveChanges();
        }

        using var check = _db.NewContext();
        var reloaded = check.Chapters.Single(c => c.Id == removed);
        Assert.NotNull(reloaded.ChapterFileId);
        Assert.Null(reloaded.FileRemovedAt);
    }
}
