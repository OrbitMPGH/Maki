using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
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
}
