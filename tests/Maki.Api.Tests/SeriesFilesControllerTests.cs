using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Security;
using Maki.Core.Tests;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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

    private SeriesController Controller(MakiDbContext db, MakiPermission permissions = MakiPermission.Admin, ICurrentUser? currentUser = null)
    {
        var user = currentUser ?? new TestCurrentUser(1, permissions: permissions);
        return new(
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
            readingProfiles: null!,
            readingTimeEstimates: null!,
            sourceAvailability: null!,
            currentUser: user,
            userSettings: new UserSettingsService(db, user),
            notifications: null!,
            locales: null!,
            logger: NullLogger<SeriesController>.Instance);
    }

    /// <summary>An <see cref="ICurrentUser"/> granted only specific root folders, not all of them.</summary>
    private sealed class GrantedRootUser(int userId, MakiPermission permissions, params int[] rootFolderIds) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public int UserId => userId;
        public string UserName => "u";
        public MakiPermission Permissions => permissions;
        public bool AllRootFolders => false;
        public IReadOnlySet<int> RootFolderIds => rootFolderIds.ToHashSet();
        public string MaxContentRating => "erotica";
    }

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

    private static string? Code(IActionResult result)
    {
        var body = Assert.IsType<BadRequestObjectResult>(result).Value;
        return (string?)body!.GetType().GetProperty("code")!.GetValue(body);
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
    public async Task Move_refuses_a_destination_root_the_caller_has_no_grant_for()
    {
        var (seriesId, fromId, toId, from, to) = SeedTwoFolderSeries(Path.Combine(_temp, "b"));
        Directory.CreateDirectory(to);

        using var db = _db.NewContext(userId: 1);
        var user = new GrantedRootUser(1, MakiPermission.EditMetadata, fromId);
        var result = await Controller(db, currentUser: user).Move(
            seriesId, new SeriesController.MoveSeriesRequest(toId), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.True(File.Exists(Path.Combine(from, "Berserk", "Berserk Ch.1.cbz")));
        Assert.False(File.Exists(Path.Combine(to, "Berserk", "Berserk Ch.1.cbz")));
        using var check = _db.NewContext();
        Assert.Equal(fromId, check.Series.Single(s => s.Id == seriesId).RootFolderId);
    }

    /// <summary>
    /// The cross-volume fallback copies and then deletes the source tree, which would drop a nested
    /// link and strand the rows reached through it, so any link in the tree refuses the move.
    /// </summary>
    [Fact]
    public async Task Move_refuses_a_series_folder_with_a_nested_link()
    {
        var (seriesId, fromId, toId, from, to) = SeedTwoFolderSeries(Path.Combine(_temp, "b"));
        Directory.CreateDirectory(to);
        var outside = Path.Combine(_temp, "outside");
        Write(Path.Combine(outside, "Berserk Ch.3.cbz"));
        var link = Path.Combine(from, "Berserk", "Extras", "linked");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!TestLinks.TryLinkDirectory(link, outside))
        {
            return;
        }

        try
        {
            using var db = _db.NewContext(userId: 1);
            var result = await Controller(db).Move(seriesId, new SeriesController.MoveSeriesRequest(toId), CancellationToken.None);

            Assert.Equal("error.series.folderContainsLinks", Code(result));
            Assert.True(File.Exists(Path.Combine(from, "Berserk", "Berserk Ch.1.cbz")));
            Assert.True(File.Exists(Path.Combine(link, "Berserk Ch.3.cbz")));
            Assert.False(Directory.Exists(Path.Combine(to, "Berserk")));
            using var check = _db.NewContext();
            Assert.Equal(fromId, check.Series.Single(s => s.Id == seriesId).RootFolderId);
        }
        finally
        {
            TestLinks.UnlinkDirectory(link);
        }
    }

    [Fact]
    public async Task Move_refuses_a_series_folder_that_is_itself_a_link()
    {
        var (seriesId, fromId, toId, from, to) = SeedTwoFolderSeries(Path.Combine(_temp, "b"));
        Directory.CreateDirectory(to);
        var outside = Path.Combine(_temp, "outside");
        var seriesFolder = Path.Combine(from, "Berserk");
        Directory.Move(seriesFolder, outside);
        if (!TestLinks.TryLinkDirectory(seriesFolder, outside))
        {
            return;
        }

        try
        {
            using var db = _db.NewContext(userId: 1);
            var result = await Controller(db).Move(seriesId, new SeriesController.MoveSeriesRequest(toId), CancellationToken.None);

            Assert.Equal("error.series.folderContainsLinks", Code(result));
            Assert.True(File.Exists(Path.Combine(outside, "Berserk Ch.1.cbz")));
            Assert.False(Directory.Exists(Path.Combine(to, "Berserk")));
            using var check = _db.NewContext();
            Assert.Equal(fromId, check.Series.Single(s => s.Id == seriesId).RootFolderId);
        }
        finally
        {
            TestLinks.UnlinkDirectory(seriesFolder);
        }
    }

    /// <summary>A <see cref="MakiDbContext"/> whose next save throws once, then behaves normally.</summary>
    private sealed class ThrowOnceDbContext(DbContextOptions<MakiDbContext> options, DataScope? scope)
        : MakiDbContext(options, scope)
    {
        private bool _armed = true;

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
        {
            if (_armed)
            {
                _armed = false;
                throw new InvalidOperationException("simulated save failure");
            }

            return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        }
    }

    /// <summary>
    /// The move loop finishes relocating every file, but the RootFolderId save that follows it fails
    /// (standing in for a cancellation or a DB error observed only at that point). The undo list must
    /// still run so the files land back in the source root and the series keeps its old RootFolderId.
    /// </summary>
    [Fact]
    public async Task Move_undoes_the_file_moves_when_the_save_after_the_loop_fails()
    {
        var (seriesId, fromId, toId, from, to) = SeedTwoFolderSeries(Path.Combine(_temp, "b"));
        Directory.CreateDirectory(to);

        var scope = new DataScope();
        scope.SetUser(1, allRootFolders: true);
        using var db = new ThrowOnceDbContext(_db.Options, scope);
        var result = await Controller(db).Move(seriesId, new SeriesController.MoveSeriesRequest(toId), CancellationToken.None);

        Assert.Equal(500, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.True(File.Exists(Path.Combine(from, "Berserk", "Berserk Ch.1.cbz")));
        Assert.True(File.Exists(Path.Combine(from, "Old Berserk", "Berserk Ch.2.cbz")));
        Assert.False(Directory.Exists(Path.Combine(to, "Berserk")));
        Assert.False(File.Exists(Path.Combine(to, "Old Berserk", "Berserk Ch.2.cbz")));
        using var check = _db.NewContext();
        Assert.Equal(fromId, check.Series.Single(s => s.Id == seriesId).RootFolderId);
    }

    /// <summary>
    /// The rollback after a failed save must not leave behind the destination directory it created
    /// for the partial folder's files, or a retry's Directory.Exists(target) pre-check sees it and
    /// fails with error.series.destinationExists even though nothing actually moved.
    /// </summary>
    [Fact]
    public async Task Move_succeeds_on_retry_after_a_rolled_back_save_failure()
    {
        var (seriesId, fromId, toId, from, to) = SeedTwoFolderSeries(Path.Combine(_temp, "b"));
        Directory.CreateDirectory(to);

        var scope = new DataScope();
        scope.SetUser(1, allRootFolders: true);
        using var db = new ThrowOnceDbContext(_db.Options, scope);
        var controller = Controller(db);

        var failed = await controller.Move(seriesId, new SeriesController.MoveSeriesRequest(toId), CancellationToken.None);
        Assert.Equal(500, Assert.IsAssignableFrom<ObjectResult>(failed).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(to, "Old Berserk")));

        var retried = await controller.Move(seriesId, new SeriesController.MoveSeriesRequest(toId), CancellationToken.None);

        Assert.IsType<OkObjectResult>(retried);
        Assert.True(File.Exists(Path.Combine(to, "Berserk", "Berserk Ch.1.cbz")));
        Assert.True(File.Exists(Path.Combine(to, "Old Berserk", "Berserk Ch.2.cbz")));
        using var check = _db.NewContext();
        Assert.Equal(toId, check.Series.Single(s => s.Id == seriesId).RootFolderId);
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

    [Fact]
    public async Task Relink_waits_for_the_series_lock()
    {
        var seriesId = _db.SeedSeries();
        using (var seed = _db.NewContext())
        {
            seed.DownloadQueue.Add(new DownloadQueueItem { SeriesId = seriesId, Status = QueueStatus.Downloading });
            seed.SaveChanges();
        }

        using var db = _db.NewContext(userId: 1);
        Task<IActionResult> relink;
        using (await SeriesLocks.SeriesAsync(seriesId, CancellationToken.None))
        {
            relink = Controller(db).Relink(
                seriesId, new SeriesController.RelinkRequest(null, null, DeleteSuperseded: false), CancellationToken.None);
            await Task.Delay(200);
            Assert.False(relink.IsCompleted);
        }

        Assert.Equal(409, Assert.IsAssignableFrom<ObjectResult>(await relink).StatusCode);
    }

    private static SeriesFilesSummaryDto Summary(IActionResult result) =>
        Assert.IsType<SeriesFilesSummaryDto>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task Files_summary_matches_the_listing_and_rewalks_only_when_the_records_change()
    {
        var (seriesId, _, _, from, _) = SeedTwoFolderSeries(Path.Combine(_temp, "b"));
        var cache = new MemoryCache(new MemoryCacheOptions());
        using var db = _db.NewContext(userId: 1);

        // Two records on disk and one untracked file, none linked by a chapter.
        var first = Summary(await Controller(db).FilesSummary(seriesId, cache, CancellationToken.None));
        Assert.Equal(new SeriesFilesSummaryDto(3, 3), first);

        // Linking is read fresh every time; only the folder walk is cached.
        using (var seed = _db.NewContext())
        {
            var file = seed.ChapterFiles.Single(f => f.SeriesId == seriesId && f.RelativePath.StartsWith("Berserk"));
            seed.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 1, ChapterFileId = file.Id });
            seed.SaveChanges();
        }

        Assert.Equal(new SeriesFilesSummaryDto(3, 2),
            Summary(await Controller(db).FilesSummary(seriesId, cache, CancellationToken.None)));

        // A file dropped in by hand changes no record, so the cached walk stands.
        var dropped = Path.Combine("Berserk", "Berserk Ch.3.cbz");
        Write(Path.Combine(from, dropped));
        Assert.Equal(new SeriesFilesSummaryDto(3, 2),
            Summary(await Controller(db).FilesSummary(seriesId, cache, CancellationToken.None)));

        // Importing it adds a record, which is what every import, rescan, relink and delete does.
        using (var seed = _db.NewContext())
        {
            seed.ChapterFiles.Add(new ChapterFile { SeriesId = seriesId, RelativePath = dropped, DateAdded = DateTime.UtcNow });
            seed.SaveChanges();
        }

        Assert.Equal(new SeriesFilesSummaryDto(4, 3),
            Summary(await Controller(db).FilesSummary(seriesId, cache, CancellationToken.None)));
    }

    [Fact]
    public async Task The_full_listing_refreshes_the_summary_walk()
    {
        var (seriesId, _, _, from, _) = SeedTwoFolderSeries(Path.Combine(_temp, "b"));
        var cache = new MemoryCache(new MemoryCacheOptions());
        using var db = _db.NewContext(userId: 1);
        Assert.Equal(3, Summary(await Controller(db).FilesSummary(seriesId, cache, CancellationToken.None)).Count);

        Write(Path.Combine(from, "Berserk", "Berserk Ch.9.cbz"));
        var listing = await Controller(db).Files(
            seriesId, new UpgradeEvaluationService(db, TestQuality.Create()), cache, CancellationToken.None);
        var files = Assert.IsAssignableFrom<IEnumerable<Maki.Api.Dtos.SeriesFileDto>>(
            Assert.IsType<OkObjectResult>(listing).Value).ToList();

        var summary = Summary(await Controller(db).FilesSummary(seriesId, cache, CancellationToken.None));
        Assert.Equal(files.Count, summary.Count);
        Assert.Equal(files.Count(f => f.OnDisk && f.Status != "linked"), summary.UnlinkedOnDisk);
        Assert.Equal(4, summary.Count);
    }

    [Fact]
    public async Task A_file_backing_an_unnumbered_chapter_counts_as_linked()
    {
        var (seriesId, _, _, _, _) = SeedTwoFolderSeries(Path.Combine(_temp, "b"));
        using (var seed = _db.NewContext())
        {
            var file = seed.ChapterFiles.Single(f => f.SeriesId == seriesId && f.RelativePath.StartsWith("Berserk"));
            seed.Chapters.Add(new Chapter { SeriesId = seriesId, Number = null, IsOneShot = true, ChapterFileId = file.Id });
            seed.SaveChanges();
        }

        var cache = new MemoryCache(new MemoryCacheOptions());
        using var db = _db.NewContext(userId: 1);
        Assert.Equal(new SeriesFilesSummaryDto(3, 2),
            Summary(await Controller(db).FilesSummary(seriesId, cache, CancellationToken.None)));

        var listing = await Controller(db).Files(
            seriesId, new UpgradeEvaluationService(db, TestQuality.Create()), cache, CancellationToken.None);
        var files = Assert.IsAssignableFrom<IEnumerable<Maki.Api.Dtos.SeriesFileDto>>(
            Assert.IsType<OkObjectResult>(listing).Value).ToList();
        var linked = Assert.Single(files, f => f.RelativePath.StartsWith("Berserk"));
        Assert.Equal("linked", linked.Status);
        Assert.Empty(linked.MappedChapters);
    }

    [Fact]
    public async Task Move_refuses_a_root_where_another_series_already_owns_the_folder_name()
    {
        var (seriesId, _, toId, from, to) = SeedTwoFolderSeries(Path.Combine(_temp, "b"));
        Directory.CreateDirectory(to);
        using (var seed = _db.NewContext())
        {
            seed.Series.Add(new Series
            {
                Title = "Berserk (other)", SortTitle = "berserk", FolderName = "berserk", RootFolderId = toId,
            });
            seed.SaveChanges();
        }

        using var db = _db.NewContext(userId: 1);
        var result = await Controller(db).Move(seriesId, new SeriesController.MoveSeriesRequest(toId), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.True(Directory.Exists(Path.Combine(from, "Berserk")));
        Assert.False(Directory.Exists(Path.Combine(to, "Berserk")));
        using var verify = _db.NewContext();
        Assert.NotEqual(toId, verify.Series.Single(s => s.Id == seriesId).RootFolderId);
    }

    [Fact]
    public async Task Move_without_files_refuses_a_root_where_another_series_owns_the_folder_name()
    {
        var (seriesId, _, toId, _, to) = SeedTwoFolderSeries(Path.Combine(_temp, "b"));
        Directory.CreateDirectory(Path.Combine(to, "Berserk"));
        using (var seed = _db.NewContext())
        {
            seed.Series.Add(new Series
            {
                Title = "Berserk (other)", SortTitle = "berserk", FolderName = "Berserk", RootFolderId = toId,
            });
            seed.SaveChanges();
        }

        using var db = _db.NewContext(userId: 1);
        var result = await Controller(db).Move(
            seriesId, new SeriesController.MoveSeriesRequest(toId, MoveFiles: false), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }
}
