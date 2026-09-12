using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CloudAccessGrantPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // New grants default to Read (0).
            migrationBuilder.AddColumn<int>(
                name: "Permission",
                table: "cloud_access_grants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Preserve prior behavior: on a share that was read-write, every existing
            // grant becomes Write (1); grants on a read-only share stay Read (0).
            migrationBuilder.Sql(
                """
                UPDATE cloud_access_grants AS g
                SET "Permission" = 1
                FROM cloud_access_shares AS s
                WHERE g."ShareId" = s."Id" AND s."IsReadOnly" = false;
                """);

            migrationBuilder.DropColumn(
                name: "IsReadOnly",
                table: "cloud_access_shares");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Permission",
                table: "cloud_access_grants");

            migrationBuilder.AddColumn<bool>(
                name: "IsReadOnly",
                table: "cloud_access_shares",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
