namespace Maki.Core.Komga;

/// <summary>
/// Komga's <c>numberSort</c> for a series' books, and the read-progress arithmetic the Mihon Komga
/// tracker runs on it.
/// <para>
/// Mihon uses <c>numberSort</c> as the chapter number and syncs one figure per series, "read up to
/// numberSort N", so these have to be stable and never decrease along reading order. A chapter's own
/// number is used where it fits; a chapter with none (one-shots, volume-only uploads) or one that
/// <c>ChapterOrder</c> placed after a higher number takes the previous value plus one or the previous
/// value, so the sequence stays monotonic.
/// </para>
/// </summary>
public static class KomgaNumbering
{
    /// <param name="numbers">Chapter numbers, already in reading order.</param>
    public static double[] NumberSorts(IReadOnlyList<decimal?> numbers)
    {
        var result = new double[numbers.Count];
        double? last = null;
        for (var i = 0; i < numbers.Count; i++)
        {
            var value = numbers[i] is { } n ? (double)n : (last ?? 0) + 1;
            if (last is { } previous && value < previous)
            {
                value = previous;
            }

            result[i] = value;
            last = value;
        }

        return result;
    }

    /// <summary>
    /// The highest <c>numberSort</c> reached without a gap: every group at or below it has a read
    /// book. Books sharing a <c>numberSort</c> (the same chapter in two languages) count as one, read
    /// when any of them is. Zero when the first group is unread.
    /// </summary>
    public static double LastContinuousRead(IReadOnlyList<(double NumberSort, bool Read)> books)
    {
        var last = 0d;
        foreach (var group in books.GroupBy(b => b.NumberSort).OrderBy(g => g.Key))
        {
            if (!group.Any(b => b.Read))
            {
                break;
            }

            last = group.Key;
        }

        return last;
    }

    /// <summary>
    /// The <c>numberSort</c> groups a tracker update to <paramref name="upTo"/> newly marks read: at
    /// or below it, with no book read yet. Never unmarks anything, matching Komga.
    /// </summary>
    public static List<double> NewlyRead(IReadOnlyList<(double NumberSort, bool Read)> books, double upTo) =>
        books.GroupBy(b => b.NumberSort)
            .Where(g => g.Key <= upTo && !g.Any(b => b.Read))
            .Select(g => g.Key)
            .Order()
            .ToList();
}
