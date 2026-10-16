using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vouch.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrivateDatabasePhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PhotoAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Width = table.Column<long>(type: "bigint", nullable: false),
                    Height = table.Column<long>(type: "bigint", nullable: false),
                    ProcessingVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OriginalJpeg = table.Column<string>(type: "text", nullable: false),
                    AbstractJpeg = table.Column<string>(type: "text", nullable: false),
                    QuarterJpeg = table.Column<string>(type: "text", nullable: false),
                    SixtyJpeg = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhotoAssets_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoAssets_OwnerId",
                table: "PhotoAssets",
                column: "OwnerId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhotoAssets");
        }
    }
}
