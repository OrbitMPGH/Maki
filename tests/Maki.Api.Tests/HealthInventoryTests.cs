using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

public sealed class HealthInventoryTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private (int SeriesId, int FromId, int ToId) Seed()
    {
        var seriesId = _db.SeedSeries();
        using var db = _db.NewContext();
        var fromId = db.Series.Single(s => s.Id == seriesId).RootFolderId;
        var to = new RootFolder { Path = "/other" };
        db.RootFolders.Add(to);
        db.SaveChanges();
        return (seriesId, fromId, to.Id);
    }

    [Fact]
    public async Task A_linked_occupant_keeps_its_link_when_the_moving_row_has_none()
    {
        var (seriesId, fromId, toId) = Seed();
        int occupantId, chapterFileId;
        using (var db = _db.NewContext())
        {
            var chapterFile = new ChapterFile { SeriesId = seriesId, RelativePath = "Test Series/a.cbz" };
            db.ChapterFiles.Add(chapterFile);
            db.SaveChanges();
            chapterFileId = chapterFile.Id;
            var occupant = new HealthFile { RootFolderId = toId, SeriesId = seriesId, ChapterFileId = chapterFile.Id, RelativePath = "Test Series/a.cbz", ContentHash = "H" };
            db.HealthFiles.AddRange(occupant, new HealthFile { RootFolderId = fromId, SeriesId = seriesId, RelativePath = "Test Series/a.cbz", ContentHash = "H" });
            db.SaveChanges();
            occupantId = occupant.Id;
        }

        using (var db = _db.NewContext())
        {
            await HealthInventory.MoveSeriesAsync(db, seriesId, toId, [], CancellationToken.None);
            await db.SaveChangesAsync();
        }

        using var check = _db.NewContext();
        var live = check.HealthFiles.Single(f => !f.Removed);
        Assert.Equal(occupantId, live.Id);
        Assert.Equal(chapterFileId, live.ChapterFileId);
    }

    [Fact]
    public async Task A_row_stored_with_the_other_separator_counts_as_the_same_file()
    {
        var (seriesId, fromId, toId) = Seed();
        int movingId;
        using (var db = _db.NewContext())
        {
            var moving = new HealthFile { RootFolderId = fromId, SeriesId = seriesId, RelativePath = "Test Series\\a.cbz", ContentHash = "H", AnalysisJson = "{\"status\":\"complete\"}" };
            db.HealthFiles.AddRange(moving, new HealthFile { RootFolderId = toId, RelativePath = "Test Series/a.cbz", Removed = true });
            db.SaveChanges();
            movingId = moving.Id;
            db.HealthFindings.Add(new HealthFinding { FileId = moving.Id, Version = moving.Version, Kind = "corrupt" });
            db.SaveChanges();
        }

        using (var db = _db.NewContext())
        {
            await HealthInventory.MoveSeriesAsync(db, seriesId, toId, [], CancellationToken.None);
            await db.SaveChangesAsync();
        }

        using var check = _db.NewContext();
        var live = check.HealthFiles.Single(f => !f.Removed);
        Assert.Equal(toId, live.RootFolderId);
        Assert.Equal("Test Series/a.cbz", live.RelativePath);
        Assert.Equal("H", live.ContentHash);
        Assert.Equal("{\"status\":\"complete\"}", live.AnalysisJson);
        Assert.Equal(seriesId, live.SeriesId);
        Assert.True(check.HealthFiles.Single(f => f.Id == movingId).Removed);
        Assert.Equal("open", check.HealthFindings.Single(f => f.FileId == live.Id).State);
        Assert.Equal("resolved", check.HealthFindings.Single(f => f.FileId == movingId).State);
    }

    [Fact]
    public async Task Rows_moved_without_a_collision_keep_their_analysis()
    {
        var (seriesId, fromId, toId) = Seed();
        using (var db = _db.NewContext())
        {
            db.HealthFiles.Add(new HealthFile { RootFolderId = fromId, SeriesId = seriesId, RelativePath = "Test Series/a.cbz", AnalysisJson = "{\"status\":\"complete\"}" });
            db.SaveChanges();
        }

        using (var db = _db.NewContext())
        {
            await HealthInventory.MoveSeriesAsync(db, seriesId, toId, [], CancellationToken.None);
            await db.SaveChangesAsync();
        }

        using var check = _db.NewContext();
        var row = check.HealthFiles.Single();
        Assert.Equal(toId, row.RootFolderId);
        Assert.Equal("{\"status\":\"complete\"}", row.AnalysisJson);
    }

    [Fact]
    public async Task Touched_lists_what_it_attached_even_when_nothing_is_saved()
    {
        var (seriesId, fromId, toId) = Seed();
        using (var db = _db.NewContext())
        {
            db.HealthFiles.Add(new HealthFile { RootFolderId = fromId, SeriesId = seriesId, RelativePath = "Test Series/a.cbz" });
            db.SaveChanges();
        }

        using var context = _db.NewContext();
        var touched = new List<object>();
        await HealthInventory.MoveSeriesAsync(context, seriesId, toId, touched, CancellationToken.None);
        foreach (var entity in touched) context.Entry(entity).State = EntityState.Detached;

        Assert.False(context.ChangeTracker.HasChanges());
    }
}
