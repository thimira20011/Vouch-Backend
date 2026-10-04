using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vouch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SecureInvitesAndUniversityIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AmbassadorInvites_Token",
                table: "AmbassadorInvites");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AmbassadorApprovedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AmbassadorApprovedByArchitectId",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EmailVerifiedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OnboardingCompletedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RedeemedByUserId",
                table: "AmbassadorInvites",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TokenHash",
                table: "AmbassadorInvites",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            // Preserve existing random invites as digests. Recipient-less legacy invites can no longer redeem.
            migrationBuilder.Sql("""
                UPDATE "AmbassadorInvites" SET "TokenHash" = encode(sha256(convert_to("Token", 'UTF8')), 'hex');
                ALTER TABLE "AmbassadorInvites" ALTER COLUMN "TokenHash" DROP DEFAULT;
                UPDATE "Campuses" SET "IsSoftLaunchUnlocked" = false, "LaunchReadinessScore" = 0;
                UPDATE "Users" SET "Status" = 1 WHERE "Status" = 2 AND "Role" <> 4;
                UPDATE "Users" SET "HasFoundingMemberBadge" = false WHERE "Role" = 3;
                """);
            migrationBuilder.DropColumn(name: "Token", table: "AmbassadorInvites");

            migrationBuilder.CreateTable(
                name: "EmailVerifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampusId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailLookupHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailVerifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailVerifications_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AmbassadorInvites_TokenHash",
                table: "AmbassadorInvites",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerifications_TokenHash",
                table: "EmailVerifications",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailVerifications_UserId",
                table: "EmailVerifications",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Invite digests cannot be converted to bearer tokens. Restore the reviewed pre-upgrade backup for rollback.");
        }
    }
}
