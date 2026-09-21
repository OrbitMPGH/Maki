using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public record FeedbackCommand(string Action, Guid ClientMutationId, long ExpectedRevision,
    string? Medium = null, FeedbackContext? Context = null);
public record FeedbackContext(string? Surface, string? ProfileVersion);
public record FeedbackState(long MangaBakaId, string Suppression, string[] Exposure,
    DateTime? DismissedUntilUtc, long Revision, string? Title, string Sentiment = "none",
    string? CoverUrl = null, string[]? Genres = null, DateTime? UpdatedAtUtc = null);
/// <summary>Catalogue fields the lab surfaces need for a title it already knows the id of.</summary>
public record CatalogueEntry(string Title, string? CoverUrl, string[] Genres);
public record FeedbackMutation(bool Changed, long? EventId, FeedbackState State,
    long FeedbackRevision, long SignalRevision, string QueueEffect, string TasteEffect,
    string FeedbackEffect = "none");
public record FeedbackPage<T>(IReadOnlyList<T> Items, long? NextCursor, long FeedbackRevision, long SignalRevision);
public record FeedbackActivity(long Id, long MangaBakaId, string? Title, string Action,
    DateTime OccurredAtUtc, long StateRevision, DateTime? DismissedUntilUtc,
    string QueueEffect, string TasteEffect, string? CoverUrl = null);
public record SignalOverrideState(long MangaBakaId, bool IgnoreAsSeed, long Revision);
public record SignalOverrideCommand(bool IgnoreAsSeed, Guid ClientMutationId, long ExpectedRevision);
public record SignalOverrideMutation(bool Changed, SignalOverrideState State, long SignalRevision);
public record FranchiseFeedbackCommand(string Action, Guid ClientMutationId);
public record FranchiseFeedbackTitle(long MangaBakaId, string? Title);
public record FranchiseFeedbackResult(int Changed, IReadOnlyList<FranchiseFeedbackTitle> Titles,
    long FeedbackRevision);
/// <summary>
/// A failure this service reports to a caller, carrying the catalogue key rather than a sentence.
/// The service has no request locale of its own worth spending here and the controller already
/// localizes, so the message stays the key: greppable in a log, worded once on the way out.
/// </summary>
public abstract class FeedbackException(string key, object? args = null) : Exception(key)
{
    public string Key { get; } = key;
    public object? Args { get; } = args;
}

public sealed class FeedbackConflictException(string key, object? args = null) : FeedbackException(key, args);
public sealed class FeedbackValidationException(string key, object? args = null) : FeedbackException(key, args);
public sealed class FeedbackNotFoundException(string key, object? args = null) : FeedbackException(key, args);
public sealed class FeedbackMetadataUnavailableException(string key, object? args = null)
    : FeedbackException(key, args);

