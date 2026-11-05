namespace Maki.Core.Quality;

/// <summary>
/// Spots a source that serves only a preview of each chapter. A preview reads as a successful
/// download, so failures never show it; its page counts do.
/// </summary>
public static class SourcePageCounts
{
    public readonly record struct Sample(int MappingId, int ChapterId, int PageCount);

    /// <summary>A copy this much shorter than what the other sources list for the same chapter is a preview.</summary>
    public const double ShortShare = 0.6;

    /// <summary>Short by fewer pages than this is noise, not a preview.</summary>
    public const int MinPagesShort = 5;

    public const int MinFixedChapters = 3;
    public const int FixedSpread = 1;
    public const int FixedMaxPages = 15;

    /// <summary>
    /// Mappings that look truncated: short on most of the chapters they share with other sources, or
    /// three or more different chapters of one short, fixed length (a preview cap). Real chapters vary.
    /// </summary>
    public static IReadOnlySet<int> Truncated(IReadOnlyCollection<Sample> samples)
    {
        var truncated = new HashSet<int>();
        var shortCopies = new Dictionary<int, int>();
        var compared = new Dictionary<int, int>();
        foreach (var chapter in samples.GroupBy(s => s.ChapterId))
        {
            var copies = chapter.GroupBy(s => s.MappingId).Select(g => g.First()).ToList();
            if (copies.Count < 2)
            {
                continue;
            }

            foreach (var copy in copies)
            {
                var others = copies.Where(c => c.MappingId != copy.MappingId).Select(c => c.PageCount).Order().ToList();
                var median = others.Count % 2 == 1
                    ? others[others.Count / 2]
                    : (others[others.Count / 2 - 1] + others[others.Count / 2]) / 2.0;
                compared[copy.MappingId] = compared.GetValueOrDefault(copy.MappingId) + 1;
                if (copy.PageCount < median * ShortShare && median - copy.PageCount >= MinPagesShort)
                {
                    shortCopies[copy.MappingId] = shortCopies.GetValueOrDefault(copy.MappingId) + 1;
                }
            }
        }

        foreach (var (mappingId, count) in shortCopies)
        {
            if (count >= 2 && count * 2 > compared[mappingId])
            {
                truncated.Add(mappingId);
            }
        }

        foreach (var mapping in samples.GroupBy(s => s.MappingId))
        {
            var counts = mapping.GroupBy(s => s.ChapterId).Select(g => g.First().PageCount).ToList();
            if (counts.Count >= MinFixedChapters && counts.Max() <= FixedMaxPages && counts.Max() - counts.Min() <= FixedSpread)
            {
                truncated.Add(mapping.Key);
            }
        }

        return truncated;
    }
}
