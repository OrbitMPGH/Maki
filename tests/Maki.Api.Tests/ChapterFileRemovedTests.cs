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
        // The UI count takes a read whatever the file state; progression only counts chapters it had.
        Assert.Equal(3, ReadCounts.Read(scoped).Count(p => p.SeriesId == seriesId));
        Assert.Equal(2, ReadCounts.ReadFor(scoped, Owner).Count(p => p.SeriesId == seriesId));
    }

    /// <summary>
    /// Turning Wanted on for a removed chapter clears its stamp and turning it off again leaves it
    /// cleared, which is the state below. The reads must survive that round trip, and a read chapter
    /// switched off is not "left out" of the total, so read never passes it.
    /// </summary>
    [Fact]
    public async Task Read_chapters_switched_off_without_a_file_still_count_inside_the_total()
    {
        var seriesId = _db.SeedSeries();
        int[] read;
        using (var db = _db.NewContext())
        {
            var chapters = Enumerable.Range(1, 4)
                .Select(n => new Chapter { SeriesId = seriesId, Number = n, Wanted = false })
                .ToList();
            db.Chapters.AddRange(chapters);
            db.SaveChanges();
            read = [.. chapters.Take(3).Select(c => c.Id)];
            db.ChapterProgress.AddRange(read.Select(id => Completed(seriesId, id)));
            db.SaveChanges();
        }

        using var scoped = _db.NewContext(Owner);
        var tallies = await LibraryListRows.ChapterTalliesAsync(
            scoped.Chapters.Where(c => c.SeriesId == seriesId), ReadCounts.Read(scoped), CancellationToken.None);
        var counts = tallies[seriesId];
        var readCount = ReadCounts.Read(scoped).Count(p => p.SeriesId == seriesId);
        var totals = LibraryListRows.ProgressTotals(counts, readCount);

        Assert.Equal(3, counts.Wanted);
        Assert.Equal(1, counts.SwitchedOff);
        Assert.Equal(3, totals.Read);
        Assert.Equal(3, totals.Total);
        Assert.Equal(1, totals.Skipped);
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
