using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Security;
using Maki.Metadata.Catalogue;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/recommendations")]
public class RecommendationController(
    RecommendationService recommendations,
    ICurrentUser currentUser,
    DiscoverService discover,
    RecentActivityRailService recentActivity,
    TasteProfileService tasteProfile,
    TasteInsightsService tasteInsights,
    ReadingBehaviourService readingBehaviour,
    ReaderCohortService readerCohorts,
    MangaBakaLocalStore store,
    EmbeddingStore embeddings,
    IUserSettings userSettings,
    MalReviewClient reviews) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Get([FromBody] RecommendationRequest? request, CancellationToken ct)
    {
        try
        {
            return Ok(await recommendations.GetAsync(request ?? new RecommendationRequest(), currentUser, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// The caller's own taste profile: what they read most, weighted the way the recommender weights
    /// its seeds.
    ///
    /// <para>
    /// There is no user parameter and deliberately no <c>UserViewResolver</c> hook. Every other
    /// aggregate on this instance can be read for somebody else by an admin; this one answers only
    /// for whoever asked.
    /// </para>
    /// </summary>
    /// <param name="view">
    /// <c>shelf</c> for the whole library, anything else for the series they have actually read.
    /// </param>
    [HttpGet("taste-profile")]
    public async Task<IActionResult> TasteProfile(
        [FromQuery] string? view, [FromQuery] bool refresh, CancellationToken ct)
    {
        if (!await store.IsAvailableAsync(ct))
        {
            return BadRequest(new
            {
                error = "Your taste profile needs the local MangaBaka database (Settings → Metadata → local DB)",
            });
        }

        var parsed = string.Equals(view, "shelf", StringComparison.OrdinalIgnoreCase)
            ? TasteView.Shelf
            : TasteView.Read;
        return Ok(await tasteProfile.GetAsync(currentUser, parsed, refresh, ct));
    }

    /// <summary>
    /// What the vectors say about the caller: the distinct things they read, which of their series
    /// is the odd one out, how their taste has moved, and what sits next to them untouched.
    ///
    /// <para>
    /// Never errors on a missing index or a thin library. Those are ordinary states and come back as
    /// <c>unavailable</c> with a reason, because the page around this has other sections that work.
    /// </para>
    /// </summary>
    [HttpGet("taste-insights")]
    public async Task<IActionResult> TasteInsights(
        [FromQuery] string? view, [FromQuery] bool refresh, CancellationToken ct)
    {
        if (!await store.IsAvailableAsync(ct))
        {
            return Ok(new
            {
                unavailable = "Needs the local MangaBaka database (Settings → Metadata → local DB)",
                clusters = Array.Empty<object>(),
                drift = Array.Empty<object>(),
            });
        }

        var parsed = string.Equals(view, "shelf", StringComparison.OrdinalIgnoreCase)
            ? TasteView.Shelf
            : TasteView.Read;
        return Ok(await tasteInsights.GetAsync(currentUser, parsed, refresh, ct));
    }

    /// <summary>
    /// How the caller reads: what they finish, how fast, where they give up. Needs no catalogue, so
    /// unlike everything else on this controller it answers on an install with no dump at all.
    /// </summary>
    [HttpGet("reading-behaviour")]
    public async Task<IActionResult> ReadingBehaviour([FromQuery] bool refresh, CancellationToken ct) =>
        Ok(await readingBehaviour.GetAsync(currentUser, refresh, ct));

    /// <summary>
    /// Catalogue-browse rails (Popular / New / Trending / Top rated / per-type) for the Discover
    /// tab — independent of the library, but bounded by the caller's own content-rating ceiling.
    /// Cached per ceiling; <paramref name="refresh"/> recomputes the caller's.
    /// </summary>
    [HttpGet("discover")]
    public async Task<IActionResult> Discover([FromQuery] bool refresh, CancellationToken ct)
    {
        try
        {
            return Ok(await discover.GetFeedsAsync(refresh, currentUser.MaxContentRating, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// The one personalised Discover rail: picks seeded from the caller's most recently read series.
    /// Per user, so it is fetched separately from <see cref="Discover"/> rather than folded into it —
    /// those rails are cached across users and know nothing about the viewer beyond their
    /// content-rating ceiling.
    /// <para>
    /// Answers <c>null</c> (200, not 404) when the caller has no reading history to seed with, or no
    /// recently-read series carrying a MangaBaka id. That is an ordinary state for a new account, not
    /// a missing resource, and the client just leaves the row out.
    /// </para>
    /// </summary>
    [HttpGet("discover/recent")]
    public async Task<IActionResult> DiscoverRecent([FromQuery] bool refresh, CancellationToken ct)
    {
        try
        {
            return Ok(await recentActivity.GetAsync(currentUser, refresh, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>One "Popular in {genre}" rail per genre for the Discover Genres tab. Cached.</summary>
    [HttpGet("discover/genres")]
    public async Task<IActionResult> DiscoverGenres([FromQuery] bool refresh, CancellationToken ct)
    {
        try
        {
            return Ok(await discover.GetGenreFeedsAsync(refresh, currentUser.MaxContentRating, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// The expanded "Show more" view of a single rail: same ordering, the user's filters applied,
    /// a higher limit. Not cached.
    /// </summary>
    [HttpPost("discover/feed")]
    public async Task<IActionResult> DiscoverFeed([FromBody] DiscoverFeedRequest request, CancellationToken ct)
    {
        try
        {
            var clamped = (request.Filters ?? RecommendationFilters.None) with
            {
                ContentRatings = request.Filters?.ContentRatings is { Count: > 0 } requested
                    ? ContentRating.Clamp(requested, currentUser.MaxContentRating)
                    : ContentRating.Allowed(currentUser.MaxContentRating)
            };
            return Ok(await discover.GetFeedAsync(request with { Filters = clamped }, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Free-text Discover search: a plot description, a mood, or a title. Answered by the
    /// embedding index when it's built, by the FTS5 title index otherwise (the response's
    /// <c>mode</c> says which). Not cached — it's a per-keystroke user query.
    /// </summary>
    [HttpPost("discover/search")]
    public async Task<IActionResult> DiscoverSearch([FromBody] DiscoverSearchRequest request, CancellationToken ct)
    {
        try
        {
            var clamped = (request.Filters ?? RecommendationFilters.None) with
            {
                ContentRatings = request.Filters?.ContentRatings is { Count: > 0 } requested
                    ? ContentRating.Clamp(requested, currentUser.MaxContentRating)
                    : ContentRating.Allowed(currentUser.MaxContentRating)
            };
            return Ok(await discover.SearchAsync(request with { Filters = clamped }, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// One creator, artist or publisher and their works. POST rather than GET because it carries a
    /// filter body, and because it needs the same content-rating clamp the other two POSTs do.
    /// 404 when the name is not in the catalogue, which is also what an unbuilt credit index gives.
    /// </summary>
    [HttpPost("creator")]
    public async Task<IActionResult> Creator([FromBody] CreatorRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "name is required" });
        }

        try
        {
            var clamped = (request.Filters ?? RecommendationFilters.None) with
            {
                ContentRatings = request.Filters?.ContentRatings is { Count: > 0 } requested
                    ? ContentRating.Clamp(requested, currentUser.MaxContentRating)
                    : ContentRating.Allowed(currentUser.MaxContentRating)
            };

            var profile = await discover.GetCreatorAsync(request with { Filters = clamped }, ct);
            return profile is null ? NotFound(new { error = "No such creator" }) : Ok(profile);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Name suggestions for a partly typed creator or publisher, for the search box's autocomplete.
    /// </summary>
    [HttpGet("credits")]
    public async Task<IActionResult> Credits(
        [FromQuery] string q, [FromQuery] string? role, [FromQuery] int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Ok(Array.Empty<ResolvedCredit>());
        }

        return Ok(await discover.SuggestCreditsAsync(q, role, limit <= 0 ? 10 : limit, ct));
    }

    /// <summary>
    /// The caller's saved Recommended-panel defaults, applied by the client on first render. Reads
    /// as an empty spec when they have never saved one — no separate "unset" shape, because a spec
    /// with nothing set means exactly the same thing.
    /// </summary>
    [HttpGet("defaults")]
    public async Task<IActionResult> GetDefaults(CancellationToken ct) =>
        Ok(RecommendationDefaultsSpec.Parse(
            await userSettings.GetAsync(SettingKeys.RecommendationsDefaults, ct)));

    /// <summary>
    /// Saves the panel as the caller's default. Per user and needs no permission — it is that
    /// person's own preference, and it changes nothing about what they are allowed to see.
    /// <para>
    /// A spec with nothing set deletes the row instead of storing "{}", so the same button clears a
    /// default (reset the panel, save) as sets one.
    /// </para>
    /// </summary>
    [HttpPut("defaults")]
    public async Task<IActionResult> SetDefaults(
        [FromBody] RecommendationDefaultsSpec request, CancellationToken ct)
    {
        var spec = (request ?? RecommendationDefaultsSpec.Empty).Normalize();
        await userSettings.SetAsync(
            SettingKeys.RecommendationsDefaults,
            spec.IsEmpty ? null : RecommendationDefaultsSpec.Serialize(spec),
            ct);
        return Ok(spec);
    }

    /// <summary>
    /// The caller's saved Discover-search filter defaults, applied by the client on first render.
    /// Reads as an empty spec when they have never saved one. Separate from
    /// <see cref="GetDefaults"/> because the two panels carry different things — see
    /// <see cref="SearchDefaultsSpec"/>.
    /// </summary>
    [HttpGet("discover/searchdefaults")]
    public async Task<IActionResult> GetSearchDefaults(CancellationToken ct) =>
        Ok(SearchDefaultsSpec.Parse(
            await userSettings.GetAsync(SettingKeys.DiscoverSearchDefaults, ct)));

    /// <summary>
    /// Saves the search filter panel as the caller's default. Per user and needs no permission —
    /// it is that person's own preference, and it constrains what they see rather than widening it.
    /// <para>
    /// A spec with nothing set deletes the row instead of storing "{}", so the same button clears a
    /// default (reset the panel, save) as sets one.
    /// </para>
    /// </summary>
    [HttpPut("discover/searchdefaults")]
    public async Task<IActionResult> SetSearchDefaults(
        [FromBody] SearchDefaultsSpec request, CancellationToken ct)
    {
        var spec = (request ?? SearchDefaultsSpec.Empty).Normalize();
        await userSettings.SetAsync(
            SettingKeys.DiscoverSearchDefaults,
            spec.IsEmpty ? null : SearchDefaultsSpec.Serialize(spec),
            ct);
        return Ok(spec);
    }

    /// <summary>
    /// Tag names for the Discover tag filter, from the embedding index's tags_v2 vocabulary
    /// (non-spoiler, most-used first). Empty until the index has been built.
    /// </summary>
    [HttpGet("tags")]
    public IActionResult Tags()
    {
        embeddings.EnsureSchema();
        var names = embeddings.GetVocab().Values
            .Where(t => !t.IsSpoiler)
            .OrderByDescending(t => t.SeriesCount)
            .Select(t => t.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(names);
    }

    /// <summary>Rich detail for one MangaBaka series (for the Discover detail card).</summary>
    [HttpGet("detail/{id:long}")]
    public async Task<IActionResult> Detail(long id, CancellationToken ct)
    {
        if (!await store.IsAvailableAsync(ct))
        {
            return BadRequest(new { error = "The local MangaBaka database is not available." });
        }

        var detail = await store.GetDetailAsync(id, ct);
        if (detail is null)
        {
            return NotFound();
        }

        // Composed here rather than inside the store: the detail row is the same for everybody and
        // the hint is the caller's alone, so mixing them at the query would put a user in a path
        // that has no business knowing about one. Same split MangaBakaRecommendation's "why" flags
        // already use, which the recommender fills rather than the store.
        return Ok(detail with { ReaderHint = await readerCohorts.GetHintAsync(currentUser, id, ct) });
    }

    /// <summary>A few MyAnimeList reviews for a series (lazy; best-effort, scraped from MAL).</summary>
    [HttpGet("reviews/{malId:int}")]
    public async Task<IActionResult> Reviews(int malId, CancellationToken ct) =>
        Ok(await reviews.GetReviewsAsync(malId, ct));
}
