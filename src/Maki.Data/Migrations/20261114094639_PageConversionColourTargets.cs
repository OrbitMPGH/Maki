using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class PageConversionColourTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ColourFormat",
                table: "PageConversionRules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ColourStop",
                table: "PageConversionRules",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Format",
                table: "PageConversionRules",
                type: "INTEGER",
                nullable: true);

            // A rule used to convert in the plan's format. Writing that format onto each convert rule keeps
            // it converting the same way after the default format changes. PageConversionFormat: Webp 0, Avif 1.
            migrationBuilder.Sql("""
                UPDATE "PageConversionRules"
                SET "Format" = CASE (SELECT LOWER(TRIM("Value")) FROM "AppConfig" WHERE "Key" = 'library.pageconversion.format')
                    WHEN 'avif' THEN 1 WHEN 'webp' THEN 0 ELSE NULL END
                WHERE "Skip" = 0 AND "Stop" IS NOT NULL AND "Stop" <> 4;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ColourFormat",
                table: "PageConversionRules");

            migrationBuilder.DropColumn(
                name: "ColourStop",
                table: "PageConversionRules");

            migrationBuilder.DropColumn(
                name: "Format",
                table: "PageConversionRules");
        }
    }
}
