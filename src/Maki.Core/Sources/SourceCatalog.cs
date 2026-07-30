namespace Maki.Core.Sources;

/// <summary>
/// The "no search endpoint" fallback, shared by every source that has to match against its own
/// full title list — TCB Scans, MANGA Plus and Flame Comics all publish a complete catalog and
/// no way to query it.
/// <para>
/// Each of them fetched that catalog behind the same double-checked TTL cache and ranked it with
/// the same three-tier normalized-title comparison, copied verbatim. That is the shape
/// <see cref="SourceChapterList"/> already records the cost of: a fix to one copy never reaches
/// the others. Only the fetch differs, so only the fetch is passed in.
/// </para>
/// </summary>
/// <remarks>
/// The fetch is passed per call rather than to the constructor so a source can hold this as a
/// plain <c>readonly</c> field initializer — a C# field initializer cannot reference an instance
/// method, and every source's fetch is one.
/// </remarks>
public sealed class SourceCatalog(TimeSpan ttl)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<SourceSeriesResult> _entries = [];
    private DateTime _fetchedAt = DateTime.MinValue;

    /// <summary>
    /// Catalog entries whose normalized title relates to <paramref name="title"/>, best first:
    /// exact match, then either title being a prefix of the other, then either containing the
    /// other. Ordering is stable, so equally-scored entries keep catalog order.
    /// </summary>
    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(
        string title, Func<CancellationToken, Task<List<SourceSeriesResult>>> fetch, CancellationToken ct = default)
    {
        var query = Normalize(title);
        if (query.Length == 0)
        {
            return [];
        }

        var scored = new List<(int Score, SourceSeriesResult Series)>();
        foreach (var series in await LoadAsync(fetch, ct))
        {
            var score = ScoreOf(query, Normalize(series.Title));
            if (score > 0)
            {
                scored.Add((score, series));
            }
        }

        return scored.OrderByDescending(s => s.Score).Select(s => s.Series).ToList();
    }

    /// <summary>The whole catalog, refetched only once its TTL has passed.</summary>
    public async Task<List<SourceSeriesResult>> LoadAsync(
        Func<CancellationToken, Task<List<SourceSeriesResult>>> fetch, CancellationToken ct = default)
    {
        if (IsFresh)
        {
            return _entries;
        }

        await _lock.WaitAsync(ct);
        try
        {
            // Re-check: a caller that queued behind the lock has the fetch it waited for.
            if (IsFresh)
            {
                return _entries;
            }

            _entries = await fetch(ct);
            _fetchedAt = DateTime.UtcNow;
            return _entries;
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool IsFresh => _entries.Count > 0 && DateTime.UtcNow - _fetchedAt < ttl;

    internal static int ScoreOf(string query, string candidate) =>
        candidate.Length == 0 ? 0
        : candidate == query ? 3
        : candidate.StartsWith(query, StringComparison.Ordinal) ||
          query.StartsWith(candidate, StringComparison.Ordinal) ? 2
        : candidate.Contains(query, StringComparison.Ordinal) ||
          query.Contains(candidate, StringComparison.Ordinal) ? 1
        : 0;

    /// <summary>Letters and digits only, lowercased — punctuation and spacing differ per site.</summary>
    public static string Normalize(string? text) =>
        text is null
            ? string.Empty
            : new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
