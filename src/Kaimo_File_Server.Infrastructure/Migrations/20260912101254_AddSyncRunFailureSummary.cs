using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncRunFailureSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastRunFailureCount",
                table: "sync_definition_runtimes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastRunFailures",
                table: "sync_definition_runtimes",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastRunFailureCount",
                table: "sync_definition_runtimes");

            migrationBuilder.DropColumn(
                name: "LastRunFailures",
                table: "sync_definition_runtimes");
        }
    }
}
