using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vouch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionsAndAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuthSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_AuthSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "AuthSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthSessions_UserId_RevokedAt",
                table: "AuthSessions",
                columns: new[] { "UserId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_SessionId",
                table: "RefreshTokens",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION public.vouch_revoke_changed_sessions() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Status" NOT IN (1, 2) OR NEW."Role" IS DISTINCT FROM OLD."Role"
                       OR NEW."CampusId" IS DISTINCT FROM OLD."CampusId"
                       OR NEW."PasswordHash" IS DISTINCT FROM OLD."PasswordHash" THEN
                        UPDATE public."AuthSessions" SET "RevokedAt" = CURRENT_TIMESTAMP
                        WHERE "UserId" = NEW."Id" AND "RevokedAt" IS NULL;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER vouch_revoke_changed_sessions AFTER UPDATE OF "Status", "Role", "CampusId", "PasswordHash"
                ON public."Users" FOR EACH ROW EXECUTE FUNCTION public.vouch_revoke_changed_sessions();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER vouch_revoke_changed_sessions ON public.\"Users\"; DROP FUNCTION public.vouch_revoke_changed_sessions();");
            migrationBuilder.DropTable(
                name: "RefreshTokens");

            migrationBuilder.DropTable(
                name: "AuthSessions");
        }
    }
}
