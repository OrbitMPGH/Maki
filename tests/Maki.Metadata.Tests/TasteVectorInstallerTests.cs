using System.Globalization;
using Maki.Metadata.Taste;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// <see cref="TasteVectorInstaller.ValidateStaged"/> only. It is the last gate before a downloaded
/// file replaces the live one, and two of its checks have no runtime symptom at all: a fold-limited
/// build works and simply scores worse, and a working database full of per-user reading rows would
/// install perfectly happily.
/// </summary>
public class TasteVectorInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "maki-taste-" + Guid.NewGuid().ToString("N"));

    public TasteVectorInstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory we could not clean up is not a test failure.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AcceptsAFullModel()
    {
        var path = Build();
        Assert.Equal(1200, TasteVectorInstaller.ValidateStaged(path));
    }

    [Fact]
    public void RefusesPerUserReadingTables()
    {
        // The trainer's working database holds one row per reader per series and sits in the same
        // folder as the export. Publishing it would be a privacy incident rather than a broken
        // feature, so this check runs FIRST and is not skippable by force.
        var path = Build(extraTable: "CREATE TABLE user_entry (user_id INTEGER, media_id INTEGER)");

        var ex = Assert.Throws<InvalidOperationException>(() => TasteVectorInstaller.ValidateStaged(path));
        Assert.Contains("per-user reading tables", ex.Message);
    }

    [Theory]
    [InlineData("user_state")]
    [InlineData("pending_user")]
    public void RefusesEveryPerUserTableByName(string table)
    {
        var path = Build(extraTable: $"CREATE TABLE {table} (user_id INTEGER)");
        Assert.Throws<InvalidOperationException>(() => TasteVectorInstaller.ValidateStaged(path));
    }

    [Fact]
    public void RefusesAFoldLimitedEvaluationBuild()
    {
        // Built by `build-taste-vectors.cs --fold-out` so the eval can grade it honestly. It is a
        // perfectly valid file that is silently missing a quarter of the readers, and nothing about
        // it would ever look wrong at runtime.
        var path = Build(trainingFold: "1,2,3");

        var ex = Assert.Throws<InvalidOperationException>(() => TasteVectorInstaller.ValidateStaged(path));
        Assert.Contains("fold-limited", ex.Message);
    }

    [Fact]
    public void RefusesAnArtifactThatDeclaresNoDimension()
    {
        var path = Build(dimensions: null);
        Assert.Throws<InvalidOperationException>(() => TasteVectorInstaller.ValidateStaged(path));
    }

    [Fact]
    public void RefusesVectorsOfTheWrongWidth()
    {
        // A vector narrower than the declared dimension would be copied into a row-aligned buffer
        // and read past its own end.
        var path = Build(vectorBytes: 7);
        var ex = Assert.Throws<InvalidOperationException>(() => TasteVectorInstaller.ValidateStaged(path));
        Assert.Contains("bytes wide", ex.Message);
    }

    [Fact]
    public void RefusesANullScale()
    {
        // SQLite has no NaN and stores one as NULL, so a scale that went wrong upstream arrives
        // missing rather than invalid. `scale <= 0` would compare against NULL, yield NULL, and let
        // exactly the rows it is meant to catch through.
        var path = Build(nullScaleRows: 3);
        var ex = Assert.Throws<InvalidOperationException>(() => TasteVectorInstaller.ValidateStaged(path));
        Assert.Contains("no usable scale", ex.Message);
    }

    [Fact]
    public void RefusesAScaleOfZero()
    {
        // Zero is the layer's own "this row has no vector" marker, so a stored zero would make the
        // row silently invisible rather than wrong.
        var path = Build(zeroScaleRows: 2);
        Assert.Throws<InvalidOperationException>(() => TasteVectorInstaller.ValidateStaged(path));
    }

    [Fact]
    public void RefusesATruncatedFile()
    {
        var path = Build(rows: 10);
        var ex = Assert.Throws<InvalidOperationException>(() => TasteVectorInstaller.ValidateStaged(path));
        Assert.Contains("only 10 vectors", ex.Message);
    }

    [Fact]
    public void RefusesAFileWithNoVectorTable()
    {
        var path = Path.Combine(_dir, "empty.db");
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            Execute(conn, "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        }

        var ex = Assert.Throws<InvalidOperationException>(() => TasteVectorInstaller.ValidateStaged(path));
        Assert.Contains("item_vectors", ex.Message);
    }

    private string Build(
        int rows = 1200, int? dimensions = 8, string trainingFold = "all", string? extraTable = null,
        int vectorBytes = 8, int nullScaleRows = 0, int zeroScaleRows = 0)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        Execute(conn, "CREATE TABLE item_vectors (id INTEGER PRIMARY KEY, scale REAL, vec BLOB NOT NULL)");
        Execute(conn, "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        if (extraTable is not null)
        {
            Execute(conn, extraTable);
        }

        using (var tx = conn.BeginTransaction())
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO item_vectors (id, scale, vec) VALUES ($i, $s, $v)";
            var pi = cmd.Parameters.Add("$i", SqliteType.Integer);
            var ps = cmd.Parameters.Add("$s", SqliteType.Real);
            var pv = cmd.Parameters.Add("$v", SqliteType.Blob);
            for (var i = 0; i < rows; i++)
            {
                pi.Value = i + 1;
                ps.Value = i < nullScaleRows
                    ? DBNull.Value
                    : i < nullScaleRows + zeroScaleRows ? 0.0 : 0.01;
                pv.Value = new byte[vectorBytes];
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        using (var meta = conn.CreateCommand())
        {
            meta.CommandText = "INSERT INTO meta (key, value) VALUES ('trainingFold', $f)";
            meta.Parameters.AddWithValue("$f", trainingFold);
            meta.ExecuteNonQuery();
        }

        if (dimensions is { } dims)
        {
            using var meta = conn.CreateCommand();
            meta.CommandText = "INSERT INTO meta (key, value) VALUES ('dimensions', $d)";
            meta.Parameters.AddWithValue("$d", dims.ToString(CultureInfo.InvariantCulture));
            meta.ExecuteNonQuery();
        }

        return path;
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
