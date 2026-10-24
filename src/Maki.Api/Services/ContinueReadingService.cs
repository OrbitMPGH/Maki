using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// What to read next in a series, and how much of it is left.
/// </summary>
/// <param name="ChapterId">The next downloaded chapter that has not been read.</param>
/// <param name="Label">Rendered server-side — see <see cref="ChapterLabel"/> for why.</param>
/// <param name="UnreadChapters">Downloaded chapters in the series still unread, this one included.</param>
public record NextChapter(int ChapterId, string Label, int UnreadChapters);

/// <summary>
/// Resolves "what do I read next" for a <em>set</em> of series in a fixed number of queries.
/// <para>
/// The per-series reader endpoint used to load every chapter row of the series and order them in
/// memory. That is fine for one series behind a detail page and quadratic behind a dashboard rail,
/// so both callers now come through here. The in-memory ordering itself stays: <c>Chapter.Number</c>
/// is a decimal stored as REAL and one-shots sort last on a null, neither of which SQLite can
/// express in an ORDER BY.
/// </para>
/// <para>
/// Reads <see cref="Maki.Core.Entities.ChapterProgress"/> and nothing else. <c>ReadingState</c> is
/// deliberately never joined here: <c>MaxChapter</c> is a forward-only mark that reports chapters
/// read which were never opened, and that table legally holds duplicate rows per <c>SeriesId</c>
/// (two Kavita series can resolve to one local series), so a join would also multiply rows.
/// </para>
/// </summary>
public class ContinueReadingService(MakiDbContext db)
{
    /// <summary>
    /// The next unread downloaded chapter per series. Series with nothing left to read are absent
    /// from the result rather than present with a null — callers drop them from their rails.
    /// </summary>
    public async Task<Dictionary<int, NextChapter>> NextForAsync(
        IReadOnlyCollection<int> seriesIds, CancellationToken ct)
    {
        if (seriesIds.Count == 0)
        {
            return [];
        }

        // A tombstone (explicitly marked unread) is Completed = false, so it correctly falls out of
        // this set and the chapter is offered again as next-to-read.
        var completed = (await db.ChapterProgress
                .Where(p => seriesIds.Contains(p.SeriesId) && p.Completed)
                .Select(p => p.ChapterId)
                .ToListAsync(ct))
            .ToHashSet();

        // Every chapter, not just downloaded ones: a volume's chapter 0 sorts by where the rest of
        // its volume sits, and those chapters may not be on disk.
        var chapters = await db.Chapters
            .Where(c => seriesIds.Contains(c.SeriesId))
            .Select(c => new { c.Id, c.SeriesId, c.Number, c.Volume, c.Title, c.IsOneShot, HasFile = c.ChapterFileId != null })
            .ToListAsync(ct);

        var result = new Dictionary<int, NextChapter>();
        foreach (var group in chapters.GroupBy(c => c.SeriesId))
        {
            // A chapter read in one language is read: another language's copy of the same number is
            // not something left to read, or a series with two languages downloaded would never
            // drop off the rail. Sources can disagree on the volume, so the number alone identifies
            // it; only a volume's chapter 0 repeats across volumes and keeps the volume in its key.
            static (decimal Number, int? Volume)? SlotOf(decimal? number, int? volume) =>
                number is { } n ? (n, n > 0 ? null : volume) : null;
            var readSlots = group
                .Where(c => completed.Contains(c.Id) && SlotOf(c.Number, c.Volume) is not null)
                .Select(c => SlotOf(c.Number, c.Volume)!.Value)
                .ToHashSet();
            bool IsRead(int id, decimal? number, int? volume) =>
                completed.Contains(id) || (SlotOf(number, volume) is { } slot && readSlots.Contains(slot));

            var ordered = ChapterOrder.Sort(group, c => c.Number, c => c.Volume, c => c.Id);
            var unread = ordered.Where(c => c.HasFile && !IsRead(c.Id, c.Number, c.Volume)).ToList();
            if (unread.Count == 0)
            {
                continue;
            }

            // The earliest unread chapter after the reader's place, else the earliest unread
            // (everything past the place is done, so offer what was skipped). Another language's
            // copy of the chapter at the place is not "next".
            //
            // The place is the furthest read whole-numbered chapter whose previous whole-numbered
            // chapter is read too (or that has none). A bonus read out of curiosity (37.5) never
            // moves it, and neither does a lone read far ahead (a misclick, a Kavita mark): with
            // 1-10 read and 50 marked, Read offers 11, not 51. A real jump ahead takes over once
            // the chapter after it is read. With no such chapter, the furthest read whole-numbered
            // chapter is the place, so someone who started at 100 is offered 101. A volume-only
            // entry (number 0) is placed by estimate and is never the place.
            static bool Whole(decimal? number) => number is { } n && n >= 0 && n % 1 == 0;
            bool FollowsARead(int index)
            {
                var number = ordered[index].Number;
                for (var i = index - 1; i >= 0; i--)
                {
                    var p = ordered[i];
                    if (Whole(p.Number) && p.Number > 0 && p.Number != number)
                    {
                        return IsRead(p.Id, p.Number, p.Volume);
                    }
                }

                return true;
            }

            var furthestRead = -1;
            var furthestAny = -1;
            for (var i = ordered.Count - 1; i >= 0; i--)
            {
                var c = ordered[i];
                if (!Whole(c.Number) || c.Number == 0 || !IsRead(c.Id, c.Number, c.Volume))
                {
                    continue;
                }

                if (furthestAny < 0)
                {
                    furthestAny = i;
                }

                if (FollowsARead(i))
                {
                    furthestRead = i;
                    break;
                }
            }

            if (furthestRead < 0)
            {
                furthestRead = furthestAny;
            }

            var furthestNumber = furthestRead < 0 ? null : ordered[furthestRead].Number;
            var next = ordered.Skip(furthestRead + 1)
                           .FirstOrDefault(c => c.HasFile && !IsRead(c.Id, c.Number, c.Volume) && (furthestRead < 0 || c.Number != furthestNumber))
                       ?? unread[0];

            result[group.Key] = new NextChapter(
                next.Id,
                ChapterLabel.For(next.Number, next.Volume, next.Title, next.IsOneShot),
                unread.Count);
        }

        return result;
    }

    /// <summary>Convenience wrapper for the single-series reader endpoint.</summary>
    public async Task<NextChapter?> NextForAsync(int seriesId, CancellationToken ct) =>
        (await NextForAsync(new[] { seriesId }, ct)).GetValueOrDefault(seriesId);
}
