using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Core.Sources;
using Maki.Data.Identity;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Tests;

/// <summary>The slim reads beside the library list: <c>GET series/lookup</c> and <c>GET series/summary</c>.</summary>
public sealed class SeriesLookupSummaryTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static SeriesController Controller(Maki.Data.MakiDbContext db)
    {
        var settings = new FakeAppSettings();
        return new SeriesController(new TestLocalizer(), db, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            new SourceAvailability(settings, new SourceRegistry([])), null!, new NoUserSettings(), null!, null!, null!);
    }

    private static List<T> Body<T>(IActionResult result) => [.. (IEnumerable<T>)((OkObjectResult)result).Value!];

    private void Grant(int userId, int seriesId)
    {
        using var db = _db.NewContext();
        var folder = db.Series.Single(s => s.Id == seriesId).RootFolderId;
        db.UserRootFolders.Add(new UserRootFolder { UserId = userId, RootFolderId = folder });
        db.SaveChanges();
    }

    [Fact]
    public async Task Lookup_lists_id_mangabaka_id_cover_and_archived_flag()
    {
        var alice = _db.SeedUser("alice");
        var linked = _db.SeedSeries("Linked", configure: s =>
        {
            s.MangaBakaId = 42;
            s.CoverPath = "/covers/linked.jpg";
        });
        var archived = _db.SeedSeries("Archived", configure: s => s.ArchivedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        using var db = _db.NewContext(alice);
        var rows = Body<SeriesLookupDto>(await Controller(db).Lookup(default));

        var first = rows.Single(r => r.Id == linked);
        Assert.Equal(42, first.MangaBakaId);
        Assert.StartsWith($"/api/v1/mediacover/{linked}/cover.jpg?v=", first.CoverUrl);
        Assert.False(first.Archived);
        var second = rows.Single(r => r.Id == archived);
        Assert.Null(second.MangaBakaId);
        Assert.Null(second.CoverUrl);
        Assert.True(second.Archived);
    }

    [Fact]
    public async Task Lookup_shows_the_same_series_as_the_list()
    {
        var reader = _db.SeedUser("reader", MakiPermission.None, allRootFolders: false);
        var granted = _db.SeedSeries("Granted");
        _db.SeedSeries("Elsewhere");
        Grant(reader, granted);

        using var db = _db.NewContext(reader, allRootFolders: false);
        var controller = Controller(db);
        var lookup = Body<SeriesLookupDto>(await controller.Lookup(default));
        var list = Body<SeriesDto>(await controller.List(default));

        Assert.Equal(granted, Assert.Single(lookup).Id);
        Assert.Equal(list.Select(s => s.Id), lookup.Select(r => r.Id));
    }

    private static readonly DateTime At = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Two series with a bit of everything the list counts: chapters, a file, a queue item, reads, tags, a rating.</summary>
    private (int Busy, int Quiet) SeedLibrary(int userId)
    {
        var busy = _db.SeedSeries("Busy", configure: s => s.AltTitles = [new LocalizedTitle("Isogashii", "ja-ro")],
            mappings: new SourceMapping { SourceName = "mangadex", SourceSeriesId = "busy", Enabled = true });
        var quiet = _db.SeedSeries("Quiet");
        using var db = _db.NewContext();
        var file = new ChapterFile { SeriesId = busy, RelativePath = "busy/1.cbz", SourceName = "mangadex", DateAdded = At };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var read = new Chapter { SeriesId = busy, Number = 1, Wanted = true, ChapterFileId = file.Id };
        var queued = new Chapter { SeriesId = busy, Number = 2, Wanted = true };
        db.Chapters.AddRange(read, queued, new Chapter { SeriesId = busy, Number = 3 }, new Chapter { SeriesId = quiet, Number = 1, Wanted = true });
        db.SaveChanges();
        db.DownloadQueue.Add(new DownloadQueueItem
        {
            SeriesId = busy, ChapterId = queued.Id, Protocol = AcquisitionProtocol.Scraper, Status = QueueStatus.Queued, QueuedAt = At,
        });
        db.ChapterProgress.Add(new ChapterProgress
        {
            UserId = userId, SeriesId = busy, ChapterId = read.Id, Completed = true, StartedAt = At, UpdatedAt = At,
        });
        var tag = new Tag { Label = "Favourite" };
        db.Tags.Add(tag);
        db.SaveChanges();
        db.SeriesTags.Add(new SeriesTag { SeriesId = busy, TagId = tag.Id });
        db.UserSeriesStates.Add(new UserSeriesState { UserId = userId, SeriesId = busy, Rating = 8 });
        db.SaveChanges();
        return (busy, quiet);
    }

    [Fact]
    public async Task Summary_rows_are_the_list_rows_for_those_series()
    {
        var alice = _db.SeedUser("alice");
        var (busy, quiet) = SeedLibrary(alice);

        using var db = _db.NewContext(alice);
        var controller = Controller(db);
        var list = Body<SeriesDto>(await controller.List(default));
        var summary = Body<SeriesDto>(await controller.Summary([busy, busy], default));

        var row = Assert.Single(summary);
        Assert.Equal(JsonSerializer.Serialize(list.Single(s => s.Id == busy)), JsonSerializer.Serialize(row));
        Assert.Equal((3, 1, 1, 1, 8), (row.KnownChapterCount, row.ChapterFileCount, row.QueuedCount, row.ReadChapterCount, row.Rating));
        Assert.Equal(["mangadex"], row.Sources);
        Assert.Single(row.TagIds);
        Assert.Equal(list.Select(s => s.Id).Order(), Body<SeriesDto>(await controller.Summary([quiet, busy], default)).Select(s => s.Id).Order());
    }

    [Fact]
    public async Task Summary_leaves_out_series_the_caller_cannot_see()
    {
        var reader = _db.SeedUser("reader", MakiPermission.None, allRootFolders: false);
        var (busy, quiet) = SeedLibrary(reader);
        Grant(reader, quiet);

        using var db = _db.NewContext(reader, allRootFolders: false);
        var rows = Body<SeriesDto>(await Controller(db).Summary([busy, quiet, 9999], default));

        Assert.Equal(quiet, Assert.Single(rows).Id);
    }

    [Fact]
    public async Task List_rows_send_alt_titles_as_names_and_no_links()
    {
        var alice = _db.SeedUser("alice");
        var (busy, _) = SeedLibrary(alice);

        using var db = _db.NewContext(alice);
        var row = Body<SeriesDto>(await Controller(db).List(default)).Single(s => s.Id == busy);
        var json = JsonSerializer.Serialize(row, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(["Isogashii"], row.AltTitleNames);
        Assert.Null(row.AltTitles);
        Assert.Null(row.Links);
        Assert.DoesNotContain("\"links\"", json);
        Assert.DoesNotContain("\"altTitles\"", json);
        Assert.Contains("\"altTitleNames\":[\"Isogashii\"]", json);
    }

    private sealed class NoUserSettings : IUserSettings
    {
        public int UserId => 1;

        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<Dictionary<string, string>> GetManyAsync(
            IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, string>());

        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }
}
