namespace Maki.Core.Images;

/// <summary>
/// Lines up the same page across several sources' copies of one chapter.
/// <para>
/// Sources don't agree on where a chapter starts: one leads with a scanlation credit page, another
/// with a colour cover, a third with neither. Comparing "page 2 of each" therefore compares three
/// different drawings, which is worse than useless when the whole point is judging one image
/// against another.
/// </para>
/// <para>
/// Alignment is a <b>whole-sequence offset search</b>, not a per-page nearest-match. Two sites'
/// copies of one page can sit 20 bits apart when they come from different scanlation releases —
/// close enough that some unrelated pair will beat it by chance. Scoring a whole offset averages
/// over every overlapping page at once, so one noisy pair can't decide the answer, and the thing
/// being detected (a fixed number of extra pages at the front) is a constant offset anyway.
/// </para>
/// </summary>
public static class PageAlignment
{
    /// <summary>How many leading pages a source may differ by. Nobody prepends five credit pages.</summary>
    public const int MaxShift = 4;

    /// <summary>
    /// An offset judged on one or two overlapping pages is a coin flip, not a measurement — the
    /// extreme shifts overlap least and so are exactly the ones noise can hand a flattering score.
    /// </summary>
    private const int MinOverlap = 3;

    /// <summary>
    /// Mean bit distance below which two sequences are really showing the same pages. Measured
    /// against live sources: two sites carrying the same scanlation release score ~13 once lined up
    /// and ~28 when they aren't, while two different releases of the same chapter never get below
    /// ~22 at any offset, because the drawings genuinely differ.
    /// </summary>
    private const double ConfidentDistance = 20;

    /// <summary>
    /// Bits of mean distance a shift must beat "leave it alone" by. Guards the case where a source
    /// is already aligned and some other offset happens to score a shade better.
    /// </summary>
    private const double MinImprovement = 4;

    /// <summary>
    /// Page index to show for each panel, per slot: <c>result[slot][panel]</c>. Only slots where
    /// every panel has a page are returned, so each row is genuinely the same page everywhere.
    /// </summary>
    /// <param name="panels">Per panel, its pages' hashes in the order the source served them.</param>
    /// <param name="want">Slots to return at most.</param>
    public static List<int[]> Align(IReadOnlyList<IReadOnlyList<ulong>> panels, int want)
    {
        if (panels.Count == 0 || panels.Any(p => p.Count == 0) || want <= 0)
        {
            return [];
        }

        // The longest listing is the likeliest to contain everything the others do, which makes it
        // the one every other panel can be measured against without running off its end.
        var reference = 0;
        for (var i = 1; i < panels.Count; i++)
        {
            if (panels[i].Count > panels[reference].Count)
            {
                reference = i;
            }
        }

        var shifts = new int[panels.Count];
        var placed = new bool[panels.Count];
        for (var i = 0; i < panels.Count; i++)
        {
            if (i == reference)
            {
                placed[i] = true;
                continue;
            }

            if (BestShift(panels[reference], panels[i]) is { } shift)
            {
                shifts[i] = shift;
                placed[i] = true;
            }
        }

        // A panel nothing matched still has to be put somewhere, and "leave it at the reference's
        // own indexes" is only neutral if the reference has no extra leading pages — which is
        // exactly what it does have whenever any shift was found. So follow the crowd: take the
        // shift the placed panels agreed on. Most sources don't prepend anything, so the majority
        // answer is the better guess, and it is right by construction when the reference is the odd
        // one out.
        var consensus = Consensus(shifts, placed, reference);
        for (var i = 0; i < panels.Count; i++)
        {
            if (!placed[i])
            {
                shifts[i] = consensus;
            }
        }

        var slots = new List<int[]>();
        for (var r = 0; r < panels[reference].Count; r++)
        {
            var row = new int[panels.Count];
            var usable = true;
            for (var p = 0; p < panels.Count; p++)
            {
                var index = r + shifts[p];
                if (index < 0 || index >= panels[p].Count)
                {
                    usable = false;
                    break;
                }

                row[p] = index;
            }

            if (usable)
            {
                slots.Add(row);
            }
        }

        // A single offset cannot line up front matter, because there is nothing to line it up
        // *to*: one release opens with a translator's preface, another with a title page, and
        // neither carries the other's. Those slots survive the offset search — the pages after them
        // agree, which is what set the offset — so drop the ones whose images plainly don't match
        // and start the comparison where the sources really do converge.
        var judges = Enumerable.Range(0, panels.Count).Where(i => placed[i]).ToList();
        if (judges.Count >= 2)
        {
            var agreeing = slots.Where(row => Agrees(panels, row, judges)).ToList();
            if (agreeing.Count > 0)
            {
                slots = agreeing;
            }
        }

        // Every offset ran off somebody's end — better to show the raw first pages, misaligned, than
        // an empty comparison.
        return slots.Count > 0 ? [.. slots.Take(want)] : Unaligned(panels, want);
    }



