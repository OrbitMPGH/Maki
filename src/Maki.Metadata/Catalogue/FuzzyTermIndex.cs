using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.Catalogue;

/// <summary>One alternative spelling a query token could have meant.</summary>
public readonly record struct TermExpansion(string Term, int Distance, int DocFrequency);

/// <summary>
/// The title index's term dictionary, in memory, so a mistyped query can be rewritten into the
/// spellings that actually exist. This is what Elasticsearch's fuzzy query does: correct against
/// the vocabulary, then let the normal index do the retrieval, rather than building a second
/// approximate index over the documents.
///
/// <para>
/// The vocabulary is free. FTS5 exposes its own dictionary through <c>fts5vocab</c>, so this reads
/// the terms the shipped <c>maki_search</c> index already contains rather than deriving its own:
/// 684,271 terms over 5.2 MB of text, loaded in 0.63 s, against 2,180,575 title rows totalling
/// 50 MB. Indexing the titles themselves was the alternative and it is an order of magnitude more
/// memory for a worse answer.
/// </para>
///
/// <para>
/// Only ASCII terms of three characters or more are kept. A query token has to clear
/// <see cref="CatalogueText.IsExpandable"/> to be corrected at all, and an ASCII token cannot land
/// within two edits of a CJK or Cyrillic term, so those rows could never be returned and are not
/// worth the bytes. Terms are sorted by (length, ordinal) and a per-length offset table turns "all
/// candidates within k of this length" into two array slices.
/// </para>
/// </summary>
public sealed class FuzzyTermIndex
{
    /// <summary>Shortest term worth keeping: a length-5 token with a 2-edit budget reaches down to 3.</summary>
    private const int MinTermLength = 3;

    public static readonly FuzzyTermIndex Empty = new([], [0], [], [], BuildEmptyLengthStarts());

    private readonly byte[] _terms;
    private readonly int[] _offsets;
    private readonly int[] _docFrequency;

    /// <summary>Per-term <see cref="CatalogueText.LetterMask"/>, the prefilter that keeps the scan cheap.</summary>
    private readonly uint[] _charMask;

    /// <summary>
    /// <c>_lengthStart[n]</c> is the first term index whose length is at least <c>n</c>, so the
    /// terms of exactly length <c>n</c> are <c>[_lengthStart[n], _lengthStart[n + 1])</c>.
    /// </summary>
    private readonly int[] _lengthStart;

    private FuzzyTermIndex(byte[] terms, int[] offsets, int[] docFrequency, uint[] charMask, int[] lengthStart)
    {
        _terms = terms;
        _offsets = offsets;
        _docFrequency = docFrequency;
        _charMask = charMask;
        _lengthStart = lengthStart;
    }

    public int TermCount => _offsets.Length - 1;

    public bool IsEmpty => TermCount == 0;

