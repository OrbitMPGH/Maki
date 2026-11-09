using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public static class ChapterNoteCleanup
{
    /// <summary>
    /// Stages what an account deletion does to that user's notes, for the caller's next save: private
    /// notes, replies and shared roots nobody else answered go; shared roots that others answered become
    /// tombstones, which the <c>AuthorId</c> SetNull then leaves authorless. A tombstone whose last reply
    /// this removes goes too.
    /// <para>
    /// Unfiltered on purpose: the admin deleting the account cannot see its private notes, and they
    /// still have to go. It only deletes or blanks rows.
    /// </para>
    /// </summary>
    public static async Task ForgetAuthorAsync(MakiDbContext db, int userId, CancellationToken ct)
    {
        var own = await db.ChapterNotes.IgnoreQueryFilters().Where(n => n.AuthorId == userId).ToListAsync(ct);
        if (own.Count == 0)
        {
            return;
        }

        var rootIds = own.Where(n => n.ParentId == null).Select(n => n.Id).ToList();
        var answered = (await db.ChapterNotes.IgnoreQueryFilters()
                .Where(n => n.ParentId != null && rootIds.Contains(n.ParentId.Value) && n.AuthorId != userId)
                .Select(n => n.ParentId!.Value)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        foreach (var note in own)
        {
            if (note.ParentId is null && answered.Contains(note.Id))
            {
                note.Body = string.Empty;
                note.DeletedAt ??= DateTime.UtcNow;
            }
            else
            {
                db.ChapterNotes.Remove(note);
            }
        }

        var ownReplyIds = own.Where(n => n.ParentId != null).Select(n => n.Id).ToList();
        var foreignParents = own
            .Where(n => n.ParentId is { } p && !rootIds.Contains(p))
            .Select(n => n.ParentId!.Value)
            .Distinct()
            .ToList();
        var emptied = await db.ChapterNotes.IgnoreQueryFilters()
            .Where(r => foreignParents.Contains(r.Id) && r.DeletedAt != null &&
                        !db.ChapterNotes.IgnoreQueryFilters().Any(c => c.ParentId == r.Id && !ownReplyIds.Contains(c.Id)))
            .ToListAsync(ct);
        db.ChapterNotes.RemoveRange(emptied);
    }
}
