using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastCheckAtUtc",
                table: "backup_repositories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastCheckOk",
                table: "backup_repositories",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastMaintenanceSlotUtc",
                table: "backup_repositories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastReadDataAtUtc",
                table: "backup_repositories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceSchedule",
                table: "backup_repositories",
                type: "text",
                nullable: false,
                // Existing repositories get the default maintenance: Sundays at 03:00.
                defaultValue: "{\"Times\":[\"03:00:00\"],\"Days\":[0]}");

            migrationBuilder.AddColumn<Guid>(
                name: "MirrorTargetId",
                table: "backup_repositories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReadDataMonthly",
                table: "backup_repositories",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "TestRestoreSampleSize",
                table: "backup_repositories",
                type: "integer",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.AddColumn<int>(
                name: "AlertAfterDays",
                table: "backup_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<string>(
                name: "Retention",
                table: "backup_jobs",
                type: "text",
                nullable: false,
                // Existing jobs keep every backup point until a retention is configured.
                defaultValue: "{}");

            migrationBuilder.CreateIndex(
                name: "IX_backup_repositories_MirrorTargetId",
                table: "backup_repositories",
                column: "MirrorTargetId");

            migrationBuilder.AddForeignKey(
                name: "FK_backup_repositories_backup_repositories_MirrorTargetId",
                table: "backup_repositories",
                column: "MirrorTargetId",
                principalTable: "backup_repositories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_backup_repositories_backup_repositories_MirrorTargetId",
                table: "backup_repositories");

            migrationBuilder.DropIndex(
                name: "IX_backup_repositories_MirrorTargetId",
                table: "backup_repositories");

            migrationBuilder.DropColumn(
                name: "LastCheckAtUtc",
                table: "backup_repositories");

            migrationBuilder.DropColumn(
                name: "LastCheckOk",
                table: "backup_repositories");

            migrationBuilder.DropColumn(
                name: "LastMaintenanceSlotUtc",
                table: "backup_repositories");

            migrationBuilder.DropColumn(
                name: "LastReadDataAtUtc",
                table: "backup_repositories");

            migrationBuilder.DropColumn(
                name: "MaintenanceSchedule",
                table: "backup_repositories");

            migrationBuilder.DropColumn(
                name: "MirrorTargetId",
                table: "backup_repositories");

            migrationBuilder.DropColumn(
                name: "ReadDataMonthly",
                table: "backup_repositories");

            migrationBuilder.DropColumn(
                name: "TestRestoreSampleSize",
                table: "backup_repositories");

            migrationBuilder.DropColumn(
                name: "AlertAfterDays",
                table: "backup_jobs");

            migrationBuilder.DropColumn(
                name: "Retention",
                table: "backup_jobs");
        }
    }
}
