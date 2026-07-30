using Maki.Core.Entities;
using Maki.Core.Kavita;
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
    /// <summary>Kavita marks specials/uncounted items with huge sentinel numbers.</summary>
    private const double Sentinel = 10000;

    /// <summary>
    /// Chapter numbers Kavita reports as <em>fully</em> read. A partially-read chapter is not a
    /// read one, and Kavita tags specials/uncounted entries with huge sentinel numbers that must
    /// never be matched against a real local chapter number.
    /// </summary>
    public static HashSet<decimal> ReadChapterNumbers(List<KavitaProgress.KavitaVolumeDto> volumes) =>
        volumes
            .SelectMany(v => v.Chapters ?? [])
            .Where(c => !c.IsSpecial && c.Pages > 0 && c.PagesRead >= c.Pages &&
                        c.Number is { } n && n > 0 && n < Sentinel)
            .Select(c => (decimal)c.Number!.Value)
            .ToHashSet();

    /// <summary>
    /// Marks every downloaded local chapter whose number Kavita reports as fully read. Returns how
    /// many rows changed, so an idempotent re-run reports 0.
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
    public async Task<int> MarkAsync(int userId, int seriesId, HashSet<decimal> readNumbers, CancellationToken ct)
    {
        if (readNumbers.Count == 0)
        {
            return 0;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var chapters = await db.Chapters
            .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null && c.Number != null)
            .Select(c => new { c.Id, c.Number })
            .ToListAsync(ct);

        var targets = chapters.Where(c => readNumbers.Contains(c.Number!.Value)).Select(c => c.Id).ToList();
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
}
