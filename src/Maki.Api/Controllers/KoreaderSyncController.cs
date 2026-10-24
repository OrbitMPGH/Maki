using System.Text.Json;
using Maki.Api.Dtos;
using Maki.Api.Jobs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quartz;

namespace Maki.Api.Controllers;

/// <summary>
/// A KOReader progress sync ("kosync") server, so a chapter downloaded to KOReader and read offline
/// still shows up as read here. KOReader's custom sync server setting points at this prefix and calls
/// <c>users/auth</c>, <c>users/create</c> and <c>syncs/progress</c> under it.
/// <para>
/// <b>The username is the user's OPDS token.</b> KOReader sends the username as typed in
/// <c>x-auth-user</c> but only an MD5 of the password in <c>x-auth-key</c>, and Maki keeps only a
/// SHA-256 of each token, so the password could never be checked. The token therefore rides in the
/// username, resolved through <see cref="OpdsAccessService"/> like a feed URL, and the password is
/// ignored. It is a header, so unlike the feed URL it stays out of request logs.
/// </para>
/// <para>
/// Status codes follow what KOReader's client accepts, which is narrower than REST would suggest: it
/// counts anything but 200 as a failed push, queues it, and stops draining that queue at the first
/// failure. So every authorized push answers 200 at once, a book Maki does not know included, and is
/// written by <see cref="KoreaderPushWriter"/> afterwards.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/kosync")]
// Authenticated by the token in x-auth-user, checked on every action, since KOReader holds no cookie
// and cannot be told to send X-Api-Key.
[AllowAnonymous]
public class KoreaderSyncController(
    OpdsAccessService access,
    KoreaderSyncService sync,
    KoreaderPushWriter pushes,
    KoreaderDocumentIndexer indexer,
    MakiDbContext db,
    ISchedulerFactory schedulerFactory,
    ILocalizer localizer,
    ILogger<KoreaderSyncController> logger) : ControllerBase
{
    private const int UnauthorizedCode = 2001;
    private const int RegistrationCode = 2002;

    private async Task<OpdsAccess?> AuthorizeAsync(string? token, CancellationToken ct)
    {
        var resolved = await access.ResolveAsync(token?.Trim(), ct);
        if (resolved is not null)
        {
            db.Scope.SetUser(resolved.UserId, resolved.AllRootFolders);
            return resolved;
        }

        logger.LogWarning("KOReader sync request rejected: {Method} {Path}", Request.Method, Request.Path);
        return null;
    }

    private string? HeaderToken => Request.Headers["x-auth-user"].FirstOrDefault();

    private ObjectResult Unauthorized401() =>
        StatusCode(StatusCodes.Status401Unauthorized,
            new KoreaderError(UnauthorizedCode, localizer.Get("error.koreaderSync.unauthorized")));

    [HttpGet("healthcheck")]
    public IActionResult Healthcheck() => Ok(new { state = "OK" });

    [HttpGet("users/auth")]
    public async Task<IActionResult> Auth(CancellationToken ct) =>
        await AuthorizeAsync(HeaderToken, ct) is null ? Unauthorized401() : Ok(new { authorized = "OK" });

    /// <summary>
    /// There are no KOReader accounts to create, but someone will tap Register: with a valid token as
    /// the username it succeeds as a login would, and otherwise says what to enter instead.
    /// </summary>
    [HttpPost("users/create")]
    public async Task<IActionResult> Register([FromBody] KoreaderRegisterRequest? request, CancellationToken ct)
    {
        if (await AuthorizeAsync(request?.Username, ct) is null)
        {
            return StatusCode(StatusCodes.Status402PaymentRequired,
                new KoreaderError(RegistrationCode, localizer.Get("error.koreaderSync.registerWithToken")));
        }

        return StatusCode(StatusCodes.Status201Created, new { username = request!.Username });
    }

    [HttpPut("syncs/progress")]
    public async Task<IActionResult> Push([FromBody] KoreaderProgressRequest request, CancellationToken ct)
    {
        if (await AuthorizeAsync(HeaderToken, ct) is not { } user)
        {
            return Unauthorized401();
        }

        if (string.IsNullOrWhiteSpace(request.Document))
        {
            return BadRequest();
        }

        var progress = request.Progress.ValueKind switch
        {
            JsonValueKind.String => request.Progress.GetString(),
            JsonValueKind.Number => request.Progress.GetRawText(),
            _ => null
        };

        pushes.Enqueue(user.UserId, request.Document, progress, request.Percentage, request.Device, request.DeviceId);
        return Ok(new KoreaderPushResponse(request.Document, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    [HttpGet("syncs/progress/{document}")]
    public async Task<IActionResult> Pull(string document, CancellationToken ct)
    {
        if (await AuthorizeAsync(HeaderToken, ct) is not { } user)
        {
            return Unauthorized401();
        }

        if (await sync.PullAsync(user.UserId, document, ct) is not { } found)
        {
            await KoreaderDocumentIndexJob.TriggerSoonAsync(schedulerFactory, logger,
                () => indexer.AnyUnindexedAsync(ct));
            return Ok(new { });
        }

        return Ok(new KoreaderProgressResponse(
            document, found.Progress, found.Percentage, found.Device, found.DeviceId, found.Timestamp));
    }
}
