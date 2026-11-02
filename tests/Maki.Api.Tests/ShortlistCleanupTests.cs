using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// What removes or moves a shortlist entry besides the reader: adding the title, and the re-key that
/// follows a dump merging one work into another.
/// </summary>
public sealed class ShortlistCleanupTests : IDisposable
{
    private readonly TestDb _db = new TestDb().WithDistinctSeriesIds();
    private readonly DumpDbBuilder _dump = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maki-shortlist-add-" + Guid.NewGuid().ToString("N"));
    private readonly int _rootFolderId;

    // Ids no other test class uses: the provider-id lock is process-wide.
    private const int Canonical = 7_301_000;
    private const int Merged = 7_301_001;
    private const int MergedToo = 7_301_002;
    private const int OtherCanonical = 7_301_010;
    private const int OtherMerged = 7_301_011;

    public ShortlistCleanupTests()
    {
        Directory.CreateDirectory(_root);
        using var db = _db.NewContext();
        var root = new RootFolder { Path = _root };
        db.RootFolders.Add(root);
        db.SaveChanges();
        _rootFolderId = root.Id;

        _dump.AddSeries(Canonical, "Canonical")
            .AddSeries(Merged, "Merged", state: "merged", mergedWith: Canonical.ToString(), contentRating: null)
            .AddSeries(MergedToo, "Merged too", state: "merged", mergedWith: Canonical.ToString(), contentRating: null)
            .AddSeries(OtherCanonical, "Other")
            .AddSeries(OtherMerged, "Other merged", state: "merged", mergedWith: OtherCanonical.ToString(), contentRating: null);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dump.Dispose();
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Store(int userId, int mangaBakaId, DateTime? created = null)
    {
        using var db = _db.NewContext();
        db.ShortlistEntries.Add(new ShortlistEntry
        {
            UserId = userId, MangaBakaId = mangaBakaId, Title = "t", SavedFrom = "other",
            CreatedAtUtc = created ?? DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    private List<(int UserId, int MangaBakaId)> Entries()
    {
        using var db = _db.NewContext();
        return db.ShortlistEntries.AsNoTracking().OrderBy(x => x.UserId).ThenBy(x => x.MangaBakaId)
            .Select(x => new { x.UserId, x.MangaBakaId }).AsEnumerable()
            .Select(x => (x.UserId, x.MangaBakaId)).ToList();
    }

    private SeriesCreationService Creation(MakiDbContext db) => new(
        db, [new Provider()],
        coverService: null!, sourceMatchService: null!, chapterSyncService: null!,
        sourceMatchQueue: new SourceMatchQueue(),
        stats: null!, identity: null!, appSettings: new FakeAppSettings(),
        naming: new NamingService(new FakeAppSettings()),
        notifications: new RecordingNotifications(), locales: new TestUserLocaleResolver(),
        catalog: new TestLocalizer(), localizer: new TestLocalizer(), logger: NullLogger<SeriesCreationService>.Instance);

    private sealed class Provider : IMetadataProvider
    {
        public string Name => "stub";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(new SeriesMetadata
            {
                ProviderId = providerId, Title = "Title " + providerId, MangaBakaId = int.Parse(providerId),
            });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Full")]
    public async Task An_add_removes_the_adders_entry_and_leaves_everyone_elses(string? incognito)
    {
        var adder = _db.SeedUser("adder", MakiPermission.AddSeries);
        var other = _db.SeedUser("other");
        Store(adder, Canonical);
        Store(other, Canonical);

        using var db = _db.NewContext(adder);
        var result = await Creation(db).CreateAsync(Canonical.ToString(), _rootFolderId, monitored: false, null,
            default, deferSourceMatching: true, incognito: incognito, attributedUserId: adder);

        Assert.Null(result.Error);
        Assert.Equal([(other, Canonical)], Entries());
    }

    [Fact]
    public async Task Approving_a_request_under_the_admins_scope_removes_the_requesters_entry()
    {
        var admin = _db.SeedUser("admin");
        var requester = _db.SeedUser("requester", MakiPermission.None);
        Store(requester, Canonical);
        Store(admin, Canonical);

        using var db = _db.NewContext(admin);
        var result = await Creation(db).CreateAsync(Canonical.ToString(), _rootFolderId, monitored: false, null,
            default, deferSourceMatching: true, attributedUserId: requester, addedFrom: "request");

        Assert.Null(result.Error);
        Assert.Equal([(admin, Canonical)], Entries());
    }

    [Fact]
    public async Task A_failed_insert_keeps_the_entry()
    {
        var adder = _db.SeedUser("adder", MakiPermission.AddSeries);
        Store(adder, Canonical);
        var scope = new DataScope();
        scope.SetUser(adder, allRootFolders: true);
        var options = new DbContextOptionsBuilder<MakiDbContext>(_db.Options)
            .AddInterceptors(new FailSeriesInsert()).Options;

        using var db = new MakiDbContext(options, scope);
        await Assert.ThrowsAsync<DbUpdateException>(() => Creation(db).CreateAsync(Canonical.ToString(), _rootFolderId,
            monitored: false, null, default, deferSourceMatching: true, attributedUserId: adder));

        Assert.Equal([(adder, Canonical)], Entries());
        using var verify = _db.NewContext();
        Assert.False(await verify.Series.AnyAsync(s => s.MangaBakaId == Canonical));
    }

    [Fact]
    public async Task An_add_by_the_canonical_id_leaves_a_merged_key_entry_reading_as_in_the_library()
    {
        var adder = _db.SeedUser("adder", MakiPermission.AddSeries);
        Store(adder, Merged);

        using (var db = _db.NewContext(adder))
        {
            Assert.Null((await Creation(db).CreateAsync(Canonical.ToString(), _rootFolderId, monitored: false, null,
                default, deferSourceMatching: true, attributedUserId: adder)).Error);
        }

        Assert.Equal([(adder, Merged)], Entries());
        using var scoped = _db.NewContext(adder);
        var store = new MangaBakaLocalStore(new MangaBakaDumpOptions(_dump.Path, Path.GetTempPath()),
            NullLogger<MangaBakaLocalStore>.Instance);
        var hidden = new HiddenContentService(new NoSettings(adder), null!, null!, store);
        var list = await new ShortlistService(scoped, store, new TestCurrentUser(adder), hidden).ListAsync();
        var item = Assert.Single(list.Entries);
        Assert.Equal(Merged, item.MangaBakaId);
        Assert.NotNull(item.Library?.SeriesId);
    }

    private ShortlistMergeService Merge(IServiceScopeFactory? scopes = null) => new(
        scopes ?? _db.ScopeFactory(),
        new MangaBakaLocalStore(new MangaBakaDumpOptions(_dump.Path, Path.GetTempPath()), NullLogger<MangaBakaLocalStore>.Instance),
        NullLogger<ShortlistMergeService>.Instance);

    [Fact]
    public async Task The_rekey_moves_a_merged_entry_and_keeps_the_oldest_of_two()
    {
        var one = _db.SeedUser("one");
        var two = _db.SeedUser("two");
        Store(one, Merged);
        Store(two, Merged, created: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        Store(two, Canonical, created: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Store(two, MergedToo, created: new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, await Merge().RekeyAsync());

        Assert.Equal([(one, Canonical), (two, Canonical)], Entries());
        using var db = _db.NewContext();
        var kept = await db.ShortlistEntries.AsNoTracking().SingleAsync(x => x.UserId == two);
        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), kept.CreatedAtUtc);
        Assert.Equal(0, await Merge().RekeyAsync());
    }

    [Fact]
    public async Task The_rekey_reaches_merged_keys_past_the_first_ten_thousand()
    {
        var one = _db.SeedUser("one");
        var two = _db.SeedUser("two");
        using (var db = _db.NewContext())
        {
            db.ShortlistEntries.AddRange(Enumerable.Range(1, 10_000).Select(i => new ShortlistEntry
            {
                UserId = one, MangaBakaId = i, Title = "t", SavedFrom = "other",
                CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow,
            }));
            db.SaveChanges();
        }

        Store(two, Merged);

        Assert.Equal(1, await Merge().RekeyAsync());
        Assert.Contains((two, Canonical), Entries());
    }

    [Fact]
    public async Task Canonical_resolution_follows_every_id_past_one_inline_chunk()
    {
        var store = new MangaBakaLocalStore(
            new MangaBakaDumpOptions(_dump.Path, Path.GetTempPath()), NullLogger<MangaBakaLocalStore>.Instance);
        var ids = Enumerable.Range(1, 10_000).Select(i => (long)i).Append(Merged).Append(Canonical).ToList();

        var resolved = await store.ResolveCanonicalIdsAsync(ids);

        Assert.Equal(2, resolved.Count);
        Assert.Equal(Canonical, resolved[Merged]);
        Assert.Equal(Canonical, resolved[Canonical]);
    }

    [Fact]
    public async Task The_rekey_leaves_an_entry_with_no_dump_row_or_no_merge_alone()
    {
        var one = _db.SeedUser("one");
        Store(one, Canonical);
        Store(one, 7_301_999);

        Assert.Equal(0, await Merge().RekeyAsync());

        Assert.Equal([(one, Canonical), (one, 7_301_999)], Entries());
    }

    [Fact]
    public async Task A_save_landing_between_the_scan_and_the_lock_is_folded_in_not_collided_with()
    {
        var one = _db.SeedUser("one");
        Store(one, Merged, created: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        Task<int> run;
        using (await SeriesLocks.ProviderIdAsync(Canonical, default))
        {
            run = Merge().RekeyAsync();
            await Wait.UntilAsync(() => run.IsCompleted || SeriesLocks.ProviderIdUsers(Canonical) >= 2,
                "the rekey never reached the canonical id's lock");
            Store(one, Canonical);
        }

        Assert.Equal(1, await run);
        Assert.Equal([(one, Canonical)], Entries());
        using var db = _db.NewContext();
        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            (await db.ShortlistEntries.AsNoTracking().SingleAsync()).CreatedAtUtc);
    }

    [Fact]
    public async Task A_failed_group_does_not_stop_the_others()
    {
        var one = _db.SeedUser("one");
        Store(one, Merged);
        Store(one, OtherMerged);
        var failing = new FailFirstSave();
        var options = new DbContextOptionsBuilder<MakiDbContext>(_db.Options).AddInterceptors(failing).Options;
        var services = new ServiceCollection();
        services.AddScoped(_ => new MakiDbContext(options));
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        Assert.Equal(1, await Merge(scopes).RekeyAsync());

        var entries = Entries();
        Assert.Equal(2, entries.Count);
        Assert.Single(entries, e => e.MangaBakaId is Canonical or OtherCanonical);
        Assert.Equal(1, await Merge().RekeyAsync());
        Assert.All(Entries(), e => Assert.Contains(e.MangaBakaId, new[] { Canonical, OtherCanonical }));
    }

    private sealed class FailSeriesInsert : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Series>().Any(e => e.State == EntityState.Added))
            {
                throw new DbUpdateException("insert refused");
            }

            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    private sealed class FailFirstSave : SaveChangesInterceptor
    {
        private int _saves;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _saves) == 1)
            {
                throw new DbUpdateException("first group refused");
            }

            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    private sealed class NoSettings(int userId) : Maki.Core.Configuration.IUserSettings
    {
        public int UserId => userId;

        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<Dictionary<string, string>> GetManyAsync(
            IReadOnlyCollection<string> keys, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, string>());

        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }
}
