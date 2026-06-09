using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DepartmentDirectFK : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "department_groups");

            migrationBuilder.DropTable(
                name: "department_shares");

            migrationBuilder.AddColumn<Guid>(
                name: "DepartmentId",
                table: "share_definitions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "DepartmentId",
                table: "groups",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.CreateIndex(
                name: "IX_share_definitions_DepartmentId",
                table: "share_definitions",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_groups_DepartmentId",
                table: "groups",
                column: "DepartmentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_share_definitions_DepartmentId",
                table: "share_definitions");

            migrationBuilder.DropIndex(
                name: "IX_groups_DepartmentId",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "share_definitions");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "groups");

            migrationBuilder.CreateTable(
                name: "department_groups",
                columns: table => new
                {
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_department_groups", x => new { x.DepartmentId, x.GroupId });
                });

            migrationBuilder.CreateTable(
                name: "department_shares",
                columns: table => new
                {
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_department_shares", x => new { x.DepartmentId, x.ShareId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_department_groups_GroupId",
                table: "department_groups",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_department_shares_ShareId",
                table: "department_shares",
                column: "ShareId");
        }
    }
}
