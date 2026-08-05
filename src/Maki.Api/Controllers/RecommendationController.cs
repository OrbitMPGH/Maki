using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Security;
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
    /// Catalogue-browse rails (Popular / New / Trending / Top rated / per-type) for the Discover
    /// tab — independent of the library. Cached; <paramref name="refresh"/> recomputes.
    /// </summary>
    [HttpGet("discover")]
    public async Task<IActionResult> Discover([FromQuery] bool refresh, CancellationToken ct)
    {
        try
        {
            return Ok(await discover.GetFeedsAsync(refresh, ct));
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
            return Ok(await discover.GetGenreFeedsAsync(refresh, ct));
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
            return Ok(await discover.GetFeedAsync(request, ct));
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
            return Ok(await discover.SearchAsync(request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
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
        return detail is null ? NotFound() : Ok(detail);
    }

    /// <summary>A few MyAnimeList reviews for a series (lazy; best-effort, scraped from MAL).</summary>
    [HttpGet("reviews/{malId:int}")]
    public async Task<IActionResult> Reviews(int malId, CancellationToken ct) =>
        Ok(await reviews.GetReviewsAsync(malId, ct));
}
