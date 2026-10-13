using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportIgnoreAndBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportBatchFolders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BatchId = table.Column<string>(type: "TEXT", nullable: false),
                    RootFolderId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: true),
                    SeriesTitle = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedSeries = table.Column<bool>(type: "INTEGER", nullable: false),
                    OriginalFolderName = table.Column<string>(type: "TEXT", nullable: false),
                    FolderName = table.Column<string>(type: "TEXT", nullable: false),
                    OperationsJson = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UndoneAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportBatchFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportBatchFolders_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ImportBatchFolders_RootFolders_RootFolderId",
                        column: x => x.RootFolderId,
                        principalTable: "RootFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImportIgnoredFolders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RootFolderId = table.Column<int>(type: "INTEGER", nullable: false),
                    FolderName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportIgnoredFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportIgnoredFolders_RootFolders_RootFolderId",
                        column: x => x.RootFolderId,
                        principalTable: "RootFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatchFolders_BatchId",
                table: "ImportBatchFolders",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatchFolders_CreatedAt",
                table: "ImportBatchFolders",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatchFolders_RootFolderId",
                table: "ImportBatchFolders",
                column: "RootFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatchFolders_UserId",
                table: "ImportBatchFolders",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportIgnoredFolders_RootFolderId_FolderName",
                table: "ImportIgnoredFolders",
                columns: new[] { "RootFolderId", "FolderName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportBatchFolders");

            migrationBuilder.DropTable(
                name: "ImportIgnoredFolders");
        }
    }
}
