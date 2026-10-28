using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kaimo_File_Server.Infrastructure.Migrations
{
    /// <summary>
    /// Enabling/disabling accounts and file services now needs EnableDisableUsers (was
    /// EditUserProfiles), adding/removing department members needs AssignDepartments (was
    /// EditDepartment). The role editor offered neither bit before, so custom roles get it where
    /// they carried the right that used to gate the action. System roles are kept in sync by the
    /// seeder on every start.
    /// </summary>
    public partial class PreserveScopedUserRights : Migration
    {
        // ManagementPermission bits, fixed values of the persisted flags enum.
        private const long EditUserProfiles = 1L << 2;
        private const long EnableDisableUsers = 1L << 4;
        private const long AssignDepartments = 1L << 18;
        private const long EditDepartment = 1L << 32;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GrantWhereHeld(EditUserProfiles, EnableDisableUsers));
            migrationBuilder.Sql(GrantWhereHeld(EditDepartment, AssignDepartments));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: afterwards the granted bits cannot be told apart from ones an
            // administrator added on purpose.
        }

        private static string GrantWhereHeld(long held, long granted)
            => $"""
                UPDATE roles
                SET "ManagementPermissions" = "ManagementPermissions" | {granted}
                WHERE "IsSystemRole" = false AND ("ManagementPermissions" & {held}) <> 0;
                """;
    }
}
