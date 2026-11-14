using Maki.Core.Images;
using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

/// <summary>
/// The <c>PageConversionColourTargets</c> migration: a convert rule takes the plan's format, so it
/// keeps converting the same way once rules carry their own. Skip and Lossless rules get none.
/// </summary>
public class PageConversionColourTargetsMigrationTests
{
    private const string PreMigration = "20261114051403_ChapterFileNamedByMaki";

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Theory]
    [InlineData(" AVIF ", PageConversionFormat.Avif)]
    [InlineData("webp", PageConversionFormat.Webp)]
    [InlineData(null, PageConversionFormat.Webp)]
    public void Convert_rules_take_the_plans_format(string? planFormat, PageConversionFormat expected)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(connection).Options;
        using (var before = new MakiDbContext(options))
        {
            before.Database.GetInfrastructure().GetRequiredService<IMigrator>().Migrate(PreMigration);
        }

        if (planFormat is not null)
        {
            Exec(connection, $"""INSERT INTO AppConfig ("Key", "Value") VALUES ('library.pageconversion.format', '{planFormat}')""");
        }

        Exec(connection, """
            INSERT INTO PageConversionRules ("Order", Name, ConditionsJson, Skip, Stop) VALUES
                (0, 'Converts', '{}', 0, 1),
                (1, 'Skips', '{}', 1, NULL),
                (2, 'Lossless', '{}', 0, 4)
            """);

        using var after = new MakiDbContext(options);
        after.Database.Migrate();

        var rules = after.PageConversionRules.AsNoTracking().OrderBy(r => r.Order).ToList();
        Assert.Equal(expected, rules[0].Format);
        Assert.Null(rules[1].Format);
        Assert.Null(rules[2].Format);
        Assert.All(rules, r => Assert.Equal((null, null), (r.ColourFormat, r.ColourStop)));
    }
}
