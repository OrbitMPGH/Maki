using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class RecommendationFeedbackTests : IDisposable
{
    private readonly TestDb _fixture = new();

    private RecommendationFeedbackService Service(Maki.Data.MakiDbContext db, int userId) => new(
        db,
        new MangaBakaLocalStore(new MangaBakaDumpOptions("", ""), new FakeAppSettings(),
            NullLogger<MangaBakaLocalStore>.Instance),
        new TestCurrentUser(userId));

    public void Dispose() => _fixture.Dispose();

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
}
