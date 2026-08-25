namespace Maki.Core.Images;

/// <summary>
/// Where each panel's pages sit in the comparison grid, and which panels could be lined up at all.
/// <para>
/// A slot is one row of the grid. <c>Slots[row][panel]</c> is the page index to show, or null when
/// that panel has nothing for the row — which happens to a panel carrying a different edition
/// entirely, kept in the grid so it can still be ranked but never pretended to match.
/// </para>
/// </summary>
public record PageAlignmentResult(List<int?[]> Slots, bool[] Aligned);

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
    /// Works out the grid. <paramref name="panels"/> holds each panel's page hashes in served order;
    /// a null hash is a page that could not be decoded (AVIF, which ImageSharp cannot read) and so
    /// takes no part in matching, though it still occupies its index and is still displayable.
    /// </summary>
    /// <param name="want">Rows to return at most.</param>
    public static PageAlignmentResult Align(IReadOnlyList<IReadOnlyList<ulong?>> panels, int want)
    {
        var aligned = new bool[panels.Count];
        if (panels.Count == 0 || want <= 0 || panels.All(p => p.Count == 0))
        {
            return new PageAlignmentResult([], aligned);
        }

        if (panels.Count == 1)
        {
            aligned[0] = true;
            return new PageAlignmentResult(
                [.. Enumerable.Range(0, Math.Min(want, panels[0].Count)).Select(r => new int?[] { r })],
                aligned);
        }

        var reference = PickReference(panels);
        var shifts = new int[panels.Count];
        aligned[reference] = true;

        for (var i = 0; i < panels.Count; i++)
        {
            if (i == reference)
            {
                continue;
            }

            if (BestShift(panels[reference], panels[i]) is { } shift)
            {
                shifts[i] = shift;
                aligned[i] = true;
            }
        }

        // A panel nothing matched still has to be put somewhere, and "leave it at the reference's
        // own indexes" is only neutral if the reference has no extra leading pages — which is
        // exactly what it does have whenever any shift was found. So follow the crowd: take the
        // shift the aligned panels agreed on.
        var consensus = Consensus(shifts, aligned, reference);
        for (var i = 0; i < panels.Count; i++)
        {
            if (!aligned[i])
            {
                shifts[i] = consensus;
            }
        }

        var slots = BuildSlots(panels, shifts, aligned, reference);

        // A single offset cannot line up front matter, because there is nothing to line it up *to*:
        // one release opens with a translator's preface, another with a title page, and neither
        // carries the other's. Those rows survive the offset search — the pages after them agree,
        // which is what set the offset — so drop the ones whose images plainly don't match and start
        // the comparison where the sources really do converge.
        var judges = Enumerable.Range(0, panels.Count).Where(i => aligned[i]).ToList();
        if (judges.Count >= 2)
        {
            var agreeing = slots.Where(row => Agrees(panels, row, judges)).ToList();
            if (agreeing.Count > 0)
            {
                slots = agreeing;
            }
        }

        return new PageAlignmentResult([.. slots.Take(want)], aligned);
    }

    /// <summary>
    /// Rows are sized by the panels that actually line up. A source carrying a different edition
    /// must not shorten the comparison for everyone else — it used to, because its page count
    /// capped the grid — so it contributes a page where it has one and a gap where it doesn't.
    /// </summary>
    private static List<int?[]> BuildSlots(
        IReadOnlyList<IReadOnlyList<ulong?>> panels, int[] shifts, bool[] aligned, int reference)
    {
        var slots = new List<int?[]>();
        for (var r = 0; r < panels[reference].Count; r++)
        {
            var row = new int?[panels.Count];
            var usable = true;
            for (var p = 0; p < panels.Count; p++)
            {
                var index = r + shifts[p];
                var inRange = index >= 0 && index < panels[p].Count;

                if (aligned[p])
                {
                    if (!inRange)
                    {
                        usable = false;
                        break;
                    }

                    row[p] = index;
                }
                else
                {
                    row[p] = inRange ? index : null;
                }
            }

            if (usable)
            {
                slots.Add(row);
            }
        }

        return slots;
    }

    /// <summary>
    /// The panel to measure everyone else against: whichever one the most others line up with.
    /// Picking the longest listing instead lets a source carrying a different edition become the
    /// yardstick whenever it happens to serve the most images, and then nothing matches anything.
    /// </summary>
    private static int PickReference(IReadOnlyList<IReadOnlyList<ulong?>> panels)
    {
        var best = 0;
        var bestAgreement = -1;

        for (var candidate = 0; candidate < panels.Count; candidate++)
        {
            if (panels[candidate].Count == 0)
            {
                continue;
            }

            var agreement = 0;
            for (var other = 0; other < panels.Count; other++)
            {
                if (other != candidate && BestShift(panels[candidate], panels[other]) is not null)
                {
                    agreement++;
                }
            }

            // Ties on agreement go to the longer listing: it reaches further into the chapter, so
            // more rows survive the range check.
            if (agreement > bestAgreement ||
                (agreement == bestAgreement && panels[candidate].Count > panels[best].Count))
            {
                bestAgreement = agreement;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether the panels in a slot are really showing the same drawing, judged only on the ones the
    /// offset search could place. A panel that had to be placed by consensus matches nothing by
    /// definition, so letting it vote would throw away every slot.
    /// </summary>
    private static bool Agrees(IReadOnlyList<IReadOnlyList<ulong?>> panels, int?[] row, List<int> judges)
    {
        double total = 0;
        var pairs = 0;
        for (var a = 0; a < judges.Count; a++)
        {
            for (var b = a + 1; b < judges.Count; b++)
            {
                if (Hash(panels, judges[a], row) is not { } left || Hash(panels, judges[b], row) is not { } right)
                {
                    continue;
                }

                total += PerceptualHash.Distance(left, right);
                pairs++;
            }
        }

        return pairs == 0 || total / pairs <= ConfidentDistance;
    }

    private static ulong? Hash(IReadOnlyList<IReadOnlyList<ulong?>> panels, int panel, int?[] row) =>
        row[panel] is { } index ? panels[panel][index] : null;

    /// <summary>Most common shift among the panels that matched, or 0 when none did.</summary>
    private static int Consensus(int[] shifts, bool[] aligned, int reference)
    {
        var agreed = Enumerable.Range(0, shifts.Length)
            .Where(i => aligned[i] && i != reference)
            .Select(i => shifts[i])
            .ToList();

        return agreed.Count == 0
            ? 0
            : agreed.GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => Math.Abs(g.Key)).First().Key;
    }

    /// <summary>
    /// How far <paramref name="other"/> is shifted relative to <paramref name="reference"/>:
    /// <c>other[r + shift]</c> is the same page as <c>reference[r]</c>. Negative means the
    /// reference has extra pages at the front.
    /// <para>
    /// Null when nothing matches at any offset: a source carrying a different scanlation release
    /// scores about the same however it is slid, and picking between two equally bad offsets on a
    /// 2-bit difference is noise, not a measurement. A panel that matches best exactly where it
    /// already is returns 0, which is a placement, not a refusal.
    /// </para>
    /// </summary>
    private static int? BestShift(IReadOnlyList<ulong?> reference, IReadOnlyList<ulong?> other)
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
    private static double? Score(IReadOnlyList<ulong?> reference, IReadOnlyList<ulong?> other, int shift)
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

            // An undecodable page has no hash and cannot vote either way.
            if (reference[r] is not { } left || other[index] is not { } right)
            {
                continue;
            }

            total += PerceptualHash.Distance(left, right);
            overlap++;
        }

        return overlap >= MinOverlap ? total / overlap : null;
    }
}
