using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vouch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableAccountDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountDeletions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampusId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestTimeInferred = table.Column<bool>(type: "boolean", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeadlineMissedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    BackupSubjectId = table.Column<string>(type: "text", nullable: true),
                    PolicyVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountDeletions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountDeletions_Campuses_CampusId",
                        column: x => x.CampusId,
                        principalTable: "Campuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountDeletions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountDeletions_CampusId_RequestedAt",
                table: "AccountDeletions",
                columns: new[] { "CampusId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountDeletions_NextAttemptAt_LeaseUntil",
                table: "AccountDeletions",
                columns: new[] { "NextAttemptAt", "LeaseUntil" },
                filter: "\"CompletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccountDeletions_UserId",
                table: "AccountDeletions",
                column: "UserId",
                unique: true);
            migrationBuilder.Sql("""
                INSERT INTO "AccountDeletions" ("Id", "CampusId", "UserId", "RequestedAt", "DeadlineAt", "RequestTimeInferred",
                    "NextAttemptAt", "Attempts", "PolicyVersion", "CreatedAt")
                SELECT gen_random_uuid(), "CampusId", "Id",
                    LEAST(COALESCE("DeletionRequestedAt", "UpdatedAt", "CreatedAt"), CURRENT_TIMESTAMP),
                    LEAST(COALESCE("DeletionRequestedAt", "UpdatedAt", "CreatedAt"), CURRENT_TIMESTAMP) + INTERVAL '30 days',
                    "DeletionRequestedAt" IS NULL, CURRENT_TIMESTAMP, 0, 'erase-conversations-v1', CURRENT_TIMESTAMP
                FROM "Users" WHERE "Status" = 4 OR "DeletionRequestedAt" IS NOT NULL;
                UPDATE "Users" u SET "Status"=4, "DeletionRequestedAt"=d."RequestedAt"
                FROM "AccountDeletions" d WHERE d."UserId"=u."Id";
                CREATE FUNCTION public.vouch_preserve_deletion_request() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'UPDATE' AND OLD."DeletionRequestedAt" IS NOT NULL THEN
                        NEW."DeletionRequestedAt" := OLD."DeletionRequestedAt";
                    ELSIF NEW."Status" = 4 AND NEW."DeletionRequestedAt" IS NULL THEN
                        NEW."DeletionRequestedAt" := CURRENT_TIMESTAMP;
                    END IF;
                    IF NEW."DeletionRequestedAt" IS NOT NULL THEN
                        NEW."Status" := 4;
                        IF TG_OP = 'UPDATE' THEN
                            UPDATE public."AuthSessions" SET "RevokedAt"=CURRENT_TIMESTAMP
                            WHERE "UserId"=NEW."Id" AND "RevokedAt" IS NULL;
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER vouch_preserve_deletion_request BEFORE INSERT OR UPDATE ON public."Users"
                FOR EACH ROW EXECUTE FUNCTION public.vouch_preserve_deletion_request();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Erased account data cannot be reconstructed by a downgrade. Follow the reviewed backup/restore and deletion-ledger replay procedure.");
        }
    }
}
