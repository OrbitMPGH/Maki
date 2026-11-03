using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Core.Sources;
using Maki.Data.Identity;
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
