using System.Globalization;
using System.Text.Json;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.Embedding;

/// <summary>
/// Natural-language catalogue search: "a quiet manga about cooking in a fantasy village" is
/// embedded with the same model that indexed every series' description, then cosined against the
/// whole in-memory index (<see cref="VectorIndex"/>).
///
/// Dense-only search is bad at titles ("berserk" is a word, not a plot), so the dense ranking is
/// fused with the FTS5 title index by reciprocal rank fusion — ranks add, scores never have to be
/// calibrated against each other. Rating breaks ties and nothing else, so a query's meaning always
/// outranks popularity.
/// </summary>
public class SemanticSearcher(
    MangaBakaDumpOptions dumpOptions,
    EmbeddingStore store,
    VectorIndexCache cache,
    TextEmbedder embedder,
    MangaBakaLocalStore localStore,
    ILogger<SemanticSearcher> logger)
{
    /// <summary>
    /// bge is an asymmetric retrieval model: passages are embedded bare (as the indexer does) and
    /// queries carry this instruction. Without it, short queries land in the wrong region of the
    /// space and recall drops noticeably.
    /// </summary>
    private const string QueryInstruction = "Represent this sentence for searching relevant passages: ";

    /// <summary>Standard RRF damping. Larger = flatter, less dominated by whichever list ranked first.</summary>
    private const double RrfK = 60;

    /// <summary>Enough vectors for the index to be worth searching at all.</summary>
    private const int MinIndexed = 1000;

    /// <summary>True once the embedding index holds enough vectors to search.</summary>
    public bool IsReady() => store.Count() >= MinIndexed;

    /// <summary>
    /// Ranked matches for a free-text query. Empty when the index isn't built — the caller falls
    /// back to title search rather than showing nothing.
    /// </summary>
    public async Task<IReadOnlyList<MangaBakaRecommendation>> SearchAsync(
        string query, RecommendationFilters? filters = null, int limit = 60, CancellationToken ct = default)
    {
        query = query?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return [];
        }

        limit = Math.Clamp(limit, 1, 200);

        if (!await embedder.EnsureReadyAsync(ct))
        {
            logger.LogWarning("Semantic search skipped — the embedding model isn't available");
            return [];
        }

        var index = await cache.GetAsync(ct);
        if (index is null || index.Count < MinIndexed)
        {
            return [];
        }

        var plan = index.Plan(filters);
        if (plan.Impossible)
        {
            return [];
        }

        // Pool wide enough that the lexical fusion and the tag post-filter still have material to
        // work with after they cut, but small enough that hydration stays one cheap query.
        var pool = Math.Clamp(limit * 8, 200, 2000);

        var started = DateTime.UtcNow;
        var queryVector = await Task.Run(() => embedder.Embed(QueryInstruction + query), ct);
        var dense = index.Search(queryVector, plan, pool, ct);
        var lexical = await GetLexicalRanksAsync(query, ct);

        // Reciprocal rank fusion over the two rankings. A title hit that the dense pass missed
        // entirely still gets in (as long as it's indexed and passes the filters), which is what
        // makes an exact-title query work.
        var fused = new Dictionary<int, double>(dense.Count + lexical.Count);
        for (var rank = 0; rank < dense.Count; rank++)
        {
            fused[dense[rank].Row] = 1.0 / (RrfK + rank + 1);
        }

        foreach (var (id, rank) in lexical)
        {
            if (!index.TryGetRow(id, out var row) || !index.Matches(row, plan))
            {
                continue;
            }

            fused[row] = fused.GetValueOrDefault(row) + (1.0 / (RrfK + rank + 1));
        }

        var ranked = fused
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => index.RatingAt(kv.Key))
            .Select(kv => index.IdAt(kv.Key))
            .ToList();

        ranked = ApplyTagFilter(ranked, filters);

        var winners = ranked.Take(limit).ToList();
        var results = await HydrateAsync(winners, ct);
        logger.LogInformation(
            "Semantic search for {Length}-char query returned {Count} of {Pool} candidates in {Elapsed:F0}ms",
            query.Length, results.Count, fused.Count, (DateTime.UtcNow - started).TotalMilliseconds);
        return results;
    }

    /// <summary>MangaBaka id → its 0-based rank in the FTS5 title index (empty when nothing matches).</summary>
    private async Task<IReadOnlyList<(long Id, int Rank)>> GetLexicalRanksAsync(string query, CancellationToken ct)
    {
        try
        {
            var hits = await localStore.SearchAsync(query, ct);
            return hits
                .Select((hit, rank) => (
                    Ok: long.TryParse(hit.ProviderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id),
                    Id: id,
                    Rank: rank))
                .Where(x => x.Ok)
                .Select(x => (x.Id, x.Rank))
                .ToList();
        }
        catch (SqliteException ex)
        {
            // A long natural-language query is mostly noise to FTS5 and can fail to parse; the
            // dense ranking alone is still a good answer.
            logger.LogDebug(ex, "Lexical side of the search failed; using the dense ranking alone");
            return [];
        }
    }

    /// <summary>
    /// Tags aren't in the in-memory index (they're packed blobs in their own table), so they're
    /// applied to the already-ranked candidate pool instead — one small keyed read.
    /// </summary>
    private List<long> ApplyTagFilter(List<long> ranked, RecommendationFilters? filters)
    {
        if (filters?.Tags is not { Count: > 0 } wanted || ranked.Count == 0)
        {
            return ranked;
        }

        var vocab = store.GetVocab();
        // Casing variants are distinct vocab ids, so a name maps to a set of ids and carrying any
        // one of them satisfies it. Matches the recommender's tag filter.
        var requiredIds = wanted
            .Select(name => vocab
                .Where(kv => string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key)
                .ToArray())
            .ToList();
        if (requiredIds.Any(ids => ids.Length == 0))
        {
            return [];
        }

        var blobs = store.GetTagBlobs(ranked);
        return ranked
            .Where(id => TagMath.ContainsAll(blobs.GetValueOrDefault(id), requiredIds))
            .ToList();
    }

    /// <summary>Reads the display columns for the winners, preserving the ranked order.</summary>
    private async Task<IReadOnlyList<MangaBakaRecommendation>> HydrateAsync(
        IReadOnlyList<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        using var conn = new SqliteConnection($"Data Source={dumpOptions.DatabasePath};Mode=ReadOnly;Pooling=False");
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, title, cover_raw_url, year, status, rating, total_chapters, genres, description
            FROM series
            WHERE id IN ({string.Join(",", ids)})
            """;

        var byId = new Dictionary<long, MangaBakaRecommendation>(ids.Count);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            byId[id] = new MangaBakaRecommendation(
                id.ToString(CultureInfo.InvariantCulture),
                GetString(reader, 1) ?? string.Empty,
                GetString(reader, 2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                GetString(reader, 8),
                MangaBakaProvider.MapStatus(GetString(reader, 4)),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                ParseCount(GetString(reader, 6)),
                // No seed profile to match against in a search, so the card just shows what the
                // series is; the query itself is the "why".
                ParseStringArray(GetString(reader, 7)).Take(3).ToList(),
                [],
                false,
                null,
                null);
        }

        return ids.Select(byId.GetValueOrDefault).OfType<MangaBakaRecommendation>().ToList();
    }

    private static IReadOnlyList<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static int? ParseCount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
        {
            return whole;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var frac)
            ? (int)frac
            : null;
    }

    private static string? GetString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
