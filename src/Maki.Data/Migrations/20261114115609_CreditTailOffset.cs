using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreditTailOffset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CreditNoticeReads_Crc_Length_Share",
                table: "CreditNoticeReads");

            migrationBuilder.AddColumn<bool>(
                name: "Bottom",
                table: "CreditNoticeReads",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "TailOffset",
                table: "ChapterCredits",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateIndex(
                name: "IX_CreditNoticeReads_Crc_Length_Share_Bottom",
                table: "CreditNoticeReads",
                columns: new[] { "Crc", "Length", "Share", "Bottom" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CreditNoticeReads_Crc_Length_Share_Bottom",
                table: "CreditNoticeReads");

            migrationBuilder.DropColumn(
                name: "Bottom",
                table: "CreditNoticeReads");

            migrationBuilder.DropColumn(
                name: "TailOffset",
                table: "ChapterCredits");

            migrationBuilder.CreateIndex(
                name: "IX_CreditNoticeReads_Crc_Length_Share",
                table: "CreditNoticeReads",
                columns: new[] { "Crc", "Length", "Share" },
                unique: true);
        }
    }
}
