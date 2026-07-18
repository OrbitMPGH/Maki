using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class TorrentQueueSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "ChapterId",
                table: "DownloadQueue",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "SeriesId",
                table: "DownloadQueue",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "DownloadQueue",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadQueue_SeriesId",
                table: "DownloadQueue",
                column: "SeriesId");

            migrationBuilder.AddForeignKey(
                name: "FK_DownloadQueue_Series_SeriesId",
                table: "DownloadQueue",
                column: "SeriesId",
                principalTable: "Series",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // Backfill: pre-existing rows were all chapter-level scraper items.
            migrationBuilder.Sql(
                "UPDATE DownloadQueue SET SeriesId = (SELECT SeriesId FROM Chapters WHERE Chapters.Id = DownloadQueue.ChapterId) WHERE ChapterId IS NOT NULL;");
            migrationBuilder.Sql(
                "DELETE FROM DownloadQueue WHERE SeriesId = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DownloadQueue_Series_SeriesId",
                table: "DownloadQueue");

            migrationBuilder.DropIndex(
                name: "IX_DownloadQueue_SeriesId",
                table: "DownloadQueue");

            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "DownloadQueue");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "DownloadQueue");

            migrationBuilder.AlterColumn<int>(
                name: "ChapterId",
                table: "DownloadQueue",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
