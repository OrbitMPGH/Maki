using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="LibraryCompositionService"/>: what the collection is made of, and the fact that it
/// only ever answers about root folders the caller can see.
/// </summary>
public sealed class LibraryCompositionTests : IDisposable
{
    private const int Owner = 1;

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private LibraryCompositionService Service(MakiDbContext db, int userId = Owner) =>
        new(db, new TestCurrentUser(userId), new MemoryCache(new MemoryCacheOptions()));

    /// <summary>Adds a downloaded chapter with a backing file, and returns the file's series.</summary>
    private void SeedFile(int seriesId, string source, long size, DateTime? added = null)
    {
        using var db = _db.NewContext();
        var file = new ChapterFile
        {
            SeriesId = seriesId,
            RelativePath = $"{Guid.NewGuid()}.cbz",
            Size = size,
            SourceName = source,
            DateAdded = added ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();

        db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 1, ChapterFileId = file.Id });
        db.SaveChanges();
    }

    [Fact]
    public async Task TotalsCountChaptersFilesAndBytes()
    {
        var a = _db.SeedSeries("A");
        var b = _db.SeedSeries("B", monitor: NewChapterMonitorMode.None);
        SeedFile(a, "MangaDex", 1_000);
        SeedFile(a, "MangaDex", 2_000);
        SeedFile(b, "Asura", 500);

        using var db = _db.NewContext(Owner);
        var stats = await Service(db).GetAsync(CancellationToken.None);

        Assert.Equal(2, stats.Totals.SeriesCount);
        // "None" is the only mode that means unmonitored; the other three all watch something.
        Assert.Equal(1, stats.Totals.MonitoredCount);
        Assert.Equal(3, stats.Totals.ChapterCount);
        Assert.Equal(3, stats.Totals.DownloadedChapterCount);
        Assert.Equal(3, stats.Totals.FileCount);
        Assert.Equal(3_500, stats.Totals.TotalBytes);
    }

    [Fact]
    public async Task EmptyLibraryReportsZeroRatherThanFailingOnANullSum()
    {
        using var db = _db.NewContext(Owner);
        var stats = await Service(db).GetAsync(CancellationToken.None);

        Assert.Equal(0, stats.Totals.SeriesCount);
        Assert.Equal(0, stats.Totals.TotalBytes);
        Assert.Empty(stats.BySource);
        Assert.Empty(stats.Growth);
    }

    [Fact]
    public async Task SourcesRankByBytesAndCarryTheirFileCount()
    {
        var a = _db.SeedSeries("A");
        SeedFile(a, "MangaDex", 100);
        SeedFile(a, "MangaDex", 100);
        SeedFile(a, "Asura", 5_000);

        using var db = _db.NewContext(Owner);
        var stats = await Service(db).GetAsync(CancellationToken.None);

        Assert.Equal("Asura", stats.BySource[0].Name);
        Assert.Equal(5_000, stats.BySource[0].Bytes);
        Assert.Equal(1, stats.BySource[0].Files);
        Assert.Equal(200, stats.BySource[1].Bytes);
        Assert.Equal(2, stats.BySource[1].Files);
    }

    [Fact]
    public async Task TypeAndStatusGroupWithUnknownNamed()
    {
        _db.SeedSeries("A", configure: s => { s.Type = "manga"; s.Status = SeriesStatus.Ongoing; });
        _db.SeedSeries("B", configure: s => { s.Type = "manga"; s.Status = SeriesStatus.Completed; });
        // Never refreshed since the Type column landed, so it is null rather than absent.
        _db.SeedSeries("C", configure: s => s.Status = SeriesStatus.Ongoing);

        using var db = _db.NewContext(Owner);
        var stats = await Service(db).GetAsync(CancellationToken.None);

        Assert.Equal(2, stats.ByType.Single(t => t.Name == "manga").Count);
        Assert.Equal(1, stats.ByType.Single(t => t.Name == "Unknown").Count);
        Assert.Equal(2, stats.ByStatus.Single(s => s.Name == "Ongoing").Count);
        Assert.Equal(1, stats.ByStatus.Single(s => s.Name == "Completed").Count);
    }

    [Fact]
    public async Task GrowthAccumulatesMonthByMonth()
    {
        _db.SeedSeries("A", configure: s => s.Added = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));
        _db.SeedSeries("B", configure: s => s.Added = new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc));
        _db.SeedSeries("C", configure: s => s.Added = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc));

        using var db = _db.NewContext(Owner);
        var stats = await Service(db).GetAsync(CancellationToken.None);

        Assert.Equal(["2026-01", "2026-03"], stats.Growth.Select(g => g.Bucket));
        Assert.Equal(2, stats.Growth[0].SeriesAdded);
        Assert.Equal(2, stats.Growth[0].Cumulative);
        Assert.Equal(1, stats.Growth[1].SeriesAdded);
        Assert.Equal(3, stats.Growth[1].Cumulative);
    }

    [Fact]
    public async Task GenresAreCountedPerSeriesNotPerChapter()
    {
        var a = _db.SeedSeries("A", configure: s => s.Genres = ["Action", "Comedy"]);
        _db.SeedSeries("B", configure: s => s.Genres = ["Action"]);
        // Several files on one series must not multiply its genres.
        SeedFile(a, "MangaDex", 10);
        SeedFile(a, "MangaDex", 10);

        using var db = _db.NewContext(Owner);
        var stats = await Service(db).GetAsync(CancellationToken.None);

        Assert.Equal(2, stats.TopGenres.Single(g => g.Name == "Action").Count);
        Assert.Equal(1, stats.TopGenres.Single(g => g.Name == "Comedy").Count);
    }

    [Fact]
    public async Task LargestSeriesRankByTotalBytes()
    {
        var small = _db.SeedSeries("Small");
        var big = _db.SeedSeries("Big", configure: s => s.CoverPath = "covers/big.jpg");
        SeedFile(small, "MangaDex", 1_000);
        SeedFile(big, "MangaDex", 4_000);
        SeedFile(big, "MangaDex", 4_000);

        using var db = _db.NewContext(Owner);
        var stats = await Service(db).GetAsync(CancellationToken.None);

        Assert.Equal("Big", stats.Largest[0].Title);
        Assert.Equal(8_000, stats.Largest[0].Bytes);
        Assert.Equal(2, stats.Largest[0].Files);
        Assert.NotNull(stats.Largest[0].CoverUrl);
        Assert.Equal("Small", stats.Largest[1].Title);
        Assert.Null(stats.Largest[1].CoverUrl);
    }

    [Fact]
    public async Task RootFolderVisibilityHidesSeriesAndTheirBytes()
    {
        var mine = _db.SeedSeries("Mine", configure: s => s.Genres = ["Action"]);
        var hidden = _db.SeedSeries("Hidden", configure: s => s.Genres = ["Horror"]);
        SeedFile(mine, "MangaDex", 1_000);
        SeedFile(hidden, "Asura", 9_000);

        var restricted = _db.SeedUser("restricted", allRootFolders: false);
        using (var seed = _db.NewContext())
        {
            var myRoot = seed.Series.Single(s => s.Id == mine).RootFolderId;
            seed.Set<UserRootFolder>().Add(new UserRootFolder { UserId = restricted, RootFolderId = myRoot });
            seed.SaveChanges();
        }

        using var db = _db.NewContext(restricted, allRootFolders: false);
        var stats = await Service(db, restricted).GetAsync(CancellationToken.None);

        Assert.Equal(1, stats.Totals.SeriesCount);
        Assert.Equal(1_000, stats.Totals.TotalBytes);
        Assert.Equal(1, stats.Totals.FileCount);
        Assert.Single(stats.BySource, s => s.Name == "MangaDex");
        Assert.DoesNotContain(stats.TopGenres, g => g.Name == "Horror");
        Assert.DoesNotContain(stats.Largest, s => s.Title == "Hidden");
    }
}
