using Maki.Api.Controllers;
using Maki.Api.Hubs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Core.Scrobbling;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class ImportListServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "maki-importlist-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTracker _tracker = new();
    private readonly FakeStore _store = new();
    private readonly FakeMetadataProvider _metadata;
    private readonly RecordingInbox _inbox = new();
    private readonly RecordingNotifications _notifications = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly int _rootFolderId;

    public ImportListServiceTests()
    {
        _metadata = new FakeMetadataProvider(_store);
        Directory.CreateDirectory(_rootPath);
        using (var db = _db.NewContext())
        {
            var root = new RootFolder { Path = _rootPath };
            db.RootFolders.Add(root);
            db.SaveChanges();
            _rootFolderId = root.Id;
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddScoped(sp => new SeriesCreationService(
            sp.GetRequiredService<MakiDbContext>(), [_metadata],
            coverService: null!, sourceMatchService: null!, chapterSyncService: null!,
            sourceMatchQueue: new SourceMatchQueue(),
            stats: null!, identity: null!, appSettings: new FakeAppSettings(),
            naming: new NamingService(new FakeAppSettings()),
            notifications: _notifications, locales: new TestUserLocaleResolver(), catalog: new TestLocalizer(), localizer: new TestLocalizer(),
            logger: NullLogger<SeriesCreationService>.Instance));
        services.AddScoped(sp => new ShortlistService(
            sp.GetRequiredService<MakiDbContext>(), _store, new User(0, true), hidden: null!));
        services.AddScoped(sp => new SeriesRequestSubmitter(
            sp.GetRequiredService<MakiDbContext>(), [_metadata], new SilentBroadcaster(), _inbox, _notifications,
            new TestUserLocaleResolver(), new TestLocalizer(), NullLogger<SeriesRequestSubmitter>.Instance));
        _scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_rootPath, recursive: true); } catch (IOException) { }
    }

    private ImportListService? _service;

    // One instance, as in the app (a singleton): the per-user run lock lives on it.
    private ImportListService Service() => _service ??= new(
        _scopes, new FakeAppSettings(), new UserSettingsStoreService(_scopes), [_tracker], _store, _inbox,
        new TestUserLocaleResolver(), new TestLocalizer(), NullLogger<ImportListService>.Instance);

    private int SeedUser(string name, MakiPermission permissions, int maxPerRun = 10, string ceiling = "erotica")
    {
        var id = _db.SeedUser(name, permissions, maxContentRating: ceiling);
        var prefs = new Dictionary<string, ImportListTrackerPrefs>
        {
            [FakeTracker.ServiceName] = new(Enabled: true, MaxPerRun: maxPerRun),
        };
        _db.SetUserConfig(id, (SettingKeys.ImportListPrefs, ImportListPrefs.Serialize(prefs)));
        return id;
    }

    private int Adder(int maxPerRun = 10) =>
        SeedUser("adder", MakiPermission.AddSeries | MakiPermission.UseTrackers, maxPerRun);

    private static RemoteListEntry Entry(
        string remoteId, long? mangaBaka = null, long? aniList = null, string? mangaUpdates = null,
        ScrobbleStatus status = ScrobbleStatus.Reading) =>
        new(remoteId, status, $"Title {remoteId}", AniListId: aniList, MangaBakaId: mangaBaka,
            MangaUpdatesId: mangaUpdates);

    private static RemoteListEntry Planned(string remoteId, long mangaBaka) =>
        Entry(remoteId, mangaBaka, status: ScrobbleStatus.PlanToRead);

    private List<(int UserId, int MangaBakaId, string SavedFrom, string? Detail)> Shortlist()
    {
        using var db = _db.NewContext();
        return [.. db.ShortlistEntries.AsNoTracking().OrderBy(x => x.UserId).ThenBy(x => x.MangaBakaId)
            .Select(x => new { x.UserId, x.MangaBakaId, x.SavedFrom, x.SavedFromDetail }).AsEnumerable()
            .Select(x => (x.UserId, x.MangaBakaId, x.SavedFrom, x.SavedFromDetail))];
    }

    private Dictionary<string, ImportListSkipReason> Reasons()
    {
        using var db = _db.NewContext();
        return db.ImportListSkips.AsNoTracking().ToDictionary(x => x.RemoteId, x => x.Reason);
    }

    private List<int?> LibraryIds()
    {
        using var db = _db.NewContext();
        return [.. db.Series.Select(s => s.MangaBakaId).OrderBy(x => x)];
    }

    [Fact]
    public async Task Resolves_by_MangaBaka_id_and_by_AniList_id()
    {
        var user = Adder();
        _tracker.Entries = [Entry("r1", mangaBaka: 101), Entry("r2", aniList: 5001)];
        _store.AniList[5001] = 102;

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(2, result!.Added);
        Assert.Equal(new int?[] { 101, 102 }, LibraryIds());
        using var db = _db.NewContext();
        Assert.All(db.ImportListSkips.ToList(), x => Assert.Equal(ImportListSkipReason.Added, x.Reason));
        Assert.All(db.UserSeriesStates.ToList(), x => Assert.Equal(ImportListService.AddedFrom, x.AddedFrom));
        Assert.Single(_inbox.Raised, r => r.Type == Maki.Core.Inbox.InboxEventType.ImportListFinished);
    }

    [Fact]
    public async Task Resolves_a_MangaUpdates_slug_to_a_MangaBaka_id()
    {
        var user = Adder();
        _tracker.Entries = [Entry("pb8uwds", mangaUpdates: "pb8uwds"), Entry("zzzzzzz", mangaUpdates: "zzzzzzz")];
        _store.MangaUpdates["pb8uwds"] = 103;

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(1, result!.Added);
        Assert.Equal(new int?[] { 103 }, LibraryIds());
        using var db = _db.NewContext();
        Assert.Contains(db.ImportListSkips.ToList(),
            x => x.RemoteId == "zzzzzzz" && x.Reason == ImportListSkipReason.Unmatched);
    }

    [Fact]
    public async Task A_missing_dump_leaves_MangaUpdates_entries_for_a_later_tick()
    {
        var user = Adder();
        _store.Available = false;
        _tracker.Entries = [Entry("pb8uwds", mangaUpdates: "pb8uwds")];
        _store.MangaUpdates["pb8uwds"] = 103;

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.True(result!.DumpUnavailable);
        Assert.Empty(LibraryIds());
        using var db = _db.NewContext();
        Assert.Empty(db.ImportListSkips);
    }

    [Fact]
    public async Task Series_already_in_the_library_is_not_added_again()
    {
        var user = Adder();
        _db.SeedSeries("Existing", configure: s => s.MangaBakaId = 101);
        _tracker.Entries = [Entry("r1", mangaBaka: 101)];

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(0, result!.Added);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Single(LibraryIds());
        Assert.Empty(_inbox.Raised);
    }

    [Fact]
    public async Task An_archived_series_counts_as_already_in_the_library()
    {
        var user = Adder();
        _db.SeedSeries("Existing", configure: s =>
        {
            s.MangaBakaId = 101;
            s.ArchivedAt = DateTime.UtcNow;
        });
        _tracker.Entries = [Entry("r1", mangaBaka: 101)];

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(0, result!.Added);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Single(LibraryIds());
    }

    [Fact]
    public async Task A_run_is_capped_and_a_full_run_ignores_the_cap()
    {
        var user = Adder(maxPerRun: 2);
        _tracker.Entries = [.. Enumerable.Range(1, 5).Select(i => Entry($"r{i}", mangaBaka: 100 + i))];

        var capped = await Service().RunUserAsync(user, null, full: false, default);
        Assert.Equal(2, capped!.Added);
        Assert.Equal(2, LibraryIds().Count);

        var full = await Service().RunUserAsync(user, null, full: true, default);
        Assert.Equal(3, full!.Added);
        Assert.Equal(2, full.AlreadyPresent);
        Assert.Equal(5, LibraryIds().Count);
    }

    [Fact]
    public async Task A_full_run_still_caps_requests()
    {
        var user = SeedUser("reader", MakiPermission.UseTrackers, maxPerRun: 2);
        _tracker.Entries = [.. Enumerable.Range(1, 5).Select(i => Entry($"r{i}", mangaBaka: 100 + i))];

        var result = await Service().RunUserAsync(user, null, full: true, default);

        Assert.Equal(2, result!.Requested);
        using var db = _db.NewContext();
        Assert.Equal(2, db.SeriesRequests.Count());
    }

    [Fact]
    public async Task An_unmatched_row_is_retried_after_a_week()
    {
        var user = Adder();
        _tracker.Entries = [Entry("old", aniList: 5001), Entry("recent", aniList: 5002)];
        _store.AniList[5001] = 101;
        _store.AniList[5002] = 102;
        using (var db = _db.NewContext())
        {
            db.ImportListSkips.AddRange(
                new ImportListSkip
                {
                    UserId = user, Service = FakeTracker.ServiceName, RemoteId = "old", Title = "Old",
                    Reason = ImportListSkipReason.Unmatched, CreatedAt = DateTime.UtcNow.AddDays(-8),
                },
                new ImportListSkip
                {
                    UserId = user, Service = FakeTracker.ServiceName, RemoteId = "recent", Title = "Recent",
                    Reason = ImportListSkipReason.Unmatched, CreatedAt = DateTime.UtcNow.AddDays(-1),
                });
            await db.SaveChangesAsync();
        }

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(1, result!.Added);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(new int?[] { 101 }, LibraryIds());
        using var check = _db.NewContext();
        Assert.Equal(ImportListSkipReason.Added, check.ImportListSkips.Single(x => x.RemoteId == "old").Reason);
        Assert.Equal(ImportListSkipReason.Unmatched, check.ImportListSkips.Single(x => x.RemoteId == "recent").Reason);
    }

    [Fact]
    public async Task A_missing_dump_skips_the_tracker_without_an_error()
    {
        var user = Adder();
        _store.Available = false;
        _tracker.Entries = [Entry("r1", aniList: 5001)];

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(0, result!.Errors);
        Assert.True(result.DumpUnavailable);
        Assert.Empty(_inbox.Raised);
        using var db = _db.NewContext();
        Assert.Empty(db.ImportListSkips);
        var lastRun = ImportListLastRun.Parse(await new UserSettingsStoreService(_scopes)
            .GetAsync(user, SettingKeys.ImportListLastRunKey(FakeTracker.ServiceName)));
        Assert.True(lastRun!.DumpUnavailable);
    }

    [Fact]
    public async Task A_missing_dump_also_waits_on_entries_that_already_carry_a_MangaBaka_id()
    {
        var user = Adder();
        _store.Available = false;
        _tracker.Entries = [Entry("r1", mangaBaka: 101), Entry("r2", aniList: 5001)];

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(0, result!.Added);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Errors);
        Assert.True(result.DumpUnavailable);
        Assert.Empty(LibraryIds());
        using var db = _db.NewContext();
        Assert.Empty(db.ImportListSkips);
    }

    [Fact]
    public async Task A_MangaBaka_id_the_dump_does_not_have_yet_is_retried_after_a_day()
    {
        var user = Adder();
        _store.NotInDump.Add(101);
        _tracker.Entries = [Entry("r1", mangaBaka: 101)];

        var first = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(1, first!.Skipped);
        Assert.Empty(LibraryIds());
        using (var db = _db.NewContext())
        {
            var row = Assert.Single(db.ImportListSkips);
            Assert.Equal(ImportListSkipReason.Unmatched, row.Reason);
            Assert.Equal(101, row.MangaBakaId);
            row.CreatedAt = DateTime.UtcNow - ImportListService.NotInDumpRetryAfter - TimeSpan.FromMinutes(1);
            await db.SaveChangesAsync();
        }

        _store.NotInDump.Clear();
        var second = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(1, second!.Added);
        Assert.Equal(new int?[] { 101 }, LibraryIds());
    }

    [Fact]
    public async Task A_novel_on_a_MangaBaka_list_is_not_retried_daily()
    {
        var user = Adder();
        _metadata.Novels.Add(101);
        _tracker.Entries = [Entry("r1", mangaBaka: 101)];

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(1, result!.Skipped);
        using var db = _db.NewContext();
        var row = Assert.Single(db.ImportListSkips);
        Assert.Equal(ImportListSkipReason.Unmatched, row.Reason);
        Assert.Null(row.MangaBakaId);
    }

    [Fact]
    public async Task A_tracker_id_the_dump_cannot_resolve_still_waits_a_week()
    {
        var user = Adder();
        using (var db = _db.NewContext())
        {
            db.ImportListSkips.Add(new ImportListSkip
            {
                UserId = user, Service = FakeTracker.ServiceName, RemoteId = "r1", Title = "Title r1",
                Reason = ImportListSkipReason.Unmatched, CreatedAt = DateTime.UtcNow.AddDays(-2),
            });
            await db.SaveChangesAsync();
        }

        _tracker.Entries = [Entry("r1", aniList: 5001)];
        _store.AniList[5001] = 102;

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(1, result!.Skipped);
        Assert.Empty(LibraryIds());
    }

    [Fact]
    public async Task An_unresolvable_entry_is_recorded_once_as_unmatched()
    {
        var user = Adder();
        _tracker.Entries = [Entry("r1", aniList: 9999)];

        var first = await Service().RunUserAsync(user, null, full: false, default);
        var second = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(1, first!.Skipped);
        Assert.Equal(1, second!.Skipped);
        using var db = _db.NewContext();
        var row = Assert.Single(db.ImportListSkips);
        Assert.Equal(ImportListSkipReason.Unmatched, row.Reason);
        Assert.Equal("Title r1", row.Title);
        Assert.Empty(LibraryIds());
    }

    [Fact]
    public async Task A_user_who_cannot_add_series_files_a_request()
    {
        var user = SeedUser("reader", MakiPermission.UseTrackers);
        _tracker.Entries = [Entry("r1", mangaBaka: 101)];

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(0, result!.Added);
        Assert.Equal(1, result.Requested);
        Assert.Empty(LibraryIds());
        using var db = _db.NewContext();
        var request = Assert.Single(db.SeriesRequests);
        Assert.Equal(user, request.UserId);
        Assert.Equal("101", request.MetadataProviderId);
        Assert.Equal(SeriesRequestStatus.Pending, request.Status);
        Assert.Contains("notify.importList.requestNote", request.Note);

        // Pending already, so the next run does not file a second one.
        var again = await Service().RunUserAsync(user, null, full: false, default);
        Assert.Equal(0, again!.Requested);
        Assert.Single(db.SeriesRequests);
    }

    [Fact]
    public async Task A_deleted_series_is_not_added_back()
    {
        var user = Adder();
        var other = SeedUser("other", MakiPermission.AddSeries | MakiPermission.UseTrackers);
        _tracker.Entries = [Entry("r1", mangaBaka: 101)];
        await Service().RunUserAsync(user, null, full: false, default);

        // Already in the library by the time the second user's list runs: still recorded, or the
        // delete below would leave nothing to flip for them and their list would re-add it.
        var second = await Service().RunUserAsync(other, null, full: false, default);
        Assert.Equal(1, second!.AlreadyPresent);

        using (var db = _db.NewContext())
        {
            db.Series.RemoveRange(db.Series);
            await db.SaveChangesAsync();
            await ImportListService.MarkRemovedAsync(db, 101, default);
        }

        foreach (var id in new[] { user, other })
        {
            var result = await Service().RunUserAsync(id, null, full: false, default);
            Assert.Equal(0, result!.Added);
            Assert.Equal(1, result.Skipped);
        }

        Assert.Empty(LibraryIds());
        using var check = _db.NewContext();
        Assert.Equal(2, check.ImportListSkips.Count());
        Assert.All(check.ImportListSkips.ToList(), x => Assert.Equal(ImportListSkipReason.Removed, x.Reason));
    }

    [Fact]
    public async Task One_failing_entry_does_not_stop_the_others()
    {
        var user = Adder();
        _tracker.Entries = [Entry("bad", mangaBaka: FakeMetadataProvider.Throws), Entry("good", mangaBaka: 101)];

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(1, result!.Errors);
        Assert.Equal(1, result.Added);
        Assert.Equal(new int?[] { 101 }, LibraryIds());
    }

    [Fact]
    public async Task A_disabled_tracker_is_left_alone_unless_named()
    {
        var user = _db.SeedUser("adder", MakiPermission.AddSeries | MakiPermission.UseTrackers);
        _tracker.Entries = [Entry("r1", mangaBaka: 101)];

        var scheduled = await Service().RunUserAsync(user, null, full: false, default);
        Assert.Equal(0, scheduled!.Added);

        var named = await Service().RunUserAsync(user, FakeTracker.ServiceName, full: false, default);
        Assert.Equal(1, named!.Added);
    }

    [Fact]
    public async Task TickAsync_cancelled_mid_loop_does_not_stamp_LastRunAt()
    {
        var first = Adder();
        var second = SeedUser("second", MakiPermission.AddSeries | MakiPermission.UseTrackers);
        _tracker.Entries = [Entry("r1", mangaBaka: 101)];

        using var cts = new CancellationTokenSource();
        // Cancels once the loop reaches the second user, simulating a shutdown mid-tick. The first
        // user's run must still have gone through, and the tick must stop rather than swallow the
        // cancellation and stamp the gate as if it ran to completion.
        _tracker.OnList = userId =>
        {
            if (userId == second)
            {
                cts.Cancel();
            }
        };

        var appSettings = new FakeAppSettings();
        var service = new ImportListService(
            _scopes, appSettings, new UserSettingsStoreService(_scopes), [_tracker], _store, _inbox,
            new TestUserLocaleResolver(), new TestLocalizer(), NullLogger<ImportListService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TickAsync(cts.Token));

        Assert.Equal(new int?[] { 101 }, LibraryIds());
        Assert.Null(await appSettings.GetAsync(SettingKeys.ImportListLastRunAt));
    }

    // ---- controller ----

    private ImportListsController Controller(int userId, bool allRootFolders = true)
    {
        var db = _db.NewContext(userId, allRootFolders);
        var user = new User(userId, allRootFolders);
        return new ImportListsController(
            new TestLocalizer(), Service(), new UserSettingsService(db, user), db, user);
    }

    private static string? Code(IActionResult result) =>
        ((ObjectResult)result).Value?.GetType().GetProperty("code")?.GetValue(((ObjectResult)result).Value) as string;

    [Theory]
    [InlineData("nope", new[] { "Reading" }, 10, "error.importLists.unknownService")]
    [InlineData(FakeTracker.ServiceName, new string[0], 10, "error.importLists.statusesInvalid")]
    [InlineData(FakeTracker.ServiceName, new[] { "Dropped" }, 10, "error.importLists.statusesInvalid")]
    [InlineData(FakeTracker.ServiceName, new[] { "Reading" }, 0, "error.importLists.maxPerRunInvalid")]
    [InlineData(FakeTracker.ServiceName, new[] { "Reading" }, 101, "error.importLists.maxPerRunInvalid")]
    public async Task Prefs_are_validated(string service, string[] statuses, int maxPerRun, string code)
    {
        var user = _db.SeedUser("u", MakiPermission.UseTrackers);
        var result = await Controller(user).SetPrefs(
            new ImportListsController.PrefsRequest(service, true, statuses, null, MaxPerRun: maxPerRun), default);
        Assert.Equal(code, Code(result));
    }

    [Fact]
    public async Task A_root_folder_the_user_cannot_see_is_refused()
    {
        var user = _db.SeedUser("u", MakiPermission.UseTrackers, allRootFolders: false);
        var result = await Controller(user, allRootFolders: false).SetPrefs(
            new ImportListsController.PrefsRequest(FakeTracker.ServiceName, true, ["Reading"], _rootFolderId), default);
        Assert.Equal("error.importLists.rootFolderNotVisible", Code(result));
    }

    [Fact]
    public async Task Valid_prefs_are_stored_and_read_back()
    {
        var user = _db.SeedUser("u", MakiPermission.UseTrackers);
        var put = await Controller(user).SetPrefs(
            new ImportListsController.PrefsRequest(
                FakeTracker.ServiceName, true, ["completed", "Reading"], _rootFolderId, false, "mainonly", 25), default);
        Assert.IsType<NoContentResult>(put);

        var dto = (ImportListsController.ImportListsDto)((OkObjectResult)await Controller(user).Get(default)).Value!;
        var prefs = Assert.Single(dto.Trackers).Prefs;
        Assert.True(prefs.Enabled);
        Assert.Equal(new[] { "Completed", "Reading" }, prefs.Statuses);
        Assert.Equal(_rootFolderId, prefs.RootFolderId);
        Assert.False(prefs.Monitored);
        Assert.Equal("MainOnly", prefs.MonitorNewItems);
        Assert.Equal(25, prefs.MaxPerRun);
    }

    /// <summary>The detached run's last act is the inbox row, which it raises from its own thread.</summary>
    private Task RunFinished() => Wait.UntilAsync(() =>
    {
        lock (_inbox.Raised)
        {
            return _inbox.Raised.Any(r => r.Type == Maki.Core.Inbox.InboxEventType.ImportListFinished);
        }
    }, "the background run never finished");

    [Fact]
    public async Task A_full_run_starts_in_the_background_and_holds_the_lock()
    {
        var user = Adder();
        _tracker.Entries = [Entry("r1", mangaBaka: 101)];
        _tracker.Hold = new TaskCompletionSource();

        var started = await Controller(user).Run(new ImportListsController.RunRequest(null, Full: true), default);
        var accepted = Assert.IsType<AcceptedResult>(started);
        Assert.Equal(true, accepted.Value!.GetType().GetProperty("started")!.GetValue(accepted.Value));

        var again = await Controller(user).Run(new ImportListsController.RunRequest(null, Full: true), default);
        Assert.Equal(409, ((ObjectResult)again).StatusCode);
        var partial = await Controller(user).Run(new ImportListsController.RunRequest(null), default);
        Assert.Equal(409, ((ObjectResult)partial).StatusCode);

        _tracker.Hold.SetResult();
        await RunFinished();

        Assert.Equal(new int?[] { 101 }, LibraryIds());
        var after = await Controller(user).Run(new ImportListsController.RunRequest(null), default);
        Assert.IsType<AcceptedResult>(after);
    }

    [Fact]
    public async Task A_partial_manual_run_returns_at_once_and_reports_through_the_inbox()
    {
        var user = Adder();
        _tracker.Entries = [Entry("r1", mangaBaka: 101), Entry("r2", mangaBaka: 102)];
        _tracker.Hold = new TaskCompletionSource();
        await Controller(user).SetPrefs(
            new ImportListsController.PrefsRequest(FakeTracker.ServiceName, true, ["Reading"], null, MaxPerRun: 1), default);

        using var cts = new CancellationTokenSource();
        var started = await Controller(user).Run(new ImportListsController.RunRequest(null), cts.Token);
        Assert.IsType<AcceptedResult>(started);
        Assert.Empty(LibraryIds());

        cts.Cancel();
        _tracker.Hold.SetResult();
        await RunFinished();

        Assert.Equal(new int?[] { 101 }, LibraryIds());
    }

    [Fact]
    public async Task A_partial_manual_run_with_nothing_to_add_still_raises_the_inbox_row()
    {
        var user = Adder();
        _tracker.Entries = [];

        Assert.IsType<AcceptedResult>(await Controller(user).Run(new ImportListsController.RunRequest(null), default));
        await RunFinished();
    }

    [Fact]
    public async Task Skipped_rows_belong_to_their_owner()
    {
        var alice = _db.SeedUser("alice", MakiPermission.UseTrackers);
        var bob = _db.SeedUser("bob", MakiPermission.UseTrackers);
        int aliceRow, bobRow;
        using (var db = _db.NewContext())
        {
            var a = new ImportListSkip { UserId = alice, Service = "fake", RemoteId = "1", Title = "A", CreatedAt = DateTime.UtcNow };
            var b = new ImportListSkip { UserId = bob, Service = "fake", RemoteId = "1", Title = "B", CreatedAt = DateTime.UtcNow };
            db.ImportListSkips.AddRange(a, b);
            await db.SaveChangesAsync();
            (aliceRow, bobRow) = (a.Id, b.Id);
        }

        Assert.IsType<NotFoundResult>(await Controller(alice).DeleteSkipped(bobRow, default));
        Assert.IsType<NotFoundResult>(await Controller(alice).IgnoreSkipped(bobRow, default));

        var listed = (ImportListsController.ImportListsDto)((OkObjectResult)await Controller(alice).Get(default)).Value!;
        Assert.Equal(aliceRow, Assert.Single(listed.Skipped).Id);

        Assert.IsType<NoContentResult>(await Controller(alice).IgnoreSkipped(aliceRow, default));
        using (var check = _db.NewContext())
        {
            Assert.Equal(ImportListSkipReason.Ignored, check.ImportListSkips.Single(x => x.Id == aliceRow).Reason);
            Assert.Equal(ImportListSkipReason.Unmatched, check.ImportListSkips.Single(x => x.Id == bobRow).Reason);
        }

        listed = (ImportListsController.ImportListsDto)((OkObjectResult)await Controller(alice).Get(default)).Value!;
        Assert.Equal("Ignored", Assert.Single(listed.Skipped).Reason);

        Assert.IsType<NoContentResult>(await Controller(alice).DeleteSkipped(aliceRow, default));
        using (var check = _db.NewContext())
        {
            Assert.DoesNotContain(check.ImportListSkips, x => x.Id == aliceRow);
        }
    }

    [Fact]
    public async Task Added_and_removed_rows_stay_hidden()
    {
        var alice = _db.SeedUser("alice", MakiPermission.UseTrackers);
        int addedRow;
        using (var db = _db.NewContext())
        {
            var added = new ImportListSkip
            {
                UserId = alice, Service = "fake", RemoteId = "1", Title = "A", MangaBakaId = 1,
                Reason = ImportListSkipReason.Added, CreatedAt = DateTime.UtcNow,
            };
            db.ImportListSkips.AddRange(added, new ImportListSkip
            {
                UserId = alice, Service = "fake", RemoteId = "2", Title = "B", MangaBakaId = 2,
                Reason = ImportListSkipReason.Removed, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            addedRow = added.Id;
        }

        var listed = (ImportListsController.ImportListsDto)((OkObjectResult)await Controller(alice).Get(default)).Value!;
        Assert.Empty(listed.Skipped);
        Assert.IsType<NotFoundResult>(await Controller(alice).DeleteSkipped(addedRow, default));
    }

    [Fact]
    public async Task Plan_to_read_goes_to_the_shortlist_for_a_user_who_can_add_and_one_who_cannot()
    {
        var adder = Adder();
        var reader = SeedUser("reader", MakiPermission.UseTrackers);
        _tracker.Entries = [Planned("r1", 101), Planned("r2", 102)];
        _store.Catalogue(101, 102);

        var forAdder = await Service().RunUserAsync(adder, null, full: false, default);
        var forReader = await Service().RunUserAsync(reader, null, full: false, default);

        Assert.Equal(2, forAdder!.Shortlisted);
        Assert.Equal(2, forReader!.Shortlisted);
        Assert.Equal(0, forAdder.Added + forReader.Requested);
        var stored = Shortlist();
        Assert.Equal(4, stored.Count);
        Assert.All(stored, x => Assert.Equal(("importlist", "Fake"), (x.SavedFrom, x.Detail)));
        Assert.Equal(new[] { 101, 102 }, stored.Where(x => x.UserId == adder).Select(x => x.MangaBakaId));
        Assert.Equal(new[] { 101, 102 }, stored.Where(x => x.UserId == reader).Select(x => x.MangaBakaId));
        Assert.Empty(LibraryIds());
        using var db = _db.NewContext();
        Assert.Empty(db.SeriesRequests);
        Assert.All(db.ImportListSkips.ToList(), x => Assert.Equal(ImportListSkipReason.Shortlisted, x.Reason));
        Assert.Equal(4, db.ImportListSkips.Count());
    }

    [Fact]
    public async Task A_run_resolves_the_stored_keys_once_and_finds_an_entry_under_a_merged_id()
    {
        var user = Adder();
        using (var db = _db.NewContext())
        {
            db.ShortlistEntries.Add(new ShortlistEntry
            {
                UserId = user, MangaBakaId = 90, Title = "t", SavedFrom = "other",
                CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        _store.Merged[90] = 101;
        _tracker.Entries = [Planned("r1", 101), Planned("r2", 102), Planned("r3", 103)];
        _store.Catalogue(101, 102, 103);

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Single(_store.Resolved, ids => ids.Contains(90));
        Assert.Equal(2, result!.Shortlisted);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Equal(new[] { 90, 102, 103 }, Shortlist().Select(x => x.MangaBakaId));
        Assert.Equal(ImportListSkipReason.Shortlisted, Reasons()["r1"]);
    }

    [Fact]
    public async Task A_refused_shortlist_insert_is_recorded_unmatched()
    {
        var user = SeedUser("careful", MakiPermission.UseTrackers, ceiling: "safe");
        _tracker.Entries = [Planned("above", 101), Planned("novel", 102), Planned("newer", 103), Planned("ok", 104)];
        _store.Catalogue(104);
        _store.Catalogue(101, rating: "erotica");
        _store.NotInDump.Add(103);

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(1, result!.Shortlisted);
        Assert.Equal(3, result.Skipped);
        Assert.Equal(104, Assert.Single(Shortlist()).MangaBakaId);
        using var db = _db.NewContext();
        var rows = db.ImportListSkips.ToDictionary(x => x.RemoteId);
        Assert.Equal(ImportListSkipReason.Unmatched, rows["above"].Reason);
        Assert.Null(rows["above"].MangaBakaId);
        Assert.Equal(ImportListSkipReason.Unmatched, rows["novel"].Reason);
        Assert.Null(rows["novel"].MangaBakaId);
        Assert.Equal(ImportListSkipReason.Unmatched, rows["newer"].Reason);
        Assert.Equal(103, rows["newer"].MangaBakaId);
        Assert.Equal(ImportListSkipReason.Shortlisted, rows["ok"].Reason);
    }

    [Fact]
    public async Task A_user_who_can_add_but_has_no_root_folder_still_gets_the_shortlist()
    {
        var user = Adder();
        using (var db = _db.NewContext())
        {
            db.RootFolders.RemoveRange(db.RootFolders);
            await db.SaveChangesAsync();
        }

        _tracker.Entries = [Planned("r1", 101)];
        _store.Catalogue(101);

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(1, result!.Shortlisted);
        Assert.Equal(0, result.Errors);
        Assert.Equal(101, Assert.Single(Shortlist()).MangaBakaId);
    }

    [Fact]
    public async Task A_shortlist_survives_the_per_run_cap_on_everything_else()
    {
        var user = Adder(maxPerRun: 1);
        _tracker.Entries =
        [
            Planned("p1", 101), Planned("p2", 102), Planned("p3", 103),
            Entry("a1", 201), Entry("a2", 202),
        ];
        _store.Catalogue(101, 102, 103);

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(3, result!.Shortlisted);
        Assert.Equal(1, result.Added);
        Assert.Equal(3, Shortlist().Count);
    }

    [Fact]
    public async Task A_reading_duplicate_of_a_plan_to_read_entry_is_added_not_shortlisted()
    {
        var user = Adder();
        _tracker.Entries = [Planned("p", 101), Entry("r", 101)];
        _store.Catalogue(101);

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(1, result!.Added);
        Assert.Equal(0, result.Shortlisted);
        Assert.Equal(new int?[] { 101 }, LibraryIds());
        Assert.Empty(Shortlist());
        Assert.All(Reasons().Values, r => Assert.Equal(ImportListSkipReason.Added, r));
        Assert.Equal(2, Reasons().Count);
    }

    [Fact]
    public async Task A_later_reading_status_adds_a_shortlisted_entry()
    {
        var user = Adder();
        _tracker.Entries = [Planned("r1", 101)];
        _store.Catalogue(101);
        await Service().RunUserAsync(user, null, full: false, default);
        Assert.Single(Shortlist());

        var again = await Service().RunUserAsync(user, null, full: false, default);
        Assert.Equal(1, again!.AlreadyPresent);
        Assert.Equal(0, again.Added);

        _tracker.Entries = [Entry("r1", 101)];
        var started = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(1, started!.Added);
        Assert.Equal(new int?[] { 101 }, LibraryIds());
        Assert.Empty(Shortlist());
        Assert.Equal(ImportListSkipReason.Added, Reasons()["r1"]);
    }

    [Fact]
    public async Task A_later_reading_status_files_a_request_for_a_user_who_cannot_add()
    {
        var user = SeedUser("reader", MakiPermission.UseTrackers);
        _tracker.Entries = [Planned("r1", 101)];
        _store.Catalogue(101);
        await Service().RunUserAsync(user, null, full: false, default);

        _tracker.Entries = [Entry("r1", 101)];
        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(1, result!.Requested);
        using var db = _db.NewContext();
        Assert.Single(db.SeriesRequests);
        Assert.Equal(ImportListSkipReason.Added, Reasons()["r1"]);
    }

    [Fact]
    public async Task An_entry_removed_from_the_shortlist_by_hand_is_not_shortlisted_again()
    {
        var user = Adder();
        _tracker.Entries = [Planned("r1", 101)];
        _store.Catalogue(101);
        await Service().RunUserAsync(user, null, full: false, default);
        using (var db = _db.NewContext())
        {
            db.ShortlistEntries.RemoveRange(db.ShortlistEntries);
            await db.SaveChangesAsync();
        }

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(0, result!.Shortlisted);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Empty(Shortlist());
        Assert.Equal(ImportListSkipReason.Shortlisted, Reasons()["r1"]);
    }

    [Fact]
    public async Task A_full_shortlist_stops_the_run_without_writing_a_row()
    {
        var user = Adder();
        using (var db = _db.NewContext())
        {
            db.ShortlistEntries.AddRange(Enumerable.Range(0, ShortlistEntry.MaxPerUser).Select(i => new ShortlistEntry
            {
                UserId = user, MangaBakaId = 900_000 + i, Title = "t", SavedFrom = "other",
                CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            }));
            await db.SaveChangesAsync();
        }

        _tracker.Entries = [Planned("r1", 101), Planned("r2", 102)];
        _store.Catalogue(101, 102);

        var result = await Service().RunUserAsync(user, null, full: false, default);

        Assert.Equal(0, result!.Shortlisted);
        Assert.True(result.ShortlistFull);
        Assert.Empty(Reasons());
        Assert.Equal(ShortlistEntry.MaxPerUser, Shortlist().Count);
        var lastRun = ImportListLastRun.Parse(await new UserSettingsStoreService(_scopes)
            .GetAsync(user, SettingKeys.ImportListLastRunKey(FakeTracker.ServiceName)));
        Assert.True(lastRun!.ShortlistFull);
    }

    [Fact]
    public async Task A_run_that_only_shortlists_raises_the_inbox_row()
    {
        var user = Adder();
        _tracker.Entries = [Planned("r1", 101)];
        _store.Catalogue(101);

        await Service().RunUserAsync(user, null, full: false, default);

        var raised = Assert.Single(_inbox.Raised, r => r.Type == Maki.Core.Inbox.InboxEventType.ImportListFinished);
        Assert.Equal(1, raised.Message.Params!["shortlisted"]);
        var lastRun = ImportListLastRun.Parse(await new UserSettingsStoreService(_scopes)
            .GetAsync(user, SettingKeys.ImportListLastRunKey(FakeTracker.ServiceName)));
        Assert.Equal(1, lastRun!.Shortlisted);
    }

    [Fact]
    public void A_last_run_stored_before_the_shortlist_still_reads()
    {
        var parsed = ImportListLastRun.Parse(
            """{"at":"2026-01-01T00:00:00Z","added":2,"requested":1,"skipped":3,"errors":0,"dumpUnavailable":false}""");

        Assert.Equal(2, parsed!.Added);
        Assert.Equal(0, parsed.Shortlisted);
        Assert.False(parsed.ShortlistFull);
    }

    // ---- fakes ----

    private sealed class User(int userId, bool allRootFolders) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public int UserId => userId;
        public string UserName => "u";
        public MakiPermission Permissions => MakiPermission.UseTrackers;
        public bool AllRootFolders => allRootFolders;
        public IReadOnlySet<int> RootFolderIds => new HashSet<int>();
        public string MaxContentRating => "erotica";
    }

    private sealed class FakeTracker : IScrobbleTracker
    {
        public const string ServiceName = "fake";
        public List<RemoteListEntry> Entries { get; set; } = [];

        /// <summary>When set, <see cref="ListAsync"/> waits on it, so a run can be held open.</summary>
        public TaskCompletionSource? Hold { get; set; }

        /// <summary>Called with the user id at the start of every <see cref="ListAsync"/> call, so a
        /// test can cancel the run's token from inside the loop it is iterating.</summary>
        public Action<int>? OnList { get; set; }

        public string Name => ServiceName;
        public string Label => "Fake";
        public bool UsesOAuth => false;
        public Task<bool> ConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AuthenticatedAsync(int userId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<string?> UsernameAsync(int userId, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public async Task<IReadOnlyList<RemoteListEntry>> ListAsync(
            int userId, IReadOnlyCollection<ScrobbleStatus> statuses, CancellationToken ct = default)
        {
            OnList?.Invoke(userId);
            ct.ThrowIfCancellationRequested();
            if (Hold is { } hold) await hold.Task;
            return [.. Entries.Where(e => statuses.Contains(e.Status))];
        }

        public Task<RemoteEntry> GetEntryAsync(int userId, string remoteId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task UpdateAsync(int userId, string remoteId, int chapter, int volume, ScrobbleStatus status,
            CancellationToken ct = default, bool keepStatus = false) => throw new NotSupportedException();
        public Task UpdateRatingAsync(int userId, string remoteId, int score, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(int userId, string title, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public string EntryUrl(string remoteId) => remoteId;
    }

    private sealed class FakeStore() : MangaBakaLocalStore(
        new MangaBakaDumpOptions("", Path.GetTempPath()), NullLogger<MangaBakaLocalStore>.Instance)
    {
        public Dictionary<long, long> AniList { get; } = [];
        public Dictionary<string, long> MangaUpdates { get; } = [];
        public bool Available { get; set; } = true;

        /// <summary>Ids the dump has no row for: series newer than the snapshot.</summary>
        public HashSet<long> NotInDump { get; } = [];

        /// <summary>Merged ids and the canonical id each points at.</summary>
        public Dictionary<long, long> Merged { get; } = [];

        public int ResolveCalls { get; private set; }
        public List<IReadOnlyCollection<long>> Resolved { get; } = [];

        public override Task<IReadOnlyDictionary<long, long>> ResolveCanonicalIdsAsync(
            IReadOnlyCollection<long> ids, CancellationToken ct = default)
        {
            ResolveCalls++;
            Resolved.Add([.. ids]);
            return Task.FromResult<IReadOnlyDictionary<long, long>>(ids
                .Where(id => Merged.ContainsKey(id) || _catalogue.ContainsKey(id))
                .ToDictionary(id => id, id => Merged.GetValueOrDefault(id, id)));
        }

        private readonly Dictionary<long, string> _catalogue = [];

        /// <summary>Ids the shortlist insert can find, each with the content rating the dump gives it.</summary>
        public void Catalogue(params long[] ids) => Catalogue(ids, "safe");

        public void Catalogue(long id, string rating) => Catalogue([id], rating);

        private void Catalogue(IEnumerable<long> ids, string rating)
        {
            foreach (var id in ids) _catalogue[id] = rating;
        }

        public override Task<IReadOnlyDictionary<long, MangaBakaShortlistRow>> GetShortlistRowsAsync(
            IReadOnlyCollection<long> ids, IReadOnlyList<string>? contentRatings, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, MangaBakaShortlistRow>>(ids
                .Where(id => _catalogue.TryGetValue(id, out var rating) &&
                             (contentRatings is null || contentRatings.Contains(rating)))
                .ToDictionary(id => id, id => new MangaBakaShortlistRow(id, "active",
                    new MangaBakaRecommendation(id.ToString(), $"Series {id}", null, 2020, null, SeriesStatus.Ongoing,
                        null, 10, [], [], false, null, null))));

        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);

        public override Task<bool> HasSeriesAsync(long id, CancellationToken ct = default) =>
            Task.FromResult(Available && !NotInDump.Contains(id));

        public override Task<IReadOnlyDictionary<string, long>> GetIdsByMangaUpdatesIdsAsync(
            IReadOnlyCollection<string> slugs, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, long>>(
                slugs.Select(s => s.ToLowerInvariant()).Where(MangaUpdates.ContainsKey)
                    .Distinct().ToDictionary(s => s, s => MangaUpdates[s]));

        public override Task<IReadOnlyDictionary<long, long>> GetIdsByExternalIdsAsync(
            ExternalSource source, IReadOnlyCollection<long> externalIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, long>>(source == ExternalSource.AniList
                ? externalIds.Where(AniList.ContainsKey).ToDictionary(id => id, id => AniList[id])
                : new Dictionary<long, long>());
    }

    /// <summary>
    /// Answers like MangaBakaProvider: nothing without the dump, nothing for an id it lacks, and
    /// nothing for a novel, which the dump does have.
    /// </summary>
    private sealed class FakeMetadataProvider(FakeStore store) : IMetadataProvider
    {
        public const long Throws = 666;

        public HashSet<long> Novels { get; } = [];

        public string Name => "fake";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            providerId == Throws.ToString()
                ? throw new HttpRequestException("provider down")
                : !store.Available || store.NotInDump.Contains(long.Parse(providerId)) ||
                  Novels.Contains(long.Parse(providerId))
                ? Task.FromResult<SeriesMetadata?>(null)
                : Task.FromResult<SeriesMetadata?>(new SeriesMetadata
                {
                    ProviderId = providerId,
                    Title = $"Series {providerId}",
                    MangaBakaId = int.Parse(providerId),
                    Year = 2020,
                });
    }

    private sealed class SilentBroadcaster() : EventBroadcaster(null!, null!)
    {
        public override Task SeriesRequested(int requestId, string title, string requestedBy) => Task.CompletedTask;
    }
}
