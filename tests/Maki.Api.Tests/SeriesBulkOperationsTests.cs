using Maki.Api.Auth;
using Maki.Api.Controllers;
using Maki.Api.Hubs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The Library's bulk endpoints: which ids get queued for whom, and how the worker records each
/// series. The single-series actions they run are covered by their own tests.
/// </summary>
public sealed class SeriesBulkOperationsTests : IDisposable
{
    private readonly TestDb _db = new TestDb().WithDistinctSeriesIds();

    public void Dispose() => _db.Dispose();

    private static SeriesController Controller(MakiDbContext db, ICurrentUser user) =>
        new(localizer: new TestLocalizer(), db: db, coverService: null!, chapterSyncService: null!,
            cbzLinkService: null!, relinkPlanner: null!, seriesCreation: null!, seriesRename: null!,
            metadataRefresh: null!, downloadQueue: null!, downloadBatches: null!, appSettings: null!,
            kavitaScans: null!, scrobbler: null!, stats: null!, mangaBakaStore: null!, similarSeries: null!,
            recommendationFeedback: null!, readingProfiles: null!, readingTimeEstimates: null!,
            sourceAvailability: null!, currentUser: user, userSettings: null!, notifications: null!,
            locales: null!, logger: NullLogger<SeriesController>.Instance);

    private sealed class FixedLocale : IRequestLocale
    {
        public string Locale => "en";
    }

    private int RootOf(int seriesId)
    {
        using var db = _db.NewContext();
        return db.Series.Single(s => s.Id == seriesId).RootFolderId;
    }

    private void Grant(int userId, int rootFolderId)
    {
        using var db = _db.NewContext();
        db.UserRootFolders.Add(new UserRootFolder { UserId = userId, RootFolderId = rootFolderId });
        db.SaveChanges();
    }

    private static DataScope Scope(int userId, bool allRootFolders)
    {
        var scope = new DataScope();
        scope.SetUser(userId, allRootFolders);
        return scope;
    }

    [Fact]
    public async Task Ids_outside_the_callers_root_folders_are_skipped_not_queued()
    {
        var alice = _db.SeedUser("alice", MakiPermission.EditMetadata, allRootFolders: false);
        var mine = _db.SeedSeries("Mine");
        var theirs = _db.SeedSeries("Theirs");
        Grant(alice, RootOf(mine));
        var user = new TestCurrentUser(alice, permissions: MakiPermission.EditMetadata, allRootFolders: false,
            rootFolderIds: new HashSet<int> { RootOf(mine) });
        var operations = new SeriesBulkOperations();

        using var db = _db.NewContext(alice, allRootFolders: false);
        var result = await Controller(db, user).RefreshBulk(
            new SeriesController.BulkSeriesRequest([mine, theirs, 999_999_999]), operations,
            Scope(alice, false), new FixedLocale(), default);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var body = System.Text.Json.JsonSerializer.SerializeToElement(accepted.Value);
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal([theirs, 999_999_999],
            body.GetProperty("skipped").EnumerateArray().Select(e => e.GetInt32()).ToArray());

        var operation = operations.Get(body.GetProperty("operationId").GetGuid(), alice);
        Assert.NotNull(operation);
        Assert.Equal([mine], operation.SeriesIds);
        Assert.Null(operations.Get(operation.Id, alice + 1));
    }

    [Fact]
    public async Task Monitor_mode_bulk_writes_only_the_series_the_caller_can_see()
    {
        var alice = _db.SeedUser("alice", MakiPermission.EditMetadata, allRootFolders: false);
        var mine = _db.SeedSeries("Mine", NewChapterMonitorMode.All);
        var theirs = _db.SeedSeries("Theirs", NewChapterMonitorMode.All);
        Grant(alice, RootOf(mine));
        var user = new TestCurrentUser(alice, permissions: MakiPermission.EditMetadata, allRootFolders: false,
            rootFolderIds: new HashSet<int> { RootOf(mine) });

        using (var db = _db.NewContext(alice, allRootFolders: false))
        {
            var result = await Controller(db, user).SetMonitorModeBulk(
                new SeriesController.BulkMonitorModeRequest([mine, theirs], "None"), default);
            Assert.IsType<OkObjectResult>(result);
        }

        using var check = _db.NewContext();
        Assert.Equal(NewChapterMonitorMode.None, check.Series.Single(s => s.Id == mine).MonitorNewItems);
        Assert.Equal(NewChapterMonitorMode.All, check.Series.Single(s => s.Id == theirs).MonitorNewItems);
    }

