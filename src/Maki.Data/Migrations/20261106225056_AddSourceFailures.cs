using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceFailures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceMappingId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeriesId = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorKey = table.Column<string>(type: "TEXT", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceFailures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SourceFailures_SourceMappings_SourceMappingId",
                        column: x => x.SourceMappingId,
                        principalTable: "SourceMappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceFailures_OccurredAtUtc",
                table: "SourceFailures",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SourceFailures_SourceMappingId_OccurredAtUtc",
                table: "SourceFailures",
                columns: new[] { "SourceMappingId", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceFailures");
        }
    }
}
