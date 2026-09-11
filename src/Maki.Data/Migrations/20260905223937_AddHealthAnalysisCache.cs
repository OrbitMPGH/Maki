using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthAnalysisCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HealthAnalyses",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    AnalyzerVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    AnalysisJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthAnalyses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HealthFileVersions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AnalyzerVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthFileVersions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HealthFileVersions_FileId",
                table: "HealthFileVersions",
                column: "FileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HealthAnalyses");

            migrationBuilder.DropTable(
                name: "HealthFileVersions");
        }
    }
}
