using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Data;
using Maki.Metadata.MangaBaka;

namespace Maki.Api.Tests;

public class DiscoverQueueServiceTests : IDisposable
{
    private readonly TestDb _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static MangaBakaRecommendation Pick(long id, int? franchise = null) =>
        new(id.ToString(), $"Title {id}", null, null, null, SeriesStatus.Completed, null, null, [], [],
            false, null, null, FranchiseId: franchise);

    private static List<MangaBakaRecommendation> Picks(long from, int count) =>
        Enumerable.Range(0, count).Select(i => Pick(from + i)).ToList();

    private static DiscoverQueueService Service(MakiDbContext db) =>
        new(db, new TestCurrentUser(1), null!, null!, null!, null!);

    private static Task<QueueDeck> Compose(MakiDbContext db, IReadOnlyList<MangaBakaRecommendation>? taste,
        IReadOnlyList<MangaBakaRecommendation> trending, IReadOnlyList<long>? exclude = null, int take = 12) =>
        Service(db).ComposeAsync(new QueueRequest(take, exclude), taste, trending);

    [Fact]
    public async Task Everything_the_reader_already_answered_is_excluded()
    {
        var other = _fixture.SeedUser("other");
        _fixture.SeedSeries(configure: s => s.MangaBakaId = 1);
        var now = DateTime.UtcNow;
        using (var seed = _fixture.NewContext())
        {
            seed.PlanToReadEntries.Add(new PlanToReadEntry { UserId = 1, ProviderId = 2, Title = "Saved", AddedAtUtc = now });
            seed.RecommendationFeedback.AddRange(
                new RecommendationFeedback { UserId = 1, ProviderId = 3, Suppression = RecommendationSuppression.Dismissed, DismissedUntilUtc = now.AddDays(5) },
                new RecommendationFeedback { UserId = 1, ProviderId = 4, Suppression = RecommendationSuppression.Dismissed, DismissedUntilUtc = now.AddDays(-1) },
                new RecommendationFeedback { UserId = 1, ProviderId = 5, Suppression = RecommendationSuppression.Hidden },
                new RecommendationFeedback { UserId = 1, ProviderId = 6, Sentiment = RecommendationSentiment.Liked },
                new RecommendationFeedback { UserId = 1, ProviderId = 7, Sentiment = RecommendationSentiment.Disliked, Suppression = RecommendationSuppression.Hidden },
                new RecommendationFeedback { UserId = other, ProviderId = 10, Suppression = RecommendationSuppression.Hidden });
            seed.SeriesRequests.AddRange(
                new SeriesRequest { UserId = 1, Kind = SeriesRequestKind.NewSeries, Status = SeriesRequestStatus.Pending, MetadataProviderId = "8", Title = "Asked", Created = now },
                new SeriesRequest { UserId = 1, Kind = SeriesRequestKind.NewSeries, Status = SeriesRequestStatus.Rejected, MetadataProviderId = "11", Title = "Refused", Created = now },
                new SeriesRequest { UserId = other, Kind = SeriesRequestKind.NewSeries, Status = SeriesRequestStatus.Pending, MetadataProviderId = "12", Title = "Theirs", Created = now });
            await seed.SaveChangesAsync();
        }

        using var db = _fixture.NewContext();
        var deck = await Compose(db, Picks(1, 12), [], exclude: [9]);

        Assert.Equal([4L, 10, 11, 12], deck.Cards.Select(c => long.Parse(c.ProviderId)));
        Assert.All(deck.Cards, c => Assert.Equal("taste", c.Origin));
    }

    [Fact]
    public async Task Trending_cards_land_on_the_fifth_and_tenth_positions()
    {
        using var db = _fixture.NewContext(1);

        var deck = await Compose(db, Picks(100, 20), Picks(500, 5));

        Assert.Equal(12, deck.Cards.Count);
        var trendingAt = deck.Cards.Select((c, i) => (c, i)).Where(x => x.c.Origin == "trending").Select(x => x.i + 1);
        Assert.Equal([5, 10], trendingAt);
        Assert.Equal(Enumerable.Range(100, 10).Select(i => i.ToString()),
            deck.Cards.Where(c => c.Origin == "taste").Select(c => c.ProviderId));
        Assert.False(deck.ColdStart);
        Assert.False(deck.Exhausted);
    }

