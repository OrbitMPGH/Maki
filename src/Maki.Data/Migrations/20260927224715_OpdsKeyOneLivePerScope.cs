using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class OpdsKeyOneLivePerScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Two OPDS rotations racing each other (SettingsController.MintOpdsKeyAsync, before it
            // wrapped revoke+insert in one transaction) could both read "no live key yet" and both
            // insert, leaving two live rows for the same user. Keep the newest (highest Id) live one
            // and revoke the rest, the same way a normal rotation would have. Not deleted, so the
            // audit trail stays intact.
            migrationBuilder.Sql("""
                UPDATE UserApiKeys
                SET RevokedAt = CURRENT_TIMESTAMP
                WHERE Scope = 1
                  AND RevokedAt IS NULL
                  AND Id NOT IN (
                    SELECT MAX(Id) FROM UserApiKeys
                    WHERE Scope = 1 AND RevokedAt IS NULL
                    GROUP BY UserId
                  );
                """);

            // Scoped to Scope = 1 (Opds) rather than every scope: a plain API key (Scope = 0, Full)
            // is created through AccountController.CreateApiKey, which deliberately allows several
            // live named keys per user (one per script/integration). Only the OPDS token is ever
            // revoked-and-reissued as a single live slot, so that is the only scope this index needs
            // to constrain.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IX_UserApiKeys_Opds_Live_UserId
                ON UserApiKeys (UserId, Scope)
                WHERE RevokedAt IS NULL AND Scope = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_UserApiKeys_Opds_Live_UserId;");
        }
    }
}
