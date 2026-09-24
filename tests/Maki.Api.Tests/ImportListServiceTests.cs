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
    private readonly FakeMetadataProvider _metadata = new();
    private readonly RecordingInbox _inbox = new();
    private readonly RecordingNotifications _notifications = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly int _rootFolderId;

    public ImportListServiceTests()
    {
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
            notifications: _notifications, locales: new TestUserLocaleResolver(), catalog: new TestLocalizer(),
            logger: NullLogger<SeriesCreationService>.Instance));
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

    private int SeedUser(string name, MakiPermission permissions, int maxPerRun = 10)
    {
        var id = _db.SeedUser(name, permissions);
        var prefs = new Dictionary<string, ImportListTrackerPrefs>
        {
            [FakeTracker.ServiceName] = new(Enabled: true, MaxPerRun: maxPerRun),
        };
        _db.SetUserConfig(id, (SettingKeys.ImportListPrefs, ImportListPrefs.Serialize(prefs)));
        return id;
    }

    private int Adder(int maxPerRun = 10) =>
        SeedUser("adder", MakiPermission.AddSeries | MakiPermission.UseTrackers, maxPerRun);

    private static RemoteListEntry Entry(string remoteId, long? mangaBaka = null, long? aniList = null) =>
        new(remoteId, ScrobbleStatus.Reading, $"Title {remoteId}", AniListId: aniList, MangaBakaId: mangaBaka);

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
        var inline = await Controller(user).Run(new ImportListsController.RunRequest(null), default);
        Assert.Equal(409, ((ObjectResult)inline).StatusCode);

        _tracker.Hold.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!_inbox.Raised.Any(r => r.Type == Maki.Core.Inbox.InboxEventType.ImportListFinished))
        {
            Assert.True(DateTime.UtcNow < deadline, "the background run never finished");
            await Task.Delay(20);
        }

        Assert.Equal(new int?[] { 101 }, LibraryIds());
        var after = await Controller(user).Run(new ImportListsController.RunRequest(null), default);
        Assert.IsType<OkObjectResult>(after);
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

        public string Name => ServiceName;
        public string Label => "Fake";
        public bool UsesOAuth => false;
        public Task<bool> ConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AuthenticatedAsync(int userId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<string?> UsernameAsync(int userId, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public async Task<IReadOnlyList<RemoteListEntry>> ListAsync(
            int userId, IReadOnlyCollection<ScrobbleStatus> statuses, CancellationToken ct = default)
        {
            if (Hold is { } hold) await hold.Task;
            return [.. Entries.Where(e => statuses.Contains(e.Status))];
        }

        public Task<RemoteEntry> GetEntryAsync(int userId, string remoteId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task UpdateAsync(int userId, string remoteId, int chapter, int volume, ScrobbleStatus status,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateRatingAsync(int userId, string remoteId, int score, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ScrobbleCandidate>> SearchAsync(int userId, string title, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public string EntryUrl(string remoteId) => remoteId;
    }

    private sealed class FakeStore() : MangaBakaLocalStore(
        new MangaBakaDumpOptions("", Path.GetTempPath()), new FakeAppSettings(), NullLogger<MangaBakaLocalStore>.Instance)
    {
        public Dictionary<long, long> AniList { get; } = [];
        public bool Available { get; set; } = true;

        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);

        public override Task<IReadOnlyDictionary<long, long>> GetIdsByExternalIdsAsync(
            ExternalSource source, IReadOnlyCollection<long> externalIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, long>>(source == ExternalSource.AniList
                ? externalIds.Where(AniList.ContainsKey).ToDictionary(id => id, id => AniList[id])
                : new Dictionary<long, long>());
    }

    private sealed class FakeMetadataProvider : IMetadataProvider
    {
        public const long Throws = 666;

        public string Name => "fake";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            providerId == Throws.ToString()
                ? throw new HttpRequestException("provider down")
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
