using Maki.Api.Hubs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Reading;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

public record CreateNoteRequest(string? Body, int? PageIndex, string? Visibility);

public record ReplyNoteRequest(string? Body);

/// <param name="ClearPage">Unpins the note from its page, since a null <paramref name="PageIndex"/> means "unchanged".</param>
public record UpdateNoteRequest(string? Body, int? PageIndex, string? Visibility, bool? ClearPage);

/// <param name="Name">Null when the reader may not see this author's login name and they set no display name.</param>
public record NoteAuthorDto(int Id, string? Name);

/// <param name="Author">Null once the author's account was deleted.</param>
/// <param name="Deleted"><c>author</c> or <c>moderator</c> for a tombstone, else null.</param>
/// <param name="Replies">Set on roots only.</param>
public record NoteDto(
    int Id,
    int ChapterId,
    int? ParentId,
    int? PageIndex,
    string Visibility,
    string Body,
    NoteAuthorDto? Author,
    bool IsMine,
    bool CanEdit,
    bool CanDelete,
    DateTime CreatedAt,
    DateTime? EditedAt,
    string? Deleted,
    IReadOnlyList<NoteDto>? Replies);

/// <param name="Total">Distinct visible roots: a shared root of mine is in both <paramref name="Mine"/> and <paramref name="Shared"/>.</param>
public record NoteCountDto(int ChapterId, int Mine, int Shared, int Total);

