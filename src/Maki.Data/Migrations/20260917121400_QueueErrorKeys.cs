using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class QueueErrorKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ErrorKey",
                table: "DownloadQueue",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorParamsJson",
                table: "DownloadQueue",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ErrorKey",
                table: "DownloadQueue");

            migrationBuilder.DropColumn(
                name: "ErrorParamsJson",
                table: "DownloadQueue");
        }
    }
}
