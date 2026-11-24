namespace Maki.Core.Sources;

/// <summary>
/// Detects cross-source numbering-scheme clashes: one source lists sub-chapters
/// (1.1, 1.2, …) where another lists whole chapters (1, 2, …) for the same
/// content, which makes the merged chapter list carry both as distinct rows.
/// Detection only — merging is provably unsafe (sub-chapter counts aren't
/// knowable and decimals are indistinguishable from legitimate extras), so the
/// result is surfaced as a hint to disable one of the mappings.
/// </summary>
public static class NumberingClashDetector
{
    /// <summary>At least this many sub-chapter numbers must collide with the other source's wholes…</summary>
    private const int MinSubChapters = 3;
    /// <summary>…spread over at least this many distinct integer parts (one split chapter can happen anywhere).</summary>
    private const int MinParts = 2;

    public record Clash(string SubChapterSource, string WholeChapterSource);

    /// <summary>
    /// Every clashing pair, ordered by source name so the stored value only changes when the clashes do.
    /// All of them, not the first: with many mappings the user would otherwise switch one off, refresh,
    /// and only then learn about the next pair.
    /// </summary>
    /// <param name="numbersBySource">Chapter numbers per source name (null numbers = one-shots, ignored).</param>
    public static IReadOnlyList<Clash> Detect(IReadOnlyDictionary<string, IReadOnlyCollection<decimal?>> numbersBySource)
    {
        var clashes = new List<Clash>();
        foreach (var (subSource, subNumbers) in numbersBySource)
        {
            // Sub-chapter numbers (x.1–x.4) whose whole chapter this source does
            // *not* carry itself. .5 and up is excluded: 10.5-style decimals are
            // usually omake/specials, which legitimately coexist with whole
            // chapters; x.1 alongside the source's own x is an extra, not a scheme.
            var wholes = Wholes(subNumbers);
            var subOnly = subNumbers
                .Where(n => n is { } d && d % 1 is > 0 and < 0.5m)
                .Select(n => n!.Value)
                .Where(n => !wholes.Contains(decimal.Floor(n)))
                .ToList();

            if (subOnly.Count < MinSubChapters)
            {
                continue;
            }

            foreach (var (wholeSource, wholeNumbers) in numbersBySource)
            {
                if (wholeSource == subSource)
                {
                    continue;
                }

                var otherWholes = Wholes(wholeNumbers);
                var colliding = subOnly.Where(n => otherWholes.Contains(decimal.Floor(n))).ToList();
                if (colliding.Count >= MinSubChapters &&
                    colliding.Select(decimal.Floor).Distinct().Count() >= MinParts)
                {
                    clashes.Add(new Clash(subSource, wholeSource));
                }
            }
        }

        return [.. clashes
            .OrderBy(c => c.SubChapterSource, StringComparer.Ordinal)
            .ThenBy(c => c.WholeChapterSource, StringComparer.Ordinal)];
    }

    /// <summary>The <c>Series.NumberingClash</c> value: "sub|whole" pairs joined by ';', or null for none.</summary>
    public static string? Format(IReadOnlyList<Clash> clashes) =>
        clashes.Count == 0 ? null : string.Join(';', clashes.Select(c => $"{c.SubChapterSource}|{c.WholeChapterSource}"));

    private static HashSet<decimal> Wholes(IReadOnlyCollection<decimal?> numbers) =>
        numbers.Where(n => n is { } d && d % 1 == 0).Select(n => n!.Value).ToHashSet();
}
