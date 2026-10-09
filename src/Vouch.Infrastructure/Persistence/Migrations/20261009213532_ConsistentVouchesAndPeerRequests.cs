using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vouch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConsistentVouchesAndPeerRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCliqueFlagged",
                table: "Vouches",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MutualVoucherCountAtSubmission",
                table: "Vouches",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VouchRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedVoucherId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VouchRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VouchRequests_Users_RequestedVoucherId",
                        column: x => x.RequestedVoucherId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VouchRequests_Users_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VouchRequests_RequestedVoucherId_CreatedAt",
                table: "VouchRequests",
                columns: new[] { "RequestedVoucherId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_VouchRequests_RequesterId_CreatedAt",
                table: "VouchRequests",
                columns: new[] { "RequesterId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_VouchRequests_RequesterId_RequestedVoucherId",
                table: "VouchRequests",
                columns: new[] { "RequesterId", "RequestedVoucherId" },
                unique: true,
                filter: "\"Status\" = 1");
            // Historic shared-voucher counts were never recorded; do not invent that evidence.
            migrationBuilder.Sql("UPDATE \"Vouches\" SET \"IsCliqueFlagged\" = true WHERE \"CliqueDampeningMultiplier\" = 0.5;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VouchRequests");

            migrationBuilder.DropColumn(
                name: "IsCliqueFlagged",
                table: "Vouches");

            migrationBuilder.DropColumn(
                name: "MutualVoucherCountAtSubmission",
                table: "Vouches");
        }
    }
}
