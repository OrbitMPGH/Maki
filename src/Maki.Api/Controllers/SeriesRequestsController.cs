using Maki.Api.Auth;
using Maki.Api.Dtos;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Metadata;
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
    MakiDbContext db,
    IEnumerable<IMetadataProvider> metadataProviders,
    SeriesCreationService seriesCreation,
    DownloadQueueService downloadQueue,
    DownloadBatchNotifier downloadBatches,
    EventBroadcaster events,
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
            return BadRequest(new { error = "Unknown request kind" });
        }

        var (start, end) = NormalizeRange(body.ChapterStart, body.ChapterEnd);
        if (start is not null && end is not null && end < start)
        {
            return BadRequest(new { error = "The last chapter can't be lower than the first" });
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
                return BadRequest(new { error = "A series to request is required" });
            }

            // Resolved from the provider rather than taken from the request body: the admin reviewing
            // this has to be looking at the title that provider id actually resolves to, not at text
            // a client supplied alongside it.
            var metadata = await metadataProviders.First().GetAsync(body.MetadataProviderId, ct);
            if (metadata is null)
            {
                return BadRequest(new { error = "Series not found on metadata provider" });
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
                    return Conflict(new { error = "Series already exists in library", seriesId = existing });
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
                return BadRequest(new { error = "A series is required" });
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
            return Conflict(new { error = "You already have that request pending" });
        }

        db.SeriesRequests.Add(request);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "{User} requested {Kind} '{Title}'", currentUser.UserName, request.Kind, request.Title);

        await events.SeriesRequested(request.Id, request.Title, currentUser.UserName);

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
            return Conflict(new { error = "That request has already been resolved" });
        }

        var (start, end) = NormalizeRange(body.ChapterStart, body.ChapterEnd);
        if (start is not null && end is not null && end < start)
        {
            return BadRequest(new { error = "The last chapter can't be lower than the first" });
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

        if (request.Status != SeriesRequestStatus.Pending)
        {
            return Conflict(new { error = "That request has already been resolved" });
        }

        if (request.Kind == SeriesRequestKind.NewSeries && request.SeriesId is null)
        {
            if (body.RootFolderId is not int rootFolderId)
            {
                return BadRequest(new { error = "Pick a root folder to add the series to" });
            }

            var result = await seriesCreation.CreateAsync(
                request.MetadataProviderId!, rootFolderId, monitored: true, body.MonitorNewItems, ct);

            if (result.Series is null)
            {
                return result.Error switch
                {
                    SeriesCreationError.RootFolderNotFound => BadRequest(new { error = "Root folder not found" }),
                    SeriesCreationError.MetadataNotFound => BadRequest(new { error = "Series not found on metadata provider" }),
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

        var queued = request.SeriesId is int seriesId
            ? await QueueRangeAsync(seriesId, request.Title, request.ChapterStart, request.ChapterEnd, ct)
            : 0;

        request.Status = SeriesRequestStatus.Approved;
        request.QueuedCount = queued;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolvedByUserId = currentUser.UserId;
        request.ResolutionNote = Trimmed(body.Note);
        await db.SaveChangesAsync(ct);

        return Ok((await ToDtosAsync([request], ct))[0]);
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

        if (request.Status != SeriesRequestStatus.Pending)
        {
            return Conflict(new { error = "That request has already been resolved" });
        }

        request.Status = SeriesRequestStatus.Rejected;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolvedByUserId = currentUser.UserId;
        request.ResolutionNote = Trimmed(body.Note);
        await db.SaveChangesAsync(ct);

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

        if (!IsAdmin && request.Status != SeriesRequestStatus.Pending)
        {
            return Conflict(new { error = "That request has already been resolved" });
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
    private async Task<int> QueueRangeAsync(
        int seriesId, string title, decimal? start, decimal? end, CancellationToken ct)
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
                if (await downloadQueue.EnqueueChapterAsync(chapterId, ct) is { } item)
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

        downloadBatches.Queued(seriesId, title, queuedItemIds);
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
        request.QueuedCount = 0;
        request.ResolvedAt = DateTime.UtcNow;
        request.ResolvedByUserId = currentUser.UserId;
        request.ResolutionNote = "Already in the library.";
        await db.SaveChangesAsync(ct);

        return Ok((await ToDtosAsync([request], ct))[0]);
    }

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
