using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CloudAccessOneDrive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cloud_access_connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AccountDisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    AccountEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    ProtectedCredentials = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastVerifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cloud_access_connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cloud_access_shares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RemoteRootPath = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    RemoteRootItemId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsReadOnly = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cloud_access_shares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cloud_access_shares_cloud_access_connections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "cloud_access_connections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cloud_access_grants",
                columns: table => new
                {
                    ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cloud_access_grants", x => new { x.ShareId, x.PrincipalId });
                    table.ForeignKey(
                        name: "FK_cloud_access_grants_cloud_access_shares_ShareId",
                        column: x => x.ShareId,
                        principalTable: "cloud_access_shares",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cloud_access_connections_DepartmentId",
                table: "cloud_access_connections",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_cloud_access_connections_Name",
                table: "cloud_access_connections",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cloud_access_grants_PrincipalId",
                table: "cloud_access_grants",
                column: "PrincipalId");

            migrationBuilder.CreateIndex(
                name: "IX_cloud_access_shares_ConnectionId",
                table: "cloud_access_shares",
                column: "ConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_cloud_access_shares_DepartmentId",
                table: "cloud_access_shares",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_cloud_access_shares_Name",
                table: "cloud_access_shares",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cloud_access_grants");

            migrationBuilder.DropTable(
                name: "cloud_access_shares");

            migrationBuilder.DropTable(
                name: "cloud_access_connections");
        }
    }
}
