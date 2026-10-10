using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Tests;

/// <summary>The per-series dates the Library list adds for its "Recently read" and "Last downloaded" sorts.</summary>
public sealed class SeriesListSortDatesTests : IDisposable
{
    private static readonly DateTime Older = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 4, 2, 9, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static SeriesController Controller(Maki.Data.MakiDbContext db)
    {
        var settings = new FakeAppSettings();
        return new SeriesController(new TestLocalizer(), db, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            new SourceAvailability(settings, new SourceRegistry([])), null!, new NoUserSettings(), null!, null!, null!);
    }

    private static List<SeriesDto> Listed(IActionResult result) =>
        [.. (IEnumerable<SeriesDto>)((OkObjectResult)result).Value!];

    private void AddProgress(int userId, int seriesId, DateTime at, bool watched = false)
    {
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = db.Chapters.Count() + 1 };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        db.ChapterProgress.Add(new ChapterProgress
        {
            UserId = userId, SeriesId = seriesId, ChapterId = chapter.Id, Watched = watched, StartedAt = at, UpdatedAt = at,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task Last_read_is_the_callers_newest_progress_and_null_when_never_read()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var read = _db.SeedSeries("Read");
        var untouched = _db.SeedSeries("Untouched");
        var onlyWatched = _db.SeedSeries("Watched");
        var bobsOnly = _db.SeedSeries("Bobs");
        AddProgress(alice, read, Older);
        AddProgress(alice, read, Newer);
        AddProgress(alice, onlyWatched, Newer, watched: true);
        AddProgress(bob, bobsOnly, Newer);

        using var db = _db.NewContext(alice);
        var list = Listed(await Controller(db).List(default));

        Assert.Equal(Newer, list.Single(s => s.Id == read).LastReadAt);
        Assert.Null(list.Single(s => s.Id == untouched).LastReadAt);
        Assert.Null(list.Single(s => s.Id == onlyWatched).LastReadAt);
        Assert.Null(list.Single(s => s.Id == bobsOnly).LastReadAt);
    }

    [Fact]
    public async Task Last_downloaded_is_the_newest_file_per_series_and_null_without_files()
    {
        var alice = _db.SeedUser("alice");
        var withFiles = _db.SeedSeries("With files");
        var empty = _db.SeedSeries("Empty");
        using (var seed = _db.NewContext())
        {
            seed.ChapterFiles.AddRange(
                new ChapterFile { SeriesId = withFiles, RelativePath = "a", SourceName = "x", DateAdded = Older },
                new ChapterFile { SeriesId = withFiles, RelativePath = "b", SourceName = "x", DateAdded = Newer });
            seed.SaveChanges();
        }

        using var db = _db.NewContext(alice);
        var list = Listed(await Controller(db).List(default));

        Assert.Equal(Newer, list.Single(s => s.Id == withFiles).LastDownloadedAt);
        Assert.Null(list.Single(s => s.Id == empty).LastDownloadedAt);
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
