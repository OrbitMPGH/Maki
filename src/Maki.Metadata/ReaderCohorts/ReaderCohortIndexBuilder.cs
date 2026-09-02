namespace Maki.Metadata.ReaderCohorts;

/// <summary>
/// The single copy of the fold from loose rows into <see cref="ReaderCohortIndex"/>'s CSR. Kept
/// apart from the cache for the same reason <c>PairGraphBuilder</c> is: the tests build an index
/// without going near a file, and two copies of a layout are two chances for them to disagree.
/// </summary>
public static class ReaderCohortIndexBuilder
{
    /// <param name="globalRows">One per series, over every reader.</param>
    /// <param name="cohortRows">
    /// Zero or more per series. A row naming a cohort outside <paramref name="cohortReaders"/> is
    /// dropped rather than trusted, since it would index past the end of the weight array on the
    /// serving side.
    /// </param>
    public static ReaderCohortIndex Build(
        IReadOnlyList<(long Id, int Completions, int Raters, float? Mean)> globalRows,
        IReadOnlyList<(long Id, int Cohort, int Completions, int Raters, float? Mean)> cohortRows,
        int[] cohortReaders,
        int completionP99,
        DateTime? generatedAt)
    {
        // The cohort id is packed into a byte, one per row, because there are two dozen of them and
        // 191,000 rows. A build that wanted more than 255 groups would need that column widened,
        // and silently truncating one into another cohort's aggregate is not an acceptable failure.
        if (cohortReaders.Length > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cohortReaders),
                $"{cohortReaders.Length} cohorts exceeds the {byte.MaxValue} the row layout packs into a byte");
        }

        // The id space is the union: the taste page reads global rows for series no cohort cleared
        // its floor on, and the rail reads cohort rows for series the global floor happened to
        // admit. Neither table is a subset of the other.
        var slotById = new Dictionary<long, int>(globalRows.Count + 64);
        foreach (var row in globalRows)
        {
            slotById.TryAdd(row.Id, slotById.Count);
        }

        foreach (var row in cohortRows)
        {
            if (row.Cohort >= 0 && row.Cohort < cohortReaders.Length)
            {
                slotById.TryAdd(row.Id, slotById.Count);
            }
        }

        var count = slotById.Count;
        var ids = new long[count];
        foreach (var (id, slot) in slotById)
        {
            ids[slot] = id;
        }

        var globalCompletions = new int[count];
        var globalRaters = new int[count];
        var globalMean = new float[count];
        foreach (var (id, completions, raters, mean) in globalRows)
        {
            var slot = slotById[id];
            globalCompletions[slot] = completions;
            globalRaters[slot] = raters;
            globalMean[slot] = mean is { } m && m > 0 ? m : 0;
        }

        // Two counting passes, the same shape as PairGraphBuilder: degrees into offsets, prefix
        // sum, then fill through a per-slot cursor.
        var offsets = new int[count + 1];
        foreach (var row in cohortRows)
        {
            if (row.Cohort >= 0 && row.Cohort < cohortReaders.Length)
            {
                offsets[slotById[row.Id] + 1]++;
            }
        }

        for (var slot = 0; slot < count; slot++)
        {
            offsets[slot + 1] += offsets[slot];
        }

        var total = offsets[count];
        var entryCohort = new byte[total];
        var entryCompletions = new int[total];
        var entryRaters = new int[total];
        var entryMean = new float[total];

        var cursor = new int[count];
        foreach (var (id, cohort, completions, raters, mean) in cohortRows)
        {
            if (cohort < 0 || cohort >= cohortReaders.Length)
            {
                continue;
            }

            var slot = slotById[id];
            var at = offsets[slot] + cursor[slot]++;
            entryCohort[at] = (byte)cohort;
            entryCompletions[at] = completions;
            entryRaters[at] = raters;
            entryMean[at] = mean is { } m && m > 0 ? m : 0;
        }

        return new ReaderCohortIndex(
            ids, slotById, globalCompletions, globalRaters, globalMean,
            offsets, entryCohort, entryCompletions, entryRaters, entryMean,
            cohortReaders, completionP99, generatedAt);
    }
}
