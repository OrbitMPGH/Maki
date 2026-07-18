using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStatsEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReadingStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    KavitaSeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    MaxChapter = table.Column<double>(type: "REAL", nullable: false),
                    MaxVolume = table.Column<double>(type: "REAL", nullable: false),
                    Finished = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastProgressAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadingStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReadingStates_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "StatsEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    KavitaSeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    SeriesTitle = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatsEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatsEvents_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReadingStates_KavitaSeriesId",
                table: "ReadingStates",
                column: "KavitaSeriesId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReadingStates_SeriesId",
                table: "ReadingStates",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_StatsEvents_SeriesId",
                table: "StatsEvents",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_StatsEvents_Type_Timestamp",
                table: "StatsEvents",
                columns: new[] { "Type", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReadingStates");

            migrationBuilder.DropTable(
                name: "StatsEvents");
        }
    }
}