    [Fact]
    public async Task One_card_per_franchise_across_both_lists()
    {
        using var db = _fixture.NewContext(1);
        List<MangaBakaRecommendation> taste = [Pick(1, 7), Pick(2, 7), Pick(3), Pick(4, 8), Pick(5)];
        List<MangaBakaRecommendation> trending = [Pick(50, 8), Pick(51)];

        var deck = await Compose(db, taste, trending);

        Assert.Equal(["1", "3", "4", "5", "51"], deck.Cards.Select(c => c.ProviderId));
    }

    [Fact]
    public async Task A_title_in_both_lists_is_dealt_once()
    {
        using var db = _fixture.NewContext(1);

        var deck = await Compose(db, Picks(1, 6), [Pick(3), Pick(60)]);

        Assert.Single(deck.Cards, c => c.ProviderId == "3");
        Assert.Contains(deck.Cards, c => c.ProviderId == "60" && c.Origin == "trending");
    }

    [Fact]
    public async Task No_seeds_deals_a_trending_only_cold_start_deck()
    {
        using var db = _fixture.NewContext(1);

        var deck = await Compose(db, null, Picks(500, 20));

        Assert.True(deck.ColdStart);
        Assert.False(deck.Exhausted);
        Assert.Equal(12, deck.Cards.Count);
        Assert.All(deck.Cards, c => Assert.Equal("trending", c.Origin));
    }

    [Fact]
    public async Task Both_lists_drained_is_exhausted()
    {
        using var db = _fixture.NewContext(1);

        var deck = await Compose(db, Picks(1, 3), Picks(10, 2), exclude: [1, 2, 3, 10, 11]);

        Assert.Empty(deck.Cards);
        Assert.True(deck.Exhausted);
        Assert.False(deck.ColdStart);
    }

    [Fact]
    public async Task The_deck_that_drains_both_sources_says_so()
    {
        using var db = _fixture.NewContext(1);

        var last = await Compose(db, Picks(1, 3), Picks(10, 1));
        var more = await Compose(db, Picks(1, 20), Picks(50, 5));
        var capped = await Compose(db, [Pick(1, 7), Pick(2, 7)], []);

        Assert.Equal(4, last.Cards.Count);
        Assert.True(last.Exhausted);
        Assert.False(more.Exhausted);
        Assert.Single(capped.Cards);
        Assert.False(capped.Exhausted);
    }

    [Fact]
    public async Task Each_card_carries_its_current_feedback_revision()
    {
        using (var seed = _fixture.NewContext())
        {
            seed.RecommendationFeedback.Add(new RecommendationFeedback
            {
                UserId = 1, ProviderId = 2, Suppression = RecommendationSuppression.Dismissed,
                DismissedUntilUtc = DateTime.UtcNow.AddDays(-1), Revision = 4
            });
            await seed.SaveChangesAsync();
        }

        using var db = _fixture.NewContext(1);
        var deck = await Compose(db, Picks(1, 3), []);

        Assert.Equal([0L, 4, 0], deck.Cards.Select(c => c.FeedbackRevision));
    }

    [Fact]
    public void A_card_serializes_as_a_recommendation_plus_its_origin()
    {
        var json = System.Text.Json.JsonSerializer.SerializeToElement(
            new QueueCard(Pick(5, 3), "trending", 2),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal("5", json.GetProperty("providerId").GetString());
        Assert.Equal(3, json.GetProperty("franchiseId").GetInt32());
        Assert.Equal("trending", json.GetProperty("origin").GetString());
        Assert.Equal(2, json.GetProperty("feedbackRevision").GetInt64());
    }
}
