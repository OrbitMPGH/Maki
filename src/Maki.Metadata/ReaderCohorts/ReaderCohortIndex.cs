namespace Maki.Metadata.ReaderCohorts;

/// <summary>
/// What one reader cohort scored and finished for one series. <see cref="Mean"/> is null when the
/// cohort finished the series often enough to count but did not rate it often enough to average.
/// </summary>
public readonly record struct CohortEntry(int Cohort, int Completions, int Raters, float? Mean);

/// <summary>
/// The immutable, process-wide view of <c>reader-cohorts.db</c>: how many readers are in each
/// cohort, what each cohort finished and scored, and the same over all readers as a baseline.
///
/// <para>
/// <b>Item-major, not cohort-major.</b> Placing a reader looks up every series they have finished
/// and needs that series' row in <em>every</em> cohort at once, which is one lookup here and
/// <see cref="CohortCount"/> lookups under the obvious layout. The rail wants the opposite order,
/// but it scans the whole structure anyway, so it pays nothing for the transpose while placement —
/// which runs per request — pays a lot.
/// </para>
///
/// <para>
/// Keyed on MangaBaka ids, like every other artifact. Built only by
/// <see cref="ReaderCohortIndexBuilder"/>.
/// </para>
/// </summary>
public sealed class ReaderCohortIndex
{
    private readonly long[] _ids;
    private readonly Dictionary<long, int> _slotById;

    // Global row per slot. A mean of 0 means "no mean here": scores are 1-100, so zero cannot be a
    // real average, and a nullable float array would double the allocation for one bit.
    private readonly int[] _globalCompletions;
    private readonly int[] _globalRaters;
    private readonly float[] _globalMean;

    // CSR over the cohort rows, in slot order.
    private readonly int[] _offsets;
    private readonly byte[] _entryCohort;
    private readonly int[] _entryCompletions;
    private readonly int[] _entryRaters;
    private readonly float[] _entryMean;

    internal ReaderCohortIndex(
        long[] ids,
        Dictionary<long, int> slotById,
        int[] globalCompletions,
        int[] globalRaters,
        float[] globalMean,
        int[] offsets,
        byte[] entryCohort,
        int[] entryCompletions,
        int[] entryRaters,
        float[] entryMean,
        int[] cohortReaders,
        int completionP99,
        DateTime? generatedAt)
    {
        _ids = ids;
        _slotById = slotById;
        _globalCompletions = globalCompletions;
        _globalRaters = globalRaters;
        _globalMean = globalMean;
        _offsets = offsets;
        _entryCohort = entryCohort;
        _entryCompletions = entryCompletions;
        _entryRaters = entryRaters;
        _entryMean = entryMean;
        CohortReaders = cohortReaders;
        CompletionP99 = completionP99;
        GeneratedAt = generatedAt;
        TotalReaders = cohortReaders.Sum();
    }

    /// <summary>Series this index carries a row of any kind for.</summary>
    public int Count => _ids.Length;

    public int CohortCount => CohortReaders.Length;

    /// <summary>How many readers each cohort holds. Index is the cohort id.</summary>
    public int[] CohortReaders { get; }

    /// <summary>Readers across every cohort, i.e. the denominator of a global completion rate.</summary>
    public int TotalReaders { get; }

    /// <summary>
    /// 99th percentile of the global completion counts, computed at build time over the whole
    /// distribution. The taste page scales against this rather than the maximum, which is one
    /// megahit.
    /// </summary>
    public int CompletionP99 { get; }

    public DateTime? GeneratedAt { get; }

    /// <summary>Cohort rows carried, summed over every series.</summary>
    public int EntryCount => _entryCohort.Length;

    public bool TryGetSlot(long mangaBakaId, out int slot) => _slotById.TryGetValue(mangaBakaId, out slot);

    public long IdAt(int slot) => _ids[slot];

    /// <summary>Distinct readers across every cohort who finished this series. Zero is a real answer.</summary>
    public int GlobalCompletionsAt(int slot) => _globalCompletions[slot];

    public int GlobalRatersAt(int slot) => _globalRaters[slot];

    /// <summary>The average score every rating reader gave, or null when too few of them rated it.</summary>
    public float? GlobalMeanAt(int slot) => _globalMean[slot] > 0 ? _globalMean[slot] : null;

    /// <summary>
    /// Share of all readers who finished this series. The denominator that turns a cohort's own
    /// rate into a lift.
    /// </summary>
    public double GlobalRateAt(int slot) =>
        TotalReaders <= 0 ? 0 : _globalCompletions[slot] / (double)TotalReaders;

    /// <summary>Every cohort that carries a row for this series. Usually a handful, never all of them.</summary>
    public IEnumerable<CohortEntry> EntriesAt(int slot)
    {
        for (var e = _offsets[slot]; e < _offsets[slot + 1]; e++)
        {
            yield return new CohortEntry(
                _entryCohort[e], _entryCompletions[e], _entryRaters[e],
                _entryMean[e] > 0 ? _entryMean[e] : null);
        }
    }

    /// <summary>
    /// One cohort's row for one series, or null when that cohort has none. Linear over the series'
    /// own entries, which is a handful.
    /// </summary>
    public CohortEntry? EntryAt(int slot, int cohort)
    {
        for (var e = _offsets[slot]; e < _offsets[slot + 1]; e++)
        {
            if (_entryCohort[e] == cohort)
            {
                return new CohortEntry(
                    cohort, _entryCompletions[e], _entryRaters[e], _entryMean[e] > 0 ? _entryMean[e] : null);
            }
        }

        return null;
    }

    /// <summary>
    /// Walks every cohort row in the index, handing back the slot it belongs to. This is how the
    /// rail scores candidates: one pass in slot order rather than a lookup per candidate per
    /// cohort.
    /// </summary>
    public void ForEachEntry(Action<int, CohortEntry> visit)
    {
        for (var slot = 0; slot < _ids.Length; slot++)
        {
            for (var e = _offsets[slot]; e < _offsets[slot + 1]; e++)
            {
                visit(
                    slot,
                    new CohortEntry(
                        _entryCohort[e], _entryCompletions[e], _entryRaters[e],
                        _entryMean[e] > 0 ? _entryMean[e] : null));
            }
        }
    }
}