    [Fact]
    public async Task Move_bulk_refuses_a_destination_outside_the_callers_root_folders()
    {
        var alice = _db.SeedUser("alice", MakiPermission.EditMetadata, allRootFolders: false);
        var mine = _db.SeedSeries("Mine");
        var other = _db.SeedSeries("Other");
        Grant(alice, RootOf(mine));
        var user = new TestCurrentUser(alice, permissions: MakiPermission.EditMetadata, allRootFolders: false,
            rootFolderIds: new HashSet<int> { RootOf(mine) });
        var operations = new SeriesBulkOperations();

        using var db = _db.NewContext(alice, allRootFolders: false);
        var result = await Controller(db, user).MoveBulk(
            new SeriesController.BulkMoveSeriesRequest([mine], RootOf(other)), operations,
            Scope(alice, false), new FixedLocale(), default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task A_failing_series_is_recorded_and_the_rest_still_run()
    {
        var alice = _db.SeedUser("alice", MakiPermission.EditMetadata, allRootFolders: false);
        var active = _db.SeedSeries("Active");
        var archived = _db.SeedSeries("Archived", configure: s => s.ArchivedAt = DateTime.UtcNow);
        var hidden = _db.SeedSeries("Hidden");
        Grant(alice, RootOf(active));
        Grant(alice, RootOf(archived));
        using var services = SeriesBulkTestKit.Services(_db);
        var operations = new SeriesBulkOperations();

        // No ChapterSyncService is registered, so the active series' refresh throws.
        var operation = operations.Enqueue(SeriesBulkAction.Refresh, [active, hidden, archived],
            SeriesBulkTestKit.Caller(alice, false, RootOf(active), RootOf(archived)));
        await SeriesBulkTestKit.Worker(services, operations).RunAsync(operation, default);

        var status = operation.Snapshot();
        Assert.Equal("finished", status.State);
        Assert.Equal(3, status.Done);
        Assert.Equal(0, status.Succeeded);
        Assert.Equal(
            [(active, "error.server.unexpected"), (hidden, "error.seriesRename.notFound"), (archived, "error.series.archived")],
            status.Failures.Select(f => (f.SeriesId, f.Error)).ToArray());
    }

    [Fact]
    public async Task Losing_the_permission_or_the_account_stops_the_job_at_the_next_series()
    {
        foreach (var revoke in new Action<MakiUser>[] { u => u.Permissions = MakiPermission.None, u => u.Disabled = true })
        {
            var alice = _db.SeedUser("alice" + Guid.NewGuid().ToString("N")[..6], MakiPermission.EditMetadata);
            var first = _db.SeedSeries("First", configure: s => s.ArchivedAt = DateTime.UtcNow);
            var second = _db.SeedSeries("Second", configure: s => s.ArchivedAt = DateTime.UtcNow);
            using (var db = _db.NewContext())
            {
                revoke(db.Users.Single(u => u.Id == alice));
                db.SaveChanges();
            }

            using var services = SeriesBulkTestKit.Services(_db);
            var operations = new SeriesBulkOperations();
            var operation = operations.Enqueue(SeriesBulkAction.Refresh, [first, second],
                SeriesBulkTestKit.Caller(alice, true));
            await SeriesBulkTestKit.Worker(services, operations).RunAsync(operation, default);

            var status = operation.Snapshot();
            Assert.Equal("finished", status.State);
            Assert.Equal(1, status.Done);
            Assert.Equal([(first, "error.seriesBulk.accessRevoked")],
                status.Failures.Select(f => (f.SeriesId, f.Error)).ToArray());
        }
    }

    [Fact]
    public async Task A_root_folder_taken_away_after_queueing_hides_its_series()
    {
        var alice = _db.SeedUser("alice", MakiPermission.EditMetadata, allRootFolders: false);
        var kept = _db.SeedSeries("Kept", configure: s => s.ArchivedAt = DateTime.UtcNow);
        var taken = _db.SeedSeries("Taken", configure: s => s.ArchivedAt = DateTime.UtcNow);
        Grant(alice, RootOf(kept));
        using var services = SeriesBulkTestKit.Services(_db);
        var operations = new SeriesBulkOperations();

        // Queued while both roots were granted.
        var operation = operations.Enqueue(SeriesBulkAction.Refresh, [kept, taken],
            SeriesBulkTestKit.Caller(alice, false, RootOf(kept), RootOf(taken)));
        await SeriesBulkTestKit.Worker(services, operations).RunAsync(operation, default);

        Assert.Equal(
            [(kept, "error.series.archived"), (taken, "error.seriesRename.notFound")],
            operation.Snapshot().Failures.Select(f => (f.SeriesId, f.Error)).ToArray());
    }

    [Fact]
    public async Task A_series_already_in_the_destination_counts_as_moved()
    {
        var alice = _db.SeedUser("alice");
        var series = _db.SeedSeries("Here");
        using var services = SeriesBulkTestKit.Services(_db);
        var operations = new SeriesBulkOperations();

        var operation = operations.Enqueue(SeriesBulkAction.Move, [series], SeriesBulkTestKit.Caller(alice, true),
            rootFolderId: RootOf(series), moveFiles: true);
        await SeriesBulkTestKit.Worker(services, operations).RunAsync(operation, default);

        Assert.Equal(1, operation.Snapshot().Succeeded);
    }

    [Fact]
    public async Task A_cancelled_operation_stops_before_the_next_series()
    {
        var alice = _db.SeedUser("alice");
        var series = _db.SeedSeries("One");
        using var services = SeriesBulkTestKit.Services(_db);
        var operations = new SeriesBulkOperations();

        var operation = operations.Enqueue(SeriesBulkAction.Refresh, [series], SeriesBulkTestKit.Caller(alice, true));
        Assert.True(operations.TryCancel(operation.Id, alice));
        Assert.False(operations.TryCancel(operation.Id, alice + 1));
        await SeriesBulkTestKit.Worker(services, operations).RunAsync(operation, default);

        var status = operation.Snapshot();
        Assert.Equal("cancelled", status.State);
        Assert.Equal(0, status.Done);
    }
}

/// <summary>A worker over a <see cref="TestDb"/>, with just enough of the request scope for each series.</summary>
internal static class SeriesBulkTestKit
{
    public static ServiceProvider Services(TestDb db)
    {
        var services = new ServiceCollection();
        services.AddScoped<DataScope>();
        services.AddScoped(sp => new MakiDbContext(db.Options, sp.GetRequiredService<DataScope>()));
        services.AddScoped<CurrentUserContext>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUserContext>());
        services.AddScoped<IUserSettings, NoUserSettings>();
        services.AddSingleton<IUserLocaleResolver>(new TestUserLocaleResolver());
        services.AddScoped<RequestLocaleContext>();
        services.AddSingleton<ILocalizer>(new TestLocalizer());
        return services.BuildServiceProvider();
    }

    private sealed class NoUserSettings : IUserSettings
    {
        public int UserId => 1;
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<Dictionary<string, string>> GetManyAsync(IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, string>());
        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    public static SeriesBulkWorker Worker(ServiceProvider services, SeriesBulkOperations operations) =>
        new(operations, services.GetRequiredService<IServiceScopeFactory>(),
            new EventBroadcaster(new NoopHubContext(), services.GetRequiredService<IServiceScopeFactory>()),
            NullLogger<SeriesBulkWorker>.Instance);

    public static SeriesBulkCaller Caller(int userId, bool allRootFolders, params int[] roots) => new(
        userId, "alice", MakiPermission.EditMetadata, allRootFolders, roots.ToHashSet(), "erotica",
        ScopeUnrestricted: false, userId, allRootFolders, "en");
}
