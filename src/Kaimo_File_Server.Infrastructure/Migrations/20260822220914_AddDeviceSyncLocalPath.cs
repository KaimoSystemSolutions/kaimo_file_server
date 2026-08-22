using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceSyncLocalPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LocalPath",
                table: "device_sync_profiles",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LocalPath",
                table: "device_sync_profiles");
        }
    }
}
