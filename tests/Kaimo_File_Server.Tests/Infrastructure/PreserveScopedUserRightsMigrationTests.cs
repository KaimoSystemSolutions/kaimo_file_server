using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Infrastructure.Migrations;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// The account toggles and department membership got their own rights
/// (<see cref="ManagementPermission.EnableDisableUsers"/>, <see cref="ManagementPermission.AssignDepartments"/>).
/// Custom roles could not hold them before, so the upgrade grants them to every custom role that
/// carried the right that used to gate the same actions. Runs the migration's SQL against the
/// test schema.
/// </summary>
public sealed class PreserveScopedUserRightsMigrationTests : DatabaseTestBase
{
    private Role SeedRole(ManagementPermission perms, bool isSystem = false)
    {
        var role = new Role(Guid.NewGuid(), $"Role-{Guid.NewGuid():N}", perms, isSystem);
        using var db = NewContext();
        db.Roles.Add(role);
        db.SaveChanges();
        return role;
    }

    private async Task RunMigrationAsync()
    {
        await using var db = NewContext();
        foreach (var operation in new PreserveScopedUserRights().UpOperations.OfType<SqlOperation>())
            await db.Database.ExecuteSqlRawAsync(operation.Sql);
    }

    private async Task<ManagementPermission> StoredPermissionsAsync(Role role)
    {
        await using var db = NewContext();
        return (await db.Roles.SingleAsync(r => r.Id == role.Id)).ManagementPermissions;
    }

    [Fact]
    public async Task CustomRoleWithEditUserProfiles_GainsEnableDisableUsers()
    {
        var role = SeedRole(ManagementPermission.EditUserProfiles | ManagementPermission.ResetPasswords);

        await RunMigrationAsync();

        Assert.Equal(
            ManagementPermission.EditUserProfiles | ManagementPermission.ResetPasswords
            | ManagementPermission.EnableDisableUsers,
            await StoredPermissionsAsync(role));
    }

    [Fact]
    public async Task CustomRoleWithEditDepartment_GainsAssignDepartments()
    {
        var role = SeedRole(ManagementPermission.EditDepartment | ManagementPermission.ViewDepartment);

        await RunMigrationAsync();

        Assert.Equal(
            ManagementPermission.EditDepartment | ManagementPermission.ViewDepartment
            | ManagementPermission.AssignDepartments,
            await StoredPermissionsAsync(role));
    }

    [Fact]
    public async Task CustomRoleWithoutTheGatingRights_StaysUnchanged()
    {
        var role = SeedRole(ManagementPermission.ShareAdmin | ManagementPermission.ResetPasswords);

        await RunMigrationAsync();

        Assert.Equal(ManagementPermission.ShareAdmin | ManagementPermission.ResetPasswords,
            await StoredPermissionsAsync(role));
    }

    [Fact]
    public async Task SystemRoles_AreLeftToTheSeeder()
    {
        var role = SeedRole(ManagementPermission.EditUserProfiles | ManagementPermission.EditDepartment, isSystem: true);

        await RunMigrationAsync();

        Assert.Equal(ManagementPermission.EditUserProfiles | ManagementPermission.EditDepartment,
            await StoredPermissionsAsync(role));
    }
}
