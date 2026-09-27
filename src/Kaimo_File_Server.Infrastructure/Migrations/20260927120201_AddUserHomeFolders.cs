using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserHomeFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HomeDirectoryEnabled",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsUserHomes",
                table: "share_definitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_share_definitions_IsUserHomes",
                table: "share_definitions",
                column: "IsUserHomes",
                unique: true,
                filter: "\"IsUserHomes\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_share_definitions_IsUserHomes",
                table: "share_definitions");

            migrationBuilder.DropColumn(
                name: "HomeDirectoryEnabled",
                table: "users");

            migrationBuilder.DropColumn(
                name: "IsUserHomes",
                table: "share_definitions");
        }
    }
}
