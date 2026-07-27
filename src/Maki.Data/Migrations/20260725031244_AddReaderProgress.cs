using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <summary>
    /// Makes ReadingState.KavitaSeriesId nullable so the built-in reader can own a row for a
    /// series Kavita has never reported, and adds the reader's per-chapter progress table.
    /// <para>
    /// IX_ReadingStates_NativeSeries is unique only over native rows on purpose. Do NOT
    /// "simplify" it into a plain unique index on SeriesId: two Kavita series can resolve to
    /// one local series (the scrobble library index is keyed by both title and folder name),
    /// so existing databases already contain duplicates and the index would throw inside
    /// Database.Migrate() at startup — before Kestrel binds, with no recovery path.
    /// </para>
    /// </summary>
    public partial class AddReaderProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "KavitaSeriesId",
                table: "ReadingStates",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.CreateTable(
                name: "ChapterProgress",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChapterId = table.Column<int>(type: "INTEGER", nullable: false),
                    PageIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChapterProgress", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChapterProgress_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChapterProgress_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SeriesScrobbleStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    Service = table.Column<string>(type: "TEXT", nullable: false),
                    Chapter = table.Column<int>(type: "INTEGER", nullable: false),
                    Volume = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    SyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesScrobbleStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SeriesScrobbleStates_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReadingStates_NativeSeries",
                table: "ReadingStates",
                column: "SeriesId",
                unique: true,
                filter: "\"SeriesId\" IS NOT NULL AND \"KavitaSeriesId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChapterProgress_ChapterId",
                table: "ChapterProgress",
                column: "ChapterId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChapterProgress_SeriesId",
                table: "ChapterProgress",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_ChapterProgress_UpdatedAt",
                table: "ChapterProgress",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesScrobbleStates_SeriesId_Service",
                table: "SeriesScrobbleStates",
                columns: new[] { "SeriesId", "Service" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChapterProgress");

            migrationBuilder.DropTable(
                name: "SeriesScrobbleStates");

            migrationBuilder.DropIndex(
                name: "IX_ReadingStates_NativeSeries",
                table: "ReadingStates");

            migrationBuilder.AlterColumn<int>(
                name: "KavitaSeriesId",
                table: "ReadingStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
