using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public record FeedbackCommand(string Action, Guid ClientMutationId, long ExpectedRevision,
    string? Medium = null, FeedbackContext? Context = null);
public record FeedbackContext(string? Surface, string? ProfileVersion);
public record FeedbackState(long MangaBakaId, string Suppression, string[] Exposure,
    DateTime? DismissedUntilUtc, long Revision, string? Title, string Sentiment = "none");
public record FeedbackMutation(bool Changed, long? EventId, FeedbackState State,
    long FeedbackRevision, long SignalRevision, string QueueEffect, string TasteEffect,
    string FeedbackEffect = "none");
public record FeedbackPage<T>(IReadOnlyList<T> Items, long? NextCursor, long FeedbackRevision, long SignalRevision);
public record FeedbackActivity(long Id, long MangaBakaId, string? Title, string Action,
    DateTime OccurredAtUtc, long StateRevision, DateTime? DismissedUntilUtc,
    string QueueEffect, string TasteEffect);
public record SignalOverrideState(long MangaBakaId, bool IgnoreAsSeed, long Revision);
public record SignalOverrideCommand(bool IgnoreAsSeed, Guid ClientMutationId, long ExpectedRevision);
public record SignalOverrideMutation(bool Changed, SignalOverrideState State, long SignalRevision);
public sealed class FeedbackConflictException(string message) : Exception(message);
public sealed class FeedbackValidationException(string message) : Exception(message);
public sealed class FeedbackNotFoundException(string message) : Exception(message);
public sealed class FeedbackMetadataUnavailableException(string message) : Exception(message);

