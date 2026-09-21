using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Security;
using Maki.Core.Entities;
using Maki.Data;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/recommendations")]
public class RecommendationFeedbackController(RecommendationFeedbackService feedback, ICurrentUser user,
    ILocalizer localizer,
    MakiDbContext db, SemanticRecommender semantic, BehavioralTasteService behavioural,
    IAppSettings settings, SeedWeightService seedWeights,
    TasteAvoidanceService avoidance, AnimeSignalSources animeSources) : ControllerBase
{
    [HttpGet("feedback-lab")]
    public async Task<IActionResult> Lab(CancellationToken ct)
    {
        var versions = await feedback.VersionsAsync(user.UserId, ct);
        var activity = await feedback.ActivityAsync(user.UserId, null, 20, ct);
        var allowed = ContentRating.Allowed(user.MaxContentRating);
        var now = DateTime.UtcNow;
        var feedbackCounts = await db.RecommendationFeedback.AsNoTracking()
            .Where(x => x.UserId == user.UserId &&
                // Sentiment belongs in here on its own account: a thumbs up leaves suppression and
                // exposure untouched, so without it every liked title counts as nothing.
                (x.Suppression == RecommendationSuppression.Hidden ||
                 x.Suppression == RecommendationSuppression.Dismissed && x.DismissedUntilUtc > now ||
                 x.Exposure != RecommendationExposure.None ||
                 x.Sentiment != RecommendationSentiment.None))
            .ToListAsync(ct);
        var sources = await db.Series.AsNoTracking()
            .Where(s => s.MangaBakaId != null && s.Incognito != IncognitoMode.Full &&
                (s.ContentRating == null || allowed.Contains(s.ContentRating)))
            .Select(s => new
            {
                s.MangaBakaId, s.Title,
                AddedAtUtc = db.UserSeriesStates.Where(x => x.UserId == user.UserId && x.SeriesId == s.Id)
                    .Select(x => x.AddedToLibraryAtUtc).FirstOrDefault(),
                Rating = db.UserSeriesStates.Where(x => x.UserId == user.UserId && x.SeriesId == s.Id)
                    .Select(x => x.Rating).FirstOrDefault()
            }).ToListAsync(ct);
        var shelfIds = sources.Select(x => (long)x.MangaBakaId!.Value).Distinct().ToList();
        var readIds = (await behavioural.ReadSignalsAsync(db, user.UserId, shelfIds, ct)).Keys.ToHashSet();
        var excludedIds = (await db.RecommendationSignalOverrides.AsNoTracking()
            .Where(x => x.UserId == user.UserId && x.IgnoreAsSeed)
            .Select(x => x.ProviderId).ToListAsync(ct)).ToHashSet();
        var entries = await feedback.VisibleTitlesAsync(shelfIds, ct);
        // The same snapshot the recommender steers with, so the chips describe the set that is
        // actually being subtracted rather than a second count of the feedback rows.
        var snapshot = await seedWeights.SnapshotAsync(db, user, ct);
        var avoids = await avoidance.LabelsAsync(user, snapshot.Avoided, allowed, ct);
        var weightingEnabled = await settings.GetAsync(SettingKeys.RecommendationsPersonalAddWeighting, ct) != "false";
        var labUiEnabled = await settings.GetAsync(SettingKeys.RecommendationsFeedbackLab, ct) != "false";
        // Whether the anime panel is worth rendering at all: the instance allows it and this reader
        // has a tracker it could actually read. Deliberately NOT their opt-in - the client gates the
        // whole section on this, opt-in switch included, so folding that in would make the switch
        // impossible to reach. The opt-in state is `enabled` on GET anime-signals.
        var animeSignalsAvailable =
            await settings.GetAsync(SettingKeys.RecommendationsAnimeSignals, ct) != "false" &&
            (await animeSources.ConnectedAsync(user.UserId, ct)).Count > 0;
        return Ok(new
        {
            capabilities = new { feedback = true, signalOverrides = true, labUi = labUiEnabled,
                personalAddWeighting = weightingEnabled && semantic.IsReady(),
                animeSignals = animeSignalsAvailable },
            rankingMode = semantic.IsReady() ? "semantic" : "fallback",
            versions = new { versions.FeedbackRevision, versions.SignalRevision },
            summary = new
            {
                visibleShelf = sources.Select(x => x.MangaBakaId).Distinct().Count(),
                personalAdds = sources.Where(x => x.AddedAtUtc != null).Select(x => x.MangaBakaId).Distinct().Count(),
                ratedSources = sources.Where(x => x.Rating != null).Select(x => x.MangaBakaId).Distinct().Count(),
                readSources = readIds.Count,
                excluded = excludedIds.Count(shelfIds.Contains),
                hidden = feedbackCounts.Count(x => x.Suppression == RecommendationSuppression.Hidden),
                dismissed = feedbackCounts.Count(x => x.Suppression == RecommendationSuppression.Dismissed && x.DismissedUntilUtc > now),
                exposed = feedbackCounts.Count(x => x.Exposure != RecommendationExposure.None),
                liked = feedbackCounts.Count(x => x.Sentiment == RecommendationSentiment.Liked),
                disliked = feedbackCounts.Count(x => x.Sentiment == RecommendationSentiment.Disliked),
                // Thumbs down plus low-rated shelf titles, deduplicated, because a title that is
                // both is one push and not two. Taken off the snapshot rather than recounted here.
                pushingDown = snapshot.Avoided.Count
            },
            avoids,
            sources = sources.GroupBy(x => x.MangaBakaId)
                .Select(g => new
                {
                    mangaBakaId = g.Key,
                    title = g.First().Title,
                    addedAtUtc = g.Max(x => x.AddedAtUtc),
                    rating = g.Select(x => x.Rating).FirstOrDefault(x => x != null),
                    coverUrl = entries.GetValueOrDefault((long)g.Key!.Value)?.CoverUrl,
                    genres = entries.GetValueOrDefault((long)g.Key!.Value)?.Genres ?? Array.Empty<string>(),
                    isRead = readIds.Contains((long)g.Key!.Value),
                    excluded = excludedIds.Contains((long)g.Key!.Value)
                }),
            activity
        });
    }

    [HttpGet("feedback")]
    public async Task<IActionResult> States([FromQuery] long? cursor, [FromQuery] int limit,
        [FromQuery] string? state, [FromQuery] string? sort, CancellationToken ct)
    {
        if (state is not null and not ("hidden" or "dismissed" or "exposed"))
            return this.Fail(localizer, "error.feedback.unsupportedState");
        if (sort is not null and not ("recent" or "title"))
            return this.Fail(localizer, "error.feedback.unsupportedSort");
        return Ok(await feedback.StatesAsync(user.UserId, cursor, limit <= 0 ? 40 : limit, ct, state, sort));
    }

    [HttpGet("feedback/activity")]
    public async Task<IActionResult> Activity([FromQuery] long? cursor, [FromQuery] int limit, CancellationToken ct) =>
        Ok(await feedback.ActivityAsync(user.UserId, cursor, limit <= 0 ? 40 : limit, ct));

    [HttpGet("feedback/{id:long}")]
    public async Task<IActionResult> State(long id, CancellationToken ct)
    {
        var state = await feedback.CurrentStateAsync(user.UserId, id, ct);
        return state is null ? NotFound() : Ok(state);
    }

    [HttpPut("feedback/{id:long}")]
    public async Task<IActionResult> Mutate(long id, [FromBody] FeedbackCommand command, CancellationToken ct)
    {
        try { return Ok(await feedback.MutateAsync(user.UserId, id, command, ct)); }
        // The conflict body carries the current state beside the message, so it is built here rather
        // than through ApiResults; the two fields it shares with every other failure keep their names.
        catch (FeedbackConflictException ex) { return Conflict(new { code = ex.Key,
            error = localizer.Get(ex.Key, ex.Args),
            current = await feedback.CurrentStateAsync(user.UserId, id, ct) }); }
        catch (FeedbackNotFoundException ex) { return this.NotFoundMessage(localizer, ex.Key, ex.Args); }
        catch (FeedbackMetadataUnavailableException ex) { return this.Unavailable(localizer, ex.Key, ex.Args); }
        catch (FeedbackValidationException ex) { return this.Fail(localizer, ex.Key, ex.Args); }
        catch (DbUpdateException ex) when (IsWriteConflict(ex)) { return this.Conflict(localizer, "error.feedback.changed"); }
        catch (SqliteException ex) when (IsWriteConflict(ex)) { return this.Conflict(localizer, "error.feedback.changed"); }
    }

    [HttpPost("feedback/{id:long}/franchise")]
    public async Task<IActionResult> MutateFranchise(long id, [FromBody] FranchiseFeedbackCommand command,
        CancellationToken ct)
    {
        try { return Ok(await feedback.HideFranchiseAsync(user.UserId, id, command.Action, command.ClientMutationId, ct)); }
        catch (FeedbackConflictException ex) { return this.Conflict(localizer, ex.Key, ex.Args); }
        catch (FeedbackNotFoundException ex) { return this.NotFoundMessage(localizer, ex.Key, ex.Args); }
        catch (FeedbackMetadataUnavailableException ex) { return this.Unavailable(localizer, ex.Key, ex.Args); }
        catch (FeedbackValidationException ex) { return this.Fail(localizer, ex.Key, ex.Args); }
        catch (DbUpdateException ex) when (IsWriteConflict(ex)) { return this.Conflict(localizer, "error.feedback.changed"); }
        catch (SqliteException ex) when (IsWriteConflict(ex)) { return this.Conflict(localizer, "error.feedback.changed"); }
    }

    [HttpPost("feedback/events/{id:long}/undo")]
    public async Task<IActionResult> Undo(long id, [FromBody] FeedbackUndoCommand command, CancellationToken ct)
    {
        try { return Ok(await feedback.UndoAsync(user.UserId, id, command.ClientMutationId, command.ExpectedRevision, ct)); }
        catch (FeedbackConflictException ex) { return this.Conflict(localizer, ex.Key, ex.Args); }
        catch (FeedbackNotFoundException ex) { return this.NotFoundMessage(localizer, ex.Key, ex.Args); }
        catch (FeedbackValidationException ex) { return this.Fail(localizer, ex.Key, ex.Args); }
        catch (DbUpdateException ex) when (IsWriteConflict(ex)) { return this.Conflict(localizer, "error.feedback.changed"); }
        catch (SqliteException ex) when (IsWriteConflict(ex)) { return this.Conflict(localizer, "error.feedback.changed"); }
    }

    [HttpGet("signal-overrides")]
    public async Task<IActionResult> SignalOverrides(CancellationToken ct) =>
        Ok(await feedback.SignalOverridesAsync(user.UserId, ct));

    [HttpPut("signal-overrides/{id:long}")]
    public async Task<IActionResult> SetSignalOverride(long id, [FromBody] SignalOverrideCommand command, CancellationToken ct)
    {
        try { return Ok(await feedback.SetSignalOverrideAsync(user.UserId, id, command, ct)); }
        catch (FeedbackConflictException ex) { return this.Conflict(localizer, ex.Key, ex.Args); }
        catch (FeedbackNotFoundException ex) { return this.NotFoundMessage(localizer, ex.Key, ex.Args); }
        catch (FeedbackValidationException ex) { return this.Fail(localizer, ex.Key, ex.Args); }
        catch (DbUpdateException ex) when (IsWriteConflict(ex)) { return this.Conflict(localizer, "error.feedback.signalChanged"); }
        catch (SqliteException ex) when (IsWriteConflict(ex)) { return this.Conflict(localizer, "error.feedback.signalChanged"); }
    }

    [HttpDelete("signal-overrides/{id:long}")]
    public async Task<IActionResult> ClearSignalOverride(long id,
        [FromHeader(Name = "X-Client-Mutation-Id")] Guid mutationId,
        [FromHeader(Name = "If-Match")] long expectedRevision, CancellationToken ct) =>
        await SetSignalOverride(id, new SignalOverrideCommand(false, mutationId, expectedRevision), ct);

    public record FeedbackUndoCommand(Guid ClientMutationId, long ExpectedRevision);

    private static bool IsWriteConflict(Exception exception) => exception switch
    {
        DbUpdateConcurrencyException => true,
        SqliteException sqlite => sqlite.SqliteErrorCode is 5 or 6 or 19,
        DbUpdateException update when update.InnerException is not null => IsWriteConflict(update.InnerException),
        _ => false
    };
}
