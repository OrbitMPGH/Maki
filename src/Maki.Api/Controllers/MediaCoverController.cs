using Maki.Api.Auth;
using Maki.Api.Configuration;
using Maki.Api.Dtos;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/mediacover")]
public class MediaCoverController(
    AppPaths paths,
    MakiDbContext db,
    CoverService covers,
    SeriesMetadataChangeLog changeLog,
    ICurrentUser currentUser,
    ILocalizer localizer,
    KavitaScanService kavitaScans,
    ILogger<MediaCoverController> logger) : ControllerBase
{
    internal const long CoverUploadLimit = 10 * 1024 * 1024;

    /// <summary>Either side; a decoded image is width x height x 4 bytes, so this caps the memory a single upload can cost.</summary>
    internal const int MaxCoverDimension = 8000;

    /// <summary>The multipart framing around the file, on top of <see cref="CoverUploadLimit"/>.</summary>
    private const long FormOverhead = 64 * 1024;

    /// <summary>
    /// Serves a series' poster.
    /// <para>
    /// The existence check goes through EF and not through the filesystem, and that is the whole
    /// point of the query: the path is derived from a caller-supplied id, so serving the file
    /// directly hands every cover in the instance to anyone with an account, including one granted a
    /// single root folder. Resolving the series first puts the request under the <c>Series</c> global
    /// query filter, which is where library access is decided for every other read in the app —
    /// nothing here has to know what a root-folder grant is.
    /// </para>
    /// <para>
    /// A series the caller cannot see answers <b>404</b> and not 403, so the endpoint does not
    /// confirm which ids exist.
    /// </para>
    /// </summary>
    [HttpGet("{seriesId:int}/cover.jpg")]
    public async Task<IActionResult> Cover(int seriesId, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return NotFound();
        }

        var path = Path.Combine(paths.MediaCoverDir, seriesId.ToString(), "cover.jpg");
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        // Immutable and private: CoverUrlFor's ?v= cache-buster changes whenever the cover is rewritten, and private keeps a shared proxy cache from serving it to a user without access to this series.
        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return PhysicalFile(path, "image/jpeg");
    }

    /// <summary>
    /// Replaces a series' poster with an uploaded image and locks it, so neither a metadata refresh
    /// nor the image-cache rebuild puts the provider's poster back. "Reset to provider" on the
    /// series metadata endpoint undoes it.
    /// <para>
    /// The bytes decide what the file is: a raster format by its header, then a decode. The result
    /// is re-encoded as the same resized JPEG a downloaded poster becomes, so nothing the client sent
    /// is ever served as-is.
    /// </para>
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPost("{seriesId:int}/cover")]
    [RequestSizeLimit(CoverUploadLimit + FormOverhead)]
    [RequestFormLimits(MultipartBodyLengthLimit = CoverUploadLimit + FormOverhead)]
    public async Task<IActionResult> Upload(int seriesId, IFormFile? file, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null)
        {
            return NotFound();
        }

        if (file is null || file.Length == 0)
        {
            return this.Fail(localizer, "error.metadata.coverMissing");
        }

        if (file.Length > CoverUploadLimit)
        {
            return this.Fail(localizer, "error.metadata.coverTooLarge", new { megabytes = CoverUploadLimit / (1024 * 1024) });
        }

        await using var buffer = new MemoryStream((int)file.Length);
        await using (var upload = file.OpenReadStream())
        {
            await upload.CopyToAsync(buffer, ct);
        }

        if (!IsAcceptableImage(buffer))
        {
            return this.Fail(localizer, "error.metadata.coverInvalid", new { pixels = MaxCoverDimension });
        }

        buffer.Position = 0;
        try
        {
            series.CoverPath = await covers.StoreUploadedCoverAsync(series.Id, buffer, ct);
        }
        catch (ImageFormatException ex)
        {
            logger.LogInformation(ex, "Rejected an uploaded cover for series {SeriesId}", series.Id);
            return this.Fail(localizer, "error.metadata.coverInvalid", new { pixels = MaxCoverDimension });
        }

        series.LockedFields |= SeriesMetadataField.Cover;
        changeLog.RecordReplaced(series, SeriesMetadataField.Cover, currentUser.UserId);
        await db.SaveChangesAsync(ct);
        await covers.WriteLibraryCoverAsync(series, ct);
        if (series.RootFolder is { } rootFolder)
        {
            kavitaScans.QueuePush(Path.Combine(rootFolder.Path, series.FolderName), series.Id);
        }

        return Ok(new
        {
            coverUrl = SeriesDto.CoverUrlFor(series.Id, series.CoverPath, series.LastMetadataRefresh),
            lockedFields = SeriesDto.LockedFieldNames(series.LockedFields),
        });
    }

    /// <summary>
    /// A JPEG, PNG, GIF or WebP by its header (AVIF sniffs as an image but does not decode here),
    /// whose dimensions read back within <see cref="MaxCoverDimension"/>. Identify reads only the
    /// header, so an image claiming to be enormous is refused before anything allocates its pixels.
    /// </summary>
    internal static bool IsAcceptableImage(MemoryStream buffer)
    {
        var header = buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, 32));
        if (ImageValidator.SniffMediaType(header) is null or "image/avif")
        {
            return false;
        }

        try
        {
            buffer.Position = 0;
            var info = Image.Identify(buffer);
            return info.Width is > 0 and <= MaxCoverDimension && info.Height is > 0 and <= MaxCoverDimension;
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
            return false;
        }
    }
}
