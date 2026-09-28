using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomRails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A pinned Discover preset was drawn as a catalogue rail. Custom rails replace that, so
            // each pinned preset becomes one, owned titles kept in as the preset rail showed them.
            // The preset itself stays.
            migrationBuilder.Sql("""
                INSERT INTO SavedFilters (UserId, Name, Spec, SortOrder, Created, Scope, Pinned)
                SELECT UserId, Name,
                       json_object(
                           'source', 'catalogue',
                           'filters', json(CASE WHEN json_valid(Spec) THEN Spec ELSE '{}' END),
                           'sort', 'popular',
                           'excludeOwned', json('false')),
                       SortOrder, Created, 'discoverrail', 0
                FROM SavedFilters
                WHERE Scope = 'discover' AND Pinned = 1;
                """);

            migrationBuilder.DropColumn(
                name: "Pinned",
                table: "SavedFilters");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Pinned",
                table: "SavedFilters",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("DELETE FROM SavedFilters WHERE Scope IN ('homerail', 'discoverrail');");
        }
    }
}
