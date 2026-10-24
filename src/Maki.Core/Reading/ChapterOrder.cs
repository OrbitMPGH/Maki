namespace Maki.Core.Reading;

/// <summary>
/// Reading order for a series' chapters: by number, one-shots last.
/// <para>
/// A chapter 0 that belongs to a volume other than the one chapter 1 is in is a volume-level
/// entry, not a prologue. MangaFire lists whole-volume uploads as "Ch. 0" titled "Volume 9", and
/// sorting by number alone put that at the very top of the series. Such a chapter sorts at the
/// start of its own volume instead.
/// </para>
/// <para>
/// When no numbered chapter carries that volume, its start is estimated: between the starts of the
/// nearest known volumes on either side (volume 1 starting the series when there is none below),
/// after the last chapter of the highest known volume when it lies beyond them, and after every
/// numbered chapter when the series has no volumes at all. Anywhere is closer than first: a
/// "Volume 10" upload on a series with nothing read was offered as the place to start.
/// </para>
/// </summary>
public static class ChapterOrder
{
    public static List<T> Sort<T>(
        IEnumerable<T> chapters, Func<T, decimal?> number, Func<T, int?> volume, Func<T, int> id)
    {
        var list = chapters as IReadOnlyCollection<T> ?? chapters.ToList();
        var numbered = list.Where(c => number(c) > 0).ToList();
        var volumeStart = numbered
            .Where(c => volume(c) is not null)
            .GroupBy(c => volume(c)!.Value)
            .ToDictionary(g => g.Key, g => (Start: g.Min(c => number(c)!.Value), End: g.Max(c => number(c)!.Value)));

        // The number a chapter sorts at, and whether it goes before (-1) or after (1) a chapter
        // with that same number.
        (decimal Key, int Side) Position(T c)
        {
            var n = number(c)!.Value;
            if (n != 0)
            {
                return (n, 0);
            }

            if (volume(c) is not { } v || v <= 1)
            {
                return (0, -1);
            }

            if (volumeStart.TryGetValue(v, out var own))
            {
                return (own.Start, -1);
            }

            var below = volumeStart.Keys.Where(k => k < v).DefaultIfEmpty(0).Max();
            var above = volumeStart.Keys.Where(k => k > v).DefaultIfEmpty(0).Min();
            if (above > 0)
            {
                var (fromVolume, fromNumber) = below > 0 ? (below, volumeStart[below].Start) : (1, 0m);
                var to = volumeStart[above].Start;
                return (fromNumber + (to - fromNumber) * (v - fromVolume) / (above - fromVolume), -1);
            }

            if (below > 0)
            {
                return (volumeStart[below].End, 1);
            }

            return numbered.Count > 0 ? (numbered.Max(x => number(x)!.Value), 1) : (0, -1);
        }

        return list
            .OrderBy(c => number(c) is null ? 1 : 0)
            .ThenBy(c => number(c) is null ? 0 : Position(c).Key)
            .ThenBy(c => number(c) is null ? 0 : Position(c).Side)
            .ThenBy(c => volume(c))
            .ThenBy(id)
            .ToList();
    }
}
