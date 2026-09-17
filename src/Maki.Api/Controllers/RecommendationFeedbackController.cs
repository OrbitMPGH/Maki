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
    MakiDbContext db, SemanticRecommender semantic, MangaBakaLocalStore store,
    TasteProfileService tasteProfile, BehavioralTasteService behavioural,
    IAppSettings settings) : ControllerBase
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
        var readSources = (await behavioural.ReadSignalsAsync(db, user.UserId,
            sources.Select(x => (long)x.MangaBakaId!.Value).Distinct().ToList(), ct)).Count;
        var dimensions = new List<object>();
        if (await store.IsAvailableAsync(ct))
        {
            var profile = await tasteProfile.GetAsync(user, TasteView.Shelf, refresh: false, ct);
            dimensions.AddRange(profile.Genres.Take(5).Select(x => Dimension("genre", x)));
            dimensions.AddRange(profile.Tags.Take(5).Select(x => Dimension("theme", x)));
            dimensions.AddRange(profile.Creators.Take(5).Select(x => Dimension("creator", x)));
            dimensions.AddRange(profile.Types.Take(3).Select(x => Dimension("format", x)));
        }
        var weightingEnabled = await settings.GetAsync(SettingKeys.RecommendationsPersonalAddWeighting, ct) != "false";
        var labUiEnabled = await settings.GetAsync(SettingKeys.RecommendationsFeedbackLab, ct) != "false";
        return Ok(new
        {
            capabilities = new { feedback = true, signalOverrides = true, labUi = labUiEnabled,
                personalAddWeighting = weightingEnabled && semantic.IsReady() },
            rankingMode = semantic.IsReady() ? "semantic" : "fallback",
            versions = new { versions.FeedbackRevision, versions.SignalRevision },
            summary = new
            {
                visibleShelf = sources.Select(x => x.MangaBakaId).Distinct().Count(),
                personalAdds = sources.Where(x => x.AddedAtUtc != null).Select(x => x.MangaBakaId).Distinct().Count(),
                ratedSources = sources.Where(x => x.Rating != null).Select(x => x.MangaBakaId).Distinct().Count(),
                readSources,
                hidden = feedbackCounts.Count(x => x.Suppression == RecommendationSuppression.Hidden),
                dismissed = feedbackCounts.Count(x => x.Suppression == RecommendationSuppression.Dismissed && x.DismissedUntilUtc > now),
                exposed = feedbackCounts.Count(x => x.Exposure != RecommendationExposure.None),
                liked = feedbackCounts.Count(x => x.Sentiment == RecommendationSentiment.Liked),
                disliked = feedbackCounts.Count(x => x.Sentiment == RecommendationSentiment.Disliked)
            },
            dimensions,
            sources = sources.GroupBy(x => x.MangaBakaId)
                .Select(g => new
                {
                    mangaBakaId = g.Key,
                    title = g.First().Title,
                    addedAtUtc = g.Max(x => x.AddedAtUtc),
                    rating = g.Select(x => x.Rating).FirstOrDefault(x => x != null)
                }),
            changes = activity.Items.Take(3).Select(x => new
            {
                x.MangaBakaId, x.Title, x.OccurredAtUtc,
                x.QueueEffect, x.TasteEffect
            }),
            activity
        });
    }

    [HttpGet("feedback")]
    public async Task<IActionResult> States([FromQuery] long? cursor, [FromQuery] int limit,
        [FromQuery] string? state, CancellationToken ct)
    {
        if (state is not null and not ("hidden" or "dismissed" or "exposed"))
            return BadRequest(new { error = "Unsupported feedback state" });
        return Ok(await feedback.StatesAsync(user.UserId, cursor, limit <= 0 ? 40 : limit, ct, state));
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
        catch (FeedbackConflictException ex) { return Conflict(new { error = ex.Message,
            current = await feedback.CurrentStateAsync(user.UserId, id, ct) }); }
        catch (FeedbackNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (FeedbackMetadataUnavailableException ex) { return StatusCode(503, new { error = ex.Message }); }
        catch (FeedbackValidationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (DbUpdateException ex) when (IsWriteConflict(ex)) { return Conflict(new { error = "Feedback changed. Refresh and try again." }); }
        catch (SqliteException ex) when (IsWriteConflict(ex)) { return Conflict(new { error = "Feedback changed. Refresh and try again." }); }
    }

    [HttpPost("feedback/events/{id:long}/undo")]
    public async Task<IActionResult> Undo(long id, [FromBody] FeedbackUndoCommand command, CancellationToken ct)
    {
        try { return Ok(await feedback.UndoAsync(user.UserId, id, command.ClientMutationId, command.ExpectedRevision, ct)); }
        catch (FeedbackConflictException ex) { return Conflict(new { error = ex.Message }); }
        catch (FeedbackNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (FeedbackValidationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (DbUpdateException ex) when (IsWriteConflict(ex)) { return Conflict(new { error = "Feedback changed. Refresh and try again." }); }
        catch (SqliteException ex) when (IsWriteConflict(ex)) { return Conflict(new { error = "Feedback changed. Refresh and try again." }); }
    }

    [HttpGet("signal-overrides")]
    public async Task<IActionResult> SignalOverrides(CancellationToken ct) =>
        Ok(await feedback.SignalOverridesAsync(user.UserId, ct));

    [HttpPut("signal-overrides/{id:long}")]
    public async Task<IActionResult> SetSignalOverride(long id, [FromBody] SignalOverrideCommand command, CancellationToken ct)
    {
        try { return Ok(await feedback.SetSignalOverrideAsync(user.UserId, id, command, ct)); }
        catch (FeedbackConflictException ex) { return Conflict(new { error = ex.Message }); }
        catch (FeedbackNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (FeedbackValidationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (DbUpdateException ex) when (IsWriteConflict(ex)) { return Conflict(new { error = "Signal changed. Refresh and try again." }); }
        catch (SqliteException ex) when (IsWriteConflict(ex)) { return Conflict(new { error = "Signal changed. Refresh and try again." }); }
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

    private static object Dimension(string kind, TasteFacet facet) => new
    {
        kind, label = facet.Name, evidenceCount = facet.Support,
        confidence = facet.Support >= 3 ? "supported" : "limited evidence",
        sources = Array.Empty<object>(), effect = "observed library evidence"
    };
}
