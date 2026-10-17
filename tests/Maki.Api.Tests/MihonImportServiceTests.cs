using System.Data.Common;
using Maki.Api.Auth;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public sealed class MihonImportServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "maki-mihon-" + Guid.NewGuid().ToString("N"));
    private readonly string _otherRootPath = Path.Combine(Path.GetTempPath(), "maki-mihon-other-" + Guid.NewGuid().ToString("N"));
    private readonly FakeStore _store = new();
    private readonly FakeMetadataProvider _metadata = new();
    private readonly ReadingProgressGate _gate = new();
    private readonly int _rootId;
    private readonly int _otherRootId;

    public MihonImportServiceTests()
    {
        Directory.CreateDirectory(_rootPath);
        Directory.CreateDirectory(_otherRootPath);
        using var db = _db.NewContext();
        var root = new RootFolder { Path = _rootPath };
        var other = new RootFolder { Path = _otherRootPath };
        db.RootFolders.AddRange(root, other);
        db.SaveChanges();
        (_rootId, _otherRootId) = (root.Id, other.Id);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var path in new[] { _rootPath, _otherRootPath })
        {
            try { Directory.Delete(path, recursive: true); } catch (IOException) { }
        }
    }

    private const MakiPermission Importer = MakiPermission.ImportLibrary | MakiPermission.AddSeries;

    /// <summary>A user granted only the first root folder unless <paramref name="allRootFolders"/>.</summary>
    private int SeedUser(MakiPermission permissions = Importer | MakiPermission.ManageTags, bool allRootFolders = false)
    {
        var id = _db.SeedUser("importer" + Guid.NewGuid().ToString("N")[..6], permissions, allRootFolders);
        using var db = _db.NewContext();
        db.UserRootFolders.Add(new UserRootFolder { UserId = id, RootFolderId = _rootId });
        db.SaveChanges();
        return id;
    }

    private (MihonImportService Service, MakiDbContext Db) Build(
        int userId, MakiPermission permissions = Importer | MakiPermission.ManageTags, bool allRootFolders = false)
    {
        var db = _db.NewContext(userId, allRootFolders);
        var user = new CurrentUserContext();
        user.Set(userId, "importer", permissions, allRootFolders,
            allRootFolders ? new HashSet<int>() : new HashSet<int> { _rootId }, "erotica");
        var creation = new SeriesCreationService(
            db, [_metadata],
            coverService: null!, sourceMatchService: null!, chapterSyncService: null!,
            sourceMatchQueue: new SourceMatchQueue(),
            stats: null!, identity: null!, appSettings: new FakeAppSettings(),
            naming: new NamingService(new FakeAppSettings()),
            notifications: new RecordingNotifications(), locales: new TestUserLocaleResolver(),
            catalog: new TestLocalizer(), localizer: new TestLocalizer(),
            logger: NullLogger<SeriesCreationService>.Instance);
        var service = new MihonImportService(
            db, _store, [_metadata], creation, new TagService(db),
            new ReadingProgressService(db, _gate, NullLogger<ReadingProgressService>.Instance),
            new PendingReadImportService(_db.ScopeFactory(), NullLogger<PendingReadImportService>.Instance),
            user, new TestLocalizer(), NullLogger<MihonImportService>.Instance);
        return (service, db);
    }

    private static MihonResolveItem Row(
        int index, string title = "Unknown", string? mangaBaka = null, int? aniList = null, int? mal = null, int? kitsu = null) =>
        new(index, title, null, aniList, mal, kitsu, mangaBaka);

    private MihonImportRequest Request(params MihonImportItem[] items) =>
        new("run-1", _rootId, Monitored: true, MonitorNewItems: null, ApplyTags: true, ApplyReadState: true, [.. items]);

    private static MihonImportItem Item(
        string providerId, string[]? categories = null, decimal[]? read = null, decimal? trackerLastRead = null) =>
        new(providerId, [.. categories ?? []], [.. read ?? []], trackerLastRead);

    private int SeedSeries(int mangaBakaId, int rootFolderId, string title = "Existing")
    {
        using var db = _db.NewContext();
        var series = new Series
        {
            Title = title, SortTitle = title.ToLowerInvariant(), FolderName = title + mangaBakaId,
            RootFolderId = rootFolderId, MangaBakaId = mangaBakaId, Added = DateTime.UtcNow,
        };
        db.Series.Add(series);
        db.SaveChanges();
        return series.Id;
    }

    // Resolve

    [Fact]
    public async Task Resolve_prefers_the_MangaBaka_id_then_AniList_then_MAL_then_Kitsu()
    {
        _store.AniList[5001] = 201;
        _store.Mal[6001] = 202;
        _store.Mal[6002] = 203;
        _store.Kitsu[7001] = 204;
        var (service, _) = Build(SeedUser());

        var results = await service.ResolveAsync(
        [
            Row(0, mangaBaka: "101", aniList: 5001),
            Row(1, aniList: 5001, mal: 6001),
            Row(2, aniList: 9999, mal: 6002, kitsu: 7001),
            Row(3, kitsu: 7001),
        ], default);

        Assert.Equal(["101", "201", "203", "204"], results.Select(r => r.Match!.ProviderId));
        Assert.Equal(["mangabaka", "anilist", "mal", "kitsu"], results.Select(r => r.MatchedBy));
        Assert.All(results, r => Assert.Equal("matched", r.Status));
        Assert.All(results, r => Assert.Empty(r.Candidates));
    }

    [Fact]
    public async Task Resolve_follows_a_merged_MangaBaka_id_and_falls_through_a_novel()
    {
        _store.Merged["300"] = "301";
        _store.Novels.Add("400");
        _store.AniList[5001] = 402;
        var (service, _) = Build(SeedUser());

        var results = await service.ResolveAsync(
            [Row(0, mangaBaka: "300"), Row(1, mangaBaka: "400", aniList: 5001)], default);

        Assert.Equal("301", results[0].Match!.ProviderId);
        Assert.Equal("mangabaka", results[0].MatchedBy);
        Assert.Equal("402", results[1].Match!.ProviderId);
        Assert.Equal("anilist", results[1].MatchedBy);
    }

    [Fact]
    public async Task Resolve_by_title_accepts_a_single_strong_match_including_an_alt_title()
    {
        _metadata.Search["Shingeki no Kyojin"] = [Hit("501", "Attack on Titan"), Hit("502", "Attack on Titan: Junior High")];
        _store.AltTitles["501"] = ["Shingeki no Kyojin"];
        var (service, _) = Build(SeedUser());

        var result = Assert.Single(await service.ResolveAsync([Row(0, "Shingeki no Kyojin")], default));

        Assert.Equal("matched", result.Status);
        Assert.Equal("title", result.MatchedBy);
        Assert.Equal("501", result.Match!.ProviderId);
    }

    [Fact]
    public async Task Resolve_by_title_asks_for_review_on_a_tie_or_a_weak_match_and_reports_nothing_found()
    {
        _metadata.Search["Monster"] = [Hit("601", "Monster"), Hit("602", "Monster")];
        _metadata.Search["Blue Lock"] = [Hit("603", "Blue Locker Room"), Hit("604", "Blue Period")];
        var (service, _) = Build(SeedUser());

        var results = await service.ResolveAsync(
            [Row(0, "Monster"), Row(1, "Blue Lock"), Row(2, "Nothing Like It")], default);

        Assert.Equal(["needsReview", "needsReview", "notFound"], results.Select(r => r.Status));
        Assert.Equal(["601", "602"], results[0].Candidates.Select(c => c.ProviderId));
        Assert.Equal(["603", "604"], results[1].Candidates.Select(c => c.ProviderId));
        Assert.Empty(results[2].Candidates);
        Assert.All(results, r => Assert.Null(r.Match));
        Assert.All(results, r => Assert.Null(r.MatchedBy));
    }

    [Fact]
    public async Task Resolve_offers_at_most_five_candidates()
    {
        _metadata.Search["Same"] = [.. Enumerable.Range(1, 8).Select(i => Hit($"70{i}", "Same"))];
        var (service, _) = Build(SeedUser());

        var result = Assert.Single(await service.ResolveAsync([Row(0, "Same")], default));

        Assert.Equal("needsReview", result.Status);
        Assert.Equal(MihonImportService.CandidateLimit, result.Candidates.Count);
    }

    [Fact]
    public async Task Resolve_reports_a_visible_copy_with_its_id_and_a_hidden_one_without()
    {
        var visibleId = SeedSeries(101, _rootId);
        SeedSeries(102, _otherRootId);
        var (service, _) = Build(SeedUser());

        var results = await service.ResolveAsync(
            [Row(0, mangaBaka: "101"), Row(1, mangaBaka: "102"), Row(2, mangaBaka: "103")], default);

        Assert.Equal(["visible", "elsewhere", "none"], results.Select(r => r.Library));
        Assert.Equal([visibleId, null, null], results.Select(r => r.SeriesId));
    }

    // Import

    [Fact]
    public async Task Import_creates_a_deferred_series_recorded_as_added_from_mihon()
    {
        var user = SeedUser();
        var (service, _) = Build(user);

        var result = Assert.Single(await service.ImportAsync(Request(Item("101")), default));

        Assert.Equal("created", result.Outcome);
        Assert.Null(result.Reason);
        using var db = _db.NewContext();
        var series = db.Series.Single();
        Assert.Equal(result.SeriesId, series.Id);
        Assert.Equal(101, series.MangaBakaId);
        Assert.Equal(_rootId, series.RootFolderId);
        Assert.True(series.SourceMatchPending);
        var state = db.UserSeriesStates.IgnoreQueryFilters().Single();
        Assert.Equal(user, state.UserId);
        Assert.Equal(MihonImportService.AddedFrom, state.AddedFrom);
        var receipt = db.RecommendationMutationReceipts.IgnoreQueryFilters().Single();
        Assert.Equal(MihonImportService.MutationId(user, "run-1", "101"), receipt.ClientMutationId);
    }

    [Fact]
    public async Task Import_routes_a_work_already_in_the_library_to_that_series()
    {
        var existing = SeedSeries(101, _rootId);
        var (service, _) = Build(SeedUser());

        var result = Assert.Single(await service.ImportAsync(Request(Item("101", ["Reading"])), default));

        Assert.Equal("existing", result.Outcome);
        Assert.Equal(existing, result.SeriesId);
        using var db = _db.NewContext();
        Assert.Single(db.Series);
        Assert.Equal(existing, db.SeriesTags.Single().SeriesId);
    }

    [Fact]
    public async Task Import_skips_a_work_that_only_exists_in_a_folder_the_caller_cannot_see()
    {
        SeedSeries(101, _otherRootId);
        var (service, _) = Build(SeedUser());

        var result = Assert.Single(await service.ImportAsync(Request(Item("101", ["Reading"], [1m])), default));

        Assert.Equal("skipped", result.Outcome);
        Assert.Null(result.SeriesId);
        Assert.Contains("error.mihon.inLibraryElsewhere", result.Reason);
        using var db = _db.NewContext();
        Assert.Single(db.Series);
        Assert.Empty(db.Tags);
        Assert.Empty(db.PendingReadImports);
        Assert.Empty(db.ReadingStates);
    }

    [Fact]
    public async Task Import_prefers_a_visible_copy_when_a_hidden_one_was_added_first()
    {
        SeedSeries(101, _otherRootId);
        var visible = SeedSeries(101, _rootId);
        var (service, _) = Build(SeedUser());

        var result = Assert.Single(await service.ImportAsync(Request(Item("101")), default));

        Assert.Equal("existing", result.Outcome);
        Assert.Equal(visible, result.SeriesId);
    }

    [Fact]
    public async Task Import_tolerates_duplicates_in_one_batch()
    {
        // 401 is a merged id the provider resolves to 400.
        _metadata.Canonical["401"] = 400;
        var (service, _) = Build(SeedUser());

        var results = await service.ImportAsync(Request(Item("400"), Item("400"), Item("401")), default);

        Assert.Equal(["created", "created", "existing"], results.Select(r => r.Outcome));
        Assert.Single(results.Select(r => r.SeriesId).Distinct());
        using var db = _db.NewContext();
        Assert.Single(db.Series);
    }

    [Fact]
    public async Task Import_refuses_a_root_folder_the_caller_was_not_granted()
    {
        var (service, _) = Build(SeedUser());
        var controller = new MihonImportController(new TestLocalizer(), service);

        var response = await controller.Import(Request(Item("101")) with { RootFolderId = _otherRootId }, default);

        Assert.IsType<BadRequestObjectResult>(response);
        Assert.False(await service.CanUseRootFolderAsync(_otherRootId, default));
        Assert.False(await service.CanUseRootFolderAsync(9999, default));
        Assert.True(await service.CanUseRootFolderAsync(_rootId, default));
        using var db = _db.NewContext();
        Assert.Empty(db.Series);
    }

    [Fact]
    public async Task Every_endpoint_refuses_while_the_dump_is_missing()
    {
        _store.Available = false;
        var (service, _) = Build(SeedUser());
        var controller = new MihonImportController(new TestLocalizer(), service);

        Assert.IsType<ConflictObjectResult>(await controller.Resolve(new MihonResolveRequest([Row(0)]), default));
        Assert.IsType<ConflictObjectResult>(await controller.Import(Request(Item("101")), default));
    }

    [Fact]
    public async Task A_batch_over_the_limit_is_refused()
    {
        var (service, _) = Build(SeedUser());
        var controller = new MihonImportController(new TestLocalizer(), service);
        var rows = Enumerable.Range(0, MihonImportController.MaxItemsPerRequest + 1).Select(i => Row(i)).ToList();
        var items = Enumerable.Range(0, MihonImportController.MaxItemsPerRequest + 1).Select(i => Item($"{i + 1}")).ToArray();

        Assert.IsType<BadRequestObjectResult>(await controller.Resolve(new MihonResolveRequest(rows), default));
        Assert.IsType<BadRequestObjectResult>(await controller.Import(Request(items), default));
    }

    // Tags

    [Fact]
    public async Task Tags_are_added_without_removing_any_and_reuse_labels_case_insensitively()
    {
        var existing = SeedSeries(101, _rootId);
        int keep;
        using (var db = _db.NewContext())
        {
            var tag = new Tag { Label = "Keep" };
            db.Tags.AddRange(tag, new Tag { Label = "Action", Color = "red" });
            db.SaveChanges();
            keep = tag.Id;
            db.SeriesTags.Add(new SeriesTag { SeriesId = existing, TagId = keep });
            db.SaveChanges();
        }

        var (service, _) = Build(SeedUser());
        await service.ImportAsync(Request(Item("101", ["Favourites", "favourites", " action ", ""])), default);

        using var check = _db.NewContext();
        Assert.Equal(["Action", "Favourites", "Keep"], check.Tags.Select(t => t.Label).OrderBy(l => l).ToList());
        var linked = check.SeriesTags.Where(x => x.SeriesId == existing).Select(x => x.TagId).ToList();
        Assert.Equal(3, linked.Count);
        Assert.Contains(keep, linked);
        Assert.Equal("red", check.Tags.Single(t => t.Label == "Action").Color);
    }

    [Fact]
    public async Task Tags_need_the_ManageTags_permission()
    {
        var user = SeedUser(Importer);
        var (service, _) = Build(user, Importer);

        var result = Assert.Single(await service.ImportAsync(Request(Item("101", ["Favourites"])), default));

        Assert.Equal("created", result.Outcome);
        using var db = _db.NewContext();
        Assert.Empty(db.Tags);
        Assert.Empty(db.SeriesTags);
    }

    [Fact]
    public async Task Tags_are_left_alone_when_the_caller_turns_them_off()
    {
        var (service, _) = Build(SeedUser());

        await service.ImportAsync(Request(Item("101", ["Favourites"])) with { ApplyTags = false }, default);

        using var db = _db.NewContext();
        Assert.Empty(db.Tags);
    }

    [Fact]
    public async Task A_tag_created_by_someone_else_mid_insert_is_reused()
    {
        var competitor = new CompetingTagInsert("action");
        var options = new DbContextOptionsBuilder<MakiDbContext>(_db.Options).AddInterceptors(competitor).Options;
        using var db = new MakiDbContext(options);

        var (tag, created) = await new TagService(db).FindOrCreateAsync("Action", null, default);

        Assert.True(competitor.Fired);
        Assert.False(created);
        using var check = _db.NewContext();
        Assert.Equal(check.Tags.Single().Id, tag.Id);
        Assert.Equal("action", tag.Label);
    }

    // Read state

    [Fact]
    public async Task Read_numbers_raise_the_silent_mark_to_the_highest_one()
    {
        var user = SeedUser();
        var (service, _) = Build(user);

        await service.ImportAsync(Request(Item("101", read: [1m, 2m, 5m], trackerLastRead: 10m)), default);

        using var db = _db.NewContext();
        var state = db.ReadingStates.IgnoreQueryFilters().Single();
        Assert.Equal(user, state.UserId);
        Assert.Equal(5, state.MaxChapter);
        Assert.Empty(db.StatsEvents.Where(e => e.UserId == user));
        var pending = db.PendingReadImports.IgnoreQueryFilters().Single();
        Assert.Equal(user, pending.UserId);
        Assert.Equal([1m, 2m, 5m], PendingReadImportService.Parse(pending.NumbersJson));
    }

    [Fact]
    public async Task The_tracker_number_raises_the_mark_only_without_read_numbers()
    {
        var (service, _) = Build(SeedUser());

        await service.ImportAsync(Request(Item("101", trackerLastRead: 12m)), default);

        using var db = _db.NewContext();
        Assert.Equal(12, db.ReadingStates.IgnoreQueryFilters().Single().MaxChapter);
        Assert.Empty(db.PendingReadImports);
    }

    [Fact]
    public async Task Unnumbered_reads_alone_raise_nothing()
    {
        var (service, _) = Build(SeedUser());

        await service.ImportAsync(Request(Item("101", read: [])), default);

        using var db = _db.NewContext();
        Assert.Empty(db.ReadingStates.IgnoreQueryFilters());
        Assert.Empty(db.PendingReadImports.IgnoreQueryFilters());
    }

    [Fact]
    public async Task Read_state_is_left_alone_when_the_caller_turns_it_off()
    {
        var (service, _) = Build(SeedUser());

        await service.ImportAsync(Request(Item("101", read: [1m])) with { ApplyReadState = false }, default);

        using var db = _db.NewContext();
        Assert.Empty(db.ReadingStates.IgnoreQueryFilters());
        Assert.Empty(db.PendingReadImports.IgnoreQueryFilters());
    }

    [Fact]
    public async Task Marks_on_a_series_that_already_has_chapters_are_applied_at_once()
    {
        var user = SeedUser();
        var existing = SeedSeries(101, _rootId);
        using (var db = _db.NewContext())
        {
            db.Chapters.AddRange(
                new Chapter { SeriesId = existing, Number = 1, Language = "en" },
                new Chapter { SeriesId = existing, Number = 2, Language = "en" },
                new Chapter { SeriesId = existing, Number = 3, Language = "en" });
            db.SaveChanges();
        }

        var (service, _) = Build(user);
        await service.ImportAsync(Request(Item("101", read: [1m, 2m])), default);

        using var check = _db.NewContext();
        var rows = check.ChapterProgress.IgnoreQueryFilters().ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(user, r.UserId));
        Assert.Empty(check.PendingReadImports.IgnoreQueryFilters());
    }

    private static MetadataSearchResult Hit(string id, string title) =>
        new(id, title, null, 2020, SeriesStatus.Ongoing, null, null);

    private sealed class FakeStore() : MangaBakaLocalStore(
        new MangaBakaDumpOptions("", Path.GetTempPath()), new FakeAppSettings(), NullLogger<MangaBakaLocalStore>.Instance)
    {
        public bool Available { get; set; } = true;
        public Dictionary<long, long> AniList { get; } = [];
        public Dictionary<long, long> Mal { get; } = [];
        public Dictionary<long, long> Kitsu { get; } = [];
        public Dictionary<string, string> Merged { get; } = [];
        public HashSet<string> Novels { get; } = [];
        public Dictionary<string, string[]> AltTitles { get; } = [];

        public override Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(Available);

        public override Task<IReadOnlyDictionary<long, long>> GetIdsByExternalIdsAsync(
            ExternalSource source, IReadOnlyCollection<long> externalIds, CancellationToken ct = default)
        {
            var map = source switch
            {
                ExternalSource.AniList => AniList,
                ExternalSource.MyAnimeList => Mal,
                _ => Kitsu,
            };
            return Task.FromResult<IReadOnlyDictionary<long, long>>(
                externalIds.Where(map.ContainsKey).ToDictionary(id => id, id => map[id]));
        }

        public override Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default)
        {
            var id = Merged.GetValueOrDefault(providerId, providerId);
            return Task.FromResult(Novels.Contains(id)
                ? null
                : new SeriesMetadata
                {
                    ProviderId = id,
                    Title = $"Series {id}",
                    MangaBakaId = int.Parse(id),
                    AltTitles = [.. AltTitles.GetValueOrDefault(id, []).Select(t => new LocalizedTitle(t, "ja-ro"))],
                });
        }
    }

    private sealed class FakeMetadataProvider : IMetadataProvider
    {
        public Dictionary<string, MetadataSearchResult[]> Search { get; } = [];
        public Dictionary<string, int> Canonical { get; } = [];

        public string Name => "fake";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>(Search.GetValueOrDefault(query, []));

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default)
        {
            var id = Canonical.TryGetValue(providerId, out var canonical) ? canonical : int.Parse(providerId);
            return Task.FromResult<SeriesMetadata?>(new SeriesMetadata
            {
                ProviderId = id.ToString(), Title = $"Series {id}", MangaBakaId = id, Year = 2020,
            });
        }
    }

    /// <summary>Lands a tag with the same label (other case) just before this context's own insert.</summary>
    private sealed class CompetingTagInsert(string label) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("INSERT INTO \"Tags\""))
            {
                Fired = true;
                await using var other = command.Connection!.CreateCommand();
                other.Transaction = command.Transaction;
                other.CommandText = "INSERT INTO \"Tags\" (\"Label\", \"Color\") VALUES ($label, 'blue')";
                var p = other.CreateParameter();
                p.ParameterName = "$label";
                p.Value = label;
                other.Parameters.Add(p);
                await other.ExecuteNonQueryAsync(cancellationToken);
            }

            return result;
        }
    }
}
