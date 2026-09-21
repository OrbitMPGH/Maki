using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Hubs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Metadata;
using Maki.Core.Notifications;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// The other half of the permission model: a user without <see cref="MakiPermission.AddSeries"/> or
/// <see cref="MakiPermission.DownloadChapters"/> gets a request form where the button that would
/// answer 403 used to be, and an admin actions it from one page.
/// <para>
/// Creating a request needs no permission — the whole point is that it is what someone with no
/// permissions does. Reading and resolving is admin-only, except that a requester can always see and
/// cancel their own; that split is why the list endpoint is one action with two shapes rather than
/// two endpoints, and why it reaches for <c>IgnoreQueryFilters</c> exactly once, under an explicit
/// admin test.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/requests")]
public class SeriesRequestsController(
    ILocalizer localizer,
    MakiDbContext db,
    IEnumerable<IMetadataProvider> metadataProviders,
    SeriesCreationService seriesCreation,
    DownloadQueueService downloadQueue,
    DownloadBatchNotifier downloadBatches,
    EventBroadcaster events,
    InboxService inbox,
    ICurrentUser currentUser,
    ILogger<SeriesRequestsController> logger) : ControllerBase
{
    private bool IsAdmin => currentUser.Has(MakiPermission.Admin);

    /// <summary>
    /// Admins get every request; everyone else gets their own. <paramref name="status"/> is
    /// "pending" (the default the page opens on), "resolved", or "all".
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string status = "all", CancellationToken ct = default)
    {
        // The global query filter narrows to the caller's own rows, which is exactly right for a
        // requester and exactly wrong for the admin page this endpoint also serves.
        var query = IsAdmin ? db.SeriesRequests.IgnoreQueryFilters() : db.SeriesRequests;

        query = status.ToLowerInvariant() switch
        {
            "pending" => query.Where(r => r.Status == SeriesRequestStatus.Pending),
            "resolved" => query.Where(r => r.Status != SeriesRequestStatus.Pending),
            _ => query,
        };

        var rows = await query
            // Pending first, then newest — the queue an admin works through, not a chronology.
            .OrderBy(r => r.Status == SeriesRequestStatus.Pending ? 0 : 1)
            .ThenByDescending(r => r.Created)
            .Take(500)
            .ToListAsync(ct);

        return Ok(await ToDtosAsync(rows, ct));
    }

    /// <summary>Pending count for the nav badge. Cheap enough to poll; admin-only, like the page.</summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("pendingcount")]
    public async Task<IActionResult> PendingCount(CancellationToken ct)
    {
        var count = await db.SeriesRequests
            .IgnoreQueryFilters()
            .CountAsync(r => r.Status == SeriesRequestStatus.Pending, ct);

        return Ok(new { count });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSeriesRequestBody body, CancellationToken ct)
    {
        if (!Enum.TryParse<SeriesRequestKind>(body.Kind, true, out var kind))
        {
            return this.Fail(localizer, "error.requests.unknownKind");
        }

        var (start, end) = NormalizeRange(body.ChapterStart, body.ChapterEnd);
        if (start is not null && end is not null && end < start)
        {
            return this.Fail(localizer, "error.requests.chapterRangeInvalid");
        }

        var request = new SeriesRequest
        {
            UserId = currentUser.UserId,
            Kind = kind,
            Status = SeriesRequestStatus.Pending,
            ChapterStart = start,
            ChapterEnd = end,
            Note = Trimmed(body.Note),
            Created = DateTime.UtcNow,
        };

        if (kind == SeriesRequestKind.NewSeries)
        {
            if (string.IsNullOrWhiteSpace(body.MetadataProviderId))
            {
                return this.Fail(localizer, "error.requests.seriesRequired");
            }

            // Resolved from the provider rather than taken from the request body: the admin reviewing
            // this has to be looking at the title that provider id actually resolves to, not at text
            // a client supplied alongside it.
            var metadata = await metadataProviders.First().GetAsync(body.MetadataProviderId, ct);
            if (metadata is null)
            {
                return this.Fail(localizer, "error.requests.metadataNotFound");
            }

            if (metadata.MangaBakaId is int mangaBakaId)
            {
                // IgnoreQueryFilters: the library is shared, and "already there" is true regardless of
                // whether this user has been granted its root folder. Telling them to request it
                // anyway would produce a request an admin can only reject.
                var existing = await db.Series
                    .IgnoreQueryFilters()
                    .Where(s => s.MangaBakaId == mangaBakaId)
                    .Select(s => (int?)s.Id)
                    .FirstOrDefaultAsync(ct);

                if (existing is not null)
                {
                    const string key = "error.requests.seriesAlreadyExists";
                    return Conflict(new { code = key, error = localizer.Get(key), seriesId = existing });
                }
            }

            request.MetadataProviderId = body.MetadataProviderId;
            request.Title = metadata.Title;
            request.CoverUrl = metadata.CoverUrl;
            request.Year = metadata.Year;
        }
        else
        {
            if (body.SeriesId is not int seriesId)
            {
                return this.Fail(localizer, "error.requests.seriesIdRequired");
            }

            // Through the filter on purpose: a user may only request chapters of a series they can
            // actually see.
            var series = await db.Series
                .Where(s => s.Id == seriesId)
                .Select(s => new { s.Id, s.Title, s.Year })
                .FirstOrDefaultAsync(ct);

            if (series is null)
            {
                return NotFound();
            }

            request.SeriesId = series.Id;
            request.Title = series.Title;
            request.Year = series.Year;
        }

        // A second identical pending request is noise in the admin queue, not a stronger signal.
        var duplicate = await db.SeriesRequests.AnyAsync(r =>
            r.Status == SeriesRequestStatus.Pending &&
            r.Kind == request.Kind &&
            r.MetadataProviderId == request.MetadataProviderId &&
            r.SeriesId == request.SeriesId &&
            r.ChapterStart == request.ChapterStart &&
            r.ChapterEnd == request.ChapterEnd, ct);

        if (duplicate)
        {
            return this.Conflict(localizer, "error.requests.alreadyPending");
        }

        db.SeriesRequests.Add(request);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{User} requested {Kind} '{Title}'", currentUser.UserName, request.Kind, request.Title);

        await events.SeriesRequested(request.Id, request.Title, currentUser.UserName);
        inbox.Raise(InboxEventType.RequestSubmitted, new InboxMessage(
                Key: "inbox.request.submitted",
                Params: InboxMessage.Args(new { user = currentUser.UserName, title = request.Title }),
                Url: "/requests"),
            InboxAudience.Admins);

        var dto = (await ToDtosAsync([request], ct))[0];
        return CreatedAtAction(nameof(List), new { id = request.Id }, dto);
    }

    /// <summary>
    /// Narrows what a pending request asks for before approving it — "everything" trimmed to the
    /// first ten chapters. Pending only: once approved the chapters are queued and the recorded
    /// range is a record of what was actually done, so editing it afterwards would make it a lie.
    /// <para>
    /// The requester's original bounds are snapshotted on the first edit, so the page can say
    /// "asked for everything, trimmed to 1–10" rather than presenting the admin's range as theirs.
    /// </para>
    /// </summary>
    [Authorize(Policy = Policies.Admin)]
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Edit(int id, [FromBody] EditSeriesRequestBody body, CancellationToken ct)
    {
        var request = await db.SeriesRequests.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return NotFound();
        }

        if (request.Status != SeriesRequestStatus.Pending)
        {
            return this.Conflict(localizer, "error.requests.alreadyResolved");
        }

        var (start, end) = NormalizeRange(body.ChapterStart, body.ChapterEnd);
        if (start is not null && end is not null && end < start)
        {
            return this.Fail(localizer, "error.requests.chapterRangeInvalid");
        }

        if (start == request.ChapterStart && end == request.ChapterEnd)
        {
            // No change: don't stamp an edit that didn't happen, or a saved-with-no-edits dialog
            // would permanently label the request as adjusted.
            return Ok((await ToDtosAsync([request], ct))[0]);
        }

        if (request.EditedAt is null)
        {
            request.OriginalChapterStart = request.ChapterStart;
            request.OriginalChapterEnd = request.ChapterEnd;
        }

        request.ChapterStart = start;
        request.ChapterEnd = end;
        request.EditedAt = DateTime.UtcNow;
        request.EditedByUserId = currentUser.UserId;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{User} edited request {Id} to chapters {Start}–{End}",
            currentUser.UserName, request.Id, start, end);

        inbox.Raise(InboxEventType.RequestEdited, new InboxMessage(
                Key: "inbox.request.edited",
                Params: InboxMessage.Args(new { title = request.Title, range = RangeLabel(start, end) }),
                Url: "/requests"),
            InboxAudience.User(request.UserId));

        return Ok((await ToDtosAsync([request], ct))[0]);
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{id:int}/approve")]
    public async Task<IActionResult> Approve(int id, [FromBody] ApproveSeriesRequestBody body, CancellationToken ct)
    {
        var request = await db.SeriesRequests.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return NotFound();
        }

        if (request.Kind == SeriesRequestKind.NewSeries && request.SeriesId is null)
        {
            if (body.RootFolderId is not int rootFolderId)
            {
                return this.Fail(localizer, "error.requests.rootFolderRequired");
            }

            var claimedAt = DateTime.UtcNow;
            var staleBefore = claimedAt.AddMinutes(-30);
            var claimed = await db.SeriesRequests.IgnoreQueryFilters()
                .Where(r => r.Id == id && (r.Status == SeriesRequestStatus.Pending ||
                    r.Status == SeriesRequestStatus.Processing && r.ApprovalClaimedAtUtc < staleBefore))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, SeriesRequestStatus.Processing)
                    .SetProperty(r => r.ApprovalClaimedAtUtc, claimedAt), ct);
            if (claimed != 1)
            {
                // The claim is the guard now, so it has to answer both questions the old
                // Status != Pending check answered: a request somebody already resolved reads as
                // resolved, and only one that is still Pending and held by another admin reads as
                // in progress.
                var status = await db.SeriesRequests.IgnoreQueryFilters().AsNoTracking()
                    .Where(r => r.Id == id).Select(r => (SeriesRequestStatus?)r.Status)
                    .FirstOrDefaultAsync(ct);
                return status is null or SeriesRequestStatus.Pending or SeriesRequestStatus.Processing
                    ? this.Conflict(localizer, "error.requests.approvalInProgress")
                    : this.Conflict(localizer, "error.requests.alreadyResolved");
            }
            // The claim went through ExecuteUpdate, which the change tracker never sees, so this
            // entity still holds its pre-claim values. Assigning them by hand instead of reloading
            // leaves the tracker thinking Processing was always the original: setting Status back to
            // Pending on a failure then looks like no change at all and the release writes nothing,
            // stranding the request until the stale window expires.
            await db.Entry(request).ReloadAsync(ct);

            var result = await seriesCreation.CreateAsync(
                request.MetadataProviderId!, rootFolderId, monitored: true, body.MonitorNewItems, ct,
                attributedUserId: request.UserId, addedFrom: "request", originatingRequest: request,
                clientMutationId: SeriesCreationResult.MutationIdFor(request));

            if (result.Series is null)
            {
                // Everything except "somebody already added it" leaves the request unresolved, so
                // the claim has to go back or it sits in Processing until the stale window expires.
                if (result.Error is not SeriesCreationError.AlreadyInLibrary)
                {
                    request.Status = SeriesRequestStatus.Pending;
                    request.ApprovalClaimedAtUtc = null;
                    await db.SaveChangesAsync(ct);
                }
                return result.Error switch
                {
                    SeriesCreationError.RootFolderNotFound => this.Fail(localizer, "error.requests.rootFolderNotFound"),
                    SeriesCreationError.MetadataNotFound => this.Fail(localizer, "error.requests.metadataNotFound"),
                    // An earlier approval of this request committed a series that has since been
                    // deleted. Not "already in library" and not a fresh add either: the receipt that
                    // makes approval retry-safe is keyed on the request, so creating again here would
                    // mean honouring it after its result was thrown away.
                    SeriesCreationError.OperationResultGone =>
                        this.Conflict(localizer, "error.requests.approvedSeriesDeleted"),
                    SeriesCreationError.MutationIdReused =>
                        this.Conflict(localizer, "error.requests.approvedElsewhere"),
                    // Somebody added it between the request and the approval. Nothing to do, but the
                    // request is genuinely satisfied — resolve it rather than making the admin reject
                    // a request whose outcome already happened.
                    _ => await ResolveAsAlreadyPresentAsync(request, ct),
                };
            }

            request.SeriesId = result.Series.Id;
            // The title the series actually landed under, which is what the requester will look for.
            request.Title = result.Series.Title;

            if (result.Warnings.Count > 0)
            {
                logger.LogWarning(
                    "Approving request {Id} added {Title} with warnings: {Warnings}",
                    request.Id, result.Series.Title, string.Join(" ", result.Warnings));
            }
        }
        else
        {
            var claimedAt = DateTime.UtcNow;
            var staleBefore = claimedAt.AddMinutes(-30);
            var claimed = await db.SeriesRequests.IgnoreQueryFilters()
                .Where(r => r.Id == id && (r.Status == SeriesRequestStatus.Pending ||
                    r.Status == SeriesRequestStatus.Processing && r.ApprovalClaimedAtUtc < staleBefore))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, SeriesRequestStatus.Processing)
                    .SetProperty(r => r.ApprovalClaimedAtUtc, claimedAt), ct);
            if (claimed != 1)
                return this.Conflict(localizer, "error.requests.approvalInProgress");
            // The claim went through ExecuteUpdate, which the change tracker never sees, so this
            // entity still holds its pre-claim values. Assigning them by hand instead of reloading
            // leaves the tracker thinking Processing was always the original: setting Status back to
            // Pending on a failure then looks like no change at all and the release writes nothing,
            // stranding the request until the stale window expires.
            await db.Entry(request).ReloadAsync(ct);
        }

        int queued;
        try
        {
            queued = request.SeriesId is int seriesId
                ? await QueueRangeAsync(
                    seriesId, request.Title, request.ChapterStart, request.ChapterEnd, request.UserId, ct)
                : 0;

            request.Status = SeriesRequestStatus.Approved;
            request.ApprovalClaimedAtUtc = null;
            request.QueuedCount = queued;
            request.ResolvedAt = DateTime.UtcNow;
            request.ResolvedByUserId = currentUser.UserId;
            request.ResolutionNote = Trimmed(body.Note);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // The save above is the only thing that clears the claim, so anything throwing before it
            // would leave the request reading Processing for the whole stale window — and while it
            // does, Reject and Delete both refuse it and a retried approval is refused too. The
            // branches above release the claim for the failures they can name; this covers the rest.
            //
            // A cancellation is caught too, and deliberately: an admin closing the tab mid-approval
            // is the ordinary way to reach this, and the claim has to go back for that as much as
            // for a real failure.
            logger.LogWarning(ex, "Releasing the approval claim on request {Id} after a failure", request.Id);
            await ReleaseClaimAsync(request);
            throw;
        }

        NotifyResolved(request, approved: true, queued);

        return Ok((await ToDtosAsync([request], ct))[0]);
    }

    /// <summary>
    /// Puts a claimed request back to Pending so it can be approved, rejected or withdrawn again.
    /// <para>
    /// Written straight to the row rather than through the change tracker: the tracked entity may be
    /// holding a half-applied Approved state from the save that just failed, and a second
    /// <c>SaveChangesAsync</c> on it would either write that or fail the same way. The series id and
    /// title ride along because a NewSeries approval may have committed the series before the
    /// failure — dropping them would orphan it from the request, and the mutation receipt keyed on
    /// the request would refuse to create it again.
    /// </para>
    /// <para>
    /// Takes no cancellation token on purpose. The commonest way to get here is the request's own
    /// token being cancelled, and passing it on would cancel the release as well — stranding the
    /// claim in exactly the case this exists for. Best effort otherwise: a release that cannot be
    /// written leaves the stale window as the backstop.
    /// </para>
    /// </summary>
    private async Task ReleaseClaimAsync(SeriesRequest request)
    {
        try
        {
            await db.SeriesRequests.IgnoreQueryFilters()
                .Where(r => r.Id == request.Id && r.Status == SeriesRequestStatus.Processing)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, SeriesRequestStatus.Pending)
                    .SetProperty(r => r.ApprovalClaimedAtUtc, (DateTime?)null)
                    .SetProperty(r => r.SeriesId, request.SeriesId)
                    .SetProperty(r => r.Title, request.Title), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not release the approval claim on request {Id}", request.Id);
        }
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{id:int}/reject")]
    public async Task<IActionResult> Reject(int id, [FromBody] RejectSeriesRequestBody body, CancellationToken ct)
    {
        var request = await db.SeriesRequests.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return NotFound();
        }

        // Same split Delete makes: a request another admin is mid-approval on has not been resolved,
        // and telling the rejecting admin it has sends them looking for a decision nobody took.
        if (request.Status == SeriesRequestStatus.Processing)
        {
            return this.Conflict(localizer, "error.requests.approvalInProgress");
        }

        if (request.Status != SeriesRequestStatus.Pending)
        {
            return this.Conflict(localizer, "error.requests.alreadyResolved");
        }

        request.Status = SeriesRequestStatus.Rejected;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolvedByUserId = currentUser.UserId;
        request.ResolutionNote = Trimmed(body.Note);
        await db.SaveChangesAsync(ct);

        NotifyResolved(request, approved: false, queued: 0);

        return Ok((await ToDtosAsync([request], ct))[0]);
    }

    /// <summary>
    /// A requester withdrawing their own pending request, or an admin clearing any row. A requester
    /// may not delete one that has been resolved — the resolution note is the answer they were given.
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var request = IsAdmin
            ? await db.SeriesRequests.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Id == id, ct)
            : await db.SeriesRequests.FirstOrDefaultAsync(r => r.Id == id, ct);

        if (request is null)
        {
            return NotFound();
        }

        if (request.Status == SeriesRequestStatus.Processing)
        {
            return this.Conflict(localizer, "error.requests.approvalInProgress");
        }

        if (!IsAdmin && request.Status != SeriesRequestStatus.Pending)
        {
            return this.Conflict(localizer, "error.requests.alreadyResolved");
        }

        db.SeriesRequests.Remove(request);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Queues every chapter of <paramref name="seriesId"/> in range that has no file yet.
    /// <para>
    /// The range is filtered in memory rather than in SQL: <c>Chapter.Number</c> is a nullable
    /// decimal stored as REAL, and a one-shot's null number is not comparable — it belongs to an
    /// unbounded request ("everything") and to nothing else, which no <c>WHERE</c> clause says
    /// cleanly. A series' chapter list is small enough that this costs nothing.
    /// </para>
    /// </summary>
    /// <param name="requesterId">
    /// Whose request this is, recorded on every queue row. Deliberately not the approving admin: the
    /// download is on the requester's behalf, and they are who the outcome should reach.
    /// </param>
    private async Task<int> QueueRangeAsync(
        int seriesId, string title, decimal? start, decimal? end, int requesterId, CancellationToken ct)
    {
        var candidates = await db.Chapters
            .IgnoreQueryFilters()
            .Where(c => c.SeriesId == seriesId && c.ChapterFileId == null)
            .Select(c => new { c.Id, c.Number })
            .ToListAsync(ct);

        var wanted = candidates
            .Where(c => InRange(c.Number, start, end))
            .Select(c => c.Id)
            .ToList();

        var queuedItemIds = new List<int>();
        foreach (var chapterId in wanted)
        {
            try
            {
                if (await downloadQueue.EnqueueChapterAsync(
                        chapterId, ct, DownloadOrigin.RequestApproval, requesterId) is { } item)
                {
                    queuedItemIds.Add(item.Id);
                }
            }
            catch (InvalidOperationException ex)
            {
                // A cooldown or a source that can't serve this series. Everything queued so far
                // stands, and the count the admin sees is the honest one.
                logger.LogWarning(ex, "Stopped queueing request chapters for series {SeriesId}", seriesId);
                break;
            }
        }

        await downloadBatches.QueuedAsync(seriesId, title, queuedItemIds, DownloadOrigin.RequestApproval);
        return queuedItemIds.Count;
    }

    private static bool InRange(decimal? number, decimal? start, decimal? end)
    {
        if (start is null && end is null)
        {
            return true;
        }

        // Unnumbered (one-shots, unparsed specials): there is no number to compare, so a bounded
        // request cannot be asking for it.
        if (number is not decimal n)
        {
            return false;
        }

        return (start is null || n >= start) && (end is null || n <= end);
    }

    private async Task<IActionResult> ResolveAsAlreadyPresentAsync(SeriesRequest request, CancellationToken ct)
    {
        request.Status = SeriesRequestStatus.Approved;
        request.ApprovalClaimedAtUtc = null;
        request.QueuedCount = 0;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolvedByUserId = currentUser.UserId;
        request.ResolutionNote = "Already in the library.";
        await db.SaveChangesAsync(ct);

        NotifyResolved(request, approved: true, queued: 0);

        return Ok((await ToDtosAsync([request], ct))[0]);
    }

    /// <summary>
    /// Tells the requester what happened to their request. Always the requester, never the admin who
    /// acted: an admin resolving their own request would otherwise get told about it by themselves.
    /// The resolution note is carried through verbatim, because on a rejection it <em>is</em> the
    /// answer — the request page shows the same text.
    /// </summary>
    private void NotifyResolved(SeriesRequest request, bool approved, int queued)
    {
        // Three sentences rather than one built by concatenation: "approved and queued", "approved,
        // already here" and "declined" are different statements, and a language that reorders them
        // cannot do so if they arrive as fragments. The resolution note rides along as `note`, which
        // the renderer appends verbatim: it is the admin's own words and is not ours to translate.
        var key = approved
            ? queued > 0 ? "inbox.request.approvedQueued" : "inbox.request.approvedInLibrary"
            : "inbox.request.declined";

        inbox.Raise(
            approved ? InboxEventType.RequestApproved : InboxEventType.RequestRejected,
            new InboxMessage(
                Key: key,
                Params: InboxMessage.Args(new
                {
                    title = request.Title,
                    queued,
                    note = request.ResolutionNote is { Length: > 0 } n ? n : null,
                }),
                Level: approved ? NotificationLevel.Info : NotificationLevel.Warning,
                SeriesId: approved ? request.SeriesId : null,
                Url: approved && request.SeriesId is { } sid ? $"/series/{sid}" : "/requests"),
            InboxAudience.User(request.UserId));
    }

    /// <summary>Renders an edited chapter range the way the requests page labels it.</summary>
    private static string RangeLabel(decimal? start, decimal? end) => (start, end) switch
    {
        (null, null) => "everything",
        (not null, null) => $"chapter {start} onwards",
        (null, not null) => $"up to chapter {end}",
        _ => $"chapters {start}–{end}",
    };

    /// <summary>
    /// Resolves the requester, resolver and editor display names in one query for the whole page,
    /// rather than one lookup per row.
    /// </summary>
    private async Task<List<SeriesRequestDto>> ToDtosAsync(IReadOnlyList<SeriesRequest> rows, CancellationToken ct)
    {
        var ids = rows
            .SelectMany(r => new int?[] { r.UserId, r.ResolvedByUserId, r.EditedByUserId })
            .OfType<int>()
            .Distinct()
            .ToList();

        var names = await db.Users
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, Name = u.DisplayName ?? u.UserName })
            .ToDictionaryAsync(u => u.Id, u => u.Name ?? string.Empty, ct);

        return [.. rows.Select(r => SeriesRequestDto.FromEntity(
            r,
            names.GetValueOrDefault(r.UserId, "Unknown"),
            r.ResolvedByUserId is int by ? names.GetValueOrDefault(by) : null,
            r.EditedByUserId is int editor ? names.GetValueOrDefault(editor) : null))];
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A range whose ends are both blank is "everything", not a zero-width window.</summary>
    private static (decimal?, decimal?) NormalizeRange(decimal? start, decimal? end) =>
        (start is null or < 0 ? null : start, end is null or < 0 ? null : end);
}
