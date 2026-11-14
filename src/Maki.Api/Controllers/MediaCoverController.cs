using Maki.Api.Auth;
using Maki.Api.Configuration;
using Maki.Api.Dtos;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Download;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Security;
using Maki.Core.Sources;
using Maki.Data;
using Maki.Sources.Suwayomi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>The 240 px poster the grid cards use; same visibility rule and cache-buster as <see cref="Cover"/>.</summary>
    [HttpGet("{seriesId:int}/cover-sm.jpg")]
    public async Task<IActionResult> SmallCover(int seriesId, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return NotFound();
        }

        if (await covers.EnsureSmallCoverAsync(seriesId, ct) is not { } path)
        {
            return NotFound();
        }

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

        return await StoreAsync(series, buffer.ToArray(), ct);
    }

    public record PageCoverRequest(int ChapterId, int Page);

    public record SourceCoverRequest(int MappingId);

    public record UrlCoverRequest(string? Url);

    /// <summary>How many pages a downloaded chapter of this series has, for the page picker.</summary>
    [HttpGet("{seriesId:int}/cover/chapter/{chapterId:int}/pages")]
    public async Task<IActionResult> ChapterPages(
        int seriesId, int chapterId, [FromServices] ReaderService reader, CancellationToken ct)
    {
        if (!await db.Chapters.AnyAsync(c => c.Id == chapterId && c.SeriesId == seriesId, ct) ||
            await reader.PageSliceAsync(chapterId, ct) is not { } slice)
        {
            return NotFound();
        }

        return Ok(new { pageCount = slice.PageCount });
    }

    /// <summary>
    /// Makes one page of a downloaded chapter the poster, read the way the reader serves it. The page
    /// is 0-based, as on the reader's page route.
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPost("{seriesId:int}/cover/from-page")]
    public async Task<IActionResult> FromPage(
        int seriesId,
        [FromBody] PageCoverRequest request,
        [FromServices] ReaderService reader,
        [FromServices] ReaderArchiveCache archives,
        CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null ||
            !await db.Chapters.AnyAsync(c => c.Id == request.ChapterId && c.SeriesId == seriesId, ct) ||
            await reader.PageSliceAsync(request.ChapterId, ct) is not { } slice ||
            request.Page < 0 || request.Page >= slice.PageCount)
        {
            return NotFound();
        }

        var bytes = await ReaderPageCache.ReadPageBytesAsync(paths, archives, slice, slice.StartPage + request.Page, ct);
        if (bytes is null)
        {
            return NotFound();
        }

        if (!IsAcceptableImage(bytes, allowAvif: true))
        {
            return this.Fail(localizer, "error.metadata.coverInvalid", new { pixels = MaxCoverDimension });
        }

        return await StoreAsync(series, bytes, ct);
    }

    /// <summary>
    /// Makes a linked source's own cover the poster. The source's page is read for its cover address,
    /// which is then held to the same host rule as the cover proxy at every redirect.
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPost("{seriesId:int}/cover/from-source")]
    public async Task<IActionResult> FromSource(
        int seriesId,
        [FromBody] SourceCoverRequest request,
        [FromServices] SourceRegistry sources,
        [FromServices] SourceAvailability availability,
        [FromServices] CoverFetcher fetcher,
        CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        var mapping = await db.SourceMappings.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == request.MappingId && m.SeriesId == seriesId, ct);
        if (series is null || mapping is null || !mapping.Enabled ||
            !await availability.IsEnabledAsync(mapping.SourceName, ct) || sources.Find(mapping.SourceName) is not { } source)
        {
            return NotFound();
        }

        // A Suwayomi source's own host is the admin's LAN server; its thumbnails have their own relay.
        if (source is SuwayomiSource)
        {
            return this.Fail(localizer, "error.search.hostNotServed");
        }

        string? coverUrl;
        try
        {
            coverUrl = (await source.GetSeriesAsync(mapping.SourceSeriesId, ct)).CoverUrl;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not read the series page of {Source} for series {SeriesId}", source.Name, seriesId);
            return this.BadGateway(localizer, "error.search.coverUnavailable");
        }

        if (coverUrl is null || !Uri.TryCreate(coverUrl, UriKind.Absolute, out var target))
        {
            return this.BadGateway(localizer, "error.search.coverUnavailable");
        }

        var result = await fetcher.FetchAsync(
            target, hop => CoverHostPolicy.Allows(source, hop), (int)CoverUploadLimit, new Uri($"{source.BaseUrl}/"), ct);
        switch (result.Outcome)
        {
            case CoverFetcher.Outcome.Refused:
                return this.Fail(localizer, "error.search.hostNotServed");
            case CoverFetcher.Outcome.Failed or CoverFetcher.Outcome.TooLarge:
                return this.BadGateway(localizer, "error.search.coverUnavailable");
        }

        if (!IsAcceptableImage(result.Bytes!, allowAvif: true))
        {
            return this.BadGateway(localizer, "error.search.notAnImage");
        }

        return await StoreAsync(series, result.Bytes!, ct);
    }

    /// <summary>
    /// Fetches an image from an address an admin typed and makes it the poster. A server-side fetch
    /// of a caller-chosen URL, so: admins only, http or https at every hop, public addresses only
    /// (the <c>covers</c> client refuses the LAN at connect time), a short timeout and the upload's
    /// size cap, and the bytes must pass the same check as an upload.
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{seriesId:int}/cover/from-url")]
    public async Task<IActionResult> FromUrl(
        int seriesId, [FromBody] UrlCoverRequest request, [FromServices] CoverFetcher fetcher, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null)
        {
            return NotFound();
        }

        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var target) || !CoverFetcher.IsWebAddress(target))
        {
            return this.Fail(localizer, "error.search.invalidUrl");
        }

        var result = await fetcher.FetchAsync(target, CoverFetcher.IsWebAddress, (int)CoverUploadLimit, null, ct);
        switch (result.Outcome)
        {
            case CoverFetcher.Outcome.TooLarge:
                return this.Fail(localizer, "error.metadata.coverTooLarge", new { megabytes = CoverUploadLimit / (1024 * 1024) });
            case CoverFetcher.Outcome.Refused or CoverFetcher.Outcome.Failed:
                return this.Fail(localizer, "error.metadata.coverFetchFailed",
                    new { url = target.GetLeftPart(UriPartial.Path), reason = result.Reason, status = result.Status ?? 0 });
        }

        if (!IsAcceptableImage(result.Bytes!))
        {
            return this.Fail(localizer, "error.metadata.coverInvalid", new { pixels = MaxCoverDimension });
        }

        return await StoreAsync(series, result.Bytes!, ct);
    }

    /// <summary>
    /// Stores checked image bytes as the poster through the upload's resize and re-encode, then locks
    /// the cover so neither a refresh nor the image-cache rebuild puts the provider's back.
    /// </summary>
    private async Task<IActionResult> StoreAsync(Series series, byte[] bytes, CancellationToken ct)
    {
        try
        {
            await using var content = new MemoryStream(bytes, writable: false);
            series.CoverPath = await covers.StoreUploadedCoverAsync(series.Id, content, ct);
        }
        catch (NetVips.VipsException ex)
        {
            logger.LogInformation(ex, "Rejected a chosen cover for series {SeriesId}", series.Id);
            return this.Fail(localizer, "error.metadata.coverInvalid", new { pixels = MaxCoverDimension });
        }

        changeLog.RecordReplaced(series, SeriesMetadataField.Cover, currentUser.UserId);
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.SaveChangesAsync(ct);
            await SeriesMetadataLocks.ApplyAsync(db, series, SeriesMetadataField.Cover, SeriesMetadataField.None, ct);
            await transaction.CommitAsync(ct);
        }

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
    /// A JPEG, PNG, GIF or WebP by its header (AVIF is refused: older browsers cannot show the poster),
    /// whose dimensions read back within <see cref="MaxCoverDimension"/>. Identify reads only the
    /// header, so an image claiming to be enormous is refused before anything allocates its pixels.
    /// </summary>
    internal static bool IsAcceptableImage(MemoryStream buffer) => IsAcceptableImage(buffer.ToArray());

    /// <param name="allowAvif">
    /// For a chapter page or a source cover, which only ever reach a browser as the re-encoded JPEG
    /// poster. Converted chapters store AVIF and JPEG XL pages, so this lets JPEG XL through too.
    /// </param>
    internal static bool IsAcceptableImage(byte[] bytes, bool allowAvif = false)
    {
        var mediaType = ImageValidator.SniffMediaType(bytes.AsSpan(0, Math.Min(bytes.Length, 32))) ??
                        (allowAvif && JpegXl.HasSignature(bytes) ? "image/jxl" : null);
        if (mediaType is null || (mediaType == "image/avif" && !allowAvif))
        {
            return false;
        }

        return ImageCodec.TryIdentify(bytes) is { } info &&
               info.Width is > 0 and <= MaxCoverDimension && info.Height is > 0 and <= MaxCoverDimension;
    }
}