public class RecommendationFeedbackService(
    MakiDbContext db, MangaBakaLocalStore catalogue, ICurrentUser currentUser, ILocalizer localizer,
    SemanticRecommender? semantic = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// How many members of one franchise a single action may touch. A catalogue anthology can carry
    /// dozens of rows and the reader asked for one action, not an unbounded write.
    /// </summary>
    private const int MaxFranchiseMembers = 50;

    public async Task<HashSet<long>> SuppressedAsync(int userId, CancellationToken ct = default)
        => await SuppressedAsync(db, userId, ct);

    public static async Task<HashSet<long>> SuppressedAsync(MakiDbContext db, int userId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var rows = await db.RecommendationFeedback.AsNoTracking()
            .Where(x => x.UserId == userId).ToListAsync(ct);
        return rows.Where(x => RecommendationFeedbackPolicy.Suppresses(x, now))
            .Select(x => x.ProviderId).ToHashSet();
    }

    /// <param name="sort">
    /// "recent" orders by <c>UpdatedAtUtc desc, Id desc</c>. A row's id is its creation order and its
    /// timestamp is its last change, so the two disagree and an id keyset cannot page that order.
    /// The cursor is therefore a row offset for this sort only; anything else keeps the original
    /// <c>Id asc</c> keyset with the cursor as an id.
    /// </param>
    public async Task<FeedbackPage<FeedbackState>> StatesAsync(int userId, long? cursor, int limit,
        CancellationToken ct = default, string? filter = null, string? sort = null)
    {
        var hiddenIds = await HiddenLocalIdsAsync(await db.RecommendationFeedback.AsNoTracking()
            .Where(x => x.UserId == userId).Select(x => x.ProviderId).Distinct().ToListAsync(ct), ct);
        var recent = sort == "recent";
        var now = DateTime.UtcNow;
        // A dismissal whose window has passed is no suppression at all: RecommendationFeedbackPolicy
        // stopped honouring it the moment it expired, so listing it as dismissed would describe a
        // rule that is no longer being applied. The projection below reports it as "none" for the
        // same reason, which is what the client counts its suppressed chip from.
        var query = db.RecommendationFeedback.AsNoTracking()
            .Where(x => x.UserId == userId && (recent || x.Id > (cursor ?? 0)) &&
                (x.Suppression == RecommendationSuppression.Hidden ||
                 x.Suppression == RecommendationSuppression.Dismissed && x.DismissedUntilUtc > now ||
                 x.Exposure != RecommendationExposure.None ||
                 x.Sentiment != RecommendationSentiment.None) &&
                !hiddenIds.Contains(x.ProviderId));
        query = filter switch
        {
            "hidden" => query.Where(x => x.Suppression == RecommendationSuppression.Hidden),
            "dismissed" => query.Where(x => x.Suppression == RecommendationSuppression.Dismissed &&
                x.DismissedUntilUtc > now),
            "exposed" => query.Where(x => x.Exposure != RecommendationExposure.None),
            "liked" => query.Where(x => x.Sentiment == RecommendationSentiment.Liked),
            "disliked" => query.Where(x => x.Sentiment == RecommendationSentiment.Disliked),
            _ => query
        };
        var take = Math.Clamp(limit, 1, 100);
        var offset = recent ? (int)Math.Clamp(cursor ?? 0, 0, int.MaxValue) : 0;
        var rows = await (recent
            ? query.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id).Skip(offset)
            : query.OrderBy(x => x.Id)).Take(take + 1).ToListAsync(ct);
        var versions = await VersionsAsync(userId, ct);
        var page = rows.Take(take).ToList();
        var entries = await VisibleTitlesAsync(page.Select(x => x.ProviderId), ct);
        var next = rows.Count > page.Count ? recent ? offset + page.Count : page[^1].Id : (long?)null;
        return new FeedbackPage<FeedbackState>(
            page.Select(x => Hydrate(Live(State(x), now), entries.GetValueOrDefault(x.ProviderId))).ToList(), next,
            versions.FeedbackRevision, versions.SignalRevision);
    }

    public async Task<FeedbackState?> CurrentStateAsync(int userId, long id, CancellationToken ct = default)
    {
        if ((await HiddenLocalIdsAsync([id], ct)).Contains(id)) return null;
        var state = await db.RecommendationFeedback.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.ProviderId == id, ct);
        if (state is null) return null;
        var entries = await VisibleTitlesAsync([id], ct);
        return Hydrate(Live(State(state), DateTime.UtcNow), entries.GetValueOrDefault(id));
    }

    public async Task<FeedbackPage<FeedbackActivity>> ActivityAsync(int userId, long? cursor, int limit,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 100);
        var cutoff = DateTime.UtcNow.AddDays(-90);
        var hiddenIds = await HiddenLocalIdsAsync(await db.RecommendationFeedback.AsNoTracking()
            .Where(x => x.UserId == userId).Select(x => x.ProviderId).Distinct().ToListAsync(ct), ct);
        var oldestDisplayed = await db.RecommendationFeedbackEvents.AsNoTracking()
            .Where(x => x.UserId == userId && x.OccurredAtUtc >= cutoff &&
                !hiddenIds.Contains(x.ProviderId))
            .OrderByDescending(x => x.Id).Skip(1999).Select(x => x.Id).FirstOrDefaultAsync(ct);
        var rows = await db.RecommendationFeedbackEvents.AsNoTracking()
            .Where(x => x.UserId == userId && x.Id < (cursor ?? long.MaxValue) &&
                x.OccurredAtUtc >= cutoff && (oldestDisplayed == 0 || x.Id >= oldestDisplayed) &&
                !hiddenIds.Contains(x.ProviderId))
            .OrderByDescending(x => x.Id).Take(take + 1).ToListAsync(ct);
        var versions = await VersionsAsync(userId, ct);
        var page = rows.Take(take).ToList();
        var entries = await VisibleTitlesAsync(page.Select(x => x.ProviderId), ct);
        return new FeedbackPage<FeedbackActivity>(page.Select(x =>
        {
            var after = JsonSerializer.Deserialize<FeedbackState>(x.NewState, Json)!;
            var suppressed = after.Exposure.Length > 0 || after.Suppression == "hidden" ||
                after.Suppression == "dismissed" && after.DismissedUntilUtc > DateTime.UtcNow;
            var entry = entries.GetValueOrDefault(x.ProviderId);
            return new FeedbackActivity(x.Id, x.ProviderId, entry?.Title, x.Action,
                x.OccurredAtUtc, x.StateRevision, after.DismissedUntilUtc,
                localizer.Get(suppressed ? "feedback.queue.excluded" : "feedback.queue.eligible"),
                localizer.Get(after.Sentiment switch
                {
                    "liked" => "feedback.taste.liked",
                    "disliked" => "feedback.taste.disliked",
                    _ => "feedback.taste.unchanged",
                }), entry?.CoverUrl);
        }).ToList(),
            rows.Count > take ? page[^1].Id : null, versions.FeedbackRevision, versions.SignalRevision);
    }

    public async Task<RecommendationProfileState> VersionsAsync(int userId, CancellationToken ct = default) =>
        await VersionsAsync(db, userId, ct);

    public static async Task<RecommendationProfileState> VersionsAsync(MakiDbContext db, int userId,
        CancellationToken ct = default) =>
        await db.RecommendationProfileStates.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId, ct)
        ?? new RecommendationProfileState { UserId = userId };

    /// <summary>
    /// Advances a user's revision counters, and returns what they now are.
    /// <para>
    /// The increment happens in the database rather than on a tracked entity. Read-modify-write
    /// loses one when two writers read the same value before either saves, and these counters are
    /// advanced from paths that do not share a transaction: a feedback mutation runs inside one, a
    /// series deletion bumping every affected provenance owner does not. SQLite's single writer
    /// hides most of it and would stop hiding it the day this runs on anything else.
    /// </para>
    /// </summary>
    /// <param name="userId">
    /// Whose counters. Explicit and filter-bypassing because deleting a shared series has to bump
    /// every user whose provenance it carried, not whoever happens to be making the request.
    /// </param>
    public static async Task<RecommendationProfileState> BumpAsync(
        MakiDbContext db, int userId, bool feedback, bool signal, CancellationToken ct = default)
    {
        if (await IncrementAsync(db, userId, feedback, signal, ct) == 0)
        {
            var created = new RecommendationProfileState
            {
                UserId = userId,
                FeedbackRevision = feedback ? 1 : 0,
                SignalRevision = signal ? 1 : 0,
            };
            db.RecommendationProfileStates.Add(created);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Somebody created the row between the update finding nothing and this insert.
                // Detach ours and add to theirs, so neither increment is the one that got lost.
                db.Entry(created).State = EntityState.Detached;
                await IncrementAsync(db, userId, feedback, signal, ct);
            }
        }

        return await VersionsAsync(db, userId, ct);
    }

    private static Task<int> IncrementAsync(
        MakiDbContext db, int userId, bool feedback, bool signal, CancellationToken ct) =>
        db.RecommendationProfileStates.IgnoreQueryFilters()
            .Where(x => x.UserId == userId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.FeedbackRevision, x => feedback ? x.FeedbackRevision + 1 : x.FeedbackRevision)
                .SetProperty(x => x.SignalRevision, x => signal ? x.SignalRevision + 1 : x.SignalRevision), ct);

    public async Task<IReadOnlyList<SignalOverrideState>> SignalOverridesAsync(int userId,
        CancellationToken ct = default)
    {
        var hiddenIds = await HiddenLocalIdsAsync(await db.RecommendationSignalOverrides.AsNoTracking()
            .Where(x => x.UserId == userId && x.IgnoreAsSeed).Select(x => x.ProviderId).ToListAsync(ct), ct);
        return (await db.RecommendationSignalOverrides.AsNoTracking()
            .Where(x => x.UserId == userId && x.IgnoreAsSeed && !hiddenIds.Contains(x.ProviderId))
            .OrderBy(x => x.ProviderId).ToListAsync(ct))
            .Select(x => new SignalOverrideState(x.ProviderId, x.IgnoreAsSeed, x.Revision)).ToList();
    }

    public async Task<SignalOverrideMutation> SetSignalOverrideAsync(int userId, long id,
        SignalOverrideCommand command, CancellationToken ct = default)
    {
        if (id <= 0 || command.ClientMutationId == Guid.Empty || command.ExpectedRevision < 0)
            throw new FeedbackValidationException("error.feedback.titleAndRevisionRequired");
        var hash = Hash($"{id}|{command.IgnoreAsSeed}|{command.ExpectedRevision}");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var receipt = await db.RecommendationMutationReceipts.FirstOrDefaultAsync(
            x => x.UserId == userId && x.ClientMutationId == command.ClientMutationId, ct);
        if (receipt is not null)
        {
            if (receipt.Operation != "signal" || receipt.PayloadHash != hash)
                throw new FeedbackConflictException("error.feedback.mutationIdReused");
            return JsonSerializer.Deserialize<SignalOverrideMutation>(receipt.ResultJson, Json)!;
        }
        var state = await db.RecommendationSignalOverrides.FirstOrDefaultAsync(
            x => x.UserId == userId && x.Provider == "mangabaka" && x.ProviderId == id, ct);
        if (state is null)
        {
            if (command.ExpectedRevision != 0) throw new FeedbackConflictException("error.feedback.signalChanged");
            var allowed = ContentRating.Allowed(currentUser.MaxContentRating);
            if (command.IgnoreAsSeed && !await db.Series.AnyAsync(s =>
                    s.MangaBakaId == id && s.Incognito != IncognitoMode.Full &&
                    (s.ContentRating == null || allowed.Contains(s.ContentRating)), ct))
                throw new FeedbackNotFoundException("error.feedback.notAnEligibleSource");
            state = new RecommendationSignalOverride { UserId = userId, ProviderId = id };
            db.RecommendationSignalOverrides.Add(state);
        }
        if (state.Revision != command.ExpectedRevision)
            throw new FeedbackConflictException("error.feedback.signalChanged");
        var changed = state.IgnoreAsSeed != command.IgnoreAsSeed;
        if (changed)
        {
            state.IgnoreAsSeed = command.IgnoreAsSeed;
            state.Revision++;
            state.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        var versions = changed
            ? await BumpAsync(db, userId, feedback: false, signal: true, ct)
            : await VersionsAsync(db, userId, ct);
        var result = new SignalOverrideMutation(changed,
            new SignalOverrideState(id, state.IgnoreAsSeed, state.Revision), versions.SignalRevision);
        db.RecommendationMutationReceipts.Add(new RecommendationMutationReceipt
        {
            UserId = userId, ClientMutationId = command.ClientMutationId, Operation = "signal",
            ProviderId = id, PayloadHash = hash, ResultJson = JsonSerializer.Serialize(result, Json),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(90)
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<FeedbackMutation> MutateAsync(int userId, long id, FeedbackCommand command,
        CancellationToken ct = default)
    {
        if (id <= 0 || command.ClientMutationId == Guid.Empty || command.ExpectedRevision < 0)
            throw new FeedbackValidationException("error.feedback.titleAndRevisionRequired");
        if (string.IsNullOrWhiteSpace(command.Action))
            throw new FeedbackValidationException("error.feedback.actionRequired");
        var action = command.Action.Trim().ToLowerInvariant();
        if (action is not ("hide" or "dismiss" or "mark-exposed" or "clear-suppression" or
            "clear-exposure" or "like" or "dislike" or "clear-sentiment"))
            throw new FeedbackValidationException("error.feedback.unsupportedAction");
        var medium = ParseMedium(command.Medium);
        if (action == "mark-exposed" && command.Medium is not null &&
            medium == RecommendationExposure.None &&
            !string.Equals(command.Medium, "unspecified", StringComparison.OrdinalIgnoreCase))
            throw new FeedbackValidationException("error.feedback.unsupportedMedium");
        var hash = Hash($"{id}|{action}|{medium}|{command.ExpectedRevision}");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var receipt = await db.RecommendationMutationReceipts
            .FirstOrDefaultAsync(x => x.UserId == userId && x.ClientMutationId == command.ClientMutationId, ct);
        if (receipt is not null)
        {
            if (receipt.PayloadHash != hash || receipt.Operation != "feedback")
                throw new FeedbackConflictException("error.feedback.mutationIdReused");
            return await CurrentVisibilityAsync(receipt.ResultJson, ct);
        }
        var state = await db.RecommendationFeedback
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Provider == "mangabaka" && x.ProviderId == id, ct);
        if (state is null)
        {
            if (command.ExpectedRevision != 0) throw new FeedbackConflictException("error.feedback.changed");
            if (!await catalogue.IsAvailableAsync(ct))
                throw new FeedbackMetadataUnavailableException("error.feedback.catalogueUnavailable");
            var detail = await catalogue.GetDetailAsync(id, ct)
                ?? throw new FeedbackNotFoundException("error.feedback.unknownTitle");
            if (detail.ProviderId != id.ToString() ||
                detail.ContentRating is not null &&
                !ContentRating.Allowed(currentUser.MaxContentRating).Contains(detail.ContentRating))
                throw new FeedbackValidationException("error.feedback.titleNotAvailable");
            state = new RecommendationFeedback { UserId = userId, ProviderId = id, Title = detail.Title };
            db.RecommendationFeedback.Add(state);
        }
        if (state.Revision != command.ExpectedRevision)
            throw new FeedbackConflictException("error.feedback.changed");
        var before = JsonSerializer.Serialize(State(state), Json);
        var now = DateTime.UtcNow;
        var changed = RecommendationFeedbackPolicy.Apply(state, action, medium, now);
        RecommendationFeedbackEvent? evt = null;
        if (changed)
        {
            state.Revision++;
            state.UpdatedAtUtc = now;
            evt = new RecommendationFeedbackEvent
            {
                UserId = userId, ProviderId = id, Title = state.Title, Action = action,
                PreviousState = before, NewState = JsonSerializer.Serialize(State(state), Json),
                StateRevision = state.Revision, OccurredAtUtc = now, ClientMutationId = command.ClientMutationId
            };
            db.RecommendationFeedbackEvents.Add(evt);
            await db.SaveChangesAsync(ct);
        }
        var versions = changed
            ? await BumpAsync(db, userId, feedback: true, signal: false, ct)
            : await VersionsAsync(db, userId, ct);
        var permitted = await VisibleTitlesAsync([id], ct);
        var result = new FeedbackMutation(changed, evt?.Id,
            Hydrate(State(state), permitted.GetValueOrDefault(id)), versions.FeedbackRevision,
            versions.SignalRevision, RecommendationFeedbackPolicy.Suppresses(state, now) ? "suppressed" : "eligible",
            // "taste" says whether the inferred profile moved. Only the sentiment actions move it.
            // A like steers toward this one work; a dislike takes it out of the profile altogether,
            // which is a different answer and needs its own value rather than sharing "title-only".
            changed && action is "like" or "clear-sentiment" ? "title-only"
                : changed && action == "dislike" ? "negative-taste"
                : "unchanged",
            changed ? action switch
            {
                "like" => "positive-title",
                "dislike" => "negative-taste",
                "hide" => "negative-title",
                "dismiss" => "temporary",
                "mark-exposed" => "neutral-exposure",
                _ => "cleared"
            } : "none");
        db.RecommendationMutationReceipts.Add(new RecommendationMutationReceipt
        {
            UserId = userId, ClientMutationId = command.ClientMutationId, Operation = "feedback",
            ProviderId = id, PayloadHash = hash, ResultJson = JsonSerializer.Serialize(result, Json),
            ExpiresAtUtc = now.AddDays(90)
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// Applies one suppression to every member of <paramref name="id"/>'s franchise, for the reader
    /// who is looking at four near-identical rows of one anthology and wants them all gone.
    ///
    /// <para>
    /// Membership comes from the vector index's same-work component first
    /// (<see cref="SemanticRecommender.FranchiseMembersAsync"/>, the same definition the rails space
    /// a franchise out by) and from the dump's own relations when the index cannot answer
    /// (<see cref="MangaBakaLocalStore.GetSameWorkIdsAsync"/>). Members the reader cannot see are
    /// dropped before anything is written, and each member goes through
    /// <see cref="MutateAsync"/> so it gets its own event, its own revision and its own Undo.
    /// </para>
    /// </summary>
    public async Task<FranchiseFeedbackResult> HideFranchiseAsync(int userId, long id, string? action,
        Guid clientMutationId, CancellationToken ct = default)
    {
        if (id <= 0) throw new FeedbackValidationException("error.feedback.titleAndRevisionRequired");
        if (clientMutationId == Guid.Empty) throw new FeedbackValidationException("error.feedback.mutationIdRequired");
        var verb = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (verb is not ("hide" or "dismiss"))
            throw new FeedbackValidationException("error.feedback.unsupportedAction");
        if (!await catalogue.IsAvailableAsync(ct))
            throw new FeedbackMetadataUnavailableException("error.feedback.catalogueUnavailable");

        IReadOnlyList<long> members = semantic is null
            ? []
            : await semantic.FranchiseMembersAsync(id, MaxFranchiseMembers, ct);
        if (members.Count == 0)
        {
            members = await catalogue.GetSameWorkIdsAsync(id, MaxFranchiseMembers, ct);
        }

        var wanted = new List<long> { id };
        wanted.AddRange(members.Where(x => x != id));
        wanted = wanted.Take(MaxFranchiseMembers).ToList();

        var visible = await VisibleTitlesAsync(wanted, ct);
        if (!visible.ContainsKey(id)) throw new FeedbackNotFoundException("error.feedback.unknownTitle");

        var existing = await db.RecommendationFeedback.AsNoTracking()
            .Where(x => x.UserId == userId && wanted.Contains(x.ProviderId))
            .ToDictionaryAsync(x => x.ProviderId, ct);
        var changed = new List<FranchiseFeedbackTitle>();
        var revision = (await VersionsAsync(userId, ct)).FeedbackRevision;
        foreach (var member in wanted.Where(visible.ContainsKey))
        {
            var current = existing.GetValueOrDefault(member);
            if (verb == "hide" && current?.Suppression == RecommendationSuppression.Hidden) continue;
            var result = await MutateAsync(userId, member,
                new FeedbackCommand(verb, Derive(clientMutationId, member), current?.Revision ?? 0), ct);
            revision = result.FeedbackRevision;
            if (result.Changed) changed.Add(new FranchiseFeedbackTitle(member, result.State.Title));
        }

        return new FranchiseFeedbackResult(changed.Count, changed, revision);
    }

    /// <summary>
    /// One member's mutation id, derived from the batch's. Deterministic, so a client retrying the
    /// whole batch replays each member's receipt rather than writing a second event for it.
    /// </summary>
    private static Guid Derive(Guid batch, long member) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{batch}:{member}")).AsSpan(0, 16));

    public async Task<FeedbackMutation> UndoAsync(int userId, long eventId, Guid mutationId, long expectedRevision,
        CancellationToken ct = default)
    {
        if (mutationId == Guid.Empty) throw new FeedbackValidationException("error.feedback.mutationIdRequired");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var hash = Hash($"undo|{eventId}|{expectedRevision}");
        var receipt = await db.RecommendationMutationReceipts.FirstOrDefaultAsync(
            x => x.UserId == userId && x.ClientMutationId == mutationId, ct);
        if (receipt is not null)
        {
            if (receipt.Operation != "undo" || receipt.PayloadHash != hash)
                throw new FeedbackConflictException("error.feedback.mutationIdReused");
            return await CurrentVisibilityAsync(receipt.ResultJson, ct);
        }
        var evt = await db.RecommendationFeedbackEvents.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Id == eventId, ct)
            ?? throw new FeedbackNotFoundException("error.feedback.eventNotFound");
        var previous = JsonSerializer.Deserialize<FeedbackState>(evt.PreviousState, Json)!;
        var current = await db.RecommendationFeedback
            .FirstAsync(x => x.UserId == userId && x.ProviderId == evt.ProviderId, ct);
        if (current.Revision != expectedRevision || current.Revision != evt.StateRevision)
            throw new FeedbackConflictException("error.feedback.changedSinceAction");
        var before = JsonSerializer.Serialize(State(current), Json);
        current.Suppression = Enum.Parse<RecommendationSuppression>(previous.Suppression, true);
        current.Sentiment = Enum.Parse<RecommendationSentiment>(previous.Sentiment, true);
        current.DismissedUntilUtc = previous.DismissedUntilUtc;
        current.Exposure = previous.Exposure.Aggregate(RecommendationExposure.None,
            (value, medium) => value | ParseMedium(medium));
        current.Revision++;
        current.UpdatedAtUtc = DateTime.UtcNow;
        var undoEvent = new RecommendationFeedbackEvent
        {
            UserId = userId, ProviderId = current.ProviderId, Title = current.Title,
            Action = "undo", PreviousState = before,
            NewState = JsonSerializer.Serialize(State(current), Json),
            StateRevision = current.Revision, OccurredAtUtc = current.UpdatedAtUtc,
            ClientMutationId = mutationId
        };
        db.RecommendationFeedbackEvents.Add(undoEvent);
        await db.SaveChangesAsync(ct);
        var versions = await BumpAsync(db, userId, feedback: true, signal: false, ct);
        var permitted = await VisibleTitlesAsync([current.ProviderId], ct);
        var result = new FeedbackMutation(true, undoEvent.Id,
            Hydrate(State(current), permitted.GetValueOrDefault(current.ProviderId)), versions.FeedbackRevision,
            versions.SignalRevision,
            RecommendationFeedbackPolicy.Suppresses(current, DateTime.UtcNow) ? "suppressed" : "eligible",
            "unchanged", "restored");
        db.RecommendationMutationReceipts.Add(new RecommendationMutationReceipt
        {
            UserId = userId, ClientMutationId = mutationId, Operation = "undo", ProviderId = current.ProviderId,
            PayloadHash = hash, ResultJson = JsonSerializer.Serialize(result, Json),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(90)
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private async Task<FeedbackMutation> CurrentVisibilityAsync(string json, CancellationToken ct)
    {
        var result = JsonSerializer.Deserialize<FeedbackMutation>(json, Json)!;
        var entries = await VisibleTitlesAsync([result.State.MangaBakaId], ct);
        return result with { State = Hydrate(result.State, entries.GetValueOrDefault(result.State.MangaBakaId)) };
    }
    public async Task<Dictionary<long, CatalogueEntry>> VisibleTitlesAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return [];
        var hiddenIds = await HiddenLocalIdsAsync(wanted, ct);
        wanted = wanted.Where(id => !hiddenIds.Contains(id)).ToList();
        if (wanted.Count == 0) return [];
        if (!await catalogue.IsAvailableAsync(ct)) return [];
        var items = await catalogue.GetByIdsAsync(wanted,
            ContentRating.Allowed(currentUser.MaxContentRating), ct);
        return items.Where(x => long.TryParse(x.ProviderId, out _))
            .ToDictionary(x => long.Parse(x.ProviderId), x => new CatalogueEntry(
                x.Title, x.ThumbUrl ?? x.CoverUrl, [.. x.MatchedGenres]));
    }
    private async Task<HashSet<long>> HiddenLocalIdsAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return [];
        var localIds = (await db.Series.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.MangaBakaId != null && wanted.Contains(s.MangaBakaId.Value))
            .Select(s => (long)s.MangaBakaId!.Value).ToListAsync(ct)).ToHashSet();
        var allowed = ContentRating.Allowed(currentUser.MaxContentRating);
        var visibleIds = (await db.Series.AsNoTracking()
            .Where(s => s.MangaBakaId != null && wanted.Contains(s.MangaBakaId.Value) &&
                s.Incognito != IncognitoMode.Full &&
                (s.ContentRating == null || allowed.Contains(s.ContentRating)))
            .Select(s => (long)s.MangaBakaId!.Value).ToListAsync(ct)).ToHashSet();
        localIds.ExceptWith(visibleIds);
        return localIds;
    }
    private static RecommendationExposure ParseMedium(string? medium) => medium?.ToLowerInvariant() switch
    {
        "manga" => RecommendationExposure.Manga,
        "anime" => RecommendationExposure.Anime,
        "both" => RecommendationExposure.Manga | RecommendationExposure.Anime,
        "unspecified" => RecommendationExposure.Unspecified,
        null => RecommendationExposure.None,
        _ => RecommendationExposure.None
    };
    private static FeedbackState State(RecommendationFeedback x) => new(x.ProviderId,
        x.Suppression.ToString().ToLowerInvariant(),
        Enum.GetValues<RecommendationExposure>()
            .Where(flag => flag is RecommendationExposure.Manga or RecommendationExposure.Anime or RecommendationExposure.Unspecified
                && x.Exposure.HasFlag(flag))
            .Select(flag => flag.ToString().ToLowerInvariant()).ToArray(),
        x.DismissedUntilUtc, x.Revision, x.Title, x.Sentiment.ToString().ToLowerInvariant(),
        UpdatedAtUtc: x.UpdatedAtUtc);

    /// <summary>
    /// A state as the reader should read it now. An expired dismissal is not a suppression any more,
    /// and the row keeps saying "dismissed" until something writes to it, so the projection is where
    /// the window is applied. Manage signals counts its suppressed chip off this field.
    /// </summary>
    private static FeedbackState Live(FeedbackState state, DateTime now) =>
        state.Suppression == "dismissed" && state.DismissedUntilUtc <= now
            ? state with { Suppression = "none", DismissedUntilUtc = null }
            : state;

    private static FeedbackState Hydrate(FeedbackState state, CatalogueEntry? entry) =>
        state with { Title = entry?.Title, CoverUrl = entry?.CoverUrl, Genres = entry?.Genres };

    /// <summary>
    /// Catalogue ids this reader said they liked, for the seed pipeline.
    /// <para>
    /// Kept here rather than folded into <see cref="SuppressedAsync"/> because they pull the other
    /// way: a liked title is one the recommender should steer <em>towards</em>, and most of them are
    /// not in the library at all, which is the whole reason a rating could not express this.
    /// </para>
    /// </summary>
    public static async Task<HashSet<long>> LikedAsync(MakiDbContext db, int userId,
        CancellationToken ct = default) =>
        (await db.RecommendationFeedback.AsNoTracking()
            .Where(x => x.UserId == userId && x.Sentiment == RecommendationSentiment.Liked)
            .Select(x => x.ProviderId).ToListAsync(ct)).ToHashSet();

    /// <summary>
    /// Catalogue ids this reader thumbed down, for the seed pipeline's avoided set. The mirror of
    /// <see cref="LikedAsync"/>, and read the same way: most of them are not library rows either.
    /// </summary>
    public static async Task<HashSet<long>> DislikedAsync(MakiDbContext db, int userId,
        CancellationToken ct = default) =>
        (await db.RecommendationFeedback.AsNoTracking()
            .Where(x => x.UserId == userId && x.Sentiment == RecommendationSentiment.Disliked)
            .Select(x => x.ProviderId).ToListAsync(ct)).ToHashSet();
}
