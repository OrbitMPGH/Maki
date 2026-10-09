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
    IUserLocaleResolver locales,
    MakiDbContext db,
    SeriesCreationService seriesCreation,
    DownloadQueueService downloadQueue,
    DownloadBatchNotifier downloadBatches,
    InboxService inbox,
    NotificationService notifications,
    SeriesRequestSubmitter submitter,
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
            "pending" => query.Where(r =>
                r.Status == SeriesRequestStatus.Pending || r.Status == SeriesRequestStatus.Processing),
            "resolved" => query.Where(r =>
                r.Status != SeriesRequestStatus.Pending && r.Status != SeriesRequestStatus.Processing),
            _ => query,
        };

        var rows = await query
            // Pending first, then newest — the queue an admin works through, not a chronology.
            .OrderBy(r => r.Status == SeriesRequestStatus.Pending || r.Status == SeriesRequestStatus.Processing ? 0 : 1)
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
        if (!Enum.TryParse<SeriesRequestKind>(body.Kind, true, out var kind) || !Enum.IsDefined(kind))
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

            if (await submitter.FillNewSeriesAsync(
                    request, body.MetadataProviderId, ct, currentUser.MaxContentRating) is { } failed)
            {
                if (failed.Error == SeriesRequestSubmitError.SeriesAlreadyExists)
                {
                    const string key = "error.requests.seriesAlreadyExists";
                    // The id is only worth handing back when this user can open that series.
                    int? visibleId = failed.ExistingSeriesId is int existingId &&
                                     await db.Series.AnyAsync(s => s.Id == existingId, ct)
                        ? existingId
                        : null;
                    return Conflict(new { code = key, error = localizer.Get(key), seriesId = visibleId });
                }

                if (failed.Error == SeriesRequestSubmitError.ContentRatingTooHigh)
                {
                    return this.Forbidden(localizer, "error.requests.contentRating");
                }

                return this.Fail(localizer, "error.requests.metadataNotFound");
            }
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

        var submitted = await submitter.SubmitAsync(request, currentUser.UserName, ct);
        if (submitted.Error == SeriesRequestSubmitError.AlreadyPending)
        {
            return this.Conflict(localizer, "error.requests.alreadyPending");
        }

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
        var request = await db.SeriesRequests.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return NotFound();
        }

        if (request.Status != SeriesRequestStatus.Pending)
        {
            // Same disambiguation Approve and Reject use: a request another admin is still claiming
            // reads as "in progress", not "already resolved". The conditional update below would
            // draw that line anyway, so asking it directly here keeps the two answers consistent.
            return await ResolveClaimConflictAsync(id, ct);
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

        // Conditional write, same reason Approve claims this way: reading Pending and saving on the
        // tracked entity can overwrite an approval that finishes in between. On 0 rows either Approve
        // has already claimed it or another admin resolved it first: either way there is no range
        // left to edit.
        var editedAt = DateTime.UtcNow;
        int affected;
        try
        {
            affected = await db.SeriesRequests.IgnoreQueryFilters()
                .Where(r => r.Id == id && r.Status == SeriesRequestStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.OriginalChapterStart,
                        r => r.EditedAt == null ? r.ChapterStart : r.OriginalChapterStart)
                    .SetProperty(r => r.OriginalChapterEnd,
                        r => r.EditedAt == null ? r.ChapterEnd : r.OriginalChapterEnd)
                    .SetProperty(r => r.ChapterStart, start)
                    .SetProperty(r => r.ChapterEnd, end)
                    .SetProperty(r => r.EditedAt, editedAt)
                    .SetProperty(r => r.EditedByUserId, currentUser.UserId), ct);
        }
        catch (Exception e) when (SeriesRequestSubmitter.IsUniqueViolation(e))
        {
            // The new range matches another Pending request of this user's for the same series
            // (or the same provider id): the partial unique index refuses the row, same as a
            // fresh submit would. Same answer either way. ExecuteUpdateAsync doesn't wrap this in a
            // DbUpdateException the way SaveChangesAsync does, hence catching Exception itself.
            return this.Conflict(localizer, "error.requests.alreadyPending");
        }

        if (affected != 1)
        {
            return await ResolveClaimConflictAsync(id, ct);
        }

        var edited = await db.SeriesRequests.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(r => r.Id == id, ct);

        logger.LogInformation(
            "{User} edited request {Id} to chapters {Start}–{End}",
            currentUser.UserName, id, start, end);

        inbox.Raise(InboxEventType.RequestEdited, new InboxMessage(
                Key: "inbox.request.edited",
                Params: InboxMessage.Args(new { title = edited.Title, range = RangeKey(start, end), start = ChapterLabel(start), end = ChapterLabel(end) }),
                Url: "/requests"),
            InboxAudience.User(edited.UserId));

        return Ok((await ToDtosAsync([edited], ct))[0]);
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
            return await ResolveClaimConflictAsync(id, ct);
        }
        // The claim went through ExecuteUpdate, which the change tracker never sees, so this
        // entity still holds its pre-claim values. Assigning them by hand instead of reloading
        // leaves the tracker thinking Processing was always the original: setting Status back to
        // Pending on a failure then looks like no change at all and the release writes nothing,
        // stranding the request until the stale window expires.
        await db.Entry(request).ReloadAsync(ct);

        int queued;
        try
        {
            if (request.Kind == SeriesRequestKind.NewSeries && request.SeriesId is null)
            {
                if (body.RootFolderId is not int rootFolderId)
                {
                    await ReleaseClaimAsync(request);
                    return this.Fail(localizer, "error.requests.rootFolderRequired");
                }

                // Inside the claim-release try: a throw from CreateAsync (provider call, folder
                // naming, first save) used to leave the request in Processing for the whole 30-minute
                // stale window with nothing to release it early.
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
                        await ReleaseClaimAsync(request);
                        return result.Error switch
                        {
                            SeriesCreationError.RootFolderNotFound =>
                                this.Fail(localizer, "error.requests.rootFolderNotFound"),
                            SeriesCreationError.MetadataNotFound =>
                                this.Fail(localizer, "error.requests.metadataNotFound"),
                            // An earlier approval of this request committed a series that has since
                            // been deleted. Not "already in library" and not a fresh add either: the
                            // receipt that makes approval retry-safe is keyed on the request, so
                            // creating again here would mean honouring it after its result was thrown
                            // away.
                            SeriesCreationError.OperationResultGone =>
                                this.Conflict(localizer, "error.requests.approvedSeriesDeleted"),
                            SeriesCreationError.MutationIdReused =>
                                this.Conflict(localizer, "error.requests.approvedElsewhere"),
                            _ => this.Fail(localizer, "error.requests.metadataNotFound"),
                        };
                    }

                    // Somebody added it between the request and the approval. Nothing to do, but the
                    // request is genuinely satisfied — resolve it rather than making the admin reject
                    // a request whose outcome already happened. The series it already landed under is
                    // still worth linking: dropping it here is exactly the bug this branch exists to
                    // avoid.
                    if (result.ExistingSeriesId is int existingSeriesId)
                    {
                        request.SeriesId = existingSeriesId;
                        request.Title = result.ExistingSeriesTitle ?? request.Title;
                    }
                    return await ResolveAsAlreadyPresentAsync(request, ct);
                }

                request.SeriesId = result.Series.Id;
                // The title the series actually landed under, which is what the requester will
                // look for.
                request.Title = result.Series.Title;

                if (result.Warnings.Count > 0)
                {
                    logger.LogWarning(
                        "Approving request {Id} added {Title} with warnings: {Warnings}",
                        request.Id, result.Series.Title, string.Join(" ", result.Warnings));
                }
            }

            if (request.SeriesId is not int seriesId)
            {
                // A chapters request whose series was deleted since: nothing to queue, so approving
                // it would only tell the requester their chapters are on the way.
                await ReleaseClaimAsync(request);
                return this.Conflict(localizer, "error.requests.seriesGone");
            }

            queued = await QueueRangeAsync(
                seriesId, request.Title, request.ChapterStart, request.ChapterEnd, request.UserId, ct);

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
            // Only the save above clears the claim, so any throw here (including cancellation, e.g.
            // an admin closing the tab mid-approval) must release it or the request stays stuck
            // reading Processing, refusing Reject, Delete and retried approvals until it expires.
            logger.LogWarning(ex, "Releasing the approval claim on request {Id} after a failure", request.Id);
            await ReleaseClaimAsync(request);
            throw;
        }

        await NotifyResolvedAsync(request, approved: true, queued, ct);

        return Ok((await ToDtosAsync([request], ct))[0]);
    }

    /// <summary>
    /// The claim on a resolve action (approve, reject, edit) lost the race: answers whether that is
    /// because another admin is still mid-approval, or because the request was already resolved one
    /// way or another. Shared so the three call sites agree on the same read.
    /// </summary>
    private async Task<IActionResult> ResolveClaimConflictAsync(int id, CancellationToken ct)
    {
        var status = await db.SeriesRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.Id == id).Select(r => (SeriesRequestStatus?)r.Status)
            .FirstOrDefaultAsync(ct);
        return status is null or SeriesRequestStatus.Pending or SeriesRequestStatus.Processing
            ? this.Conflict(localizer, "error.requests.approvalInProgress")
            : this.Conflict(localizer, "error.requests.alreadyResolved");
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
    /// <para>
    /// The submitter's pre-check treats a Processing request as already pending, but that read is
    /// itself best-effort: an identical request can still land as Pending while this one is
    /// Processing, and putting this one back to Pending then collides with it on the partial unique
    /// index. That is a genuine duplicate by then, not a claim worth holding, so it resolves as one
    /// instead of sitting Processing until the stale window expires.
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
        catch (Exception ex) when (SeriesRequestSubmitter.IsUniqueViolation(ex))
        {
            logger.LogWarning(ex,
                "Request {Id} could not be released to Pending, a duplicate is already pending; rejecting it instead",
                request.Id);
            await ResolveAsDuplicateAsync(request);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not release the approval claim on request {Id}", request.Id);
        }
    }

    /// <summary>
    /// Rejects a Processing request that lost the race to an identical Pending one instead of
    /// leaving it stranded. Best effort like <see cref="ReleaseClaimAsync"/>: a failure here still
    /// has the stale window as its backstop.
    /// </summary>
    private async Task ResolveAsDuplicateAsync(SeriesRequest request)
    {
        try
        {
            var note = localizer.GetFor(
                await locales.ResolveAsync(request.UserId, CancellationToken.None),
                "error.requests.alreadyPending");
            await db.SeriesRequests.IgnoreQueryFilters()
                .Where(r => r.Id == request.Id && r.Status == SeriesRequestStatus.Processing)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, SeriesRequestStatus.Rejected)
                    .SetProperty(r => r.ApprovalClaimedAtUtc, (DateTime?)null)
                    .SetProperty(r => r.ResolvedAt, DateTime.UtcNow)
                    .SetProperty(r => r.ResolutionNote, note), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not resolve request {Id} as a duplicate after a failed release", request.Id);
        }
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{id:int}/reject")]
    public async Task<IActionResult> Reject(int id, [FromBody] RejectSeriesRequestBody body, CancellationToken ct)
    {
        var exists = await db.SeriesRequests.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(r => r.Id == id, ct);
        if (!exists)
        {
            return NotFound();
        }

        // Conditional write instead of read-then-save on a tracked entity: a Chapters approval that
        // completes between the read and this save used to be overwritten by Rejected here, telling
        // the requester their request was declined when it had just been granted. On 0 rows the
        // request is either mid-approval or already resolved one way or another.
        var resolvedAt = DateTime.UtcNow;
        var note = Trimmed(body.Note);
        var affected = await db.SeriesRequests.IgnoreQueryFilters()
            .Where(r => r.Id == id && r.Status == SeriesRequestStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Status, SeriesRequestStatus.Rejected)
                .SetProperty(r => r.ResolvedAt, resolvedAt)
                .SetProperty(r => r.ResolvedByUserId, currentUser.UserId)
                .SetProperty(r => r.ResolutionNote, note), ct);

        if (affected != 1)
        {
            return await ResolveClaimConflictAsync(id, ct);
        }

        var request = await db.SeriesRequests.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(r => r.Id == id, ct);

        await NotifyResolvedAsync(request, approved: false, queued: 0, ct);

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
        // Rendered now, in the requester's own language, rather than carrying a key: ResolutionNote
        // is also where an admin's free-text rejection note lives, and the requests page and the
        // inbox notification both show it verbatim (see NotifyResolved). A stored key would have
        // nowhere to fall back to for that free-text case.
        request.ResolutionNote = localizer.GetFor(
            await locales.ResolveAsync(request.UserId, ct), "error.requests.alreadyInLibrary");
        await db.SaveChangesAsync(ct);

        await NotifyResolvedAsync(request, approved: true, queued: 0, ct);

        return Ok((await ToDtosAsync([request], ct))[0]);
    }

    /// <summary>
    /// Tells the requester what happened to their request. Always the requester, never the admin who
    /// acted: an admin resolving their own request would otherwise get told about it by themselves.
    /// The resolution note is carried through verbatim, because on a rejection it <em>is</em> the
    /// answer — the request page shows the same text.
    /// </summary>
    private async Task NotifyResolvedAsync(SeriesRequest request, bool approved, int queued, CancellationToken ct)
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

        // Outbound goes to the admins' channel rather than the requester, so it names who asked.
        // The resolution is already saved, so a failure here must not turn it into an error response.
        try
        {
            var requester = await db.Users.Where(u => u.Id == request.UserId)
                .Select(u => u.DisplayName ?? u.UserName).FirstOrDefaultAsync(ct);
            var locale = await locales.DefaultAsync(ct);
            notifications.Dispatch(NotificationEventType.RequestResolved, new NotificationMessage(
                NotificationEventType.RequestResolved,
                Title: localizer.GetFor(locale, "notify.request.resolved.title"),
                Body: localizer.GetFor(locale, "notify.request.resolved.body", new
                {
                    outcome = approved ? "approved" : "rejected",
                    user = requester ?? string.Empty,
                    series = request.Title,
                }),
                Level: approved ? NotificationLevel.Info : NotificationLevel.Warning,
                SeriesTitle: request.Title,
                SeriesId: request.SeriesId,
                Url: request.SeriesId is { } seriesId ? $"/series/{seriesId}" : "/requests"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not send the resolved notification for request {Id}", request.Id);
        }
    }

    /// <summary>The catalogue key for an edited chapter range, which <c>InboxRenderer</c> words at read time.</summary>
    private static string RangeKey(decimal? start, decimal? end) => (start, end) switch
    {
        (null, null) => "inbox.request.range.all",
        (not null, null) => "inbox.request.range.from",
        (null, not null) => "inbox.request.range.upTo",
        _ => "inbox.request.range.between",
    };

    private static string ChapterLabel(decimal? number) =>
        number?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

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
            names.GetValueOrDefault(r.UserId) ?? localizer.Get("error.requests.unknownUser"),
            r.ResolvedByUserId is int by ? names.GetValueOrDefault(by) : null,
            r.EditedByUserId is int editor ? names.GetValueOrDefault(editor) : null))];
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A range whose ends are both blank is "everything", not a zero-width window.</summary>
    private static (decimal?, decimal?) NormalizeRange(decimal? start, decimal? end) =>
        (start is null or < 0 ? null : start, end is null or < 0 ? null : end);
}
