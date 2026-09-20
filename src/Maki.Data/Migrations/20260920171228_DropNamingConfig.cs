using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropNamingConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NamingConfigs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NamingConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChapterFileFormat = table.Column<string>(type: "TEXT", nullable: false),
                    ChapterFileFormatWithVolume = table.Column<string>(type: "TEXT", nullable: false),
                    RenameChapters = table.Column<bool>(type: "INTEGER", nullable: false),
                    SeriesFolderFormat = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NamingConfigs", x => x.Id);
                });
        }
    }
}