    /// <summary>
    /// Whether the panels in a slot are really showing the same drawing, judged only on the ones the
    /// offset search could place. A panel that had to be placed by consensus matches nothing by
    /// definition, so letting it vote would throw away every slot.
    /// </summary>
    private static bool Agrees(IReadOnlyList<IReadOnlyList<ulong>> panels, int[] row, List<int> judges)
    {
        double total = 0;
        var pairs = 0;
        for (var a = 0; a < judges.Count; a++)
        {
            for (var b = a + 1; b < judges.Count; b++)
            {
                total += PerceptualHash.Distance(panels[judges[a]][row[judges[a]]], panels[judges[b]][row[judges[b]]]);
                pairs++;
            }
        }

        return pairs == 0 || total / pairs <= ConfidentDistance;
    }

    /// <summary>Most common shift among the panels that matched, or 0 when none did.</summary>
    private static int Consensus(int[] shifts, bool[] placed, int reference)
    {
        var agreed = Enumerable.Range(0, shifts.Length)
            .Where(i => placed[i] && i != reference)
            .Select(i => shifts[i])
            .ToList();

        return agreed.Count == 0
            ? 0
            : agreed.GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => Math.Abs(g.Key)).First().Key;
    }

    private static List<int[]> Unaligned(IReadOnlyList<IReadOnlyList<ulong>> panels, int want)
    {
        var shortest = panels.Min(p => p.Count);
        return [.. Enumerable.Range(0, Math.Min(want, shortest)).Select(r => Enumerable.Repeat(r, panels.Count).ToArray())];
    }

    /// <summary>
    /// How far <paramref name="other"/> is shifted relative to <paramref name="reference"/>:
    /// <c>other[r + shift]</c> is the same page as <c>reference[r]</c>. Negative means the
    /// reference has extra pages at the front.
    /// <para>
    /// Null when nothing matches at any offset: a source carrying a different scanlation release
    /// scores about the same however it is slid, and picking between two equally bad offsets on a
    /// 2-bit difference is noise, not a measurement. The caller falls back to what the other panels
    /// agreed on. A panel that matches best exactly where it already is returns 0, which is a
    /// placement, not a refusal.
    /// </para>
    /// </summary>
    private static int? BestShift(IReadOnlyList<ulong> reference, IReadOnlyList<ulong> other)
    {
        var best = 0;
        var bestScore = double.MaxValue;
        var zeroScore = double.MaxValue;

        for (var shift = -MaxShift; shift <= MaxShift; shift++)
        {
            if (Score(reference, other, shift) is not { } score)
            {
                continue;
            }

            if (shift == 0)
            {
                zeroScore = score;
            }

            // Ties go to the smaller shift: the loop runs from the most negative up, so a strict
            // comparison already keeps the first (largest negative) — check magnitude explicitly.
            if (score < bestScore || (score == bestScore && Math.Abs(shift) < Math.Abs(best)))
            {
                bestScore = score;
                best = shift;
            }
        }

        // Two separate questions. "Do these sequences match at all?" decides whether the panel is
        // placed — a panel that already lines up is just as placed as one that had to move, and
        // treating it otherwise leaves it out of the slot-agreement vote it should be part of.
        // "Is moving it an improvement?" only decides where.
        if (bestScore > ConfidentDistance)
        {
            return null;
        }

        return zeroScore - bestScore >= MinImprovement ? best : 0;
    }

    /// <summary>Mean bit distance over the pages the two sequences share at this offset, or null when too few.</summary>
    private static double? Score(IReadOnlyList<ulong> reference, IReadOnlyList<ulong> other, int shift)
    {
        double total = 0;
        var overlap = 0;
        for (var r = 0; r < reference.Count; r++)
        {
            var index = r + shift;
            if (index < 0 || index >= other.Count)
            {
                continue;
            }

            total += PerceptualHash.Distance(reference[r], other[index]);
            overlap++;
        }

        return overlap >= MinOverlap ? total / overlap : null;
    }
}
