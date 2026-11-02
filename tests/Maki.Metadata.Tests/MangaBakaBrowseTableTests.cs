using Maki.Core.Entities;
using Maki.Metadata.MangaBaka;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// The rails and id lookups read <see cref="MangaBakaDumpService.BrowseTableName"/> once it exists
/// and <c>series</c> before that. These pin that both give the same answer, in the same order, so
/// the table is a speed-up and nothing else.
/// </summary>
public class MangaBakaBrowseTableTests : IDisposable
{
    private readonly DumpDbBuilder _db = new();

    public void Dispose() => _db.Dispose();

    private MangaBakaLocalStore Store => new(
        new MangaBakaDumpOptions(_db.Path, Path.GetTempPath()), NullLogger<MangaBakaLocalStore>.Instance);

    private static readonly string[] Genres = ["Action", "Romance", "Horror", "Comedy"];
    private static readonly string[] Ratings = ["safe", "suggestive", "erotica", "pornographic"];
    private static readonly string[] Types = ["manga", "manhwa", "manhua", "novel"];

    /// <summary>
    /// A catalogue with ties in every sort key, rows each gate excludes, duplicate display titles and
    /// ids spread past one search chunk, so ordering and filtering both get exercised.
    /// </summary>
    private void Seed()
    {
        var random = new Random(7);
        for (var i = 1; i <= 400; i++)
        {
            var id = i * 37L;
            _db.AddSeries(
                id,
                i % 50 == 0 ? "Shared Title" : $"Series {i}",
                state: i % 23 == 0 ? "merged" : "active",
                type: Types[i % Types.Length],
                contentRating: Ratings[random.Next(Ratings.Length)],
                year: 1990 + random.Next(36),
                status: i % 3 == 0 ? "completed" : "releasing",
                totalChapters: (random.Next(300) + 1).ToString(),
                rating: i % 17 == 0 ? null : Math.Round(5 + (random.Next(10) / 2.0), 1),
                coverUrl: i % 19 == 0 ? null : "c",
                genresJson: $"[\"{Genres[i % Genres.Length]}\", \"{Genres[(i / 4) % Genres.Length]}\"]",
                popularity: i % 13 == 0 ? null : random.Next(1, 1200),
                popularityHistory1W: random.Next(1, 1500),
                popularityHistory1Mo: random.Next(1, 1500),
                popularityHistory3Mo: i % 5 == 0 ? null : random.Next(1, 1500),
                publishedStartDate: i % 11 == 0 ? null : $"20{10 + random.Next(16)}-0{1 + random.Next(9)}-1{random.Next(9)}",
                aniListId: i % 2 == 0 ? i : null,
                malId: i % 3 == 0 ? i + 5000 : null,
                kitsuId: i % 4 == 0 ? i + 9000 : null,
                mangaUpdatesId: i % 5 == 0 ? (i % 10 == 0 ? $"Slug{i}" : $"slug{i}") : null);
        }

        using var conn = _db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE series SET popularity_type_current = (id * 7) % 300 WHERE type IN ('manhwa', 'manhua')";
        cmd.ExecuteNonQuery();

        // The indexes installs had on series before the browse table, which is what "the same as
        // today" means: tie order follows the plan, and Top rated walks the popularity index, so
        // a series without them orders tied ratings differently from any real install.
        const string gate = "state = 'active' AND type != 'novel' AND rating IS NOT NULL AND cover_raw_url IS NOT NULL";
        string[] legacy =
        [
            $"CREATE INDEX ix_browse_pop ON series (popularity_global_current) WHERE {gate} AND popularity_global_current IS NOT NULL",
            $"CREATE INDEX ix_browse_trend ON series (popularity_global_current, popularity_global_history_1mo) WHERE {gate} AND popularity_global_current IS NOT NULL AND popularity_global_history_1mo IS NOT NULL",
            $"CREATE INDEX ix_browse_new ON series (published_start_date DESC) WHERE {gate} AND published_start_date IS NOT NULL",
            $"CREATE INDEX ix_browse_rating ON series (rating DESC) WHERE {gate} AND popularity_global_current IS NOT NULL",
            $"CREATE INDEX ix_browse_type ON series (type, popularity_type_current) WHERE {gate} AND popularity_type_current IS NOT NULL",
            "CREATE INDEX ix_title_nocase ON series (title COLLATE NOCASE) WHERE state = 'active'",
            "CREATE INDEX ix_ext_anilist ON series (source_anilist_id) WHERE source_anilist_id IS NOT NULL",
            "CREATE INDEX ix_ext_mal ON series (source_my_anime_list_id) WHERE source_my_anime_list_id IS NOT NULL",
            "CREATE INDEX ix_ext_kitsu ON series (source_kitsu_id) WHERE source_kitsu_id IS NOT NULL",
            "CREATE INDEX ix_ext_mangaupdates ON series (lower(source_manga_updates_id)) WHERE source_manga_updates_id IS NOT NULL",
        ];
        foreach (var sql in legacy)
        {
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"ANALYZE {sql.Split(' ')[2]}";
            cmd.ExecuteNonQuery();
        }
    }

