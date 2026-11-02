using System.IO.Compression;
using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class HealthOperationDismissRetryTests : IDisposable
{
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly TestDb fixture = new();
    private readonly string root = Directory.CreateTempSubdirectory("maki-health-retry-").FullName;
    public void Dispose() { fixture.Dispose(); Directory.Delete(root, true); }

    private HealthOperationService Operations(MakiDbContext db) => new(db, null!, null!,
        new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
        new EventBroadcaster(new NoopHubContext(), fixture.ScopeFactory()),
        new KavitaScanService(null!, null!, fixture.ScopeFactory(), NullLogger<KavitaScanService>.Instance),
        TestQuality.Create());

    private HealthController Controller(MakiDbContext db) =>
        new(db, null!, Operations(db), null!, new TestCurrentUser(1), null!, new TestLocalizer(), null!, null!, NullLogger<HealthController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private static JsonElement Body(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value, Camel);

    private sealed class NoUserSettings : IUserSettings
    {
        public int UserId => 1;
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<Dictionary<string, string>> GetManyAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, string>());
        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private async Task<HealthFile> SeedFile(MakiDbContext db, string name)
    {
        var folder = db.RootFolders.FirstOrDefault() ?? db.RootFolders.Add(new RootFolder { Path = root }).Entity;
        await db.SaveChangesAsync();
        using (var zip = ZipFile.Open(Path.Combine(root, name), ZipArchiveMode.Create))
        { using var stream = zip.CreateEntry("ComicInfo.xml").Open(); stream.Write("<ComicInfo/>"u8); }
        var file = new HealthFile { RootFolderId = folder.Id, RelativePath = name };
        db.HealthFiles.Add(file); await db.SaveChangesAsync();
        await new HealthScanService(db).AnalyzeAsync(file, root, true, default, 0, true);
        return file;
    }

    private static HealthOperation Failed(HealthFile file, string kind = "delete") => new()
    {
        Kind = kind, Status = "failed", FileId = file.Id, Version = file.Version, ErrorKey = "health.operation.error.deleteFailed",
    };

    private async Task<int> ListedCount(HealthController controller) =>
        Body(await controller.Operations(new NoUserSettings(), 1, default))
            .GetProperty("items").EnumerateArray().Sum(g => g.GetProperty("count").GetInt32());

    [Fact]
    public async Task A_failure_on_a_removed_or_changed_file_drops_off_the_list()
    {
        using var db = fixture.NewContext();
        var current = await SeedFile(db, "current.cbz");
        var removed = await SeedFile(db, "removed.cbz");
        var changed = await SeedFile(db, "changed.cbz");
        db.HealthOperations.AddRange(Failed(current), Failed(removed), Failed(changed));
        removed.Removed = true;
        changed.Version = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync();

        var controller = Controller(db);
        var groups = Body(await controller.Operations(new NoUserSettings(), 1, default)).GetProperty("items");

        Assert.Equal(1, groups.GetArrayLength());
        Assert.Equal(1, groups[0].GetProperty("count").GetInt32());
        Assert.Equal(current.Id, groups[0].GetProperty("rows")[0].GetProperty("fileId").GetInt32());
        var key = groups[0].GetProperty("key").GetString()!;
        Assert.Equal(1, Body(await controller.OperationGroup(new NoUserSettings(), key, 100, default)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Dismissing_a_finished_group_clears_it_and_a_running_group_is_refused()
    {
        using var db = fixture.NewContext();
        var file = await SeedFile(db, "one.cbz");
        db.HealthOperations.AddRange(Failed(file), Failed(file), new HealthOperation { Kind = "delete", Status = "review", FileId = file.Id, Version = file.Version });
        await db.SaveChangesAsync();
        var controller = Controller(db);
        var groups = Body(await controller.Operations(new NoUserSettings(), 1, default)).GetProperty("items").EnumerateArray().ToList();
        string KeyOf(string status) => groups.Single(g => g.GetProperty("status").GetString() == status).GetProperty("key").GetString()!;

        Assert.IsType<ConflictObjectResult>(await controller.DismissOperationGroup(new(KeyOf("review")), default));
        var dismissed = Body(await controller.DismissOperationGroup(new(KeyOf("failed")), default));

        Assert.Equal(2, dismissed.GetProperty("dismissed").GetInt32());
        Assert.Equal(1, await ListedCount(controller));
        Assert.Equal(3, db.HealthOperations.Count());
    }

    [Fact]
    public async Task A_single_operation_is_dismissed_only_once_it_has_finished()
    {
        using var db = fixture.NewContext();
        var file = await SeedFile(db, "one.cbz");
        var running = new HealthOperation { Kind = "repair", Status = "downloading", FileId = file.Id, Version = file.Version };
        var failed = Failed(file);
        db.HealthOperations.AddRange(running, failed);
        await db.SaveChangesAsync();
        var controller = Controller(db);

        Assert.IsType<ConflictObjectResult>(await controller.DismissOperation(running.Id, default));
        Body(await controller.DismissOperation(failed.Id, default));

        Assert.NotNull(failed.DismissedAt);
        Assert.Null(running.DismissedAt);
        Assert.Equal(1, await ListedCount(controller));
    }

    [Fact]
    public async Task Retrying_a_failed_delete_opens_a_new_review_and_dismisses_the_failure()
    {
        using var db = fixture.NewContext();
        var file = await SeedFile(db, "one.cbz");
        var failed = Failed(file);
        db.HealthOperations.Add(failed);
        await db.SaveChangesAsync();
        var controller = Controller(db);

        Assert.True(Body(await controller.Operation(failed.Id, default)).GetProperty("retryable").GetBoolean());
        var next = Body(await controller.RetryOperation(failed.Id, default));

        Assert.Equal("review", next.GetProperty("status").GetString());
        Assert.Equal("delete", next.GetProperty("kind").GetString());
        Assert.NotEqual(failed.Id, next.GetProperty("id").GetInt32());
        Assert.NotNull(failed.DismissedAt);
        Assert.True(File.Exists(Path.Combine(root, "one.cbz")));
    }

    [Fact]
    public async Task A_failure_on_a_removed_file_cannot_be_retried()
    {
        using var db = fixture.NewContext();
        var file = await SeedFile(db, "one.cbz");
        var failed = Failed(file);
        db.HealthOperations.Add(failed);
        file.Removed = true;
        await db.SaveChangesAsync();
        var controller = Controller(db);

        Assert.False(Body(await controller.Operation(failed.Id, default)).GetProperty("retryable").GetBoolean());
        Assert.IsType<ConflictObjectResult>(await controller.RetryOperation(failed.Id, default));
        Assert.Null(failed.DismissedAt);
        Assert.Single(db.HealthOperations);
    }
}
