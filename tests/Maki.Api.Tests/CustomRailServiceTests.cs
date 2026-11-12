using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Data.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="CustomRailService.CountAsync"/>: Recommendations has no catalogue count to give (it
/// draws from the ranked pool, not a filtered scan), so it must answer null rather than falling
/// through to the catalogue's <c>DiscoverService.CountAsync</c> path, which needs dependencies this
/// test leaves null.
/// </summary>
public sealed class CustomRailServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private CustomRailService Service(int userId)
    {
        var db = _db.NewContext(userId);
        return new CustomRailService(db, new TestCurrentUser(userId), null!, null!, null!, null!, null!);
    }

    [Fact]
    public async Task CountAsync_returns_null_for_a_recommendations_spec()
    {
        var alice = _db.SeedUser("alice");
        var spec = new CustomRailSpec(Source: CustomRailSources.Recommendations);

        var count = await Service(alice).CountAsync(spec, CancellationToken.None);

        Assert.Null(count);
    }

    [Fact]
    public async Task Excluding_interacted_titles_also_drops_owned_liked_and_disliked_ones()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        _db.SeedSeries("Owned", configure: s => s.MangaBakaId = 1);
        using (var db = _db.NewContext())
        {
            db.RecommendationFeedback.AddRange(
                new RecommendationFeedback { UserId = alice, ProviderId = 2, Sentiment = RecommendationSentiment.Liked },
                new RecommendationFeedback { UserId = alice, ProviderId = 3, Sentiment = RecommendationSentiment.Disliked },
                new RecommendationFeedback { UserId = alice, ProviderId = 4, Exposure = RecommendationExposure.Manga },
                new RecommendationFeedback { UserId = bob, ProviderId = 5, Sentiment = RecommendationSentiment.Liked });
            db.SaveChanges();
        }

        var plain = await Service(alice).ExclusionsAsync(excludeOwned: false, excludeInteracted: false);
        var owned = await Service(alice).ExclusionsAsync(excludeOwned: true, excludeInteracted: false);
        var interacted = await Service(alice).ExclusionsAsync(excludeOwned: false, excludeInteracted: true);

        Assert.Equal([4], plain.Order());
        Assert.Equal([1, 4], owned.Order());
        Assert.Equal([1, 2, 3, 4], interacted.Order());
    }

    [Fact]
    public async Task Library_rows_are_reused_within_the_window_and_not_across_users()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        _db.SeedSeries("First");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var spec = new CustomRailSpec(Source: CustomRailSources.Library);
        CustomRailService WithCache(int userId) => new(
            _db.NewContext(userId), new TestCurrentUser(userId), null!, null!, null!, null!, null!, cache);

        var first = await WithCache(alice).CountAsync(spec, CancellationToken.None);
        _db.SeedSeries("Second");
        var reused = await WithCache(alice).CountAsync(spec, CancellationToken.None);
        var other = await WithCache(bob).CountAsync(spec, CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(1, reused);
        Assert.Equal(2, other);
    }

    private CustomRailService SavedFilterService(int userId, bool allRootFolders = true)
    {
        var settings = new FakeAppSettings();
        return new CustomRailService(
            _db.NewContext(userId, allRootFolders), new TestCurrentUser(userId, allRootFolders: allRootFolders),
            null!, null!, null!, null!, null!, null,
            new SourceAvailability(settings, new SourceRegistry([])), settings);
    }

    private int SaveLibraryFilter(int userId, string spec)
    {
        using var db = _db.NewContext();
        var filter = new SavedFilter
        {
            UserId = userId, Name = "Filter", Scope = SavedFilter.LibraryScope, Spec = spec, Created = DateTime.UtcNow,
        };
        db.SavedFilters.Add(filter);
        db.SaveChanges();
        return filter.Id;
    }

    private static CustomRailSpec FromFilter(int id) => new(Source: CustomRailSources.Library, LibraryFilterId: id);

    [Fact]
    public async Task A_saved_library_filter_rail_shows_what_the_filter_shows_in_its_sort()
    {
        var alice = _db.SeedUser("alice");
        var berserk = _db.SeedSeries("Berserk", configure: s => s.Genres = ["Action"]);
        _db.SeedSeries("Yotsuba", configure: s => s.Genres = ["Comedy"]);
        var claymore = _db.SeedSeries("Claymore", configure: s => s.Genres = ["Action"]);
        var filter = SaveLibraryFilter(alice, """{"genres":["Action"],"sort":"title","status":"all"}""");

        var items = await SavedFilterService(alice).ItemsAsync(FromFilter(filter), 20);

        Assert.Equal([berserk, claymore], items.SeriesIds);
        Assert.Null(items.Unavailable);
        Assert.Equal(2, await SavedFilterService(alice).CountAsync(FromFilter(filter)));
    }

    [Fact]
    public async Task A_deleted_or_someone_elses_filter_says_so_instead_of_showing_everything()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        _db.SeedSeries("Berserk");
        var bobs = SaveLibraryFilter(bob, """{"status":"all"}""");

        var foreign = await SavedFilterService(alice).ItemsAsync(FromFilter(bobs), 20);
        var missing = await SavedFilterService(alice).ItemsAsync(FromFilter(bobs + 100), 20);

        Assert.Equal(CustomRailService.FilterDeleted, foreign.Unavailable);
        Assert.Empty(foreign.SeriesIds);
        Assert.Equal(CustomRailService.FilterDeleted, missing.Unavailable);
        Assert.Null(await SavedFilterService(alice).CountAsync(FromFilter(bobs)));
    }

    [Fact]
    public async Task A_saved_filter_rail_only_lists_root_folders_the_caller_can_see()
    {
        var reader = _db.SeedUser("reader", Maki.Core.Security.MakiPermission.None, allRootFolders: false);
        var granted = _db.SeedSeries("Granted");
        _db.SeedSeries("Hidden");
        var archived = _db.SeedSeries("Archived", configure: s => s.ArchivedAt = DateTime.UtcNow);
        using (var db = _db.NewContext())
        {
            foreach (var id in new[] { granted, archived })
            {
                db.UserRootFolders.Add(new UserRootFolder { UserId = reader, RootFolderId = db.Series.Single(s => s.Id == id).RootFolderId });
            }

            db.SaveChanges();
        }

        var filter = SaveLibraryFilter(reader, "{}");

        var items = await SavedFilterService(reader, allRootFolders: false).ItemsAsync(FromFilter(filter), 20);

        Assert.Equal([granted], items.SeriesIds);
    }

    [Fact]
    public void Only_a_library_rail_keeps_a_saved_filter_reference()
    {
        Assert.Equal(5, new CustomRailSpec(Source: CustomRailSources.Library, LibraryFilterId: 5).Normalize().LibraryFilterId);
        Assert.Null(new CustomRailSpec(Source: CustomRailSources.Catalogue, LibraryFilterId: 5).Normalize().LibraryFilterId);
        Assert.Null(new CustomRailSpec(Source: CustomRailSources.Library, LibraryFilterId: 0).Normalize().LibraryFilterId);
        var parsed = CustomRailSpec.Parse(CustomRailSpec.Serialize(new CustomRailSpec(Source: CustomRailSources.Library, LibraryFilterId: 9)));
        Assert.Equal(9, parsed.LibraryFilterId);
    }
}