    private void BuildBrowseTable()
    {
        using var conn = _db.Open();
        MangaBakaDumpService.BuildBrowseIndexes(conn);
    }

    private static IEnumerable<RecommendationFilters> FilterSets() =>
    [
        RecommendationFilters.None,
        new(ContentRatings: ContentRating.Allowed(ContentRating.Safe)),
        new(ContentRatings: ContentRating.Allowed(ContentRating.Erotica)),
        new(YearMin: 2000, YearMax: 2015),
        new(MinRating: 7),
        new(MinChapters: 50),
        new(Statuses: ["completed"]),
        new(Genres: ["Romance"]),
    ];

    private async Task<List<string>> AllRailsAsync()
    {
        var store = Store;
        var lines = new List<string>();
        foreach (var filters in FilterSets())
        {
            foreach (var feed in Enum.GetValues<BrowseFeed>())
            {
                var genre = feed == BrowseFeed.GenreSpotlight ? "Action" : null;
                var rail = await store.GetBrowseAsync(feed, 30, genre, filters);
                lines.Add($"{feed} {filters}: {string.Join(",", rail.Select(r => $"{r.ProviderId}:{r.Title}"))}");
            }

            var since = await store.GetBrowseAsync(BrowseFeed.New, 30, filters: filters, releasedSince: new DateOnly(2018, 1, 1));
            lines.Add($"since {filters}: {string.Join(",", since.Select(r => r.ProviderId))}");
        }

        return lines;
    }

    [Fact]
    public async Task Every_rail_is_the_same_with_and_without_the_browse_table()
    {
        Seed();
        var before = await AllRailsAsync();

        BuildBrowseTable();
        using (var conn = _db.Open())
        {
            Assert.Equal(MangaBakaDumpService.BrowseTableName, MangaBakaDumpService.BrowseSource(conn));
        }

        var after = await AllRailsAsync();

        Assert.Contains(before, line => line.Split(':').Length > 3);
        var diffs = before.Zip(after).Where(p => p.First != p.Second).Select(p => p.First + "\n  vs\n" + p.Second).ToList();
        Assert.True(diffs.Count == 0, string.Join("\n\n", diffs.Take(3)));
    }

    [Fact]
    public async Task External_id_lookups_are_the_same_with_and_without_the_browse_table()
    {
        Seed();
        var store = Store;
        var probe = Enumerable.Range(0, 450).Select(i => (long)i).ToList();
        var slugs = Enumerable.Range(0, 450).Select(i => $"SLUG{i}").Append("missing").ToList();

        async Task<string> LookupsAsync() => string.Join(" | ",
            string.Join(",", (await store.GetIdsByExternalIdsAsync(MangaBakaLocalStore.ExternalSource.AniList, probe)).OrderBy(kv => kv.Key)),
            string.Join(",", (await store.GetIdsByExternalIdsAsync(MangaBakaLocalStore.ExternalSource.MyAnimeList, probe.Select(p => p + 5000).ToList())).OrderBy(kv => kv.Key)),
            string.Join(",", (await store.GetIdsByExternalIdsAsync(MangaBakaLocalStore.ExternalSource.Kitsu, probe.Select(p => p + 9000).ToList())).OrderBy(kv => kv.Key)),
            string.Join(",", (await store.GetIdsByMangaUpdatesIdsAsync(slugs)).OrderBy(kv => kv.Key)));

        var before = await LookupsAsync();
        BuildBrowseTable();
        var after = await LookupsAsync();

        Assert.NotEqual(" |  |  | ", before);
        Assert.Equal(before, after);
    }

    [Fact]
    public void The_copy_made_during_the_search_pass_matches_a_full_copy()
    {
        Seed();
        using var conn = _db.Open();
        MangaBakaDumpService.BuildSearchIndex(conn, withBrowseTable: true);
        var chunked = Dump(conn);

        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = $"DROP TABLE {MangaBakaDumpService.BrowseTableName}";
            drop.ExecuteNonQuery();
        }

        MangaBakaDumpService.BuildBrowseIndexes(conn);

