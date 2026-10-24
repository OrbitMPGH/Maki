using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class HealthOperationGroupTests : IDisposable
{
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly TestDb fixture = new();

    public void Dispose() => fixture.Dispose();

    private HealthController Controller(MakiDbContext db) =>
        new(db, null!, null!, null!, new TestCurrentUser(1), null!, new TestLocalizer(), null!, null!, NullLogger<HealthController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private static JsonElement Body(IActionResult result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value, Camel);

    private async Task Seed(MakiDbContext db, int count, string kind, string status, string? errorKey, string? legacyError = null)
    {
        for (var i = 0; i < count; i++)
        {
            db.HealthOperations.Add(new HealthOperation
            {
                Kind = kind, Status = status, FileId = 1000 + i, ErrorKey = errorKey, Error = legacyError,
            });
        }

        await db.SaveChangesAsync();
    }

    private sealed class NoUserSettings : IUserSettings
    {
        public int UserId => 1;
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<Dictionary<string, string>> GetManyAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, string>());
        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_hundred_identical_failures_are_one_group_with_the_full_count()
    {
        using var db = fixture.NewContext();
        await Seed(db, 2, "repair", "review", null);
        await Seed(db, 100, "delete", "failed", "health.operation.error.deleteFailed");
        await Seed(db, 1, "repair", "failed", null, "No source had a candidate");

        var body = Body(await Controller(db).Operations(new NoUserSettings(), 1, default));

        Assert.Equal(3, body.GetProperty("total").GetInt32());
        var groups = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["repair", "delete", "repair"], groups.Select(g => g.GetProperty("kind").GetString()));
        Assert.Equal([1, 100, 2], groups.Select(g => g.GetProperty("count").GetInt32()));
        Assert.Equal("No source had a candidate", groups[0].GetProperty("error").GetString());

        var deletions = groups[1];
        var rows = deletions.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(rows.OrderByDescending(id => id), rows);
    }

    [Fact]
    public async Task Groups_page_instead_of_rows()
    {
        using var db = fixture.NewContext();
        for (var i = 0; i < 32; i++)
        {
            await Seed(db, 1, "delete", "failed", $"health.operation.error.k{i}");
        }

        var controller = Controller(db);
        var first = Body(await controller.Operations(new NoUserSettings(), 1, default));
        var second = Body(await controller.Operations(new NoUserSettings(), 2, default));

        Assert.Equal(32, first.GetProperty("total").GetInt32());
        Assert.Equal(30, first.GetProperty("items").GetArrayLength());
        Assert.Equal(2, second.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Show_all_returns_the_groups_rows_by_its_key_and_nothing_else()
    {
        using var db = fixture.NewContext();
        await Seed(db, 40, "delete", "failed", "health.operation.error.deleteFailed");
        await Seed(db, 5, "delete", "completed", null);
        var controller = Controller(db);

        var key = Body(await controller.Operations(new NoUserSettings(), 1, default))
            .GetProperty("items").EnumerateArray()
            .Single(g => g.GetProperty("status").GetString() == "failed").GetProperty("key").GetString()!;
        var all = Body(await controller.OperationGroup(new NoUserSettings(), key, 100, default));

        Assert.Equal(40, all.GetProperty("total").GetInt32());
        Assert.Equal(40, all.GetProperty("items").GetArrayLength());
        Assert.All(all.GetProperty("items").EnumerateArray(), r => Assert.Equal("failed", r.GetProperty("status").GetString()));

        var some = Body(await controller.OperationGroup(new NoUserSettings(), key, 10, default));
        Assert.Equal(40, some.GetProperty("total").GetInt32());
        Assert.Equal(10, some.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_key_that_is_not_one_of_ours_is_a_bad_request()
    {
        using var db = fixture.NewContext();

        var controller = Controller(db);

        Assert.IsType<BadRequestResult>(await controller.OperationGroup(new NoUserSettings(), "not-a-key", 30, default));
        Assert.IsType<BadRequestResult>(await controller.OperationGroup(new NoUserSettings(), "999999", 30, default));
        Assert.IsType<BadRequestResult>(await controller.OperationGroup(new NoUserSettings(), "", 30, default));
    }

    [Fact]
    public async Task A_long_legacy_error_travels_as_a_prefix_and_still_finds_its_group()
    {
        using var db = fixture.NewContext();
        var error = new string('x', 20_000);
        await Seed(db, 4, "repair", "failed", null, error);
        await Seed(db, 2, "repair", "failed", null, "short");
        var controller = Controller(db);

        var group = Body(await controller.Operations(new NoUserSettings(), 1, default))
            .GetProperty("items").EnumerateArray().Single(g => g.GetProperty("error").GetString() == error);
        var key = group.GetProperty("key").GetString()!;
        var all = Body(await controller.OperationGroup(new NoUserSettings(), key, 100, default));

        Assert.True(key.Length < 4_000);
        Assert.Equal(4, all.GetProperty("total").GetInt32());
        Assert.All(all.GetProperty("items").EnumerateArray(), r => Assert.Equal(error, r.GetProperty("error").GetString()));
    }

    private static async Task<string> FailedKey(HealthController controller) =>
        Body(await controller.Operations(new NoUserSettings(), 1, default))
            .GetProperty("items").EnumerateArray()
            .Single(g => g.GetProperty("status").GetString() == "failed").GetProperty("key").GetString()!;

    [Fact]
    public async Task A_groups_key_survives_operations_joining_and_leaving_it()
    {
        using var db = fixture.NewContext();
        await Seed(db, 3, "delete", "failed", "health.operation.error.deleteFailed");
        var controller = Controller(db);
        var before = await FailedKey(controller);

        await Seed(db, 2, "delete", "failed", "health.operation.error.deleteFailed");
        Assert.Equal(before, await FailedKey(controller));

        var newest = db.HealthOperations.Where(o => o.Status == "failed").OrderByDescending(o => o.Id).First();
        db.HealthOperations.Remove(newest);
        await db.SaveChangesAsync();
        Assert.Equal(before, await FailedKey(controller));

        var all = Body(await controller.OperationGroup(new NoUserSettings(), before, 100, default));
        Assert.Equal(4, all.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_key_still_resolves_after_its_newest_row_is_purged()
    {
        using var db = fixture.NewContext();
        await Seed(db, 3, "delete", "failed", "health.operation.error.deleteFailed");
        var controller = Controller(db);
        var key = await FailedKey(controller);

        db.HealthOperations.RemoveRange(db.HealthOperations.Where(o => o.Status == "failed").OrderByDescending(o => o.Id).Take(2));
        await db.SaveChangesAsync();

        var all = Body(await controller.OperationGroup(new NoUserSettings(), key, 100, default));
        Assert.Equal(1, all.GetProperty("total").GetInt32());
    }
}
