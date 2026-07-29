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

        // Build a per-series lookup: chapter id → number for completed chapters.
        var completedNumbers = (await db.ChapterProgress
                .Where(p => seriesIds.Contains(p.SeriesId) && p.Completed)
                .Join(db.Chapters,
                      progress => progress.ChapterId,
                      chapter => chapter.Id,
                      (progress, chapter) => new { progress.SeriesId, ChapterId = progress.ChapterId, Number = chapter.Number })
                .ToListAsync(ct))
            .GroupBy(p => p.SeriesId)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(p => p.ChapterId, p => p.Number));

        var candidates = await db.Chapters
            .Where(c => seriesIds.Contains(c.SeriesId) && c.ChapterFileId != null)
            .Select(c => new { c.Id, c.SeriesId, c.Number, c.Volume, c.Title, c.IsOneShot })
            .ToListAsync(ct);

        var result = new Dictionary<int, NextChapter>();
        foreach (var group in candidates.GroupBy(c => c.SeriesId))
        {
            var seriesId = group.Key;
            var unread = group.Where(c => !completed.Contains(c.Id)).ToList();
            if (unread.Count == 0)
            {
                continue;
            }

            // Find the highest-numbered completed chapter for this series.
            var maxCompletedNumber = completedNumbers.TryGetValue(seriesId, out var numbers)
                ? numbers.Values.Where(n => n is not null).Max()
                : (decimal?)null;

            NextChapter next;
            if (maxCompletedNumber.HasValue)
            {
                // Return the earliest unread chapter whose number exceeds the last completed one.
                var afterLast = unread
                    .Where(c => c.Number is null || c.Number > maxCompletedNumber.Value)
                    .OrderBy(c => c.Number is null ? 1 : 0)
                    .ThenBy(c => c.Number)
                    .ThenBy(c => c.Volume)
                    .ThenBy(c => c.Id)
                    .FirstOrDefault();

                if (afterLast is not null)
                {
                    next = new NextChapter(
                        afterLast.Id,
                        ChapterLabel.For(afterLast.Number, afterLast.Volume, afterLast.Title, afterLast.IsOneShot),
                        unread.Count);
                }
                else
                {
                    // All remaining unread chapters have lower numbers — fall back to earliest.
                    var fallback = unread
                        .OrderBy(c => c.Number is null ? 1 : 0)
                        .ThenBy(c => c.Number)
                        .ThenBy(c => c.Volume)
                        .ThenBy(c => c.Id)
                        .First();
                    next = new NextChapter(
                        fallback.Id,
                        ChapterLabel.For(fallback.Number, fallback.Volume, fallback.Title, fallback.IsOneShot),
                        unread.Count);
                }
            }
            else
            {
                // No completed chapters yet: offer the earliest unread (original behaviour).
                var first = unread
                    .OrderBy(c => c.Number is null ? 1 : 0)
                    .ThenBy(c => c.Number)
                    .ThenBy(c => c.Volume)
                    .ThenBy(c => c.Id)
                    .First();
                next = new NextChapter(
                    first.Id,
                    ChapterLabel.For(first.Number, first.Volume, first.Title, first.IsOneShot),
                    unread.Count);
            }

            result[seriesId] = next;
        }

        return result;
    }

    /// <summary>Convenience wrapper for the single-series reader endpoint.</summary>
    public async Task<NextChapter?> NextForAsync(int seriesId, CancellationToken ct) =>
        (await NextForAsync(new[] { seriesId }, ct)).GetValueOrDefault(seriesId);
}
