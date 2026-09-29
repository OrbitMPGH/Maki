using System.IO.Compression;
using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Covers three of the review's confirmed bugs together, since all three live in the health
/// delete/cancel path and share the same seeding helpers: a bulk delete must refuse a file whose
/// version moved since the caller reviewed it (rather than reading the current version itself), a
/// delete's DB cleanup must finish even if the request is cancelled right after the archive is
/// gone, and a cancelled operation must not leave its staged files behind forever.
/// </summary>
public sealed class HealthDeleteBulkAndCancelTests : IDisposable
{
    private readonly TestDb fixture = new();
    private readonly string root = Directory.CreateTempSubdirectory("maki-health-bulk-").FullName;
    public void Dispose() { fixture.Dispose(); Directory.Delete(root, true); }

    private HealthOperationService Operations(MakiDbContext db) => new(db, null!, null!,
        new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
        new EventBroadcaster(new NoopHubContext(), fixture.ScopeFactory()),
        new KavitaScanService(null!, null!, fixture.ScopeFactory(), NullLogger<KavitaScanService>.Instance),
        TestQuality.Create());

    private HealthController Controller(MakiDbContext db, HealthOperationService operations) =>
        new(db, null!, operations, null!, new TestCurrentUser(1), null!, new TestLocalizer(), null!, null!)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private async Task<HealthFile> Seed(MakiDbContext db)
    {
        var folder = new RootFolder { Path = root }; db.RootFolders.Add(folder); await db.SaveChangesAsync();
        var path = Path.Combine(root, "one.cbz");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        { using var stream = zip.CreateEntry("ComicInfo.xml").Open(); stream.Write("<ComicInfo/>"u8); }
        var file = new HealthFile { RootFolderId = folder.Id, RelativePath = "one.cbz" };
        db.HealthFiles.Add(file); await db.SaveChangesAsync();
        await new HealthScanService(db).AnalyzeAsync(file, root, true, default, 0, true);
        return file;
    }

    [Fact]
    public async Task Bulk_delete_refuses_a_file_whose_version_moved_since_review()
    {
        using var db = fixture.NewContext();
        var file = await Seed(db);
        var reviewedVersion = file.Version;
        // The reviewer's screen goes stale: the file gets rescanned (or replaced) before they
        // press delete, the same as the single-file stale-review case already covered elsewhere.
        file.Version = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync();

        var controller = Controller(db, Operations(db));
        var result = await controller.DeleteBulk(
            new HealthController.BulkDelete([new HealthController.FileReview(file.Id, reviewedVersion)], true),
            default);

        var ok = Assert.IsType<OkObjectResult>(result);
        var body = JsonSerializer.SerializeToElement(ok.Value);
        Assert.Equal(0, body.GetProperty("deleted").GetInt32());
        Assert.Equal(1, body.GetProperty("failures").GetArrayLength());
        Assert.True(File.Exists(Path.Combine(root, "one.cbz")));
        Assert.False(file.Removed);
    }

    [Fact]
    public async Task Cancelled_token_after_file_delete_still_completes_db_cleanup()
    {
        using var db = fixture.NewContext();
        var file = await Seed(db);
        var series = new Series { Title = "Bulk delete test", SortTitle = "bulk delete test", FolderName = "Test", RootFolderId = file.RootFolderId };
        db.Series.Add(series); await db.SaveChangesAsync();
        var cf = new ChapterFile { SeriesId = series.Id, RelativePath = "one.cbz", Size = new FileInfo(Path.Combine(root, "one.cbz")).Length };
        db.ChapterFiles.Add(cf); await db.SaveChangesAsync();
        db.Chapters.Add(new Chapter { SeriesId = series.Id, ChapterFileId = cf.Id, Number = 1 });
        await db.SaveChangesAsync();

        var service = Operations(db);
        var op = await service.PreviewDeleteAsync(file.Id, file.Version, 1, default);
        using var cts = new CancellationTokenSource();
        // Cancel exactly once File.Delete has run - standing in for a client disconnect at that
        // instant. Any earlier and the request would never reach the delete branch at all; the
        // cleanup that follows must still land regardless.
        service.TestHookAfterFileDeleted = cts.Cancel;

        await service.ApplyAsync(op.Id, file.Version, true, false, cts.Token);

        Assert.Equal("completed", op.Status);
        Assert.False(File.Exists(Path.Combine(root, "one.cbz")));
        Assert.Empty(db.ChapterFiles);
        Assert.True(file.Removed);

        // The audit trail row is keyed, not raw English, so it renders in the reader's own language.
        var history = db.HealthHistory.Single(h => h.FileId == file.Id);
        Assert.Equal("health.history.deletedFile", history.MessageKey);
        Assert.Contains("one.cbz", history.ParamsJson);
    }

    [Fact]
    public async Task Deleting_a_missing_files_record_is_keyed_differently_from_a_real_delete()
    {
        using var db = fixture.NewContext();
        var file = await Seed(db);
        File.Delete(Path.Combine(root, "one.cbz"));
        file.Size = -1;
        file.ModifiedAt = DateTime.MinValue;
        await db.SaveChangesAsync();

        var service = Operations(db);
        var op = await service.PreviewDeleteAsync(file.Id, file.Version, 1, default);

        await service.ApplyAsync(op.Id, file.Version, true, false, default);

        var history = db.HealthHistory.Single(h => h.FileId == file.Id);
        Assert.Equal("health.history.deletedMissing", history.MessageKey);
    }

    private async Task<HealthOperation> StageDownloading(MakiDbContext db, HealthFile file)
    {
        var op = new HealthOperation { FileId = file.Id, Version = file.Version, Status = "downloading", UserId = 1 };
        db.HealthOperations.Add(op); await db.SaveChangesAsync();
        var relative = $".maki/health/{op.Id}/chapter-1.cbz";
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        { using var stream = zip.CreateEntry("001.png").Open(); stream.WriteByte(1); }
        return op;
    }

    [Fact]
    public async Task Cancel_after_one_candidate_downloaded_leaves_no_staging_directory()
    {
        using var db = fixture.NewContext();
        var file = await Seed(db);
        var op = await StageDownloading(db, file);
        var stagingDir = Path.Combine(root, ".maki", "health", op.Id.ToString());
        Assert.True(Directory.Exists(stagingDir));

        var controller = Controller(db, Operations(db));
        await controller.CancelOperation(op.Id, default);

        Assert.Equal("cancelled", op.Status);
        Assert.False(Directory.Exists(stagingDir));
    }

    [Fact]
    public async Task Startup_recovery_removes_a_stale_cancelled_operations_staging_directory()
    {
        using var db = fixture.NewContext();
        var file = await Seed(db);
        var op = await StageDownloading(db, file);
        op.Status = "cancelled";
        await db.SaveChangesAsync();
        var stagingDir = Path.Combine(root, ".maki", "health", op.Id.ToString());
        Assert.True(Directory.Exists(stagingDir));

        await Operations(db).RecoverAsync(default);

        Assert.False(Directory.Exists(stagingDir));
    }
}
