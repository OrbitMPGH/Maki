using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HealthOperationId",
                table: "DownloadQueue",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HealthChecks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    CheckedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    NotifiedStatus = table.Column<string>(type: "TEXT", nullable: false),
                    Acknowledged = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthChecks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HealthFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RootFolderId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    ChapterFileId = table.Column<int>(type: "INTEGER", nullable: true),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: true),
                    AnalyzerVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    AnalysisJson = table.Column<string>(type: "TEXT", nullable: false),
                    AnalyzedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Removed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HealthFindings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthFindings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HealthHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    FileId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HealthOperations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    FileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false),
                    SourceMappingId = table.Column<int>(type: "INTEGER", nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    JournalJson = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HealthScans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    RootFolderId = table.Column<int>(type: "INTEGER", nullable: true),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    FileIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Force = table.Column<bool>(type: "INTEGER", nullable: false),
                    Completed = table.Column<int>(type: "INTEGER", nullable: false),
                    Total = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthScans", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HealthFiles_ContentHash",
                table: "HealthFiles",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_HealthFiles_RootFolderId_RelativePath",
                table: "HealthFiles",
                columns: new[] { "RootFolderId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HealthFindings_FileId_Version_Kind",
                table: "HealthFindings",
                columns: new[] { "FileId", "Version", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HealthHistory_CreatedAt",
                table: "HealthHistory",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_HealthOperations_FileId_Status",
                table: "HealthOperations",
                columns: new[] { "FileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthScans_Status",
                table: "HealthScans",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HealthChecks");

            migrationBuilder.DropTable(
                name: "HealthFiles");

            migrationBuilder.DropTable(
                name: "HealthFindings");

            migrationBuilder.DropTable(
                name: "HealthHistory");

            migrationBuilder.DropTable(
                name: "HealthOperations");

            migrationBuilder.DropTable(
                name: "HealthScans");

            migrationBuilder.DropColumn(
                name: "HealthOperationId",
                table: "DownloadQueue");
        }
    }
}
