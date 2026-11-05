using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudAccessMounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MountPath",
                table: "cloud_access_shares",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MountShareId",
                table: "cloud_access_shares",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Provisioning",
                table: "cloud_access_shares",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_cloud_access_shares_MountShareId_MountPath",
                table: "cloud_access_shares",
                columns: new[] { "MountShareId", "MountPath" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_cloud_access_shares_share_definitions_MountShareId",
                table: "cloud_access_shares",
                column: "MountShareId",
                principalTable: "share_definitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_cloud_access_shares_share_definitions_MountShareId",
                table: "cloud_access_shares");

            migrationBuilder.DropIndex(
                name: "IX_cloud_access_shares_MountShareId_MountPath",
                table: "cloud_access_shares");

            migrationBuilder.DropColumn(
                name: "MountPath",
                table: "cloud_access_shares");

            migrationBuilder.DropColumn(
                name: "MountShareId",
                table: "cloud_access_shares");

            migrationBuilder.DropColumn(
                name: "Provisioning",
                table: "cloud_access_shares");
        }
    }
}
