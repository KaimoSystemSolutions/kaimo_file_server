using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShareLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "share_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    RootRelativePath = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsDirectory = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BaseAddress = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StartsAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MaxAccessCount = table.Column<int>(type: "integer", nullable: true),
                    AccessCount = table.Column<int>(type: "integer", nullable: false),
                    MaxBytesPerSecond = table.Column<long>(type: "bigint", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_share_links", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_share_links_ShareId",
                table: "share_links",
                column: "ShareId");

            migrationBuilder.CreateIndex(
                name: "IX_share_links_Token",
                table: "share_links",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "share_links");
        }
    }
}
