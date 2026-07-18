using Microsoft.Data.Sqlite;

namespace Maki.Metadata.Tests;

/// <summary>
/// Builds a temp SQLite file with the subset of the MangaBaka dump schema that
/// Maki queries (the real dump has ~130 columns; tests only need these).
/// </summary>
public sealed class DumpDbBuilder : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"maki-test-{Guid.NewGuid():N}.db");

    public DumpDbBuilder()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE series (
                id INTEGER PRIMARY KEY,
                state TEXT,
                merged_with TEXT,
                title TEXT,
                native_title TEXT,
                romanized_title TEXT,
                titles TEXT,
                description TEXT,
                year INTEGER,
                status TEXT,
                final_volume TEXT,
                total_chapters TEXT,
                authors TEXT,
                artists TEXT,
                genres TEXT,
                tags TEXT,
                cover_raw_url TEXT,
                source_anilist_id INTEGER,
                source_my_anime_list_id INTEGER,
                source_manga_updates_id TEXT,
                popularity_global_current INTEGER
            )
            """;
        cmd.ExecuteNonQuery();
    }

    public DumpDbBuilder AddSeries(
        long id,
        string title,
        string state = "active",
        string? mergedWith = null,
        string? nativeTitle = null,
        string? romanizedTitle = null,
        string? titlesJson = null,
        string? description = null,
        int? year = null,
        string? status = null,
        string? finalVolume = null,
        string? totalChapters = null,
        string? authorsJson = null,
        string? artistsJson = null,
        string? genresJson = null,
        string? tagsJson = null,
        string? coverUrl = null,
        int? aniListId = null,
        int? malId = null,
        string? mangaUpdatesId = null,
        int? popularity = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO series (id, state, merged_with, title, native_title, romanized_title, titles,
                description, year, status, final_volume, total_chapters, authors, artists, genres, tags,
                cover_raw_url, source_anilist_id, source_my_anime_list_id, source_manga_updates_id,
                popularity_global_current)
            VALUES ($id, $state, $mergedWith, $title, $nativeTitle, $romanizedTitle, $titles,
                $description, $year, $status, $finalVolume, $totalChapters, $authors, $artists, $genres, $tags,
                $coverUrl, $aniListId, $malId, $mangaUpdatesId, $popularity)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$mergedWith", (object?)mergedWith ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$nativeTitle", (object?)nativeTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$romanizedTitle", (object?)romanizedTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$titles", (object?)titlesJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$description", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$year", (object?)year ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$finalVolume", (object?)finalVolume ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$totalChapters", (object?)totalChapters ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$authors", (object?)authorsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$artists", (object?)artistsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$genres", (object?)genresJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tags", (object?)tagsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$coverUrl", (object?)coverUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$aniListId", (object?)aniListId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$malId", (object?)malId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mangaUpdatesId", (object?)mangaUpdatesId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$popularity", (object?)popularity ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return this;
    }

    /// <summary>Bulk rows to satisfy the dump service's minimum-row-count sanity check.</summary>
    public DumpDbBuilder AddFillerSeries(int count, long startId = 100_000)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO series (id, state, title) VALUES ($id, 'active', $title)";
        var id = cmd.CreateParameter();
        id.ParameterName = "$id";
        cmd.Parameters.Add(id);
        var title = cmd.CreateParameter();
        title.ParameterName = "$title";
        cmd.Parameters.Add(title);
        for (var i = 0; i < count; i++)
        {
            id.Value = startId + i;
            title.Value = $"Filler Series {i}";
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return this;
    }

    /// <summary>Builds the same FTS5 index the dump service creates after a download.</summary>
    public DumpDbBuilder BuildSearchIndex()
    {
        using var conn = Open();
        MangaBaka.MangaBakaDumpService.BuildSearchIndex(conn);
        return this;
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={Path};Pooling=False");
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
        }
    }
}
