using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Metadata;
using Maki.Core.Reading;
using Maki.Core.Security;
using Maki.Metadata.MangaBaka;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

/// <summary>
/// Chapter 1 of a series that is not in the library, for the Discover detail card. Open to every
/// signed-in user, since a user who can only request series is exactly who needs a look first.
/// Reading here writes nothing: no progress, no stats, no series.
/// </summary>
[ApiController]
[Route("api/v1/preview")]
public class PreviewController(
    ILocalizer localizer,
    MangaBakaLocalStore store,
    SeriesPreviewService previews,
    ICurrentUser currentUser) : ControllerBase
{
    [HttpPost("{providerId:long}")]
    public async Task<IActionResult> Start(long providerId, CancellationToken ct)
    {
        if (!await store.IsAvailableAsync(ct))
        {
            return this.Fail(localizer, "error.recommendation.localDbUnavailable");
        }

        // The detail lookup leaves novels out, which is the "nothing to page through" check.
        var detail = await store.GetDetailAsync(providerId, ct);
        var metadata = detail is null ? null : await store.GetAsync(detail.ProviderId, ct);
        if (detail is null || metadata is null)
        {
            return NotFound();
        }

        // These are real pages off a real site, so the ceiling applies here even though the
        // detail card itself is just text.
        if (detail.ContentRating is { } rating &&
            !ContentRating.Allowed(currentUser.MaxContentRating).Contains(rating))
        {
            return this.Forbidden(localizer, "error.preview.contentRating");
        }

        try
        {
            return Ok(previews.Start(providerId, SeriesMetadataMapper.NewFromMetadata(metadata), currentUser.UserId, localizer));
        }
        catch (InvalidOperationException)
        {
            return this.Conflict(localizer, "error.preview.busy");
        }
    }

    [HttpGet("{providerId:long}")]
    public IActionResult Get(long providerId) =>
        previews.Snapshot(providerId, currentUser.UserId, localizer) is { } snapshot ? Ok(snapshot) : NotFound();

    [HttpDelete("{providerId:long}")]
    public IActionResult Release(long providerId)
    {
        previews.Release(providerId, currentUser.UserId);
        return NoContent();
    }

    [HttpGet("{providerId:long}/page/{index:int}")]
    public IActionResult Page(long providerId, int index)
    {
        if (previews.PageFile(providerId, currentUser.UserId, index) is not { } path)
        {
            return NotFound();
        }

        // Cache-busted by the ?v= the client takes from the snapshot, which changes with the source.
        Response.Headers.CacheControl = "private, max-age=3600";
        return PhysicalFile(path, CbzReader.ContentType(path));
    }
}
