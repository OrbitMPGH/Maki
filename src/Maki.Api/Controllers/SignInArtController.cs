using Maki.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Maki.Api.Controllers;

/// <summary>Art for the sign-in page, which nobody is signed in to see.</summary>
[ApiController]
[Route("api/v1/signin")]
public class SignInArtController(SignInMosaicService mosaic) : ControllerBase
{
    /// <summary>
    /// The blurred cover mosaic behind the sign-in card. 404 when an admin has not switched it on or
    /// there are too few Safe-rated covers, and the page draws its own art instead. See
    /// <see cref="SignInMosaicService"/> for what keeps this narrow.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("mosaic.webp")]
    public async Task<IActionResult> Mosaic(CancellationToken ct)
    {
        var image = await mosaic.GetAsync(ct);
        if (image is null)
        {
            return NotFound();
        }

        // Short, so switching the setting off takes effect within minutes even in a browser that
        // kept the image; the ETag makes the revalidations cheap.
        Response.Headers.CacheControl = "private, max-age=300";
        return File(image.Webp, "image/webp", lastModified: null, entityTag: new EntityTagHeaderValue($"\"{image.ETag}\""));
    }
}
