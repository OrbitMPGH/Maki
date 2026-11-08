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
                var others = copies.Where(c => c.MappingId != copy.MappingId).Select(Length).Order().ToList();
                var median = others.Count % 2 == 1
                    ? others[others.Count / 2]
                    : (others[others.Count / 2 - 1] + others[others.Count / 2]) / 2.0;
                var length = Length(copy);
                compared[copy.MappingId] = compared.GetValueOrDefault(copy.MappingId) + 1;
                if (length < median * ShortShare && median - length >= MinPagesShort)
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
                LongerElsewhere(chapters, byChapter))
            {
                truncated.Add(mapping.Key);
            }
        }

        return truncated;
    }

    /// <summary>Whether, on most of these chapters that another source also sampled, some other copy is clearly longer.</summary>
    private static bool LongerElsewhere(List<Sample> chapters, Dictionary<int, List<Sample>> byChapter)
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
            if (others.Any(o =>
                {
                    var byShape = copy.HasShape && o.HasShape;
                    var own = byShape ? copy.PageCount * copy.Aspect / PageAspect : copy.PageCount;
                    var theirs = byShape ? o.PageCount * o.Aspect / PageAspect : o.PageCount;
                    return theirs - own >= MinPagesShort;
                }))
            {
                longer++;
            }
        }

        return longer > 0 && longer * 2 > shared;
    }
}
