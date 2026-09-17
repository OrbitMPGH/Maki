using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Maki.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecommendationFeedbackLab : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddedFrom",
                table: "UserSeriesStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AddedToLibraryAtUtc",
                table: "UserSeriesStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovalClaimedAtUtc",
                table: "SeriesRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RecommendationFeedback",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Suppression = table.Column<int>(type: "INTEGER", nullable: false),
                    DismissedUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Exposure = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationFeedback", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecommendationFeedback_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecommendationFeedbackEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderId = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    PreviousState = table.Column<string>(type: "TEXT", nullable: false),
                    NewState = table.Column<string>(type: "TEXT", nullable: false),
                    StateRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClientMutationId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationFeedbackEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecommendationFeedbackEvents_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecommendationMutationReceipts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientMutationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Operation = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadHash = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationMutationReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecommendationMutationReceipts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecommendationProfileStates",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    FeedbackRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    SignalRevision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationProfileStates", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_RecommendationProfileStates_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecommendationSignalOverrides",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<long>(type: "INTEGER", nullable: false),
                    IgnoreAsSeed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecommendationSignalOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecommendationSignalOverrides_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationFeedback_UserId_Provider_ProviderId",
                table: "RecommendationFeedback",
                columns: new[] { "UserId", "Provider", "ProviderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationFeedbackEvents_UserId_ClientMutationId",
                table: "RecommendationFeedbackEvents",
                columns: new[] { "UserId", "ClientMutationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationFeedbackEvents_UserId_OccurredAtUtc_Id",
                table: "RecommendationFeedbackEvents",
                columns: new[] { "UserId", "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationMutationReceipts_ExpiresAtUtc",
                table: "RecommendationMutationReceipts",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationMutationReceipts_UserId_ClientMutationId",
                table: "RecommendationMutationReceipts",
                columns: new[] { "UserId", "ClientMutationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecommendationSignalOverrides_UserId_Provider_ProviderId",
                table: "RecommendationSignalOverrides",
                columns: new[] { "UserId", "Provider", "ProviderId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecommendationFeedback");

            migrationBuilder.DropTable(
                name: "RecommendationFeedbackEvents");

            migrationBuilder.DropTable(
                name: "RecommendationMutationReceipts");

            migrationBuilder.DropTable(
                name: "RecommendationProfileStates");

            migrationBuilder.DropTable(
                name: "RecommendationSignalOverrides");

            migrationBuilder.DropColumn(
                name: "AddedFrom",
                table: "UserSeriesStates");

            migrationBuilder.DropColumn(
                name: "AddedToLibraryAtUtc",
                table: "UserSeriesStates");

            migrationBuilder.DropColumn(
                name: "ApprovalClaimedAtUtc",
                table: "SeriesRequests");
        }
    }
}
