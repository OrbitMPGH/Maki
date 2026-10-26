using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class PageConversionRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PageConversion",
                table: "Series",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CheckedWith",
                table: "ChapterFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PageConversionRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ConditionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Skip = table.Column<bool>(type: "INTEGER", nullable: false),
                    Stop = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PageConversionRules", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PageConversionRules");

            migrationBuilder.DropColumn(
                name: "PageConversion",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "CheckedWith",
                table: "ChapterFiles");
        }
    }
}
