using System.Globalization;
using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Metadata.Catalogue;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Maki.Metadata.MangaBaka;

/// <summary>What a title search found, and the spelling it had to fall back on to find it.</summary>
/// <param name="CorrectedQuery">
/// Non-null only when the query as typed found next to nothing and a respelling did better. The UI
/// shows it as "showing results for ..."; null means these are results for exactly what was asked.
/// </param>
/// <param name="Credits">The <c>author:</c>/<c>studio:</c> terms this query resolved to, for display as chips.</param>
public sealed record TitleSearchOutcome(
    IReadOnlyList<MetadataSearchResult> Items,
    string? CorrectedQuery,
    IReadOnlyList<ResolvedCredit> Credits)
{
    public static readonly TitleSearchOutcome Empty = new([], null, []);
}

/// <summary>
/// Read-only queries against the local MangaBaka dump maintained by
/// <see cref="MangaBakaDumpService"/>. Search goes through the FTS5 index built at
/// install time (title, native/romanized titles, and every alternative title).
/// </summary>
/// <param name="catalogue">
/// Optional, and the reason typo tolerance reaches every caller at once. The Discover lexical
/// channel, the Discover title fallback, the add-series search and the command palette all funnel
/// through <see cref="SearchWithCorrectionAsync"/>, so the rescue lives here and nowhere else.
/// Null, as in the tests and the eval harness, simply means exact matching.
/// </param>
public class MangaBakaLocalStore(
    MangaBakaDumpOptions options,
    IAppSettings settings,
    ILogger<MangaBakaLocalStore> logger,
    CatalogueIndexCache? catalogue = null,
    CatalogueOptions? catalogueOptions = null)
{
    /// <summary>Rows a title search returns when the caller does not ask for a different depth.</summary>
    public const int DefaultSearchLimit = 20;

    /// <summary>
    /// Backstop on how many ids a credit restriction will inline. Callers holding a
    /// <c>SearchTuning</c> cap earlier and more meaningfully, by popularity; this only stops an
    /// unbounded set from building a statement the dump has to parse.
    /// </summary>
    private const int MaxInlineIds = 10_000;

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!File.Exists(options.DatabasePath))
        {
            return false;
        }

        var enabled = await settings.GetAsync(SettingKeys.MangaBakaUseLocalDb, ct);
        return !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Title search, reporting the spelling it fell back on when the query as typed found next to
    /// nothing.
    ///
    /// <para>
    /// The exact match always runs first and its rows always come first. The rescue only runs when
    /// that pass came back under <see cref="FuzzyOptions.RescueBelow"/>, and its rows are appended
    /// rather than merged by score, so a correction can never displace a spelling that genuinely
    /// matched. Callers upstream read this order as ranks, which carries the same guarantee into
    /// the search fusion.
    /// </para>
    ///
    /// <para>
    /// One accepted deviation: the count deciding whether to rescue is taken after the
    /// content-rating filter. A query whose only exact matches sit above the caller's ceiling will
    /// therefore rescue and show weaker corrected hits instead of nothing. Counting before the
    /// filter would cost a second query on every keystroke, and weak hits are not worse than an
    /// empty page.
    /// </para>
    /// </summary>
    /// <param name="restrictToIds">
    /// Narrow to these series, as a resolved <c>author:</c> or <c>studio:</c> term does. An empty
    /// collection is not "no restriction": it is a credit that resolved to nobody, and the honest
    /// answer is nothing rather than the whole catalogue.
    /// </param>
    public async Task<TitleSearchOutcome> SearchWithCorrectionAsync(
        string query,
        string maxContentRating,
        IReadOnlyCollection<long>? restrictToIds = null,
        int limit = DefaultSearchLimit,
        CancellationToken ct = default)
    {
        var tuning = catalogueOptions ?? CatalogueOptions.Default;
        var parsed = CatalogueQuery.Parse(query);

        // Only touch the catalogue indexes when the query actually names somebody. An ordinary
        // title search that already matches never needs them built.
        CatalogueIndexes? indexes = null;
        var credits = CreditResolution.None;
        if (parsed.HasCredits && catalogue is not null)
        {
            indexes = await catalogue.GetAsync(ct);
            if (indexes is not null)
            {
                credits = CreditResolver.Resolve(parsed, indexes.Credits, tuning);
            }
        }

        var restriction = Intersect(restrictToIds, credits.SeriesIds);
        if (credits.Impossible || restriction is { Count: 0 })
        {
            return TitleSearchOutcome.Empty with { Credits = credits.Credits };
        }

        // Words an unquoted credit value turned out not to need are still part of the search.
        var text = Combine(parsed.FreeText, credits.ExtraFreeText);
        var allowed = ContentRating.Allowed(maxContentRating);
        using var conn = Open();

        // A bare author:"..." has nothing to match titles on, so the answer is that person's works
        // in popularity order rather than an empty page.
        if (text.Length == 0)
        {
            var works = restriction is null
                ? []
                : await ListByIdsAsync(conn, restriction, allowed, limit, ct);
            return new TitleSearchOutcome(works, null, credits.Credits);
        }

        var match = BuildMatchExpression(text);
        if (match is null)
        {
            return TitleSearchOutcome.Empty with { Credits = credits.Credits };
        }

        var exact = await RunMatchAsync(conn, match, allowed, restriction, limit, ct);

        var fuzzy = tuning.Fuzzy;
        if (!fuzzy.Enabled || catalogue is null || exact.Count >= fuzzy.RescueBelow)
        {
            return new TitleSearchOutcome(exact, null, credits.Credits);
        }

        indexes ??= await catalogue.GetAsync(ct);
        if (indexes is null || indexes.Terms.IsEmpty)
        {
            return new TitleSearchOutcome(exact, null, credits.Credits);
        }

        var rescue = BuildFuzzyMatchExpression(text, indexes.Terms, fuzzy, out var corrected);
        if (rescue is null)
        {
            return new TitleSearchOutcome(exact, null, credits.Credits);
        }

        var respelled = await RunMatchAsync(conn, rescue, allowed, restriction, limit, ct);
        var seen = exact.Select(r => r.ProviderId).ToHashSet(StringComparer.Ordinal);
        var merged = new List<MetadataSearchResult>(exact);
        foreach (var hit in respelled)
        {
            if (merged.Count >= limit)
            {
                break;
            }

            if (seen.Add(hit.ProviderId))
            {
                merged.Add(hit);
            }
        }

        if (merged.Count == exact.Count)
        {
            return new TitleSearchOutcome(exact, null, credits.Credits);
        }

        logger.LogDebug(
            "Title search rescued {Count} row(s) by respelling {Query} as {Corrected}",
            merged.Count - exact.Count, text, corrected);
        return new TitleSearchOutcome(merged, corrected, credits.Credits);
    }

    /// <summary>Joins the query's own free text with anything credit resolution handed back.</summary>
    private static string Combine(string freeText, string extra) =>
        extra.Length == 0 ? freeText : freeText.Length == 0 ? extra : $"{freeText} {extra}";

    /// <summary>
    /// Combines a caller's restriction with the one the query's own credit terms imply. Null on
    /// both sides means no restriction at all; anything else intersects, keeping the caller's order
    /// so a later truncation keeps the head of it.
    /// </summary>
    private static IReadOnlyCollection<long>? Intersect(IReadOnlyCollection<long>? caller, long[]? fromQuery)
    {
        if (caller is null)
        {
            return fromQuery;
        }

        if (fromQuery is null)
        {
            return caller;
        }

        var allowed = fromQuery.ToHashSet();
        return caller.Where(allowed.Contains).ToList();
    }

    /// <summary>
    /// Reads a set of series by id, keeping the order they were given in, which for a credit set is
    /// popularity. Over-fetches because the content-rating ceiling can drop rows anywhere in the
    /// list, so slicing to <paramref name="limit"/> up front would return a short page.
    /// </summary>
    private async Task<IReadOnlyList<MetadataSearchResult>> ListByIdsAsync(
        SqliteConnection conn,
        IReadOnlyCollection<long> ids,
        IReadOnlyList<string> allowed,
        int limit,
        CancellationToken ct)
    {
        var wanted = ids.Take(Math.Min(MaxInlineIds, Math.Max(limit * 5, limit))).ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        using var cmd = conn.CreateCommand();
        var allowedNames = allowed.Select((_, i) => $"$allow{i}").ToList();
        cmd.CommandText = $"""
            SELECT s.id, {DisplayTitleSql("s")}, s.cover_raw_url, s.year, s.status, s.description, s.total_chapters
            FROM series s
            WHERE s.id IN ({string.Join(",", wanted.Select(id => id.ToString(CultureInfo.InvariantCulture)))})
              AND s.type != 'novel'
              AND {(allowed.Count < ContentRating.All.Length ? $"s.content_rating IN ({string.Join(",", allowedNames)})" : "1=1")}
            """;
        for (var i = 0; i < allowed.Count; i++)
        {
            cmd.Parameters.AddWithValue($"$allow{i}", allowed[i]);
        }

        var byId = new Dictionary<long, MetadataSearchResult>(wanted.Count);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            byId[id] = new MetadataSearchResult(
                id.ToString(CultureInfo.InvariantCulture),
                GetString(reader, 1) ?? string.Empty,
                GetString(reader, 2),
                GetInt(reader, 3),
                MangaBakaProvider.MapStatus(GetString(reader, 4)),
                GetString(reader, 5),
                ParseCount(GetString(reader, 6)));
        }

        return wanted
            .Select(id => byId.GetValueOrDefault(id))
            .OfType<MetadataSearchResult>()
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
        string query,
        string maxContentRating,
        IReadOnlyCollection<long>? restrictToIds = null,
        int limit = DefaultSearchLimit,
        CancellationToken ct = default) =>
        (await SearchWithCorrectionAsync(query, maxContentRating, restrictToIds, limit, ct)).Items;

    /// <summary>Runs one FTS5 expression against the title index. Shared by the exact and rescue passes.</summary>
    private async Task<IReadOnlyList<MetadataSearchResult>> RunMatchAsync(
        SqliteConnection conn,
        string match,
        IReadOnlyList<string> allowed,
        IReadOnlyCollection<long>? restrictToIds,
        int limit,
        CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        var allowedNames = allowed.Select((_, i) => $"$allow{i}").ToList();

        var restriction = "1=1";
        if (restrictToIds is { Count: > 0 })
        {
            // Inlined rather than parameterized, matching the other id-set scans in this file.
            // These are ids out of our own index, never caller text.
            var ids = restrictToIds.Count > MaxInlineIds ? restrictToIds.Take(MaxInlineIds) : restrictToIds;
            if (restrictToIds.Count > MaxInlineIds)
            {
                logger.LogDebug(
                    "Credit restriction of {Count} ids truncated to {Cap}", restrictToIds.Count, MaxInlineIds);
            }

            restriction = $"s.id IN ({string.Join(",", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)))})";
        }

        // A series appears once per title variant in the index; keep its best rank,
        // then break ties by global popularity (lower = more popular).
        cmd.CommandText = $"""
            SELECT s.id, {DisplayTitleSql("s")}, s.cover_raw_url, s.year, s.status, s.description, s.total_chapters
            FROM (
                SELECT series_id, MIN(rank) AS best_rank
                FROM {MangaBakaDumpService.SearchTableName}
                WHERE {MangaBakaDumpService.SearchTableName} MATCH $query
                GROUP BY series_id
            ) m
            JOIN series s ON s.id = m.series_id
            WHERE s.type != 'novel'
              AND {restriction}
              AND {(allowed.Count < ContentRating.All.Length ? $"s.content_rating IN ({string.Join(",", allowedNames)})" : "1=1")}
            ORDER BY m.best_rank, s.popularity_global_current IS NULL, s.popularity_global_current
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$query", match);
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        for (var i = 0; i < allowed.Count; i++)
        {
            cmd.Parameters.AddWithValue($"$allow{i}", allowed[i]);
        }

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
                       source_anilist_id, source_my_anime_list_id, source_manga_updates_id, has_anime,
                       anime, anime_start, anime_end, source_kitsu_id, tags_v2, titles, type, content_rating
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

            if (GetString(reader, 25) == "novel")
            {
                return null;
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
        var titles = ParsePrimaryTitles(GetString(reader, 24));

        return new SeriesMetadata
        {
            ProviderId = id.ToString(CultureInfo.InvariantCulture),
            Title = titles.EnglishTitle ?? GetString(reader, 3) ?? string.Empty,
            OriginalTitle = titles.NativeTitle ?? GetString(reader, 4),
            AltTitles = titles.OtherTitles,
            Description = GetString(reader, 5),
            CoverUrl = GetString(reader, 14),
            Year = GetInt(reader, 6),
            Status = MangaBakaProvider.MapStatus(GetString(reader, 7)),
            Type = SeriesTypes.Normalize(GetString(reader, 25)),
            Genres = ParseStringArray(GetString(reader, 12)),
            Tags = WithoutSpoilerTags(ParseStringArray(GetString(reader, 13)), GetString(reader, 23)),
            AuthorStory = authors.Count > 0 ? string.Join(", ", authors) : null,
            AuthorArt = artists.Count > 0 ? string.Join(", ", artists) : null,
            TotalChapters = ParseCount(GetString(reader, 9)),
            TotalVolumes = ParseCount(GetString(reader, 8)),
            WebUrl = $"https://mangabaka.org/{id}",
            MangaBakaId = (int)id,
            AniListId = GetInt(reader, 15),
            MalId = GetInt(reader, 16),
            MangaUpdatesId = GetString(reader, 17),
            HasAnime = GetInt(reader, 18) == 1,
            AnimeName = GetString(reader, 19) ?? string.Empty,
            AnimeStart = GetString(reader, 20) ?? string.Empty,
            AnimeEnd = GetString(reader, 21) ?? string.Empty,
            KitsuId = GetInt(reader, 22),
            ContentRating = GetString(reader, 26)
        };
    }

    /// <summary>
    /// Direct relations (sequels, prequels, spin-offs, side/main stories) of the given
    /// library series, excluding anything already in the library. Merged entries are
    /// followed to their canonical row; novels are always dropped, and content rating is
    /// restricted to <paramref name="contentRatings"/> when given (falling back to dropping
    /// only pornographic entries, same as before this parameter existed).
    /// </summary>
    public async Task<IReadOnlyList<MangaBakaRecommendation>> GetRelatedAsync(
        IReadOnlyCollection<long> seedIds, IReadOnlyCollection<long> excludeIds,
        IReadOnlyList<string>? contentRatings = null, CancellationToken ct = default)
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
            SELECT {DisplayTitleSql("series")}, {string.Join(", ", kinds.Select(k => k.Column))}
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
                SELECT id, state, merged_with, {DisplayTitleSql("series")}, cover_raw_url, year, status, rating,
                       total_chapters, description, content_rating, type,
                       cover_x250_x1, cover_x250_x2
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

                var rowContentRating = GetString(reader, 10);
                var ratingAllowed = contentRatings is { Count: > 0 }
                    ? contentRatings.Contains(rowContentRating, StringComparer.OrdinalIgnoreCase)
                    : rowContentRating != "pornographic";
                if (GetString(reader, 1) != "active" || !ratingAllowed || GetString(reader, 11) == "novel")
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
                    relation.Kind, relation.RelatedTo,
                    ThumbUrl: GetString(reader, 12),
                    ThumbUrlHiDpi: GetString(reader, 13)));
            }
        }

        return results.OrderByDescending(r => r.Rating ?? 0).ToList();
    }

    /// <summary>
    /// Scores every rated, active, non-novel entry in the dump against the library's genre/tag/author
    /// profile and returns the best matches. Content rating is bounded only by
    /// <paramref name="filters"/> — callers must resolve it to the caller's ceiling themselves (see
    /// <see cref="ContentRating.Allowed"/>), since nothing here has a user to ask. One full-table
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
            scan.CommandText = $"""
                SELECT id, {DisplayTitleSql("series")}, cover_raw_url, year, status, rating, total_chapters,
                       genres, tags, authors, cover_x250_x1, cover_x250_x2
                FROM series
                WHERE state = 'active' AND rating IS NOT NULL AND type != 'novel'
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
                    null, null,
                    ThumbUrl: GetString(reader, 10),
                    ThumbUrlHiDpi: GetString(reader, 11))));
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
    /// Reads a set of series as browse cards, keeping the order they were given in.
    ///
    /// <para>
    /// This is the hydration half of every path that decides <em>which</em> series to show in
    /// memory rather than in SQL: browsing the catalogue with filters, and a creator's works. The
    /// ordering is the caller's, because by the time it gets here the ranking is already decided.
    /// Column list matches <see cref="GetBrowseAsync"/>'s so the same card renders either way,
    /// thumbnails included.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<MangaBakaRecommendation>> GetByIdsAsync(
        IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, {DisplayTitleSql("series")}, cover_raw_url, year, status, rating, total_chapters,
                   description, cover_x250_x1, cover_x250_x2
            FROM series
            WHERE id IN ({string.Join(",", ids.Take(MaxInlineIds).Select(id => id.ToString(CultureInfo.InvariantCulture)))})
            """;
        cmd.CommandTimeout = 600;

        var byId = new Dictionary<long, MangaBakaRecommendation>(ids.Count);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            byId[id] = new MangaBakaRecommendation(
                id.ToString(CultureInfo.InvariantCulture),
                GetString(reader, 1) ?? string.Empty,
                GetString(reader, 2),
                GetInt(reader, 3),
                GetString(reader, 7),
                MangaBakaProvider.MapStatus(GetString(reader, 4)),
                reader.IsDBNull(5) ? null : reader.GetDouble(5),
                ParseCount(GetString(reader, 6)),
                [], [], false,
                null, null,
                ThumbUrl: GetString(reader, 8),
                ThumbUrlHiDpi: GetString(reader, 9));
        }

        return ids.Select(byId.GetValueOrDefault).OfType<MangaBakaRecommendation>().ToList();
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

        // Common quality gate: active, real title, has a cover. Every rail also needs a rating
        // (drops the long tail of unscored junk and powers the card's ★ badge). Content rating is
        // bounded only by filters — callers with no per-viewer ceiling (the cached global rails)
        // must pass one explicitly rather than relying on a hardcoded floor here.
        const string baseWhere =
            "state = 'active' AND type != 'novel' " +
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
            SELECT id, {DisplayTitleSql("series")}, cover_raw_url, year, status, rating, total_chapters, description,
                   cover_x250_x1, cover_x250_x2
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
                null, null,
                ThumbUrl: GetString(reader, 8),
                ThumbUrlHiDpi: GetString(reader, 9)));
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
                       source_anilist_id, source_my_anime_list_id, source_manga_updates_id, has_anime,
                       anime_start, anime_end, titles
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

            if (GetString(reader, 9) == "novel")
            {
                return null;
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
        var titles = ParsePrimaryTitles(GetString(reader, 30));

        return new MangaBakaDetail(
            id.ToString(CultureInfo.InvariantCulture),
            titles.EnglishTitle ?? GetString(reader, 3) ?? string.Empty,
            titles.NativeTitle ?? GetString(reader, 4),
            GetString(reader, 5),
            titles.OtherTitles,
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
            malId,
            GetInt(reader, 27) == 1,
            GetString(reader, 28) ?? string.Empty,
            GetString(reader, 29) ?? string.Empty);
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
    /// Drops the tags this series marks as spoilers. The flat <c>tags</c> column carries no
    /// spoiler information — MangaBaka only records it per entry in <c>tags_v2</c>, and it is
    /// genuinely per series, not per tag name: across a 4k-series sample 252 names appear both
    /// ways ("Love Triangle" is a spoiler for 140 series and ordinary for 225), so a global
    /// spoiler word list would both over- and under-hide. Names are matched case-insensitively.
    /// <para>
    /// Subtractive rather than rebuilt from <c>tags_v2</c> so the tag set stays exactly what it
    /// was, minus the spoilers. Series with no <c>tags_v2</c> (~4% of the dump) keep every tag —
    /// there is nothing to tell us which are spoilers — as does the API fallback path, which
    /// never returns the column at all.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> WithoutSpoilerTags(IReadOnlyList<string> tags, string? tagsV2Json)
    {
        if (tags.Count == 0 || string.IsNullOrWhiteSpace(tagsV2Json))
        {
            return tags;
        }

        HashSet<string> spoilers;
        try
        {
            using var doc = JsonDocument.Parse(tagsV2Json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return tags;
            }

            spoilers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object &&
                    element.TryGetProperty("is_spoiler", out var sp) && sp.ValueKind is JsonValueKind.True &&
                    element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    spoilers.Add(name.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
            return tags;
        }

        return spoilers.Count == 0 ? tags : [.. tags.Where(t => !spoilers.Contains(t))];
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
        var tokens = SplitTokens(query);
        if (tokens.Count == 0)
        {
            return null;
        }

        return string.Join(" ", tokens.Select((t, i) => i == tokens.Count - 1 ? $"\"{t}\" *" : $"\"{t}\""));
    }

    /// <summary>
    /// The same expression, with each token widened to the spellings it could have been. Null when
    /// nothing was worth respelling, which is the common case and the signal not to run a second
    /// query.
    ///
    /// <para>
    /// The shape is an AND of per-token ORs:
    /// <c>("bersek" OR "berserk") AND ("saga")</c>. That is not cosmetic. Flattening it into one
    /// large OR returns every title containing any spelling of any token, which on a two-word query
    /// is most of the catalogue in popularity order. If this expression ever looks noisy enough to
    /// tidy up, that is the tidy-up to avoid.
    /// </para>
    ///
    /// <para>
    /// The prefix star stays on the original last token and is never applied to an expansion. A
    /// prefix is already a guess about what the user had not finished typing; putting one on a
    /// corrected spelling compounds two guesses.
    /// </para>
    /// </summary>
    internal static string? BuildFuzzyMatchExpression(
        string query, FuzzyTermIndex terms, FuzzyOptions options, out string? correctedQuery)
    {
        correctedQuery = null;
        var tokens = SplitTokens(query);

        // Past a handful of tokens the query is a sentence, the dense channel is the one answering
        // it, and expanding every word just multiplies branches.
        if (tokens.Count == 0 || tokens.Count > options.MaxTokens)
        {
            return null;
        }

        var groups = new List<string>(tokens.Count);
        var corrected = new List<string>(tokens.Count);
        var expandedAny = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var isLast = i == tokens.Count - 1;
            var branches = new List<string> { $"\"{token}\"" };
            if (isLast)
            {
                branches.Add($"\"{token}\" *");
            }

            var expansions = terms.Expand(token, options);
            foreach (var expansion in expansions)
            {
                branches.Add($"\"{expansion.Term}\"");
            }

            if (expansions.Count > 0)
            {
                expandedAny = true;
            }

            // What to *show* is not what to search for. Every expansion goes into the query, because
            // OR-ing a few extra spellings costs nothing and the ranking sorts it out, but the
            // "showing results for" line is a claim about what the user meant and has to be right.
            //
            // Two rules, both learned from being wrong. A token the index already contains was
            // spelled fine, whatever else was worth OR-ing in: "vinland sga" reported itself as
            // "island sea" while correctly returning Vinland Saga. And a token with several
            // candidates at the same edit distance has no single answer, only a most-common one:
            // "sga" is one edit from both "saga" and "sea", and document frequency picks "sea".
            // When either applies the word is left as typed, so the line understates rather than
            // misleads.
            var confident =
                expansions.Count > 0 &&
                terms.DocFrequency(token) == 0 &&
                expansions.Count(e => e.Distance == expansions[0].Distance) == 1;
            corrected.Add(confident ? expansions[0].Term : token);

            groups.Add($"({string.Join(" OR ", branches)})");
        }

        if (!expandedAny)
        {
            return null;
        }

        // Every rewritten token turned out to be a word the index already knows, so there is nothing
        // to tell the user they mistyped even though the widened query may still find more.
        var respelled = string.Join(" ", corrected);
        correctedQuery = string.Equals(respelled, string.Join(" ", tokens), StringComparison.Ordinal)
            ? null
            : respelled;
        return string.Join(" AND ", groups);
    }

    /// <summary>Query text split the way both match builders need it, with FTS5 quoting stripped.</summary>
    private static List<string> SplitTokens(string query) =>
        query
            .Split(' ', '\t', '\r', '\n')
            .Select(t => t.Replace("\"", string.Empty).Trim())
            .Where(t => t.Length > 0)
            .ToList();

    /// <summary>
    /// <c>titles</c> is JSON: <c>[{"title","note","traits":[],"language","is_primary"}, …]</c>. Only
    /// <c>is_primary</c> entries are kept — the dump also carries non-primary alt spellings that
    /// aren't worth surfacing. The "en" entry becomes the display title, the one tagged "native"
    /// becomes the original-script title, and everything else primary is kept for "show more".
    /// </summary>
    private static (string? EnglishTitle, string? NativeTitle, IReadOnlyList<string> OtherTitles)
        ParsePrimaryTitles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, null, []);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            string? english = null;
            string? native = null;
            var others = new List<string>();

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var title = entry.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var isEnglish = entry.TryGetProperty("language", out var langEl) && string.Equals(langEl.GetString(), "en", StringComparison.OrdinalIgnoreCase);
                var isNative = entry.TryGetProperty("traits", out var traitsEl)
                    && traitsEl.ValueKind == JsonValueKind.Array
                    && traitsEl.EnumerateArray().Any(t => string.Equals(t.GetString(), "native", StringComparison.OrdinalIgnoreCase));
                
                if (!isEnglish && !isNative)
                    continue;
                
                if (entry.TryGetProperty("is_primary", out var primaryEl) && primaryEl.ValueKind == JsonValueKind.True)
                {
                    if (english is null && isEnglish)
                    {
                        english = title;
                    }
                    else if (native is null && isNative)
                    {
                        native = title;
                    }
                }
                else
                {
                    others.Add(title);
                }
            }

            return (english, native, others);
        }
        catch (JsonException)
        {
            return (null, null, []);
        }
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

    /// <summary>
    /// SQL for the display title of <paramref name="alias"/>.series: the primary "en" entry from
    /// its <c>titles</c> JSON when there is one, else the dump's raw <c>title</c> column. Mirrors
    /// <see cref="ParsePrimaryTitles"/> so bulk rails (browse/search/recommendations), which can't
    /// afford to parse JSON in .NET per row of a full-table scan, still show the same title a
    /// single-series fetch would.
    /// </summary>
    internal static string DisplayTitleSql(string alias) => $"""
        COALESCE(
            (SELECT json_extract(je.value, '$.title')
             FROM json_each({alias}.titles) je
             WHERE json_extract(je.value, '$.is_primary') = 1
               AND LOWER(json_extract(je.value, '$.language')) = 'en'
             LIMIT 1),
            {alias}.title)
        """;

    private static string? GetString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
