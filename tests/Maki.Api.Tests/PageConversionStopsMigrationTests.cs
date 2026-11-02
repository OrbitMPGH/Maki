using Maki.Core.Entities;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

/// <summary>The page conversion quality numbers mapped onto stops, run as a real migration.</summary>
public class PageConversionStopsMigrationTests : IDisposable
{
    private const string PreStops = "20261025092942_HealthOperationDismissedAt";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MakiDbContext> _options;

    public PageConversionStopsMigrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(_connection).Options;
    }

    public void Dispose() => _connection.Dispose();

    private Dictionary<string, string> Migrate(params (string Key, string Value)[] rows)
    {
        using (var db = new MakiDbContext(_options))
        {
            db.Database.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(PreStops);
        }

        foreach (var (key, value) in rows)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """INSERT INTO "AppConfig" ("Key", "Value") VALUES ($key, $value)""";
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }

        using (var db = new MakiDbContext(_options))
        {
            db.Database.Migrate();
        }

        using var read = _connection.CreateCommand();
        read.CommandText = """SELECT "Key", "Value" FROM "AppConfig" WHERE "Key" LIKE 'library.pageconversion.%'""";
        using var reader = read.ExecuteReader();
        var values = new Dictionary<string, string>();
        while (reader.Read())
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    [Theory]
    [InlineData("webp", "80", "balanced")]
    [InlineData("webp", "60", "compact")]
    [InlineData("webp", "59", "smallest")]
    [InlineData("webp", "85", "high")]
    [InlineData("avif", "55", "compact")]
    [InlineData("avif", "54", "smallest")]
    [InlineData("avif", "73", "high")]
    public void The_active_formats_quality_becomes_its_nearest_stop(string format, string quality, string stop)
    {
        var values = Migrate(
            ("library.pageconversion.format", format),
            ("library.pageconversion.webpquality", format == "webp" ? quality : "20"),
            ("library.pageconversion.avifquality", format == "avif" ? quality : "20"));

        Assert.Equal(stop, values["library.pageconversion.stop"]);
        Assert.Equal("true", values["library.pageconversion.pnglossless"]);
        Assert.DoesNotContain("library.pageconversion.webpquality", values.Keys);
        Assert.DoesNotContain("library.pageconversion.avifquality", values.Keys);
    }

    [Theory]
    [InlineData("webp", "balanced")]
    [InlineData("avif", "compact")]
    public void A_missing_quality_reads_as_the_old_default(string format, string stop)
    {
        var values = Migrate(("library.pageconversion.format", format));

        Assert.Equal(stop, values["library.pageconversion.stop"]);
    }

    [Fact]
    public void Conversion_off_gets_no_stop_and_loses_its_quality_rows()
    {
        var values = Migrate(
            ("library.pageconversion.webpquality", "90"),
            ("library.pageconversion.avifquality", "60"),
            ("library.pageconversion.pass", "paused"));

        Assert.Equal(["library.pageconversion.pass"], values.Keys);
    }

    /// <summary>
    /// Inserts a row into the schema as it is at this point of the migrations, filling every other
    /// required column with an empty value, since the current model has columns that schema lacks.
    /// </summary>
    private long Insert(string table, Dictionary<string, object> values)
    {
        var row = new Dictionary<string, object>(values);
        using (var info = _connection.CreateCommand())
        {
            info.CommandText = $"""PRAGMA table_info("{table}")""";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                var (name, type, notNull, hasDefault, key) =
                    (reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1, !reader.IsDBNull(4), reader.GetInt32(5) > 0);
                if (notNull && !hasDefault && !key && !row.ContainsKey(name))
                {
                    row[name] = type == "TEXT" ? "" : 0;
                }
            }
        }

        using var insert = _connection.CreateCommand();
        var names = row.Keys.ToList();
        insert.CommandText = $"""INSERT INTO "{table}" ({string.Join(", ", names.Select(n => $"\"{n}\""))}) VALUES ({string.Join(", ", names.Select((_, i) => $"$p{i}"))}); SELECT last_insert_rowid();""";
        for (var i = 0; i < names.Count; i++)
        {
            insert.Parameters.AddWithValue($"$p{i}", row[names[i]]);
        }

        return (long)insert.ExecuteScalar()!;
    }

    [Fact]
    public void Checked_files_stay_checked()
    {
        var checkedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var db = new MakiDbContext(_options))
        {
            db.Database.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(PreStops);
        }

        Insert("AppConfig", new() { ["Key"] = "library.pageconversion.format", ["Value"] = "webp" });
        var root = Insert("RootFolders", new() { ["Path"] = "/manga" });
        var series = Insert("Series", new()
        {
            ["Title"] = "One", ["SortTitle"] = "one", ["FolderName"] = "One", ["RootFolderId"] = root
        });
        Insert("ChapterFiles", new()
        {
            ["SeriesId"] = series, ["RelativePath"] = "One/1.cbz", ["SourceName"] = "x", ["DateAdded"] = "2026-01-01 00:00:00",
            ["ConversionCheckedAtUtc"] = "2026-02-01 00:00:00"
        });

        using (var db = new MakiDbContext(_options))
        {
            db.Database.Migrate();
            Assert.Equal(checkedAt, db.ChapterFiles.IgnoreQueryFilters().Single().ConversionCheckedAtUtc);
        }
    }
}
