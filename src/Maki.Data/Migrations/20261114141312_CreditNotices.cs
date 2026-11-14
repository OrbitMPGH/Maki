using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class CreditNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "TailOffset",
                table: "ChapterCredits",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateTable(
                name: "CreditNotices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Whole = table.Column<bool>(type: "INTEGER", nullable: false),
                    Share = table.Column<double>(type: "REAL", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    Rows = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Fingerprint = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Accepted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Reads = table.Column<int>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    OcrVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    TextVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    GroupSource = table.Column<int>(type: "INTEGER", nullable: false),
                    SuggestedName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Invite = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    NameVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Override = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditNotices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChapterCreditNotices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChapterId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    Tail = table.Column<bool>(type: "INTEGER", nullable: false),
                    Page = table.Column<int>(type: "INTEGER", nullable: false),
                    Share = table.Column<int>(type: "INTEGER", nullable: false),
                    NoticeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Accepted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChapterCreditNotices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChapterCreditNotices_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChapterCreditNotices_CreditNotices_NoticeId",
                        column: x => x.NoticeId,
                        principalTable: "CreditNotices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChapterCreditNotices_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CreditNoticeReads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Crc = table.Column<uint>(type: "INTEGER", nullable: false),
                    Length = table.Column<long>(type: "INTEGER", nullable: false),
                    Share = table.Column<int>(type: "INTEGER", nullable: false),
                    Bottom = table.Column<bool>(type: "INTEGER", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Colour = table.Column<bool>(type: "INTEGER", nullable: false),
                    NoticeId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditNoticeReads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CreditNoticeReads_CreditNotices_NoticeId",
                        column: x => x.NoticeId,
                        principalTable: "CreditNotices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChapterCreditNotices_ChapterId",
                table: "ChapterCreditNotices",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_ChapterCreditNotices_NoticeId",
                table: "ChapterCreditNotices",
                column: "NoticeId");

            migrationBuilder.CreateIndex(
                name: "IX_ChapterCreditNotices_SeriesId",
                table: "ChapterCreditNotices",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditNoticeReads_Crc_Length_Share_Bottom",
                table: "CreditNoticeReads",
                columns: new[] { "Crc", "Length", "Share", "Bottom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditNoticeReads_NoticeId",
                table: "CreditNoticeReads",
                column: "NoticeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChapterCreditNotices");

            migrationBuilder.DropTable(
                name: "CreditNoticeReads");

            migrationBuilder.DropTable(
                name: "CreditNotices");

            migrationBuilder.DropColumn(
                name: "TailOffset",
                table: "ChapterCredits");
        }
    }
}