public class RecommendationFeedbackService(MakiDbContext db, MangaBakaLocalStore catalogue, ICurrentUser currentUser)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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

    public async Task<FeedbackPage<FeedbackState>> StatesAsync(int userId, long? cursor, int limit,
        CancellationToken ct = default, string? filter = null)
    {
        var hiddenIds = await HiddenLocalIdsAsync(await db.RecommendationFeedback.AsNoTracking()
            .Where(x => x.UserId == userId).Select(x => x.ProviderId).Distinct().ToListAsync(ct), ct);
        var query = db.RecommendationFeedback.AsNoTracking()
            .Where(x => x.UserId == userId && x.Id > (cursor ?? 0) &&
                (x.Suppression != RecommendationSuppression.None ||
                 x.Exposure != RecommendationExposure.None ||
                 x.Sentiment != RecommendationSentiment.None) &&
                !hiddenIds.Contains(x.ProviderId));
        query = filter switch
        {
            "hidden" => query.Where(x => x.Suppression == RecommendationSuppression.Hidden),
            "dismissed" => query.Where(x => x.Suppression == RecommendationSuppression.Dismissed),
            "exposed" => query.Where(x => x.Exposure != RecommendationExposure.None),
            "liked" => query.Where(x => x.Sentiment == RecommendationSentiment.Liked),
            "disliked" => query.Where(x => x.Sentiment == RecommendationSentiment.Disliked),
            _ => query
        };
        var rows = await query
            .OrderBy(x => x.Id).Take(Math.Clamp(limit, 1, 100) + 1).ToListAsync(ct);
        var versions = await VersionsAsync(userId, ct);
        var page = rows.Take(Math.Clamp(limit, 1, 100)).ToList();
        var titles = await VisibleTitlesAsync(page.Select(x => x.ProviderId), ct);
        return new FeedbackPage<FeedbackState>(page.Select(x => State(x) with
            { Title = titles.GetValueOrDefault(x.ProviderId) }).ToList(), rows.Count > page.Count ? page[^1].Id : null,
            versions.FeedbackRevision, versions.SignalRevision);
    }

    public async Task<FeedbackState?> CurrentStateAsync(int userId, long id, CancellationToken ct = default)
    {
        if ((await HiddenLocalIdsAsync([id], ct)).Contains(id)) return null;
        var state = await db.RecommendationFeedback.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.ProviderId == id, ct);
        if (state is null) return null;
        var titles = await VisibleTitlesAsync([id], ct);
        return State(state) with { Title = titles.GetValueOrDefault(id) };
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
        var titles = await VisibleTitlesAsync(page.Select(x => x.ProviderId), ct);
        return new FeedbackPage<FeedbackActivity>(page.Select(x =>
        {
            var after = JsonSerializer.Deserialize<FeedbackState>(x.NewState, Json)!;
            var suppressed = after.Exposure.Length > 0 || after.Suppression == "hidden" ||
                after.Suppression == "dismissed" && after.DismissedUntilUtc > DateTime.UtcNow;
            return new FeedbackActivity(x.Id, x.ProviderId, titles.GetValueOrDefault(x.ProviderId), x.Action,
                x.OccurredAtUtc, x.StateRevision, after.DismissedUntilUtc,
                suppressed ? "Title excluded" : "Title eligible",
                after.Sentiment switch
                {
                    "liked" => "Used as a taste signal",
                    "disliked" => "This title only, no genre inferred",
                    _ => "Taste unchanged",
                });
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
            throw new FeedbackValidationException("A valid title, mutation ID, and revision are required.");
        var hash = Hash($"{id}|{command.IgnoreAsSeed}|{command.ExpectedRevision}");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var receipt = await db.RecommendationMutationReceipts.FirstOrDefaultAsync(
            x => x.UserId == userId && x.ClientMutationId == command.ClientMutationId, ct);
        if (receipt is not null)
        {
            if (receipt.Operation != "signal" || receipt.PayloadHash != hash)
                throw new FeedbackConflictException("Mutation ID was used for a different action.");
            return JsonSerializer.Deserialize<SignalOverrideMutation>(receipt.ResultJson, Json)!;
        }
        var state = await db.RecommendationSignalOverrides.FirstOrDefaultAsync(
            x => x.UserId == userId && x.Provider == "mangabaka" && x.ProviderId == id, ct);
        if (state is null)
        {
            if (command.ExpectedRevision != 0) throw new FeedbackConflictException("Signal changed. Refresh and try again.");
            var allowed = ContentRating.Allowed(currentUser.MaxContentRating);
            if (command.IgnoreAsSeed && !await db.Series.AnyAsync(s =>
                    s.MangaBakaId == id && s.Incognito != IncognitoMode.Full &&
                    (s.ContentRating == null || allowed.Contains(s.ContentRating)), ct))
                throw new FeedbackNotFoundException("This title is not an eligible source in your visible library.");
            state = new RecommendationSignalOverride { UserId = userId, ProviderId = id };
            db.RecommendationSignalOverrides.Add(state);
        }
        if (state.Revision != command.ExpectedRevision)
            throw new FeedbackConflictException("Signal changed. Refresh and try again.");
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
            throw new FeedbackValidationException("A valid title, mutation ID, and revision are required.");
        if (string.IsNullOrWhiteSpace(command.Action))
            throw new FeedbackValidationException("A feedback action is required.");
        var action = command.Action.Trim().ToLowerInvariant();
        if (action is not ("hide" or "dismiss" or "mark-exposed" or "clear-suppression" or
            "clear-exposure" or "like" or "dislike" or "clear-sentiment"))
            throw new FeedbackValidationException("Unsupported feedback action.");
        var medium = ParseMedium(command.Medium);
        if (action == "mark-exposed" && command.Medium is not null &&
            medium == RecommendationExposure.None &&
            !string.Equals(command.Medium, "unspecified", StringComparison.OrdinalIgnoreCase))
            throw new FeedbackValidationException("Unsupported exposure medium.");
        var hash = Hash($"{id}|{action}|{medium}|{command.ExpectedRevision}");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var receipt = await db.RecommendationMutationReceipts
            .FirstOrDefaultAsync(x => x.UserId == userId && x.ClientMutationId == command.ClientMutationId, ct);
        if (receipt is not null)
        {
            if (receipt.PayloadHash != hash || receipt.Operation != "feedback")
                throw new FeedbackConflictException("Mutation ID was used for a different action.");
            return await CurrentVisibilityAsync(receipt.ResultJson, ct);
        }
        var state = await db.RecommendationFeedback
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Provider == "mangabaka" && x.ProviderId == id, ct);
        if (state is null)
        {
            if (command.ExpectedRevision != 0) throw new FeedbackConflictException("Feedback changed. Refresh and try again.");
            if (!await catalogue.IsAvailableAsync(ct))
                throw new FeedbackMetadataUnavailableException("Catalogue metadata is temporarily unavailable.");
            var detail = await catalogue.GetDetailAsync(id, ct)
                ?? throw new FeedbackNotFoundException("Unknown catalogue title.");
            if (detail.ProviderId != id.ToString() ||
                detail.ContentRating is not null &&
                !ContentRating.Allowed(currentUser.MaxContentRating).Contains(detail.ContentRating))
                throw new FeedbackValidationException("Catalogue title is not available to this account.");
            state = new RecommendationFeedback { UserId = userId, ProviderId = id, Title = detail.Title };
            db.RecommendationFeedback.Add(state);
        }
        if (state.Revision != command.ExpectedRevision)
            throw new FeedbackConflictException("Feedback changed. Refresh and try again.");
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
        var permittedTitles = await VisibleTitlesAsync([id], ct);
        var result = new FeedbackMutation(changed, evt?.Id,
            State(state) with { Title = permittedTitles.GetValueOrDefault(id) }, versions.FeedbackRevision,
            versions.SignalRevision, RecommendationFeedbackPolicy.Suppresses(state, now) ? "suppressed" : "eligible",
            // "taste" says whether the inferred profile moved. Only the sentiment actions move it,
            // and only for this one work: nothing here teaches the ranker about a genre or an author.
            changed && action is "like" or "dislike" or "clear-sentiment" ? "title-only" : "unchanged",
            changed ? action switch
            {
                "like" => "positive-title",
                "dislike" => "negative-title",
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

    public async Task<FeedbackMutation> UndoAsync(int userId, long eventId, Guid mutationId, long expectedRevision,
        CancellationToken ct = default)
    {
        if (mutationId == Guid.Empty) throw new FeedbackValidationException("A mutation ID is required.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var hash = Hash($"undo|{eventId}|{expectedRevision}");
        var receipt = await db.RecommendationMutationReceipts.FirstOrDefaultAsync(
            x => x.UserId == userId && x.ClientMutationId == mutationId, ct);
        if (receipt is not null)
        {
            if (receipt.Operation != "undo" || receipt.PayloadHash != hash)
                throw new FeedbackConflictException("Mutation ID was used for a different action.");
            return await CurrentVisibilityAsync(receipt.ResultJson, ct);
        }
        var evt = await db.RecommendationFeedbackEvents.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Id == eventId, ct)
            ?? throw new FeedbackNotFoundException("Feedback event was not found.");
        var previous = JsonSerializer.Deserialize<FeedbackState>(evt.PreviousState, Json)!;
        var current = await db.RecommendationFeedback
            .FirstAsync(x => x.UserId == userId && x.ProviderId == evt.ProviderId, ct);
        if (current.Revision != expectedRevision || current.Revision != evt.StateRevision)
            throw new FeedbackConflictException("Feedback changed since this action.");
        var before = JsonSerializer.Serialize(State(current), Json);
        current.Suppression = Enum.Parse<RecommendationSuppression>(previous.Suppression, true);
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
        var permittedTitles = await VisibleTitlesAsync([current.ProviderId], ct);
        var result = new FeedbackMutation(true, undoEvent.Id,
            State(current) with { Title = permittedTitles.GetValueOrDefault(current.ProviderId) }, versions.FeedbackRevision,
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
        var titles = await VisibleTitlesAsync([result.State.MangaBakaId], ct);
        return result with { State = result.State with { Title = titles.GetValueOrDefault(result.State.MangaBakaId) } };
    }
    private async Task<Dictionary<long, string>> VisibleTitlesAsync(IEnumerable<long> ids, CancellationToken ct)
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
            .ToDictionary(x => long.Parse(x.ProviderId), x => x.Title);
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
        x.DismissedUntilUtc, x.Revision, x.Title, x.Sentiment.ToString().ToLowerInvariant());

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
}
