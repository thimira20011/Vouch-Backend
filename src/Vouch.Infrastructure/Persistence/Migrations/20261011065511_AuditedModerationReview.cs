using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vouch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditedModerationReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Historical decisions have no known actor. Use the only available timestamp when absent.
            migrationBuilder.Sql("UPDATE \"Reports\" SET \"ResolvedAt\" = \"CreatedAt\" WHERE \"Status\" IN (2, 3) AND \"ResolvedAt\" IS NULL;");
            migrationBuilder.AddColumn<int>(
                name: "StatusBeforeSuspension",
                table: "Users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SuspendedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ResolvedByArchitectId",
                table: "Reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ModerationAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampusId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchitectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    PreviousStatus = table.Column<int>(type: "integer", nullable: false),
                    ResultingStatus = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModerationAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModerationAudits_Reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "Reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ModerationAudits_Users_SubjectUserId",
                        column: x => x.SubjectUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModerationAudits_CampusId_SubjectUserId_CreatedAt",
                table: "ModerationAudits",
                columns: new[] { "CampusId", "SubjectUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ModerationAudits_ReportId",
                table: "ModerationAudits",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "IX_ModerationAudits_SubjectUserId",
                table: "ModerationAudits",
                column: "SubjectUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModerationAudits");

            migrationBuilder.DropColumn(
                name: "StatusBeforeSuspension",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SuspendedAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ResolvedByArchitectId",
                table: "Reports");
        }
    }
}
