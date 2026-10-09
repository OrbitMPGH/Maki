using Maki.Core.Recommendations;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// The dump path (<see cref="RecommendationFilters.BuildClause"/>) and the index path
/// (<see cref="VectorIndex.Matches"/>) are two evaluators of one filter, so the same dump rows must
/// pass or fail identically in both, including the rows with NULL columns.
/// </summary>
public class FilterParityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "maki-parity-" + Guid.NewGuid().ToString("N"));
    private readonly string _dumpPath;
    private readonly string _vectorPath;

    public FilterParityTests()
    {
        Directory.CreateDirectory(_dir);
        _dumpPath = Path.Combine(_dir, "mangabaka.db");
        _vectorPath = Path.Combine(_dir, "embeddings.db");

        using var conn = new SqliteConnection($"Data Source={_dumpPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE series (
                id INTEGER PRIMARY KEY, state TEXT, rating REAL, content_rating TEXT, type TEXT,
                status TEXT, year INTEGER, total_chapters TEXT, genres TEXT, authors TEXT, artists TEXT,
                popularity_global_current INTEGER);
            INSERT INTO series VALUES (1, 'active', 80, 'safe', 'manga', 'completed', 1999, '12', '["Action"]', NULL, NULL, 3);
            INSERT INTO series VALUES (2, 'active', 70, 'safe', 'manhwa', 'releasing', 2015, '30', '["Romance","Ecchi"]', NULL, NULL, 4);
            INSERT INTO series VALUES (3, 'active', 60, 'safe', 'manga', 'completed', NULL, NULL, NULL, NULL, NULL, 5);
            INSERT INTO series VALUES (4, 'active', 65, NULL, 'manga', 'completed', 2002, '8', '["Action"]', NULL, NULL, 6);
            INSERT INTO series VALUES (5, 'active', 90, 'pornographic', 'manga', 'completed', 2003, '9', '["Action"]', NULL, NULL, 7);
            """;
        cmd.ExecuteNonQuery();

        var store = new EmbeddingStore(new EmbeddingOptions(_dir, _vectorPath, _dir, EmbeddingModelProfile.Base));
        store.EnsureSchema();
        store.UpsertBatch(Enumerable.Range(1, 5)
            .Select(i => ((long)i, "h", new[] { i == 1 ? 1f : 0f, i == 2 ? 1f : 0f, i == 3 ? 1f : 0f, i >= 4 ? 1f : 0f }))
            .ToList());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_none_rule_keeps_rows_with_no_genres_on_both_paths()
    {
        var filters = new RecommendationFilters(
            Rules: [new CatalogueRule(CatalogueRules.None, [new CatalogueTerm(CatalogueRules.Genre, "Ecchi")])]);

        var (sql, index) = await Both(filters);

        Assert.Equal([1L, 3L, 4L, 5L], index);
        Assert.Equal(index, sql);
    }

    [Fact]
    public async Task A_ceiling_covering_every_rating_keeps_unrated_rows_on_both_paths()
    {
        var (sql, index) = await Both(new RecommendationFilters(ContentRatings: [.. ContentRating.All]));

        Assert.Equal([1L, 2L, 3L, 4L, 5L], index);
        Assert.Equal(index, sql);
    }

    [Fact]
    public async Task A_lower_ceiling_still_excludes_unrated_rows_on_both_paths()
    {
        var (sql, index) = await Both(
            new RecommendationFilters(ContentRatings: ContentRating.Allowed(ContentRating.Erotica)));

        Assert.Equal([1L, 2L, 3L], index);
        Assert.Equal(index, sql);
    }

    // Rows by id: 1 manga 1999 12ch, 2 manhwa releasing 2015 30ch, 3 no year or chapter count,
    // 4 manga 2002 8ch, 5 manga 2003 9ch. A NULL column fails a bound on both paths.
    [Fact]
    public async Task Year_bounds_agree_including_a_null_year()
    {
        await AssertParity(new RecommendationFilters(YearMin: 2000), [2, 4, 5]);
        await AssertParity(new RecommendationFilters(YearMax: 2002), [1, 4]);
    }

    [Fact]
    public async Task Type_and_status_agree()
    {
        await AssertParity(new RecommendationFilters(Types: ["manhwa"]), [2]);
        await AssertParity(new RecommendationFilters(Statuses: ["releasing"]), [2]);
    }

    [Fact]
    public async Task Chapter_bounds_agree_including_a_null_count()
    {
        await AssertParity(new RecommendationFilters(MinChapters: 10), [1, 2]);
        await AssertParity(new RecommendationFilters(MaxChapters: 10), [4, 5]);
    }

    private async Task AssertParity(RecommendationFilters filters, long[] expected)
    {
        var (sql, index) = await Both(filters);

        Assert.Equal(expected, index);
        Assert.Equal(expected, sql);
    }

    private async Task<(List<long> Sql, List<long> Index)> Both(RecommendationFilters filters)
    {
        var cache = new VectorIndexCache(
            new EmbeddingOptions(_dir, _vectorPath, _dir, EmbeddingModelProfile.Base with { Dimensions = 4 }),
            new MangaBakaDumpOptions(_dumpPath, _dir),
            NullLogger<VectorIndexCache>.Instance);
        var index = (await cache.GetAsync())!;
        var plan = index.Plan(filters);
        var viaIndex = Enumerable.Range(0, index.Count)
            .Where(row => index.Matches(row, plan))
            .Select(row => index.IdAt(row))
            .Order()
            .ToList();

        using var conn = new SqliteConnection($"Data Source={_dumpPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT d.id FROM series d WHERE d.state = 'active'" + filters.BuildClause(cmd, "d") + " ORDER BY d.id";
        var viaSql = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            viaSql.Add(reader.GetInt64(0));
        }

        return (viaSql, viaIndex);
    }
}
