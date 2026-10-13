using Maki.Metadata.ReaderCohorts;
using Maki.Metadata.Taste;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Maki.Metadata.Tests;

/// <summary>
/// The per-user-table guard shared by the reading-list artifacts. The cohorts and the behavioural
/// vectors are both derived from a working database with one row per reader per series read; a
/// mispublish of it is a privacy incident, so the guard matches any table named for a user rather
/// than a fixed list.
/// </summary>
public class ReaderCohortInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "maki-cohort-guard-" + Guid.NewGuid().ToString("N"));

    public ReaderCohortInstallerTests() => Directory.CreateDirectory(_dir);

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

    [Theory]
    [InlineData("user_entry")]
    [InlineData("user_state")]
    [InlineData("pending_user")]
    [InlineData("user_anything_nobody_listed")]
    public void The_cohort_installer_refuses_any_per_user_table(string table)
    {
        var path = WithTable(table);

        var ex = Assert.Throws<InvalidOperationException>(
            () => ReaderCohortInstaller.ValidateStaged(path, new ReaderCohortManifest()));
        Assert.Contains("per-user reading tables", ex.Message);
    }

    [Fact]
    public void The_taste_installer_refuses_a_user_prefixed_table_it_has_no_name_for()
    {
        var path = WithTable("user_anything_nobody_listed");

        var ex = Assert.Throws<InvalidOperationException>(() => TasteVectorInstaller.ValidateStaged(path));
        Assert.Contains("per-user reading tables", ex.Message);
    }

    private string WithTable(string table)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db");
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE TABLE {table} (user_id INTEGER); CREATE TABLE meta (key TEXT, value TEXT)";
        cmd.ExecuteNonQuery();
        return path;
    }
}
