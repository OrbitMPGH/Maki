using Maki.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

public class RecommendationFeedbackMigrationTests
{
    [Fact]
    public void Populated_pre_feature_database_migrates_without_attributing_old_activity()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<MakiDbContext>().UseSqlite(connection).Options;
        using (var before = new MakiDbContext(options))
        {
            before.Database.GetInfrastructure().GetRequiredService<IMigrator>()
                .Migrate("20260915094925_AddSeriesAltTitleLanguages");
            before.Database.ExecuteSqlRaw(
                "INSERT INTO AppConfig (Key, Value) VALUES ('feedback-migration-fixture', 'preserve-me')");
        }

        using (var after = new MakiDbContext(options))
        {
            after.Database.Migrate();
            Assert.Equal("preserve-me", after.AppConfig.AsNoTracking()
                .Single(x => x.Key == "feedback-migration-fixture").Value);
            Assert.Empty(after.RecommendationFeedback);
            Assert.Empty(after.RecommendationFeedbackEvents);
            Assert.Empty(after.RecommendationSignalOverrides);
            Assert.Empty(after.RecommendationMutationReceipts);
            Assert.Empty(after.UserSeriesStates.Where(x => x.AddedToLibraryAtUtc != null));
        }
    }
}
