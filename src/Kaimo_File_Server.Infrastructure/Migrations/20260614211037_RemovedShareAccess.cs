using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemovedShareAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "share_access");

            migrationBuilder.AddColumn<bool>(
                name: "IsShareHidden",
                table: "share_definitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsShareHidden",
                table: "share_definitions");

            migrationBuilder.CreateTable(
                name: "share_access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShareName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_share_access", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_share_access_ShareName_PrincipalId",
                table: "share_access",
                columns: new[] { "ShareName", "PrincipalId" },
                unique: true);
        }
    }
}
