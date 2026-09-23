using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class PurgeJobHealthHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "HealthHistory"
                WHERE "Kind" = 'job'
                  AND ("MessageKey" = 'health.check.jobSucceeded'
                       OR ("MessageKey" IS NULL AND "Message" LIKE '%last run succeeded'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
