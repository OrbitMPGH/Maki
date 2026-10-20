using System.Globalization;
using System.Text.Json;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;

namespace Maki.Metadata.Embedding;

/// <summary>
/// Series the dump holds but the vector index does not: anything unrated, or added since the
/// embeddings were built. A search that only returns indexed rows can never show such a series,
/// however exactly the query names it.
/// <para>
/// The dump can vouch for what the index would have checked on a row: the series is active, not a
/// novel, at a content rating the caller may see, inside the caller's creator scope, and clear of
/// their never-show list (tested on its own genres and tags through
/// <see cref="VectorIndex.IsHidden"/>). Preference filters (year, genre, tag rules and the rest)
/// are not tested here, so the caller only offers these series when none is set, except for an
/// exact title, which bypasses those filters for indexed series too.
/// </para>
/// </summary>
internal static class UnindexedHits
{
    /// <summary>Bounds the id list handed to SQL; these come from our own indexes, never caller text.</summary>
    internal const int MaxCandidates = 300;

    /// <param name="Popularity">Global popularity rank, or <see cref="VectorIndex.Unknown"/>.</param>
    internal sealed record Hit(long Id, int Popularity);

    /// <summary>True when the filters ask for something only an indexed row can be tested against.</summary>
    internal static bool NarrowsByPreference(RecommendationFilters? f) =>
        f is not null && (
            f.YearMin is not null || f.YearMax is not null || f.MinRating is not null ||
            f.MinChapters is not null || f.MaxChapters is not null ||
            f.Types is { Count: > 0 } || f.Statuses is { Count: > 0 } || f.Genres is { Count: > 0 } ||
            f.Tags is { Count: > 0 } || f.Rules is { Count: > 0 });

    /// <summary>
    /// The ratings the caller may see: their list, or the default ceiling when none was given, as
    /// the lexical search that found these titles assumes.
    /// </summary>
    internal static IReadOnlyList<string> Ratings(RecommendationFilters? f) =>
        f?.ContentRatings ?? ContentRating.Allowed(ContentRating.Default);

    /// <summary>The candidates the caller may see, in the order given.</summary>
    internal static async Task<IReadOnlyList<Hit>> VisibleAsync(
        string dumpPath, VectorIndex index, IReadOnlyList<long> candidates, RecommendationFilters? filters,
        CancellationToken ct)
    {
        var ratings = Ratings(filters);
        var creditScope = filters?.CreditIds?.ToHashSet();
        var ids = candidates
            .Where(id => creditScope is null || creditScope.Contains(id))
            .Distinct()
            .Take(MaxCandidates)
            .ToList();
        if (ids.Count == 0 || ratings.Count == 0)
        {
            return [];
        }

        using var conn = new SqliteConnection($"Data Source={dumpPath};Mode=ReadOnly;Pooling=False");
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        var ratingParams = new List<string>(ratings.Count);
        for (var i = 0; i < ratings.Count; i++)
        {
            var name = $"$r{i.ToString(CultureInfo.InvariantCulture)}";
            ratingParams.Add(name);
            cmd.Parameters.AddWithValue(name, ratings[i]);
        }

        cmd.CommandText = $"""
            SELECT id, genres, tags_v2, popularity_global_current FROM series
            WHERE id IN ({string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)))})
              AND state = 'active' AND type != 'novel'
              AND content_rating IN ({string.Join(",", ratingParams)})
            """;

        var hidden = filters?.Hidden is { Count: > 0 } terms ? terms : null;
        var visible = new Dictionary<long, Hit>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            if (hidden is not null)
            {
                var genres = ParseNames(reader.IsDBNull(1) ? null : reader.GetString(1));
                var tags = SeriesEmbeddingIndexer.ParseTags(reader.IsDBNull(2) ? null : reader.GetString(2))
                    .Select(t => (t.Id, t.Name, t.Class))
                    .ToList();
                if (index.IsHidden(hidden, genres, tags))
                {
                    continue;
                }
            }

            visible[id] = new Hit(id, reader.IsDBNull(3) ? VectorIndex.Unknown : reader.GetInt32(3));
        }

        return ids.Where(visible.ContainsKey).Select(id => visible[id]).ToList();
    }

    private static IReadOnlyList<string> ParseNames(string? json)
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
}
