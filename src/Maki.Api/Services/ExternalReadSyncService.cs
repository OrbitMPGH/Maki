using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Parsing;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Writes read state observed in Kavita into <see cref="ChapterProgress"/>, the table every read
/// count is derived from.
/// <para>
/// Shared by both Kavita paths on purpose. The one-off import (<see cref="KavitaReadImportService"/>)
/// backfills a library that was read in Kavita before Maki existed; the recurring scrobble tick
/// (<c>ScrobbleService.KavitaPassAsync</c>) keeps it current. Before this existed the tick only
/// advanced <c>ReadingState.MaxChapter</c>, so ongoing Kavita reading was invisible per chapter and
/// the UI had to infer read state from that mark instead — which over-reported, because the mark is
/// forward-only and covers every chapter numbered below it.
/// </para>
/// <para>
/// Emits no <see cref="StatsEvent"/>. Rewind's numbers come from the mark deltas that
/// <see cref="ReadingProgressService"/> computes, and duplicating them here would double-count
/// every chapter Kavita reports.
/// </para>
/// </summary>
public class ExternalReadSyncService(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// Marks every downloaded local chapter Kavita reports as fully read, from the same
    /// <see cref="KavitaProgress.Compute"/> result the tracker push uses. A chapter counts when its
    /// number falls in a read range, or when it lives in a volume archive whose volume Kavita reports
    /// as fully read: Kavita sees such an archive as a volume with no chapter number at all, so
    /// matching by number alone never marked anything in a library of volumes. Returns how many rows
    /// changed, so an idempotent re-run reports 0.
    /// <para>
    /// Two rows are left alone: one that is already complete (never un-complete, and never restamp
    /// a read Maki observed itself as external), and one carrying
    /// <see cref="ChapterProgress.UnreadAt"/> — an explicit local mark-unread outranks Kavita's
    /// stale flag, or the next tick would quietly undo it.
    /// </para>
    /// </summary>
    /// <param name="userId">
    /// Whose rows to write. Kavita is one external account, so this is always the user named by
    /// <c>kavita.userid</c> — but it is passed rather than assumed because both callers run outside a
    /// request (the recurring pass and the one-off import) where there is no current user to read.
    /// </param>
    public async Task<int> MarkAsync(
        int userId, int seriesId, KavitaProgress.SeriesProgress read, CancellationToken ct)
    {
        if (read.IsEmpty)
        {
            return 0;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var chapters = await db.Chapters
            .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null)
            .Select(c => new { c.Id, c.Number, c.ChapterFile!.RelativePath })
            .ToListAsync(ct);

        var targets = chapters
            .Where(c => (c.Number is { } n && read.CoversChapter(n)) || InReadVolume(read, c.RelativePath))
            .Select(c => c.Id)
            .ToList();
        if (targets.Count == 0)
        {
            return 0;
        }

        var existing = await db.ChapterProgress
            .Where(p => p.UserId == userId && p.SeriesId == seriesId && targets.Contains(p.ChapterId))
            .ToListAsync(ct);
        var byChapter = existing.ToDictionary(p => p.ChapterId);

        var now = DateTime.UtcNow;
        var changed = 0;
        foreach (var chapterId in targets)
        {
            if (byChapter.TryGetValue(chapterId, out var row))
            {
                if (row.Completed || row.UnreadAt is not null)
                {
                    continue;
                }

                // An in-progress chapter finished in Kavita: complete it but keep the position, so
                // reopening it here still lands where the reader left off.
                row.Completed = true;
                row.UpdatedAt = now;
            }
            else
            {
                // PageCount stays 0: filling it would mean opening every archive in the library.
                // The reader writes the real count the first time the chapter is opened, and
                // nothing reads PageCount for a chapter that is already complete.
                db.ChapterProgress.Add(new ChapterProgress
                {
                    UserId = userId,
                    SeriesId = seriesId,
                    ChapterId = chapterId,
                    PageIndex = 0,
                    PageCount = 0,
                    Completed = true,
                    External = true,
                    StartedAt = now,
                    UpdatedAt = now,
                });
            }

            changed++;
        }

        await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>Whether the file is a volume archive (no chapter number in its name) for a volume Kavita reports as read.</summary>
    private static bool InReadVolume(KavitaProgress.SeriesProgress read, string relativePath)
    {
        if (read.Volumes.Count == 0)
        {
            return false;
        }

        var parsed = ReleaseNameParser.ParseFileName(relativePath);
        return parsed is { IsVolume: true, Volume: { } from } &&
               read.CoversVolume(from, parsed.VolumeEnd ?? from);
    }
}