    /// <summary>
    /// Reads the dictionary out of <paramref name="conn"/>'s title index.
    ///
    /// <para>
    /// A dump with no <c>maki_search</c> table returns <see cref="Empty"/> rather than throwing:
    /// missing fuzzy is a degraded search, and a search that throws is no search at all. The vocab
    /// table is created in <c>temp</c>, which a read-only main database still allows, and
    /// <c>temp_store = MEMORY</c> keeps it off disk.
    /// </para>
    /// </summary>
    public static FuzzyTermIndex Build(SqliteConnection conn, string searchTableName, ILogger logger, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var terms = new List<(string Term, int Doc)>(700_000);

        try
        {
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA temp_store = MEMORY";
                pragma.ExecuteNonQuery();
            }

            using (var create = conn.CreateCommand())
            {
                create.CommandText =
                    $"CREATE VIRTUAL TABLE IF NOT EXISTS temp.maki_vocab USING fts5vocab(main, {searchTableName}, 'row')";
                create.ExecuteNonQuery();
            }

            using var read = conn.CreateCommand();
            read.CommandText = "SELECT term, doc FROM temp.maki_vocab";
            read.CommandTimeout = 600;
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var term = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (term is null ||
                    term.Length < MinTermLength ||
                    term.Length > CatalogueText.MaxComparableLength ||
                    !CatalogueText.IsExpandable(term))
                {
                    continue;
                }

                terms.Add((term, reader.IsDBNull(1) ? 0 : reader.GetInt32(1)));
            }
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "No usable title vocabulary in the dump; typo tolerance is off");
            return Empty;
        }

        if (terms.Count == 0)
        {
            return Empty;
        }

        terms.Sort(static (a, b) =>
        {
            var byLength = a.Term.Length.CompareTo(b.Term.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(a.Term, b.Term);
        });

        var offsets = new int[terms.Count + 1];
        var docs = new int[terms.Count];
        var totalBytes = 0;
        for (var i = 0; i < terms.Count; i++)
        {
            totalBytes += terms[i].Term.Length;
        }

        var packed = new byte[totalBytes];
        var masks = new uint[terms.Count];
        var cursor = 0;
        for (var i = 0; i < terms.Count; i++)
        {
            offsets[i] = cursor;
            docs[i] = terms[i].Doc;
            var written = Encoding.ASCII.GetBytes(terms[i].Term, packed.AsSpan(cursor));
            masks[i] = CatalogueText.LetterMask(packed.AsSpan(cursor, written));
            cursor += written;
        }

        offsets[terms.Count] = cursor;

        var lengthStart = new int[CatalogueText.MaxComparableLength + 2];
        var index = 0;
        for (var length = 0; length <= CatalogueText.MaxComparableLength + 1; length++)
        {
            while (index < terms.Count && terms[index].Term.Length < length)
            {
                index++;
            }

            lengthStart[length] = index;
        }

        logger.LogInformation(
            "Built the fuzzy term index: {Terms} terms ({Kb:F0} KB) in {Elapsed:F1}s",
            terms.Count, totalBytes / 1024.0, (DateTime.UtcNow - started).TotalSeconds);

        return new FuzzyTermIndex(packed, offsets, docs, masks, lengthStart);
    }

    /// <summary>The number of indexed titles carrying this term, or 0 when it is not in the dictionary.</summary>
    public int DocFrequency(string term)
    {
        var index = Find(term);
        return index < 0 ? 0 : _docFrequency[index];
    }

    public bool Contains(string term) => Find(term) >= 0;

    /// <summary>
    /// The spellings <paramref name="token"/> could have been, best first, never including the
    /// token itself (the caller already has that one and keeps it in its own branch of the query).
    /// Empty whenever the token is too short, not ASCII, or the options turn the pass off.
    /// </summary>
    public IReadOnlyList<TermExpansion> Expand(string token, FuzzyOptions options)
    {
        // The length ceiling is the same guard Find applies, and it has to come before the
        // stackalloc below: the token is raw query text, so a caller can hand this a megabyte of
        // letters, and a stack overflow is not an exception anything can catch. Nothing is lost by
        // rejecting it — no indexed term is longer than MaxComparableLength, so a token past it
        // could never land within budget of one anyway.
        if (IsEmpty || !options.Enabled ||
            token.Length > CatalogueText.MaxComparableLength ||
            !CatalogueText.IsExpandable(token))
        {
            return [];
        }

        var budget = options.BudgetFor(token.Length);
        if (budget == 0)
        {
            return [];
        }

        Span<byte> probe = stackalloc byte[token.Length];
        Encoding.ASCII.GetBytes(token, probe);
        var probeMask = CatalogueText.LetterMask(probe);

        // Distance is symmetric, so the query token is the DP's target and the scratch it needs is
        // a constant size for the whole scan instead of one allocation per candidate.
        var scratch = CatalogueText.RentScratch(token.Length);

        var found = new List<TermExpansion>(options.MaxExpansionsPerToken * 4);
        var lowest = Math.Max(MinTermLength, token.Length - budget);
        var highest = Math.Min(CatalogueText.MaxComparableLength, token.Length + budget);

        for (var length = lowest; length <= highest; length++)
        {
            var from = _lengthStart[length];
            var to = _lengthStart[length + 1];
            for (var i = from; i < to; i++)
            {
                // Expanding into a term that is already in most of the catalogue is how a rescue
                // turns into a popularity chart, so reject it before paying for the distance.
                if (_docFrequency[i] > options.MaxTermDocFrequency)
                {
                    continue;
                }

                // Two popcounts against the character bitmaps, and most of the length band is gone
                // before the DP ever runs.
                if (CatalogueText.MaskLowerBound(_charMask[i], probeMask) > budget)
                {
                    continue;
                }

                var candidate = TermAt(i);
                var distance = CatalogueText.BoundedDistance(candidate, probe, budget, scratch);
                if (distance is 0 || distance > budget)
                {
                    continue;
                }

                found.Add(new TermExpansion(Encoding.ASCII.GetString(candidate), distance, _docFrequency[i]));
            }
        }

        if (found.Count == 0)
        {
            return [];
        }

        // A correction has to be substantially more common than what was typed, or it is a
        // different word rather than a repair. See FuzzyOptions.MinCorrectionDominance.
        var ownDocs = DocFrequency(token);
        var floor = Math.Max(1, ownDocs * options.MinCorrectionDominance);
        found.RemoveAll(e => e.DocFrequency < floor);
        if (found.Count == 0)
        {
            return [];
        }

        // Closest first, then the spelling more titles actually use, which is the better guess when
        // two corrections are one edit away each.
        found.Sort(static (a, b) =>
        {
            var byDistance = a.Distance.CompareTo(b.Distance);
            if (byDistance != 0)
            {
                return byDistance;
            }

            var byDocs = b.DocFrequency.CompareTo(a.DocFrequency);
            return byDocs != 0 ? byDocs : string.CompareOrdinal(a.Term, b.Term);
        });

        return found.Count <= options.MaxExpansionsPerToken
            ? found
            : found[..options.MaxExpansionsPerToken];
    }

    private ReadOnlySpan<byte> TermAt(int index) =>
        _terms.AsSpan(_offsets[index], _offsets[index + 1] - _offsets[index]);

    /// <summary>Binary search inside the term's length bucket, or -1.</summary>
    private int Find(string term)
    {
        if (IsEmpty || term.Length < MinTermLength || term.Length > CatalogueText.MaxComparableLength)
        {
            return -1;
        }

        Span<byte> probe = stackalloc byte[term.Length];
        if (!CatalogueText.IsExpandable(term) || Encoding.ASCII.GetBytes(term, probe) != term.Length)
        {
            return -1;
        }

        var low = _lengthStart[term.Length];
        var high = _lengthStart[term.Length + 1] - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var comparison = TermAt(middle).SequenceCompareTo(probe);
            if (comparison == 0)
            {
                return middle;
            }

            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return -1;
    }

    private static int[] BuildEmptyLengthStarts() => new int[CatalogueText.MaxComparableLength + 2];
}
