using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropUserRolesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preserve existing direct role memberships: convert every user_roles
            // row into an equivalent GLOBAL-scoped assignment (ScopeType 0,
            // ScopeId = Guid.Empty), skipping any that already exist so the unique
            // (PrincipalId, RoleId, ScopeType, ScopeId) index is never violated.
            migrationBuilder.Sql("""
                INSERT INTO scoped_role_assignments ("Id", "PrincipalId", "RoleId", "ScopeType", "ScopeId")
                SELECT gen_random_uuid(), ur."UserId", ur."RoleId", 0, '00000000-0000-0000-0000-000000000000'
                FROM user_roles ur
                WHERE NOT EXISTS (
                    SELECT 1 FROM scoped_role_assignments s
                    WHERE s."PrincipalId" = ur."UserId"
                      AND s."RoleId" = ur."RoleId"
                      AND s."ScopeType" = 0
                      AND s."ScopeId" = '00000000-0000-0000-0000-000000000000'
                );
                """);

            migrationBuilder.DropTable(
                name: "user_roles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_roles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => new { x.UserId, x.RoleId });
                });
        }
    }
}
