using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncDeviceHardwareId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HardwareIdHash",
                table: "sync_devices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_devices_UserId_HardwareIdHash",
                table: "sync_devices",
                columns: new[] { "UserId", "HardwareIdHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sync_devices_UserId_HardwareIdHash",
                table: "sync_devices");

            migrationBuilder.DropColumn(
                name: "HardwareIdHash",
                table: "sync_devices");
        }
    }
}
