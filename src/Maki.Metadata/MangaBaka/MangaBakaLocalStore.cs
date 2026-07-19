using System.Globalization;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.MangaBaka;

/// <summary>
/// Read-only queries against the local MangaBaka dump maintained by
/// <see cref="MangaBakaDumpService"/>. Search goes through the FTS5 index built at
/// install time (title, native/romanized titles, and every alternative title).
/// </summary>
public class MangaBakaLocalStore(
    MangaBakaDumpOptions options,
    IAppSettings settings,
    ILogger<MangaBakaLocalStore> logger)
{
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!File.Exists(options.DatabasePath))
        {
            return false;
        }

        var enabled = await settings.GetAsync(SettingKeys.MangaBakaUseLocalDb, ct);
        return !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var match = BuildMatchExpression(query);
        if (match is null)
        {
            return [];
        }

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // A series appears once per title variant in the index; keep its best rank,
        // then break ties by global popularity (lower = more popular).
        cmd.CommandText = $"""
            SELECT s.id, s.title, s.cover_raw_url, s.year, s.status, s.description, s.total_chapters
            FROM (
                SELECT series_id, MIN(rank) AS best_rank
                FROM {MangaBakaDumpService.SearchTableName}
                WHERE {MangaBakaDumpService.SearchTableName} MATCH $query
                GROUP BY series_id
            ) m
            JOIN series s ON s.id = m.series_id
            ORDER BY m.best_rank, s.popularity_global_current IS NULL, s.popularity_global_current
            LIMIT 20
            """;
        cmd.Parameters.AddWithValue("$query", match);

        var results = new List<MetadataSearchResult>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new MetadataSearchResult(
                reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                GetString(reader, 1) ?? string.Empty,
                GetString(reader, 2),
                GetInt(reader, 3),
                MangaBakaProvider.MapStatus(GetString(reader, 4)),
                GetString(reader, 5),
                ParseCount(GetString(reader, 6))));
        }

        return results;
    }

    public async Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default)
    {
        if (!long.TryParse(providerId, out var id))
        {
            return null;
        }

        using var conn = Open();
        for (var hop = 0; hop < 5; hop++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, state, merged_with, title, native_title, description, year, status,
                       final_volume, total_chapters, authors, artists, genres, tags, cover_raw_url,
                       source_anilist_id, source_my_anime_list_id, source_manga_updates_id
                FROM series
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", id);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            // Merged entries point at their canonical series, same as the API.
            if (GetString(reader, 1) == "merged" && long.TryParse(GetString(reader, 2), out var canonical))
            {
                logger.LogInformation("MangaBaka series {Id} merged into {Canonical}; following", id, canonical);
                id = canonical;
                continue;
            }

            return Map(reader);
        }

        return null;
    }

    private static SeriesMetadata Map(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);
        var authors = ParseStringArray(GetString(reader, 10));
        var artists = ParseStringArray(GetString(reader, 11));

        return new SeriesMetadata
        {
            ProviderId = id.ToString(CultureInfo.InvariantCulture),
            Title = GetString(reader, 3) ?? string.Empty,
            OriginalTitle = GetString(reader, 4),
            Description = GetString(reader, 5),
            CoverUrl = GetString(reader, 14),
            Year = GetInt(reader, 6),
            Status = MangaBakaProvider.MapStatus(GetString(reader, 7)),
            Genres = ParseStringArray(GetString(reader, 12)),
            Tags = ParseStringArray(GetString(reader, 13)),
            AuthorStory = authors.Count > 0 ? string.Join(", ", authors) : null,
            AuthorArt = artists.Count > 0 ? string.Join(", ", artists) : null,
            TotalChapters = ParseCount(GetString(reader, 9)),
            TotalVolumes = ParseCount(GetString(reader, 8)),
            WebUrl = $"https://mangabaka.org/{id}",
            MangaBakaId = (int)id,
            AniListId = GetInt(reader, 15),
            MalId = GetInt(reader, 16),
            MangaUpdatesId = GetString(reader, 17)
        };
    }

    /// <summary>
    /// Direct relations (sequels, prequels, spin-offs, side/main stories) of the given
    /// library series, excluding anything already in the library. Merged entries are
    /// followed to their canonical row; novels and pornographic entries are dropped.
    /// </summary>
    public async Task<IReadOnlyList<MangaBakaRecommendation>> GetRelatedAsync(
        IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
        CancellationToken ct = default)
    {
        if (seedIds.Count == 0)
        {
            return [];
        }

        var kinds = new (string Column, string Kind)[]
        {
            ("relationships_sequel", "Sequel"),
            ("relationships_prequel", "Prequel"),
            ("relationships_spin_off", "Spin-off"),
            ("relationships_side_story", "Side story"),
            ("relationships_main_story", "Main story"),
        };

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT title, {string.Join(", ", kinds.Select(k => k.Column))}
            FROM series WHERE id IN ({string.Join(",", seedIds)})
            """;

        // relation id → (kind, which library series it relates to); first mention wins
        var wanted = new Dictionary<long, (string Kind, string RelatedTo)>();
        using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var sourceTitle = GetString(reader, 0) ?? string.Empty;
                for (var i = 0; i < kinds.Length; i++)
                {
                    foreach (var id in ParseIdArray(GetString(reader, i + 1)))
                    {
                        if (!excludeIds.Contains(id))
                        {
                            wanted.TryAdd(id, (kinds[i].Kind, sourceTitle));
                        }
                    }
                }
            }
        }

        var results = new List<MangaBakaRecommendation>();
        var pending = wanted.Keys.ToList();
        for (var hop = 0; hop < 3 && pending.Count > 0; hop++)
        {
            using var fetch = conn.CreateCommand();
            fetch.CommandText = $"""
                SELECT id, state, merged_with, title, cover_raw_url, year, status, rating,
                       total_chapters, description, content_rating, type
                FROM series WHERE id IN ({string.Join(",", pending)})
                """;
            pending = [];

            using var reader = await fetch.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                var relation = wanted[id];
                if (GetString(reader, 1) == "merged" && long.TryParse(GetString(reader, 2), out var canonical))
                {
                    if (!excludeIds.Contains(canonical) && wanted.TryAdd(canonical, relation))
                    {
                        pending.Add(canonical);
                    }

                    continue;
                }

                if (GetString(reader, 1) != "active" ||
                    GetString(reader, 10) == "pornographic" || GetString(reader, 11) == "novel")
                {
                    continue;
                }

                results.Add(new MangaBakaRecommendation(
                    id.ToString(CultureInfo.InvariantCulture),
                    GetString(reader, 3) ?? string.Empty,
                    GetString(reader, 4),
                    GetInt(reader, 5),
                    GetString(reader, 9),
                    MangaBakaProvider.MapStatus(GetString(reader, 6)),
                    reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    ParseCount(GetString(reader, 8)),
                    [], [], false,
                    relation.Kind, relation.RelatedTo));
            }
        }

        return results.OrderByDescending(r => r.Rating ?? 0).ToList();
    }

    /// <summary>
    /// Scores every rated, active, non-novel, non-pornographic entry in the dump against
    /// the library's genre/tag/author profile and returns the best matches. One full-table
    /// scan (~seconds on the ~3 GB dump) — callers cache the result.
    /// </summary>
    public async Task<IReadOnlyList<MangaBakaRecommendation>> GetSimilarAsync(
        IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
        int limit, RecommendationFilters? filters = null, CancellationToken ct = default)
    {
        if (seedIds.Count == 0)
        {
            return [];
        }

        filters ??= RecommendationFilters.None;
        using var conn = Open();

        // Seed profile: how common each genre/tag is across the seed set.
        var genreWeight = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var tagWeight = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var authors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT genres, tags, authors FROM series WHERE id IN ({string.Join(",", seedIds)})";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                foreach (var g in ParseStringArray(GetString(reader, 0)))
                {
                    genreWeight[g] = genreWeight.GetValueOrDefault(g) + 1.0 / seedIds.Count;
                }

                foreach (var t in ParseStringArray(GetString(reader, 1)))
                {
                    tagWeight[t] = tagWeight.GetValueOrDefault(t) + 1.0 / seedIds.Count;
                }

                foreach (var a in ParseStringArray(GetString(reader, 2)))
                {
                    authors.Add(a);
                }
            }
        }

        if (genreWeight.Count == 0 && tagWeight.Count == 0)
        {
            return [];
        }

        var exclude = new HashSet<long>(seedIds.Concat(excludeIds));
        var top = new List<(double Score, MangaBakaRecommendation Item)>();
        var floor = double.NegativeInfinity; // score of the worst kept candidate after a prune
        using (var scan = conn.CreateCommand())
        {
            scan.CommandText = """
                SELECT id, title, cover_raw_url, year, status, rating, total_chapters,
                       genres, tags, authors
                FROM series
                WHERE state = 'active' AND rating IS NOT NULL
                  AND content_rating != 'pornographic' AND type != 'novel'
                """ + filters.BuildClause(scan, "series");
            using var reader = await scan.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                if (exclude.Contains(id))
                {
                    continue;
                }

                var matchedGenres = ParseStringArray(GetString(reader, 7))
                    .Where(genreWeight.ContainsKey)
                    .OrderByDescending(g => genreWeight[g])
                    .ToList();
                var candidateTags = ParseStringArray(GetString(reader, 8));
                // Tag filter: candidate must carry every selected tag. The plain `tags` column
                // only covers ~half the dump, but this scan is just the pre-index fallback.
                if (filters.Tags is { Count: > 0 } wantedTags &&
                    !wantedTags.All(t => candidateTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var matchedTags = candidateTags
                    .Where(tagWeight.ContainsKey)
                    .OrderByDescending(t => tagWeight[t])
                    .ToList();
                var authorMatch = ParseStringArray(GetString(reader, 9)).Any(authors.Contains);
                if (matchedGenres.Count < 2 && !authorMatch)
                {
                    continue;
                }

                var similarity =
                    2.0 * matchedGenres.Sum(g => genreWeight[g]) +
                    1.0 * matchedTags.Sum(t => tagWeight[t]) +
                    (authorMatch ? 1.5 : 0);
                var rating = reader.GetDouble(5);
                var score = similarity * (0.5 + rating / 100.0);
                if (score <= floor)
                {
                    continue;
                }

                top.Add((score, new MangaBakaRecommendation(
                    id.ToString(CultureInfo.InvariantCulture),
                    GetString(reader, 1) ?? string.Empty,
                    GetString(reader, 2),
                    GetInt(reader, 3),
                    null, // description hydrated below for the winners only
                    MangaBakaProvider.MapStatus(GetString(reader, 4)),
                    rating,
                    ParseCount(GetString(reader, 6)),
                    matchedGenres.Take(4).ToList(),
                    matchedTags.Take(4).ToList(),
                    authorMatch,
                    null, null)));
                if (top.Count >= limit * 8)
                {
                    top = top.OrderByDescending(x => x.Score).Take(limit * 4).ToList();
                    floor = top[^1].Score;
                }
            }
        }

        var winners = top.OrderByDescending(x => x.Score).Take(limit).Select(x => x.Item).ToList();
        if (winners.Count > 0)
        {
            using var hydrate = conn.CreateCommand();
            hydrate.CommandText = $"""
                SELECT id, description FROM series
                WHERE id IN ({string.Join(",", winners.Select(w => w.ProviderId))})
                """;
            var descriptions = new Dictionary<string, string?>();
            using var reader = await hydrate.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                descriptions[reader.GetInt64(0).ToString(CultureInfo.InvariantCulture)] = GetString(reader, 1);
            }

            winners = winners
                .Select(w => w with { Description = descriptions.GetValueOrDefault(w.ProviderId) })
                .ToList();
        }

        return winners;
    }

    /// <summary>
    /// A catalogue-browse rail for the Discover page: the dump's most-popular / newest /
    /// trending / top-rated titles, independent of the user's library. Each rail is a single
    /// indexed-free full scan (~1.5s), so callers cache the results. Results are deduped by
    /// normalized title (popularity/date data lives on source-linked rows, not the merged
    /// canonical, and a title can appear as several active rows) keeping the best per the rail's
    /// ordering. Reuses <see cref="MangaBakaRecommendation"/> so the same card/detail/add flow
    /// works — the relation and matched-genre/tag fields are left empty.
    /// </summary>
    public async Task<IReadOnlyList<MangaBakaRecommendation>> GetBrowseAsync(
        BrowseFeed feed, int limit, string? genre = null,
        RecommendationFilters? filters = null, CancellationToken ct = default)
    {
        if (feed == BrowseFeed.GenreSpotlight && string.IsNullOrWhiteSpace(genre))
        {
            throw new ArgumentException("GenreSpotlight requires a genre.", nameof(genre));
        }

        filters ??= RecommendationFilters.None;

        // Common quality gate: active, safe, real title, has a cover. Every rail also needs a
        // rating (drops the long tail of unscored junk and powers the card's ★ badge).
        const string baseWhere =
            "state = 'active' AND content_rating != 'pornographic' AND type != 'novel' " +
            "AND rating IS NOT NULL AND cover_raw_url IS NOT NULL AND title NOT LIKE 'unknown title%'";

        // popularity_global_current / popularity_type_current: 1 = most popular.
        // popularity_global_history_1mo: rank a month ago, so (history - current) > 0 = climbing.
        var (where, orderBy) = feed switch
        {
            BrowseFeed.Trending => (
                baseWhere + " AND popularity_global_current IS NOT NULL " +
                "AND popularity_global_history_1mo IS NOT NULL AND popularity_global_current < 20000",
                "(popularity_global_history_1mo - popularity_global_current) DESC"),
            BrowseFeed.Popular => (
                baseWhere + " AND popularity_global_current IS NOT NULL",
                "popularity_global_current ASC"),
            BrowseFeed.New => (
                baseWhere + " AND published_start_date IS NOT NULL AND published_start_date <= $today",
                "published_start_date DESC"),
            BrowseFeed.TopRated => (
                baseWhere + " AND popularity_global_current IS NOT NULL AND popularity_global_current < 15000",
                "rating DESC"),
            BrowseFeed.PopularManhwa => (
                baseWhere + " AND type = 'manhwa' AND popularity_type_current IS NOT NULL",
                "popularity_type_current ASC"),
            BrowseFeed.PopularManhua => (
                baseWhere + " AND type = 'manhua' AND popularity_type_current IS NOT NULL",
                "popularity_type_current ASC"),
            // genres is a JSON array of quoted strings; LIKE on the quoted name is an exact
            // membership test (case-insensitive for ASCII, which covers the genre vocabulary).
            BrowseFeed.GenreSpotlight => (
                baseWhere + " AND popularity_global_current IS NOT NULL AND genres LIKE $genre",
                "popularity_global_current ASC"),
            _ => throw new ArgumentOutOfRangeException(nameof(feed), feed, null),
        };

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // Optional user filters (year/status/type/rating/chapters/genre) from the expanded view.
        var filterClause = filters.BuildClause(cmd, "series");
        // Over-fetch so title-dedupe still leaves `limit` rows even when filters thin the set.
        cmd.CommandText = $"""
            SELECT id, title, cover_raw_url, year, status, rating, total_chapters, description
            FROM series
            WHERE {where}{filterClause}
            ORDER BY {orderBy}
            LIMIT $take
            """;
        cmd.Parameters.AddWithValue("$take", limit * 5);
        if (feed == BrowseFeed.New)
        {
            cmd.Parameters.AddWithValue("$today", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        else if (feed == BrowseFeed.GenreSpotlight)
        {
            cmd.Parameters.AddWithValue("$genre", $"%\"{genre}\"%");
        }

        var results = new List<MangaBakaRecommendation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var title = GetString(reader, 1) ?? string.Empty;
            if (!seen.Add(title.Trim()))
            {
                continue; // first sighting is best per the ORDER BY; skip later duplicates
            }

            results.Add(new MangaBakaRecommendation(
                reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                title,
                GetString(reader, 2),
                GetInt(reader, 3),
                GetString(reader, 7),
                MangaBakaProvider.MapStatus(GetString(reader, 4)),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                ParseCount(GetString(reader, 6)),
                [], [], false,
                null, null));
            if (results.Count >= limit)
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Rich detail for one series (full description, categorized tags, cross-links, per-source
    /// ratings, publishers) for the Discover detail card. Follows merged rows to the canonical
    /// entry, same as <see cref="GetAsync"/>. Returns null when the id is unknown.
    /// </summary>
    public async Task<MangaBakaDetail?> GetDetailAsync(long id, CancellationToken ct = default)
    {
        using var conn = Open();
        for (var hop = 0; hop < 5; hop++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, state, merged_with, title, native_title, romanized_title, description,
                       cover_raw_url, year, type, status, content_rating, rating,
                       source_anilist_rating_normalized, source_my_anime_list_rating_normalized,
                       source_manga_updates_rating_normalized, source_kitsu_rating_normalized,
                       total_chapters, final_volume, authors, artists, publishers, genres, tags_v2,
                       source_anilist_id, source_my_anime_list_id, source_manga_updates_id
                FROM series
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$id", id);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            if (GetString(reader, 1) == "merged" && long.TryParse(GetString(reader, 2), out var canonical))
            {
                id = canonical;
                continue;
            }

            return MapDetail(reader);
        }

        return null;
    }

    private static MangaBakaDetail MapDetail(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);

        var sourceRatings = new List<MangaBakaSourceRating>();
        void AddRating(string source, int ordinal)
        {
            if (!reader.IsDBNull(ordinal))
            {
                sourceRatings.Add(new MangaBakaSourceRating(source, reader.GetDouble(ordinal)));
            }
        }

        AddRating("AniList", 13);
        AddRating("MyAnimeList", 14);
        AddRating("MangaUpdates", 15);
        AddRating("Kitsu", 16);

        var links = new List<MetadataLink> { new("mangabaka", $"https://mangabaka.org/{id}") };
        if (GetInt(reader, 24) is int aniList)
        {
            links.Add(new("anilist", $"https://anilist.co/manga/{aniList}"));
        }

        var malId = GetInt(reader, 25);
        if (malId is int mal)
        {
            links.Add(new("myanimelist", $"https://myanimelist.net/manga/{mal}"));
        }

        if (GetString(reader, 26) is { Length: > 0 } mangaUpdates)
        {
            links.Add(new("mangaupdates", $"https://www.mangaupdates.com/series/{mangaUpdates}"));
        }

        var genres = ParseStringArray(GetString(reader, 22));
        var genreSet = new HashSet<string>(genres, StringComparer.OrdinalIgnoreCase);

        return new MangaBakaDetail(
            id.ToString(CultureInfo.InvariantCulture),
            GetString(reader, 3) ?? string.Empty,
            GetString(reader, 4),
            GetString(reader, 5),
            GetString(reader, 6),
            GetString(reader, 7),
            GetInt(reader, 8),
            GetString(reader, 9),
            MangaBakaProvider.MapStatus(GetString(reader, 10)),
            GetString(reader, 11),
            reader.IsDBNull(12) ? null : reader.GetDouble(12),
            sourceRatings,
            ParseCount(GetString(reader, 17)),
            ParseCount(GetString(reader, 18)),
            ParseStringArray(GetString(reader, 19)),
            ParseStringArray(GetString(reader, 20)),
            ParsePublishers(GetString(reader, 21)),
            genres,
            ParseTags(GetString(reader, 23), genreSet),
            links,
            malId);
    }

    /// <summary>Publisher entries are objects (<c>{"name","note","type"}</c>); we surface the names.</summary>
    private static IReadOnlyList<string> ParsePublishers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var names = new List<string>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.ValueKind == JsonValueKind.Object &&
                           element.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            return names;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Weighted tags from <c>tags_v2</c>: objects with name/weight/is_genre/description. We drop
    /// genre tags (already surfaced separately and as the <c>genres</c> column) and the noisy
    /// <c>unweighted</c> bucket, keeping the core/defining/recurrent/incidental ones the site shows.
    /// </summary>
    private static IReadOnlyList<MangaBakaTag> ParseTags(string? json, HashSet<string> genres)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var tags = new List<MangaBakaTag>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !element.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = nameEl.GetString()!;
                var weight = element.TryGetProperty("weight", out var w) && w.ValueKind == JsonValueKind.String
                    ? w.GetString()!
                    : "unweighted";
                var isGenre = element.TryGetProperty("is_genre", out var g) &&
                              g.ValueKind is JsonValueKind.True;
                if (isGenre || weight == "unweighted" || genres.Contains(name))
                {
                    continue;
                }

                var description = element.TryGetProperty("description", out var d) &&
                                  d.ValueKind == JsonValueKind.String &&
                                  !string.IsNullOrWhiteSpace(d.GetString())
                    ? d.GetString()
                    : null;
                // MangaBaka hides these behind a blur — they reveal story spoilers.
                var isSpoiler = element.TryGetProperty("is_spoiler", out var sp) &&
                                sp.ValueKind is JsonValueKind.True;
                tags.Add(new MangaBakaTag(name, weight, description, isSpoiler));
            }

            // Present in the site's order: most-relevant buckets first.
            var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["core"] = 0,
                ["defining"] = 1,
                ["recurrent"] = 2,
                ["incidental"] = 3,
            };
            return tags
                .OrderBy(t => order.GetValueOrDefault(t.Weight, 9))
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<long> ParseIdArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<long>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private SqliteConnection Open()
    {
        // Pooling=False keeps handles off the file so the nightly swap can replace it.
        var conn = new SqliteConnection($"Data Source={options.DatabasePath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return conn;
    }

    /// <summary>Turns free text into an FTS5 expression: each token quoted, last token as prefix.</summary>
    internal static string? BuildMatchExpression(string query)
    {
        var tokens = query
            .Split(' ', '\t', '\r', '\n')
            .Select(t => t.Replace("\"", string.Empty).Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (tokens.Count == 0)
        {
            return null;
        }

        return string.Join(" ", tokens.Select((t, i) => i == tokens.Count - 1 ? $"\"{t}\" *" : $"\"{t}\""));
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

    /// <summary>Chapter/volume counts are TEXT in the dump and occasionally fractional ("112.5").</summary>
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

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fractional)
            ? (int)fractional
            : null;
    }

    private static string? GetString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