        Assert.Equal(400, chunked.Count);
        Assert.Equal(Dump(conn), chunked);
    }

    [Fact]
    public void The_copy_keeps_each_columns_declared_type()
    {
        Seed();
        using var conn = _db.Open();
        MangaBakaDumpService.BuildBrowseIndexes(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT b.name, b.type, s.type FROM pragma_table_info('{MangaBakaDumpService.BrowseTableName}') b
            JOIN pragma_table_info('series') s ON s.name = b.name
            """;
        using var reader = cmd.ExecuteReader();
        var columns = 0;
        while (reader.Read())
        {
            columns++;
            Assert.Equal(reader.GetString(2), reader.GetString(1));
        }

        Assert.True(columns > 15);
    }

    [Fact]
    public async Task The_live_backfill_creates_the_table_and_then_leaves_the_file_alone()
    {
        Seed();
        var service = new MangaBakaDumpService(null!,
            new MangaBakaDumpOptions(_db.Path, Path.GetTempPath()), new FakeAppSettings(),
            new MangaBakaDumpStatus(), NullLogger<MangaBakaDumpService>.Instance);

        Assert.True(await service.EnsureBrowseIndexesAsync());
        using var conn = _db.Open();
        Assert.Equal(MangaBakaDumpService.BrowseTableName, MangaBakaDumpService.BrowseSource(conn));

        using var version = conn.CreateCommand();
        version.CommandText = "PRAGMA schema_version";
        var built = version.ExecuteScalar();
        Assert.False(await service.EnsureBrowseIndexesAsync());
        Assert.Equal(built, version.ExecuteScalar());
    }

    private void SeedFranchises()
    {
        // Ids spread past one search chunk, linked through typed edges, flat columns and a
        // relation type that is not the same work, plus an edge to an id the dump does not hold.
        _db.AddSeries(1, "Root", relationshipsV2: """[{"relation_type": "sequel", "to_series_id": 25001}]""")
            .AddSeries(25001, "Sequel", sequels: "[60000]")
            .AddSeries(60000, "Third")
            .AddSeries(70000, "Adaptation", relationshipsV2: """[{"relation_type": "adaptation", "to_series_id": 1}]""")
            .AddSeries(80000, "Spin", relationshipsV2: """[{"relation_type": "spin_off", "to_series_id": 999999}]""")
            .AddSeries(90000, "Broken", relationshipsV2: "not json")
            .AddSeries(12, "Alone");
    }

    [Fact]
    public void The_franchise_table_written_during_the_search_pass_matches_a_fresh_build()
    {
        SeedFranchises();
        using var conn = _db.Open();
        var fresh = FranchiseGraph.Build(conn);
        Assert.Null(FranchiseGraph.Read(conn));

        MangaBakaDumpService.BuildSearchIndex(conn, withBrowseTable: true, withFranchiseTable: true);
        var stored = FranchiseGraph.Read(conn);

        Assert.NotNull(stored);
        Assert.Equal(fresh.OrderBy(kv => kv.Key), stored.OrderBy(kv => kv.Key));
        Assert.Equal(stored[1], stored[60000]);
        Assert.NotEqual(stored[1], stored[80000]);
        Assert.DoesNotContain(70000L, stored.Keys);
    }

    [Fact]
    public async Task The_live_backfill_writes_the_franchise_table_and_rebuilds_a_browse_table_missing_columns()
    {
        SeedFranchises();
        using (var conn = _db.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE TABLE {MangaBakaDumpService.BrowseTableName} AS SELECT id, state, title FROM series";
            cmd.ExecuteNonQuery();
        }

        var service = new MangaBakaDumpService(null!,
            new MangaBakaDumpOptions(_db.Path, Path.GetTempPath()), new FakeAppSettings(),
            new MangaBakaDumpStatus(), NullLogger<MangaBakaDumpService>.Instance);
        Assert.True(await service.EnsureBrowseIndexesAsync());

        using var check = _db.Open();
        Assert.Equal(FranchiseGraph.Build(check).OrderBy(kv => kv.Key), FranchiseGraph.Read(check)!.OrderBy(kv => kv.Key));
        using var columns = check.CreateCommand();
        columns.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{MangaBakaDumpService.BrowseTableName}') WHERE name IN ('authors', 'publishers', 'popularity_global_current')";
        Assert.Equal(3L, columns.ExecuteScalar());
        Assert.False(await service.EnsureBrowseIndexesAsync());
    }

    private static List<string> Dump(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {MangaBakaDumpService.BrowseTableName} ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(string.Join("|", values.Select(v => $"{v?.GetType().Name}:{v}")));
        }

        return rows;
    }
}
