using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>Moving a series between root folders, and the gates in front of a relink.</summary>
public sealed class SeriesFilesControllerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "maki-move-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    private SeriesController Controller(MakiDbContext db, MakiPermission permissions = MakiPermission.Admin) => new(
        localizer: new TestLocalizer(),
        db: db,
        coverService: null!,
        chapterSyncService: null!,
        cbzLinkService: null!,
        relinkPlanner: null!,
        seriesCreation: null!,
        seriesRename: null!,
        metadataRefresh: null!,
        downloadQueue: null!,
        downloadBatches: null!,
        appSettings: null!,
        kavitaScans: new KavitaScanService(
            new KavitaClient(new StubHttpClientFactory("{}")), new FakeAppSettings(), _db.ScopeFactory(),
            NullLogger<KavitaScanService>.Instance),
        scrobbler: null!,
        stats: null!,
        mangaBakaStore: null!,
        similarSeries: null!,
        recommendationFeedback: null!,
        archives: null!,
        readingProfiles: null!,
        readingTimeEstimates: null!,
        sourceAvailability: null!,
        currentUser: new TestCurrentUser(1, permissions: permissions),
        userSettings: new UserSettingsService(db, new TestCurrentUser(1, permissions: permissions)),
        notifications: null!,
        locales: null!,
        logger: NullLogger<SeriesController>.Instance);

    private (int SeriesId, int FromId, int ToId, string From, string To) SeedTwoFolderSeries(string toRoot)
    {
        var from = Path.Combine(_temp, "a");
        _db.SeedUser("admin");
        using var db = _db.NewContext();
        var fromRoot = new RootFolder { Path = from };
        var destination = new RootFolder { Path = toRoot };
        db.RootFolders.AddRange(fromRoot, destination);
        db.SaveChanges();
        var series = new Series { Title = "Berserk", SortTitle = "Berserk", FolderName = "Berserk", RootFolderId = fromRoot.Id };
        db.Series.Add(series);
        db.SaveChanges();

        foreach (var relative in new[] { Path.Combine("Berserk", "Berserk Ch.1.cbz"), Path.Combine("Old Berserk", "Berserk Ch.2.cbz") })
        {
            Write(Path.Combine(from, relative));
            db.ChapterFiles.Add(new ChapterFile { SeriesId = series.Id, RelativePath = relative, DateAdded = DateTime.UtcNow });
        }

        db.SaveChanges();
        Write(Path.Combine(from, "Old Berserk", "not tracked.cbz"));
        return (series.Id, fromRoot.Id, destination.Id, from, toRoot);
    }

    private static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "cbz");
    }

    [Fact]
    public async Task Move_takes_the_own_folder_whole_but_only_tracked_files_from_another()
    {
        var (seriesId, _, toId, from, to) = SeedTwoFolderSeries(Path.Combine(_temp, "b"));
        Directory.CreateDirectory(to);

        using var db = _db.NewContext(userId: 1);
        var result = await Controller(db).Move(seriesId, new SeriesController.MoveSeriesRequest(toId), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.False(Directory.Exists(Path.Combine(from, "Berserk")));
        Assert.True(File.Exists(Path.Combine(to, "Berserk", "Berserk Ch.1.cbz")));
        Assert.True(File.Exists(Path.Combine(to, "Old Berserk", "Berserk Ch.2.cbz")));
        Assert.False(File.Exists(Path.Combine(from, "Old Berserk", "Berserk Ch.2.cbz")));
        Assert.True(File.Exists(Path.Combine(from, "Old Berserk", "not tracked.cbz")));
        Assert.False(File.Exists(Path.Combine(to, "Old Berserk", "not tracked.cbz")));
        using var check = _db.NewContext();
        Assert.Equal(toId, check.Series.Single(s => s.Id == seriesId).RootFolderId);
    }

    /// <summary>
    /// The second folder's file cannot land (its destination path is past the OS limit), so the
    /// series folder that already moved goes back and the series stays on its old root.
    /// </summary>
    [Fact]
    public async Task A_failed_second_folder_puts_the_first_back()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var deep = Path.Combine(_temp, Path.Combine(Enumerable.Range(0, 20).Select(i => new string((char)('a' + i), 200)).ToArray()));
        var (seriesId, fromId, toId, from, to) = SeedTwoFolderSeries(deep);
        Directory.CreateDirectory(to);
        var longName = new string('x', 240) + ".cbz";
        using (var db = _db.NewContext())
        {
            var relative = Path.Combine("Old Berserk", longName);
            Write(Path.Combine(from, relative));
            db.ChapterFiles.Add(new ChapterFile { SeriesId = seriesId, RelativePath = relative, DateAdded = DateTime.UtcNow });
            db.SaveChanges();
        }

        using var context = _db.NewContext(userId: 1);
        var result = await Controller(context).Move(seriesId, new SeriesController.MoveSeriesRequest(toId), CancellationToken.None);

        Assert.Equal(500, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.True(File.Exists(Path.Combine(from, "Berserk", "Berserk Ch.1.cbz")));
        Assert.True(File.Exists(Path.Combine(from, "Old Berserk", "Berserk Ch.2.cbz")));
        Assert.True(File.Exists(Path.Combine(from, "Old Berserk", longName)));
        Assert.False(Directory.Exists(Path.Combine(to, "Berserk")));
        using var check = _db.NewContext();
        Assert.Equal(fromId, check.Series.Single(s => s.Id == seriesId).RootFolderId);
    }

    [Fact]
    public async Task Relink_refuses_deleting_superseded_files_without_delete_permission()
    {
        var seriesId = _db.SeedSeries();
        using var db = _db.NewContext(userId: 1);

        var result = await Controller(db, MakiPermission.EditMetadata).Relink(
            seriesId, new SeriesController.RelinkRequest(null, null, DeleteSuperseded: true, ["Series/a.cbz"]),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Relink_refuses_while_a_download_for_the_series_is_in_flight()
    {
        var seriesId = _db.SeedSeries();
        using (var seed = _db.NewContext())
        {
            seed.DownloadQueue.Add(new DownloadQueueItem { SeriesId = seriesId, Status = QueueStatus.Downloading });
            seed.SaveChanges();
        }

        using var db = _db.NewContext(userId: 1);
        var result = await Controller(db).Relink(
            seriesId, new SeriesController.RelinkRequest(null, null, DeleteSuperseded: false), CancellationToken.None);

        Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
    }
}
