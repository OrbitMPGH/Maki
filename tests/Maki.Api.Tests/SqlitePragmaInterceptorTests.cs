using Maki.Core.Entities;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

/// <summary>
/// The per-connection pragmas <see cref="SqlitePragmaInterceptor"/> sets, and the queue index whose
/// filter only works if it repeats the active-queue query's WHERE terms verbatim. Both need a
/// connection EF opens itself, which <see cref="TestDb"/>'s pre-opened in-memory one never is.
/// </summary>
public sealed class SqlitePragmaInterceptorTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"maki-pragmas-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch (IOException)
        {
        }
    }

    private MakiDbContext NewContext() => new(new DbContextOptionsBuilder<MakiDbContext>()
        .UseSqlite($"Data Source={_dbPath};Pooling=False")
        .Options);

    private static long Pragma(MakiDbContext db, string name)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_connection_ef_opens_gets_the_pragmas(bool async)
    {
        using var db = NewContext();
        if (async)
            await db.Database.OpenConnectionAsync();
        else
            db.Database.OpenConnection();

        Assert.Equal(1, Pragma(db, "synchronous"));
        Assert.Equal(5000, Pragma(db, "busy_timeout"));
        Assert.Equal(2, Pragma(db, "temp_store"));
    }

    [Fact]
    public void A_pooled_handle_is_configured_once_rather_than_on_every_open()
    {
        var options = new DbContextOptionsBuilder<MakiDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        try
        {
            object? first;
            using (var db = new MakiDbContext(options))
            {
                db.Database.OpenConnection();
                var connection = (SqliteConnection)db.Database.GetDbConnection();
                first = connection.Handle;
                Assert.Equal(1, Pragma(db, "synchronous"));

                // Changed by hand on this handle. A second pass of the interceptor would put it back.
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA synchronous=FULL;";
                command.ExecuteNonQuery();
            }

            using (var db = new MakiDbContext(options))
            {
                db.Database.OpenConnection();
                Assert.Same(first, ((SqliteConnection)db.Database.GetDbConnection()).Handle);
                Assert.Equal(2, Pragma(db, "synchronous"));
                Assert.Equal(5000, Pragma(db, "busy_timeout"));
            }
        }
        finally
        {
            using var pooled = new SqliteConnection($"Data Source={_dbPath}");
            SqliteConnection.ClearPool(pooled);
        }
    }

    [Fact]
    public void The_active_queue_page_uses_the_partial_index()
    {
        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Database.OpenConnection();

        // The same shape as QueueController.List, minus the includes, which only add joins.
        var query = db.DownloadQueue
            .Where(q => q.Status != QueueStatus.Completed && q.Status != QueueStatus.Cancelled)
            .OrderBy(q => q.SortOrder)
            .ThenBy(q => q.QueuedAt)
            .Take(200);

        using var command = db.Database.GetDbConnection().CreateCommand();
        var sql = new List<string>();
        foreach (var line in query.ToQueryString().Split('\n'))
        {
            // ToQueryString renders parameters as sqlite3 shell ".param set @name value" lines.
            if (line.StartsWith(".param set ", StringComparison.Ordinal))
            {
                var parts = line.TrimEnd('\r').Split(' ', 4);
                command.Parameters.Add(new SqliteParameter(parts[2], parts[3].Trim('\'')));
            }
            else
            {
                sql.Add(line);
            }
        }
        command.CommandText = "EXPLAIN QUERY PLAN " + string.Join('\n', sql);

        var plan = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                plan.Add(reader.GetString(3));
        }

        Assert.True(plan.Any(p => p.Contains("IX_DownloadQueue_SortOrder_QueuedAt", StringComparison.Ordinal)),
            string.Join(" | ", plan));
    }
}
