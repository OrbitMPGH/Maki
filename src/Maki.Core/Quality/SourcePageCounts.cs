namespace Maki.Core.Quality;

/// <summary>
/// Spots a source that serves only a preview of each chapter. A preview reads as a successful
/// download, so failures never show it; its page counts do.
/// </summary>
public static class SourcePageCounts
{
    /// <param name="Width">Median page width, 0 when unknown.</param>
    /// <param name="Height">Median page height, 0 when unknown.</param>
    public readonly record struct Sample(int MappingId, int ChapterId, int PageCount, int Width = 0, int Height = 0)
    {
        public bool HasShape => Width > 0 && Height > 0;
        public double Aspect => (double)Height / Width;
    }

    /// <summary>A copy this much shorter than what the other sources list for the same chapter is a preview.</summary>
    public const double ShortShare = 0.6;

    /// <summary>Short by fewer pages than this is noise, not a preview.</summary>
    public const int MinPagesShort = 5;

    public const int MinFixedChapters = 3;
    public const int FixedSpread = 1;
    public const int FixedMaxPages = 15;

    /// <summary>Height over width of an ordinary manga page, the unit a chapter's length is measured in.</summary>
    public const double PageAspect = 1.4;

    /// <summary>Pages at least this tall for their width are long-strip slices, whose count says nothing about length.</summary>
    public const double LongStripAspect = 2.0;

    /// <summary>
    /// Mappings that look truncated: short on most of the chapters they share with other sources, or
    /// three or more different chapters of one short, fixed length (a preview cap) that other sources
    /// carry longer copies of. Real chapters vary, but a series of genuinely short chapters can sample
    /// as one length, so the cap alone is not enough.
    /// </summary>
    /// <remarks>
    /// A long strip cut into 12 tall images and the same strip cut into 60 short ones are the same
    /// chapter, so when every copy's page size is known the comparison is on length (pages times their
    /// aspect), not page count. Resolution drops out of that, so a low-resolution source is not short.
    /// </remarks>
    public static IReadOnlySet<int> Truncated(IReadOnlyCollection<Sample> samples)
    {
        var truncated = new HashSet<int>();
        var shortCopies = new Dictionary<int, int>();
        var compared = new Dictionary<int, int>();
        var byChapter = samples.GroupBy(s => s.ChapterId)
            .ToDictionary(g => g.Key, g => g.GroupBy(s => s.MappingId).Select(m => m.First()).ToList());
        foreach (var copies in byChapter.Values)
        {
            if (copies.Count < 2)
            {
                continue;
            }

            var byShape = copies.All(c => c.HasShape);
            double Length(Sample s) => byShape ? s.PageCount * s.Aspect / PageAspect : s.PageCount;

            foreach (var copy in copies)
            {
                var median = Median(copies.Where(c => c.MappingId != copy.MappingId).Select(Length));
                var length = Length(copy);
                compared[copy.MappingId] = compared.GetValueOrDefault(copy.MappingId) + 1;
                if (IsShort(length, median))
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
            var chapters = mapping.GroupBy(s => s.ChapterId).Select(g => g.First()).ToList();
            if (chapters.Any(c => c.HasShape && c.Aspect >= LongStripAspect))
            {
                continue;
            }

            var counts = chapters.Select(c => c.PageCount).ToList();
            if (counts.Count >= MinFixedChapters && counts.Max() <= FixedMaxPages && counts.Max() - counts.Min() <= FixedSpread &&
                LongerElsewhere(chapters, byChapter, samples))
            {
                truncated.Add(mapping.Key);
            }
        }

        return truncated;
    }

    /// <summary>
    /// Whether, on most of these chapters that other sources also sampled, their typical copy is
    /// clearly longer. Using the median rather than any single source stops one longer outlier (a bad
    /// match, merged chapters, or extra credit pages) from marking every agreeing source truncated.
    /// With none sampled by another source (a source the scout measured on chapters of its own, or
    /// downloads that never overlap), the other sources' typical chapter is the yardstick.
    /// </summary>
    private static bool LongerElsewhere(
        List<Sample> chapters, Dictionary<int, List<Sample>> byChapter, IReadOnlyCollection<Sample> samples)
    {
        int shared = 0, longer = 0;
        foreach (var copy in chapters)
        {
            var others = byChapter[copy.ChapterId].Where(c => c.MappingId != copy.MappingId).ToList();
            if (others.Count == 0)
            {
                continue;
            }

            shared++;
            var byShape = copy.HasShape && others.All(o => o.HasShape);
            double Length(Sample s) => byShape ? s.PageCount * s.Aspect / PageAspect : s.PageCount;
            if (IsShort(Length(copy), Median(others.Select(Length))))
            {
                longer++;
            }
        }

        if (shared > 0)
        {
            return longer > 0 && longer * 2 > shared;
        }

        var mappingId = chapters[0].MappingId;
        var elsewhere = samples.Where(s => s.MappingId != mappingId)
            .GroupBy(s => (s.MappingId, s.ChapterId)).Select(g => g.First()).ToList();
        if (elsewhere.Count == 0)
        {
            return false;
        }

        var allShaped = chapters.All(c => c.HasShape) && elsewhere.All(o => o.HasShape);
        double FullLength(Sample s) => allShaped ? s.PageCount * s.Aspect / PageAspect : s.PageCount;
        return IsShort(chapters.Max(FullLength), Median(elsewhere.Select(FullLength)));
    }

    private static bool IsShort(double length, double reference) =>
        length < reference * ShortShare && reference - length >= MinPagesShort;

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
    }
}