/// <summary>
/// Chapter notes: private ones only their author sees, shared ones everyone who can see the series
/// reads and answers one level deep.
/// <para>
/// Visibility is the <see cref="ChapterNote"/> query filter's job and nothing here bypasses it. Never
/// add <c>IgnoreQueryFilters</c> to a notes query: it drops the nested Series filter too, so a count
/// or moderation query written with it would also lose root-folder visibility. An id the filter hides
/// is a 404 on every action, so the API never confirms that someone's private note exists.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1")]
public class NotesController(
    ILocalizer localizer,
    MakiDbContext db,
    ICurrentUser currentUser,
    InboxService inbox,
    EventBroadcaster events,
    IAppSettings appSettings,
    ILogger<NotesController> logger) : ControllerBase
{
    [HttpGet("chapters/{chapterId:int}/notes")]
    public async Task<IActionResult> List(int chapterId, CancellationToken ct)
    {
        if (!await db.Chapters.AnyAsync(c => c.Id == chapterId, ct))
        {
            return NotFound();
        }

        var notes = await db.ChapterNotes.AsNoTracking().Where(n => n.ChapterId == chapterId).ToListAsync(ct);
        return Ok(await ThreadsAsync(notes, ct));
    }

    /// <summary>Per chapter of a series: my roots, and shared roots by anyone. Tombstones count for neither.</summary>
    [HttpGet("series/{seriesId:int}/notes/counts")]
    public async Task<IActionResult> Counts(int seriesId, CancellationToken ct)
    {
        var me = currentUser.UserId;
        var counts = await db.ChapterNotes
            .Where(n => n.SeriesId == seriesId && n.ParentId == null && n.DeletedAt == null)
            .GroupBy(n => n.ChapterId)
            .Select(g => new NoteCountDto(
                g.Key,
                g.Count(n => n.AuthorId == me),
                g.Count(n => n.Visibility == NoteVisibility.Shared),
                g.Count()))
            .ToListAsync(ct);
        return Ok(counts);
    }

    [HttpPost("chapters/{chapterId:int}/notes")]
    public async Task<IActionResult> Create(int chapterId, [FromBody] CreateNoteRequest request, CancellationToken ct)
    {
        if (Invalid(request.Body, out var body) is { } bad)
        {
            return bad;
        }

        if (request.PageIndex is < 0)
        {
            return this.Fail(localizer, "error.notes.badPage");
        }

        if (!TryParseVisibility(request.Visibility, out var visibility))
        {
            return this.Fail(localizer, "error.notes.badVisibility");
        }

        var chapter = await db.Chapters.AsNoTracking()
            .Where(c => c.Id == chapterId)
            .Select(c => new { c.Id, c.SeriesId })
            .FirstOrDefaultAsync(ct);
        if (chapter is null)
        {
            return NotFound();
        }

        var note = new ChapterNote
        {
            SeriesId = chapter.SeriesId,
            ChapterId = chapter.Id,
            AuthorId = currentUser.UserId,
            PageIndex = request.PageIndex,
            Visibility = visibility,
            Body = body,
            CreatedAt = DateTime.UtcNow,
        };
        db.ChapterNotes.Add(note);
        if (await SaveAsync(ct) is { } gone)
        {
            return gone;
        }

        if (visibility == NoteVisibility.Shared)
        {
            await BroadcastAsync(note.SeriesId, note.ChapterId, ct);
        }

        return Ok((await ThreadsAsync([note], ct))[0]);
    }

    [HttpPost("notes/{id:int}/replies")]
    public async Task<IActionResult> Reply(int id, [FromBody] ReplyNoteRequest request, CancellationToken ct)
    {
        if (Invalid(request.Body, out var body) is { } bad)
        {
            return bad;
        }

        var seriesId = await db.ChapterNotes.Where(n => n.Id == id).Select(n => (int?)n.SeriesId).FirstOrDefaultAsync(ct);
        if (seriesId is null)
        {
            return NotFound();
        }

        ChapterNote? root;
        ChapterNote reply;
        using (await SeriesLocks.SeriesAsync(seriesId.Value, ct))
        {
            var target = await db.ChapterNotes.FirstOrDefaultAsync(n => n.Id == id, ct);
            root = target?.ParentId is { } parentId
                ? await db.ChapterNotes.FirstOrDefaultAsync(n => n.Id == parentId, ct)
                : target;

            // A private note has no thread, and saying so would confirm it exists.
            if (root is null || root.Visibility != NoteVisibility.Shared)
            {
                return NotFound();
            }

            if (root.DeletedAt is not null)
            {
                return this.Fail(localizer, "error.notes.deleted");
            }

            reply = new ChapterNote
            {
                SeriesId = root.SeriesId,
                ChapterId = root.ChapterId,
                AuthorId = currentUser.UserId,
                ParentId = root.Id,
                Visibility = NoteVisibility.Shared,
                Body = body,
                CreatedAt = DateTime.UtcNow,
            };
            db.ChapterNotes.Add(reply);
            if (await SaveAsync(ct) is { } gone)
            {
                return gone;
            }
        }

        await BroadcastAsync(root.SeriesId, root.ChapterId, ct);
        try
        {
            await NotifyRepliedAsync(root, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The reply is saved; a failed request here would only invite a duplicate retry.
            logger.LogWarning(ex, "Could not raise reply notifications for note {NoteId}", root.Id);
        }

        return Ok((await ThreadsAsync([reply], ct))[0]);
    }

    /// <summary>Author only. Answers with the whole thread the note belongs to.</summary>
    [HttpPatch("notes/{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateNoteRequest request, CancellationToken ct)
    {
        var seriesId = await db.ChapterNotes.Where(n => n.Id == id).Select(n => (int?)n.SeriesId).FirstOrDefaultAsync(ct);
        if (seriesId is null)
        {
            return NotFound();
        }

        int rootId;
        int chapterId;
        bool shared;
        using (await SeriesLocks.SeriesAsync(seriesId.Value, ct))
        {
            var note = await db.ChapterNotes.FirstOrDefaultAsync(n => n.Id == id, ct);
            if (note is null)
            {
                return NotFound();
            }

            if (note.AuthorId != currentUser.UserId)
            {
                return this.Forbidden(localizer, "error.notes.notYours");
            }

            if (note.DeletedAt is not null)
            {
                return this.Fail(localizer, "error.notes.deleted");
            }

            var wasShared = note.Visibility == NoteVisibility.Shared;
            var isReply = note.ParentId is not null;

            if (request.Body is not null)
            {
                if (Invalid(request.Body, out var body) is { } bad)
                {
                    return bad;
                }

                if (body != note.Body)
                {
                    note.Body = body;
                    note.EditedAt = DateTime.UtcNow;
                }
            }

            if (request.ClearPage == true || request.PageIndex is not null)
            {
                if (isReply || request.PageIndex is < 0)
                {
                    return this.Fail(localizer, "error.notes.badPage");
                }

                note.PageIndex = request.ClearPage == true ? null : request.PageIndex;
            }

            if (request.Visibility is not null)
            {
                if (!TryParseVisibility(request.Visibility, out var visibility) || (isReply && visibility != note.Visibility))
                {
                    return this.Fail(localizer, "error.notes.badVisibility");
                }

                if (visibility != note.Visibility)
                {
                    var replies = await db.ChapterNotes.Where(n => n.ParentId == note.Id).ToListAsync(ct);

                    // Turning it private would take other people's words out of view with it.
                    if (visibility == NoteVisibility.Private && replies.Any(r => r.AuthorId != note.AuthorId))
                    {
                        return this.Conflict(localizer, "error.notes.hasReplies");
                    }

                    note.Visibility = visibility;
                    foreach (var reply in replies)
                    {
                        reply.Visibility = visibility;
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            rootId = note.ParentId ?? note.Id;
            chapterId = note.ChapterId;
            shared = wasShared || note.Visibility == NoteVisibility.Shared;
        }

        if (shared)
        {
            await BroadcastAsync(seriesId.Value, chapterId, ct);
        }

        var thread = await db.ChapterNotes.AsNoTracking().Where(n => n.Id == rootId || n.ParentId == rootId).ToListAsync(ct);
        return Ok((await ThreadsAsync(thread, ct))[0]);
    }

    /// <summary>
    /// The author, or an admin for a shared note. A shared root that other people answered becomes a
    /// tombstone so their replies stay readable; the tombstone goes with its last reply.
    /// </summary>
    [HttpDelete("notes/{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var seriesId = await db.ChapterNotes.Where(n => n.Id == id).Select(n => (int?)n.SeriesId).FirstOrDefaultAsync(ct);
        if (seriesId is null)
        {
            return NotFound();
        }

        int chapterId;
        bool shared;
        using (await SeriesLocks.SeriesAsync(seriesId.Value, ct))
        {
            var note = await db.ChapterNotes.FirstOrDefaultAsync(n => n.Id == id, ct);
            if (note is null)
            {
                return NotFound();
            }

            var mine = note.AuthorId == currentUser.UserId;
            shared = note.Visibility == NoteVisibility.Shared;
            if (!mine && !(shared && currentUser.Has(MakiPermission.Admin)))
            {
                return this.Forbidden(localizer, "error.notes.notYours");
            }

            if (note.DeletedAt is not null)
            {
                return this.Fail(localizer, "error.notes.deleted");
            }

            chapterId = note.ChapterId;
            if (note.ParentId is { } parentId)
            {
                db.ChapterNotes.Remove(note);
                var root = await db.ChapterNotes.FirstOrDefaultAsync(n => n.Id == parentId, ct);
                if (root?.DeletedAt is not null &&
                    !await db.ChapterNotes.AnyAsync(n => n.ParentId == parentId && n.Id != note.Id, ct))
                {
                    db.ChapterNotes.Remove(root);
                }
            }
            else if (await db.ChapterNotes.AnyAsync(n => n.ParentId == note.Id && n.AuthorId != note.AuthorId, ct))
            {
                note.Body = string.Empty;
                note.DeletedAt = DateTime.UtcNow;
                note.DeletedByModerator = !mine;
            }
            else
            {
                // Every reply under it is the author's own, and the self-reference cascades them.
                db.ChapterNotes.Remove(note);
            }

            await db.SaveChangesAsync(ct);
        }

        if (shared)
        {
            await BroadcastAsync(seriesId.Value, chapterId, ct);
        }

        return NoContent();
    }

    private IActionResult? Invalid(string? raw, out string body)
    {
        body = raw?.Trim() ?? string.Empty;
        if (body.Length == 0)
        {
            return this.Fail(localizer, "error.notes.empty");
        }

        return body.Length > ChapterNote.MaxBodyLength
            ? this.Fail(localizer, "error.notes.tooLong", new { max = ChapterNote.MaxBodyLength })
            : null;
    }

    private static bool TryParseVisibility(string? raw, out NoteVisibility visibility)
    {
        visibility = NoteVisibility.Private;
        return raw is null || (Enum.TryParse(raw, ignoreCase: true, out visibility) && Enum.IsDefined(visibility));
    }

    /// <summary>
    /// Saves, turning a write onto a chapter row that a duplicate merge deleted meanwhile into a 404 the
    /// client answers by refetching.
    /// </summary>
    private async Task<IActionResult?> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (DbErrors.IsForeignKeyViolation(ex))
        {
            return this.NotFoundMessage(localizer, "error.notes.chapterGone");
        }
    }

    private async Task BroadcastAsync(int seriesId, int chapterId, CancellationToken ct)
    {
        var rootFolderId = await db.Series.Where(s => s.Id == seriesId).Select(s => (int?)s.RootFolderId).FirstOrDefaultAsync(ct);
        if (rootFolderId is not { } folder)
        {
            return;
        }

        try
        {
            await events.ChapterNotesChanged(seriesId, chapterId, folder);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not broadcast a notes change on chapter {ChapterId}", chapterId);
        }
    }

    /// <summary>
    /// One <see cref="InboxEventType.NoteReply"/> per participant in the thread, minus the replier and
    /// anyone who can no longer see the series. Never carries the note's text, and no parameter is called
    /// <c>note</c>: the renderer appends that one verbatim.
    /// </summary>
    private async Task NotifyRepliedAsync(ChapterNote root, CancellationToken ct)
    {
        var me = currentUser.UserId;
        var participants = await db.ChapterNotes
            .Where(n => n.Id == root.Id || n.ParentId == root.Id)
            .Where(n => n.AuthorId != null && n.AuthorId != me)
            .Select(n => n.AuthorId!.Value)
            .Distinct()
            .ToListAsync(ct);
        if (participants.Count == 0)
        {
            return;
        }

        var rootFolderId = await db.Series.Where(s => s.Id == root.SeriesId).Select(s => s.RootFolderId).FirstAsync(ct);
        var recipients = await db.Users
            .Where(u => participants.Contains(u.Id) &&
                        (u.AllRootFolders || db.UserRootFolders.Any(g => g.UserId == u.Id && g.RootFolderId == rootFolderId)))
            .Select(u => new { u.Id, u.Permissions })
            .ToListAsync(ct);
        var replier = await db.Users.Where(u => u.Id == me).Select(u => new { u.DisplayName, u.UserName }).FirstAsync(ct);
        var loginNamesVisible = await UserNameVisibility.LoginNamesVisibleAsync(appSettings, ct);
        var chapter = await db.Chapters.AsNoTracking().FirstAsync(c => c.Id == root.ChapterId, ct);
        var label = ChapterLabel.For(chapter);

        foreach (var recipient in recipients)
        {
            var name = UserNameVisibility.ShownName(replier.DisplayName, replier.UserName,
                loginNamesVisible || recipient.Permissions.Grants(MakiPermission.Admin));
            inbox.Raise(InboxEventType.NoteReply, new InboxMessage(
                Key: "inbox.noteReply",
                Params: InboxMessage.Args(new { userName = name ?? string.Empty, hasName = name is null ? "no" : "yes", chapter = label }),
                SeriesId: root.SeriesId,
                ChapterId: root.ChapterId,
                Url: $"/read/{root.ChapterId}?notes={root.Id}"), InboxAudience.User(recipient.Id));
        }
    }

    /// <summary>Roots oldest first with their replies under them; a lone reply maps to itself.</summary>
    private async Task<List<NoteDto>> ThreadsAsync(IReadOnlyCollection<ChapterNote> notes, CancellationToken ct)
    {
        var authorIds = notes.Where(n => n.AuthorId != null).Select(n => n.AuthorId!.Value).Distinct().ToList();
        var authors = await db.Users
            .Where(u => authorIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.UserName })
            .ToDictionaryAsync(u => u.Id, ct);
        var admin = currentUser.Has(MakiPermission.Admin);
        var loginNamesVisible = await UserNameVisibility.LoginNamesVisibleAsync(appSettings, ct);

        NoteDto Map(ChapterNote n, IReadOnlyList<NoteDto>? replies)
        {
            var mine = n.AuthorId == currentUser.UserId;
            var live = n.DeletedAt is null;
            var author = n.AuthorId is { } authorId
                ? new NoteAuthorDto(authorId, authors.TryGetValue(authorId, out var a)
                    ? UserNameVisibility.For(currentUser, authorId, a.DisplayName, a.UserName, loginNamesVisible)
                    : null)
                : null;
            return new NoteDto(
                n.Id, n.ChapterId, n.ParentId, n.PageIndex,
                n.Visibility == NoteVisibility.Shared ? "shared" : "private",
                n.Body, author, mine,
                CanEdit: mine && live,
                CanDelete: live && (mine || (admin && n.Visibility == NoteVisibility.Shared)),
                n.CreatedAt, n.EditedAt,
                live ? null : n.DeletedByModerator ? "moderator" : "author",
                replies);
        }

        var byParent = notes.Where(n => n.ParentId != null).ToLookup(n => n.ParentId!.Value);
        var roots = notes.Where(n => n.ParentId == null).ToList();
        var rootIds = roots.Select(r => r.Id).ToHashSet();

        return roots
            .OrderBy(n => n.CreatedAt).ThenBy(n => n.Id)
            .Select(r => Map(r, byParent[r.Id].OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).Select(n => Map(n, null)).ToList()))
            .Concat(notes.Where(n => n.ParentId is { } p && !rootIds.Contains(p)).Select(n => Map(n, null)))
            .ToList();
    }
}
