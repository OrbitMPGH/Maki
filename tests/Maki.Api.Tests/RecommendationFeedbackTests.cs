using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Core.Scrobbling;
using Maki.Metadata.CoRead;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.RecoGraph;
using Maki.Metadata.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class RecommendationFeedbackTests : IDisposable
{
    private readonly TestDb _fixture = new();

    private DumpDbBuilder? _dump;

    private RecommendationFeedbackService Service(Maki.Data.MakiDbContext db, int userId) => new(
        db, Store(""), new TestCurrentUser(userId), new TestLocalizer());

    /// <summary>A store over the fake dump once <see cref="Catalogued"/> has built one.</summary>
    private MangaBakaLocalStore Store(string path) => new(
        new MangaBakaDumpOptions(path, Path.GetTempPath()), new FakeAppSettings(),
        NullLogger<MangaBakaLocalStore>.Instance);

    private RecommendationFeedbackService Catalogued(Maki.Data.MakiDbContext db, int userId,
        Action<DumpDbBuilder> seed)
    {
        _dump = new DumpDbBuilder();
        seed(_dump);
        return new RecommendationFeedbackService(
            db, Store(_dump.Path), new TestCurrentUser(userId), new TestLocalizer());
    }

    public void Dispose()
    {
        _dump?.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public async Task Hide_undo_restores_the_original_dismissal_without_a_new_cooldown()
    {
        var expiry = DateTime.UtcNow.AddDays(4);
        using var db = _fixture.NewContext(1);
        db.RecommendationFeedback.Add(new RecommendationFeedback
        {
            UserId = 1, ProviderId = 123, Suppression = RecommendationSuppression.Dismissed,
            DismissedUntilUtc = expiry
        });
        await db.SaveChangesAsync();
        var service = Service(db, 1);

        var hidden = await service.MutateAsync(1, 123, new FeedbackCommand("hide", Guid.NewGuid(), 0));
        Assert.True(hidden.Changed);
        Assert.Equal("hidden", hidden.State.Suppression);
        Assert.False((await service.MutateAsync(1, 123,
            new FeedbackCommand("dismiss", Guid.NewGuid(), 1))).Changed);

        var restored = await service.UndoAsync(1, hidden.EventId!.Value, Guid.NewGuid(), 1);
        Assert.Equal("dismissed", restored.State.Suppression);
        Assert.Equal(expiry, restored.State.DismissedUntilUtc);
    }

    [Fact]
    public async Task Undo_restores_the_sentiment_a_like_replaced()
    {
        // The notification's Undo button is offered on every action, like and dislike included.
        using var db = _fixture.NewContext(1);
        db.RecommendationFeedback.Add(new RecommendationFeedback
        {
            UserId = 1, ProviderId = 321, Sentiment = RecommendationSentiment.Disliked
        });
        await db.SaveChangesAsync();
        var service = Service(db, 1);

        var liked = await service.MutateAsync(1, 321, new FeedbackCommand("like", Guid.NewGuid(), 0));
        Assert.Equal("liked", liked.State.Sentiment);

        var restored = await service.UndoAsync(1, liked.EventId!.Value, Guid.NewGuid(), 1);
        Assert.Equal("disliked", restored.State.Sentiment);
        Assert.Equal(RecommendationSentiment.Disliked,
            (await db.RecommendationFeedback.AsNoTracking()
                .FirstAsync(x => x.UserId == 1 && x.ProviderId == 321)).Sentiment);
    }

    [Fact]
    public async Task Replayed_mutation_is_stable_and_a_new_identical_action_is_a_no_op()
    {
        using var db = _fixture.NewContext(1);
        db.RecommendationFeedback.Add(new RecommendationFeedback { UserId = 1, ProviderId = 234 });
        await db.SaveChangesAsync();
        var service = Service(db, 1);
        var command = new FeedbackCommand("dismiss", Guid.NewGuid(), 0);
        var first = await service.MutateAsync(1, 234, command);
        var replay = await service.MutateAsync(1, 234, command);
        Assert.Equal(first.EventId, replay.EventId);
        Assert.Equal(first.State.Revision, replay.State.Revision);
        Assert.Equal(first.State.DismissedUntilUtc, replay.State.DismissedUntilUtc);
        var duplicate = await service.MutateAsync(1, 234,
            new FeedbackCommand("dismiss", Guid.NewGuid(), 1));
        Assert.False(duplicate.Changed);
        Assert.Single(await db.RecommendationFeedbackEvents.ToListAsync());
        Assert.Equal(first.State.DismissedUntilUtc, duplicate.State.DismissedUntilUtc);
    }

    [Fact]
    public async Task Feedback_is_private_to_its_owner()
    {
        var other = _fixture.SeedUser("other");
        using (var db = _fixture.NewContext(1))
        {
            db.RecommendationFeedback.Add(new RecommendationFeedback
            { UserId = 1, ProviderId = 345, Suppression = RecommendationSuppression.Hidden });
            await db.SaveChangesAsync();
        }
        using var otherDb = _fixture.NewContext(other);
        var service = Service(otherDb, other);
        Assert.Empty(await service.SuppressedAsync(other));
        Assert.Empty((await service.StatesAsync(other, null, 40)).Items);
    }

    [Fact]
    public async Task A_mutation_id_cannot_be_reused_for_another_action()
    {
        using var db = _fixture.NewContext(1);
        db.RecommendationFeedback.Add(new RecommendationFeedback { UserId = 1, ProviderId = 456 });
        await db.SaveChangesAsync();
        var service = Service(db, 1);
        var id = Guid.NewGuid();
        await service.MutateAsync(1, 456, new FeedbackCommand("hide", id, 0));

        await Assert.ThrowsAsync<FeedbackConflictException>(() =>
            service.MutateAsync(1, 456, new FeedbackCommand("clear-suppression", id, 1)));
        Assert.Equal(RecommendationSuppression.Hidden,
            (await db.RecommendationFeedback.SingleAsync()).Suppression);
    }

    [Fact]
    public async Task Exposure_survives_restoring_a_hidden_title_and_blocks_a_stale_undo()
    {
        using var db = _fixture.NewContext(1);
        db.RecommendationFeedback.Add(new RecommendationFeedback { UserId = 1, ProviderId = 567 });
        await db.SaveChangesAsync();
        var service = Service(db, 1);

        var seen = await service.MutateAsync(1, 567,
            new FeedbackCommand("mark-exposed", Guid.NewGuid(), 0, "anime"));
        var hidden = await service.MutateAsync(1, 567,
            new FeedbackCommand("hide", Guid.NewGuid(), 1));
        var restored = await service.MutateAsync(1, 567,
            new FeedbackCommand("clear-suppression", Guid.NewGuid(), 2));

        Assert.Equal("none", restored.State.Suppression);
        Assert.Equal(["anime"], restored.State.Exposure);
        Assert.Equal("suppressed", restored.QueueEffect);
        await Assert.ThrowsAsync<FeedbackConflictException>(() =>
            service.UndoAsync(1, hidden.EventId!.Value, Guid.NewGuid(), 2));
        await Assert.ThrowsAsync<FeedbackConflictException>(() =>
            service.UndoAsync(1, seen.EventId!.Value, Guid.NewGuid(), 3));
    }

    [Fact]
    public async Task Expired_dismissal_can_be_renewed_with_a_fresh_cooldown()
    {
        using var db = _fixture.NewContext(1);
        db.RecommendationFeedback.Add(new RecommendationFeedback
        {
            UserId = 1, ProviderId = 678, Suppression = RecommendationSuppression.Dismissed,
            DismissedUntilUtc = DateTime.UtcNow.AddMinutes(-1), Revision = 4
        });
        await db.SaveChangesAsync();
        var service = Service(db, 1);

        var renewed = await service.MutateAsync(1, 678,
            new FeedbackCommand("dismiss", Guid.NewGuid(), 4));

        Assert.True(renewed.Changed);
        Assert.Equal(5, renewed.State.Revision);
        Assert.True(renewed.State.DismissedUntilUtc > DateTime.UtcNow.AddDays(29));
    }

    [Fact]
    public async Task Feedback_history_for_a_hidden_root_is_not_returned()
    {
        var userId = _fixture.SeedUser("restricted-feedback", allRootFolders: false);
        _fixture.SeedSeries(configure: series => series.MangaBakaId = 789);
        using (var db = _fixture.NewContext(userId))
        {
            db.RecommendationFeedback.Add(new RecommendationFeedback { UserId = userId, ProviderId = 789 });
            await db.SaveChangesAsync();
            await Service(db, userId).MutateAsync(userId, 789,
                new FeedbackCommand("hide", Guid.NewGuid(), 0));
        }

        using var restricted = _fixture.NewContext(userId, allRootFolders: false);
        var service = Service(restricted, userId);
        Assert.Null(await service.CurrentStateAsync(userId, 789));
        Assert.Empty((await service.StatesAsync(userId, null, 40)).Items);
        Assert.Empty((await service.ActivityAsync(userId, null, 40)).Items);
    }

    [Fact]
    public async Task Stale_state_write_fails_the_sqlite_revision_check()
    {
        using (var seed = _fixture.NewContext(1))
        {
            seed.RecommendationFeedback.Add(new RecommendationFeedback { UserId = 1, ProviderId = 890 });
            await seed.SaveChangesAsync();
        }
        using var first = _fixture.NewContext(1);
        using var second = _fixture.NewContext(1);
        var current = await first.RecommendationFeedback.SingleAsync();
        var stale = await second.RecommendationFeedback.SingleAsync();
        current.Suppression = RecommendationSuppression.Hidden;
        current.Revision++;
        await first.SaveChangesAsync();
        stale.Suppression = RecommendationSuppression.Dismissed;
        stale.Revision++;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task New_target_needs_catalogue_metadata_but_existing_state_remains_manageable()
    {
        using var db = _fixture.NewContext(1);
        var service = Service(db, 1);
        await Assert.ThrowsAsync<FeedbackMetadataUnavailableException>(() =>
            service.MutateAsync(1, 901, new FeedbackCommand("hide", Guid.NewGuid(), 0)));

        db.RecommendationFeedback.Add(new RecommendationFeedback
        {
            UserId = 1, ProviderId = 901, Suppression = RecommendationSuppression.Hidden,
            Revision = 2
        });
        await db.SaveChangesAsync();
        var restored = await service.MutateAsync(1, 901,
            new FeedbackCommand("clear-suppression", Guid.NewGuid(), 2));
        Assert.Equal("none", restored.State.Suppression);
    }

    [Fact]
    public async Task Two_writers_holding_the_same_counter_cannot_lose_an_increment()
    {
        using var first = _fixture.NewContext(1);
        using var second = _fixture.NewContext(1);
        first.RecommendationProfileStates.Add(new RecommendationProfileState { UserId = 1 });
        await first.SaveChangesAsync();

        // Both contexts materialise the counter before either writes, which is exactly the state the
        // read-modify-write this replaced lost an increment from: each would have held 0, each would
        // have saved 1, and one reader's hide would have gone unannounced to every cache keyed on it.
        Assert.Equal(0, (await first.RecommendationProfileStates.FirstAsync()).FeedbackRevision);
        Assert.Equal(0, (await second.RecommendationProfileStates.FirstAsync()).FeedbackRevision);

        await RecommendationFeedbackService.BumpAsync(first, 1, feedback: true, signal: false);
        await RecommendationFeedbackService.BumpAsync(second, 1, feedback: true, signal: false);

        using var reader = _fixture.NewContext(1);
        Assert.Equal(2, (await RecommendationFeedbackService.VersionsAsync(reader, 1)).FeedbackRevision);
    }

    [Fact]
    public async Task Bump_creates_the_counter_row_when_a_user_has_none_yet()
    {
        using var db = _fixture.NewContext(1);
        var versions = await RecommendationFeedbackService.BumpAsync(db, 1, feedback: false, signal: true);

        Assert.Equal(0, versions.FeedbackRevision);
        Assert.Equal(1, versions.SignalRevision);
    }

    [Fact]
    public async Task States_and_activity_carry_the_catalogue_cover_and_genres()
    {
        using var db = _fixture.NewContext(1);
        db.RecommendationFeedback.Add(new RecommendationFeedback { UserId = 1, ProviderId = 1234 });
        await db.SaveChangesAsync();
        var service = Catalogued(db, 1, dump => dump.AddSeries(1234, "Dandadan",
            genresJson: """["Action","Comedy"]""", coverUrl: "https://covers.example/dandadan.jpg"));

        await service.MutateAsync(1, 1234, new FeedbackCommand("like", Guid.NewGuid(), 0));

        var state = Assert.Single((await service.StatesAsync(1, null, 40)).Items);
        Assert.Equal("Dandadan", state.Title);
        Assert.Equal("https://covers.example/dandadan.jpg", state.CoverUrl);
        Assert.Equal(["Action", "Comedy"], state.Genres!);
        Assert.NotNull(state.UpdatedAtUtc);

        var activity = Assert.Single((await service.ActivityAsync(1, null, 40)).Items);
        Assert.Equal("https://covers.example/dandadan.jpg", activity.CoverUrl);
    }

    [Fact]
    public async Task Sort_recent_orders_by_last_change_and_pages_without_repeating_a_row()
    {
        using var db = _fixture.NewContext(1);
        var service = Service(db, 1);
        foreach (var id in new long[] { 11, 22, 33 })
        {
            db.RecommendationFeedback.Add(new RecommendationFeedback { UserId = 1, ProviderId = id });
        }
        await db.SaveChangesAsync();

        // Oldest id changed last, so an id keyset would order these the other way round.
        await service.MutateAsync(1, 33, new FeedbackCommand("hide", Guid.NewGuid(), 0));
        await service.MutateAsync(1, 22, new FeedbackCommand("hide", Guid.NewGuid(), 0));
        await service.MutateAsync(1, 11, new FeedbackCommand("hide", Guid.NewGuid(), 0));

        var byId = await service.StatesAsync(1, null, 40);
        Assert.Equal([11, 22, 33], byId.Items.Select(x => x.MangaBakaId));

        var first = await service.StatesAsync(1, null, 2, sort: "recent");
        Assert.Equal([11, 22], first.Items.Select(x => x.MangaBakaId));
        Assert.Equal(2, first.NextCursor);
        var second = await service.StatesAsync(1, first.NextCursor, 2, sort: "recent");
        Assert.Equal([33], second.Items.Select(x => x.MangaBakaId));
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task Lab_sources_report_exclusion_reading_and_catalogue_fields()
    {
        var seriesId = _fixture.SeedSeries("Dandadan", configure: series => series.MangaBakaId = 1234);
        using var db = _fixture.NewContext(1);
        db.RecommendationSignalOverrides.Add(new RecommendationSignalOverride
        { UserId = 1, ProviderId = 1234, IgnoreAsSeed = true });
        await db.SaveChangesAsync();
        var service = Catalogued(db, 1, dump => dump.AddSeries(1234, "Dandadan",
            genresJson: """["Action","Comedy"]""", coverUrl: "https://covers.example/dandadan.jpg"));

        var controller = new RecommendationFeedbackController(service, new TestCurrentUser(1), new TestLocalizer(), db,
            new NotReadyRecommender(), new BehavioralTasteService(TasteTuning.Default),
            new FakeAppSettings(),
            new SeedWeightService(
                new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, new FakeAppSettings()),
            Avoidance(), NoAnimeSources());
        var payload = JsonSerializer.SerializeToElement(
            Assert.IsType<OkObjectResult>(await controller.Lab(default)).Value);

        var source = Assert.Single(payload.GetProperty("sources").EnumerateArray());
        Assert.Equal(1234, source.GetProperty("mangaBakaId").GetInt64());
        Assert.True(source.GetProperty("excluded").GetBoolean());
        Assert.False(source.GetProperty("isRead").GetBoolean());
        Assert.Equal("https://covers.example/dandadan.jpg", source.GetProperty("coverUrl").GetString());
        Assert.Equal(["Action", "Comedy"],
            source.GetProperty("genres").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(1, payload.GetProperty("summary").GetProperty("excluded").GetInt32());
        // Nothing pushed down and nothing to name, but both shapes are always present so the client
        // never has to branch on a missing field.
        Assert.Equal(0, payload.GetProperty("summary").GetProperty("pushingDown").GetInt32());
        Assert.Empty(payload.GetProperty("avoids").EnumerateArray());
        Assert.False(payload.TryGetProperty("dimensions", out _));
        Assert.NotEqual(0, seriesId);
    }

    [Fact]
    public async Task Lab_summary_counts_a_liked_title_that_is_not_on_the_shelf()
    {
        using var db = _fixture.NewContext(1);
        db.RecommendationFeedback.Add(new RecommendationFeedback
        { UserId = 1, ProviderId = 5548, Title = "Landmine", Sentiment = RecommendationSentiment.Liked, Revision = 1 });
        await db.SaveChangesAsync();
        var service = Catalogued(db, 1, dump => dump.AddSeries(5548, "Landmine"));

        var controller = new RecommendationFeedbackController(service, new TestCurrentUser(1), new TestLocalizer(), db,
            new NotReadyRecommender(), new BehavioralTasteService(TasteTuning.Default),
            new FakeAppSettings(),
            new SeedWeightService(
                new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, new FakeAppSettings()),
            Avoidance(), NoAnimeSources());
        var payload = JsonSerializer.SerializeToElement(
            Assert.IsType<OkObjectResult>(await controller.Lab(default)).Value);

        Assert.Equal(1, payload.GetProperty("summary").GetProperty("liked").GetInt32());
        var states = await service.StatesAsync(1, null, 100, sort: "recent");
        Assert.Equal([5548], states.Items.Select(x => x.MangaBakaId));
    }

    [Fact]
    public async Task The_observed_shelf_keeps_a_low_rated_row_the_effective_seeds_lose()
    {
        var seriesId = _fixture.SeedSeries("Disappointing", configure: s => s.MangaBakaId = 4321);
        using var db = _fixture.NewContext(1);
        db.UserSeriesStates.Add(new Maki.Data.Identity.UserSeriesState
        { UserId = 1, SeriesId = seriesId, Rating = 2 });
        await db.SaveChangesAsync();

        var tuning = TasteTuning.Default;
        var snapshot = await new SeedWeightService(
                new BehavioralTasteService(tuning), tuning, new FakeAppSettings())
            .SnapshotAsync(db, new TestCurrentUser(1));

        // The profile charts describe the shelf, so a title the reader owns and disliked is still
        // part of what they own. Only the seeds lose it.
        Assert.Contains(4321L, snapshot.Observed.EligibleIds);
        Assert.Equal(2 / 5.0, snapshot.Observed.Weights[4321]);
        Assert.DoesNotContain(4321L, snapshot.Effective.EligibleIds);
        Assert.False(snapshot.Effective.Weights.ContainsKey(4321));
        Assert.Equal(0.75, snapshot.Avoided[4321]);
    }

    /// <summary>
    /// The other half of the same rule. A thumbs down is a whole-catalogue statement, so a shelf
    /// title carrying one has to leave the positive population exactly as a low rating does:
    /// without that it steers the profile and is subtracted from the scan in the same pass.
    /// </summary>
    [Fact]
    public async Task A_thumbed_down_shelf_title_leaves_the_effective_seeds()
    {
        _fixture.SeedSeries("Rejected", configure: s => s.MangaBakaId = 4400);
        using var db = _fixture.NewContext(1);
        db.RecommendationFeedback.Add(new RecommendationFeedback
        { UserId = 1, ProviderId = 4400, Sentiment = RecommendationSentiment.Disliked, Revision = 1 });
        await db.SaveChangesAsync();

        var tuning = TasteTuning.Default;
        var snapshot = await new SeedWeightService(
                new BehavioralTasteService(tuning), tuning, new FakeAppSettings())
            .SnapshotAsync(db, new TestCurrentUser(1));

        Assert.Equal(1.0, snapshot.Avoided[4400]);
        Assert.DoesNotContain(4400L, snapshot.Effective.EligibleIds);
        Assert.False(snapshot.Effective.Weights.ContainsKey(4400));
        // The shelf half keeps it, the same way it keeps a low-rated row.
        Assert.Contains(4400L, snapshot.Observed.EligibleIds);
    }

    /// <summary>
    /// A dismissal is a cooldown, so the row still says "dismissed" long after the window closed.
    /// Manage signals counts its suppressed chip off this list, and the recommender stopped
    /// honouring the row the moment it expired.
    /// </summary>
    [Fact]
    public async Task An_expired_dismissal_no_longer_reads_as_a_suppression()
    {
        using var db = _fixture.NewContext(1);
        db.RecommendationFeedback.Add(new RecommendationFeedback
        {
            UserId = 1, ProviderId = 4500, Suppression = RecommendationSuppression.Dismissed,
            DismissedUntilUtc = DateTime.UtcNow.AddDays(-1)
        });
        db.RecommendationFeedback.Add(new RecommendationFeedback
        {
            UserId = 1, ProviderId = 4501, Suppression = RecommendationSuppression.Dismissed,
            DismissedUntilUtc = DateTime.UtcNow.AddDays(1)
        });
        await db.SaveChangesAsync();
        var service = Service(db, 1);

        var all = await service.StatesAsync(1, null, 40);
        Assert.DoesNotContain(all.Items, x => x.MangaBakaId == 4500);
        Assert.Contains(all.Items, x => x.MangaBakaId == 4501 && x.Suppression == "dismissed");

        var dismissed = await service.StatesAsync(1, null, 40, default, "dismissed");
        Assert.Single(dismissed.Items, x => x.MangaBakaId == 4501);

        // The single-state read is the same answer: no suppression rather than a stale one.
        var state = await service.CurrentStateAsync(1, 4500);
        Assert.Equal("none", state!.Suppression);
        Assert.Null(state.DismissedUntilUtc);
    }

    /// <summary>
    /// A reader with no tracker connected, which is what decides whether the Lab offers the anime
    /// panel at all. Subclassed with a null scrobbler and null settings because the override never
    /// reaches either.
    /// </summary>
    private sealed class NoSources() : AnimeSignalSources(null!, null!)
    {
        public override Task<IReadOnlyList<IAnimeListSource>> ConnectedAsync(
            int userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAnimeListSource>>([]);
    }

    private static AnimeSignalSources NoAnimeSources() => new NoSources();

    private sealed class OneSource() : AnimeSignalSources(null!, null!)
    {
        public override Task<IReadOnlyList<IAnimeListSource>> ConnectedAsync(
            int userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IAnimeListSource>>([new FakeAnimeSource()]);
    }

    private sealed class FakeAnimeSource : IAnimeListSource
    {
        public Task<IReadOnlyList<AnimeListEntry>> ListAnimeAsync(int userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AnimeListEntry>>([]);

        public Task<AnimeRelatedManga?> RelatedMangaAsync(
            int userId, long animeId, CancellationToken ct = default) =>
            Task.FromResult<AnimeRelatedManga?>(null);
    }

    /// <summary>
    /// The capability is "is this panel worth drawing", not "has this reader opted in". The client
    /// gates the whole section on it, opt-in switch included, so folding the opt-in into it would
    /// leave nobody able to turn it on.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Lab_offers_the_anime_panel_on_a_connected_tracker_alone(bool connected, bool expected)
    {
        using var db = _fixture.NewContext(1);
        var controller = new RecommendationFeedbackController(Service(db, 1), new TestCurrentUser(1), new TestLocalizer(), db,
            new NotReadyRecommender(), new BehavioralTasteService(TasteTuning.Default),
            new FakeAppSettings(),
            new SeedWeightService(
                new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default, new FakeAppSettings()),
            Avoidance(), connected ? new OneSource() : NoAnimeSources());

        var payload = JsonSerializer.SerializeToElement(
            Assert.IsType<OkObjectResult>(await controller.Lab(default)).Value);
        Assert.Equal(expected,
            payload.GetProperty("capabilities").GetProperty("animeSignals").GetBoolean());
    }

    /// <summary>
    /// The franchise the fallback walks: one anthology whose flat sequel column names two more rows,
    /// plus a fourth the reader's content ceiling puts out of reach.
    /// </summary>
    private RecommendationFeedbackService Franchise(Maki.Data.MakiDbContext db) =>
        Catalogued(db, 1, dump => dump
            .AddSeries(100, "Nagatoro", sequels: "[101,102,103]")
            .AddSeries(101, "Nagatoro Anthology 1")
            .AddSeries(102, "Nagatoro Anthology 2")
            .AddSeries(103, "Nagatoro Doujin", contentRating: "pornographic"));

    [Fact]
    public async Task Hiding_a_franchise_hides_every_visible_member_and_writes_one_event_each()
    {
        using var db = _fixture.NewContext(1);
        var service = Franchise(db);

        var result = await service.HideFranchiseAsync(1, 100, "hide", Guid.NewGuid());

        Assert.Equal(3, result.Changed);
        Assert.Equal([100, 101, 102], result.Titles.Select(x => x.MangaBakaId).Order());
        var rows = await db.RecommendationFeedback.AsNoTracking()
            .Where(x => x.UserId == 1).ToListAsync();
        Assert.All(rows, row => Assert.Equal(RecommendationSuppression.Hidden, row.Suppression));
        var events = await db.RecommendationFeedbackEvents.AsNoTracking().ToListAsync();
        Assert.Equal([100, 101, 102], events.Select(x => x.ProviderId).Order());
    }

    [Fact]
    public async Task A_franchise_member_above_the_content_ceiling_is_never_touched()
    {
        using var db = _fixture.NewContext(1);
        var service = Franchise(db);

        await service.HideFranchiseAsync(1, 100, "hide", Guid.NewGuid());

        Assert.False(await db.RecommendationFeedback.AnyAsync(x => x.ProviderId == 103));
        Assert.False(await db.RecommendationFeedbackEvents.AnyAsync(x => x.ProviderId == 103));
    }

    [Fact]
    public async Task Hiding_a_franchise_twice_changes_nothing_the_second_time()
    {
        using var db = _fixture.NewContext(1);
        var service = Franchise(db);

        await service.HideFranchiseAsync(1, 100, "hide", Guid.NewGuid());
        var again = await service.HideFranchiseAsync(1, 100, "hide", Guid.NewGuid());

        Assert.Equal(0, again.Changed);
        Assert.Empty(again.Titles);
        Assert.Equal(3, await db.RecommendationFeedbackEvents.CountAsync());
    }

    /// <summary>
    /// The avoidance labeller over nothing. Its inputs are the vector index, the dump and the
    /// reader's own shelf profile, and with an empty avoided set it answers before reaching any of
    /// them - which is why the profile service can be null here.
    /// </summary>
    private static TasteAvoidanceService Avoidance()
    {
        var options = new EmbeddingOptions("", "", "", EmbeddingModelProfile.Base);
        var dump = new MangaBakaDumpOptions("", Path.GetTempPath());
        return new TasteAvoidanceService(
            new MangaBakaLocalStore(dump, new FakeAppSettings(), NullLogger<MangaBakaLocalStore>.Instance),
            new VectorIndexCache(options, dump, NullLogger<VectorIndexCache>.Instance),
            new EmbeddingStore(options),
            null!,
            NullLogger<TasteAvoidanceService>.Instance);
    }

    /// <summary>The lab only asks the recommender whether semantic ranking is up.</summary>
    private sealed class NotReadyRecommender() : SemanticRecommender(
        new EmbeddingOptions("", "", "", EmbeddingModelProfile.Base),
        new MangaBakaDumpOptions("", ""),
        new EmbeddingStore(new EmbeddingOptions("", "", "", EmbeddingModelProfile.Base)),
        null!, null!, RecoGraphTuning.Default, null!, CoReadTuning.Default,
        NullLogger<SemanticRecommender>.Instance)
    {
        public override bool IsReady() => false;
    }
}
