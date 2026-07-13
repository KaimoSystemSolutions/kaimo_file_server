using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShareIdToFileVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_file_versions_FilePath_ContentHash",
                table: "file_versions");

            migrationBuilder.DropIndex(
                name: "IX_file_versions_FilePath_SnapshotTimestampUtc",
                table: "file_versions");

            migrationBuilder.AddColumn<Guid>(
                name: "ShareId",
                table: "file_versions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_ShareId_FilePath_ContentHash",
                table: "file_versions",
                columns: new[] { "ShareId", "FilePath", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_ShareId_FilePath_SnapshotTimestampUtc",
                table: "file_versions",
                columns: new[] { "ShareId", "FilePath", "SnapshotTimestampUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_file_versions_ShareId_FilePath_ContentHash",
                table: "file_versions");

            migrationBuilder.DropIndex(
                name: "IX_file_versions_ShareId_FilePath_SnapshotTimestampUtc",
                table: "file_versions");

            migrationBuilder.DropColumn(
                name: "ShareId",
                table: "file_versions");

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_FilePath_ContentHash",
                table: "file_versions",
                columns: new[] { "FilePath", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_file_versions_FilePath_SnapshotTimestampUtc",
                table: "file_versions",
                columns: new[] { "FilePath", "SnapshotTimestampUtc" },
                unique: true);
        }
    }
}
