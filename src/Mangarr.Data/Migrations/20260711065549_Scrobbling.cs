using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mangarr.Data.Migrations
{
    /// <inheritdoc />
    public partial class Scrobbling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScrobbleLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Service = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrobbleLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScrobbleMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    KavitaSeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    Service = table.Column<string>(type: "TEXT", nullable: false),
                    RemoteId = table.Column<string>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrobbleMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScrobbleSyncStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    KavitaSeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    Service = table.Column<string>(type: "TEXT", nullable: false),
                    Chapter = table.Column<int>(type: "INTEGER", nullable: false),
                    Volume = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    SyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrobbleSyncStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScrobbleTokens",
                columns: table => new
                {
                    Service = table.Column<string>(type: "TEXT", nullable: false),
                    AccessToken = table.Column<string>(type: "TEXT", nullable: false),
                    RefreshToken = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Username = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrobbleTokens", x => x.Service);
                });

            migrationBuilder.CreateTable(
                name: "ScrobbleUnmatched",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    KavitaSeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    Service = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    CandidatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrobbleUnmatched", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScrobbleMappings_KavitaSeriesId_Service",
                table: "ScrobbleMappings",
                columns: new[] { "KavitaSeriesId", "Service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrobbleSyncStates_KavitaSeriesId_Service",
                table: "ScrobbleSyncStates",
                columns: new[] { "KavitaSeriesId", "Service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrobbleSyncStates_SyncedAt",
                table: "ScrobbleSyncStates",
                column: "SyncedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ScrobbleUnmatched_KavitaSeriesId_Service",
                table: "ScrobbleUnmatched",
                columns: new[] { "KavitaSeriesId", "Service" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScrobbleLog");

            migrationBuilder.DropTable(
                name: "ScrobbleMappings");

            migrationBuilder.DropTable(
                name: "ScrobbleSyncStates");

            migrationBuilder.DropTable(
                name: "ScrobbleTokens");

            migrationBuilder.DropTable(
                name: "ScrobbleUnmatched");
        }
    }
}
