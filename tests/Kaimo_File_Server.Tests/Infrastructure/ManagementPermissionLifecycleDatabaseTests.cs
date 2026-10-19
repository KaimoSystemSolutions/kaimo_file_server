using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Tests.Infrastructure;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// End-to-end lifecycle tests for MANAGEMENT permissions (delegated administration),
/// mirroring the file-ACL lifecycle suite. They mutate the sources of management
/// authority — scoped role assignments and role permission bitmasks — and then ask the
/// REAL <see cref="Core.Services.ManagementAuthService"/> over the real database whether
/// the delegated right actually changed.
///
/// Protects the delegation model against silent regressions: "we removed the delegation,
/// but can the ex-admin still manage the department/share?"
/// </summary>
public class ManagementPermissionLifecycleDatabaseTests : DatabaseTestBase
{
    private Role SeedRole(string name, ManagementPermission perms)
    {
        var role = new Role(Guid.NewGuid(), name, perms);
        using var db = NewContext();
        db.Roles.Add(role);
        db.SaveChanges();
        return role;
    }

    [Fact]
    public async Task RevokingScopedDepartmentAssignment_RemovesDepartmentManagementRight()
    {
        var actor = SeedUser("delegate");
        var dept = SeedDepartment("Engineering");
        var role = SeedRole("DeptEditor", ManagementPermission.EditDepartment);
        var assignment = new ScopedRoleAssignment(actor.Id, role.Id, ScopeType.Department, dept.Id);
        await ScopedRoleRepo().CreateAsync(assignment);

        var ctx = ContextFor(actor);
        var mgmt = BuildManagementAuthService();

        Assert.True(await mgmt.CanManageDepartmentAsync(ctx, dept.Id, ManagementPermission.EditDepartment));

        // Revoke the delegation.
        await ScopedRoleRepo().DeleteAsync(assignment.Id);

        Assert.False(await mgmt.CanManageDepartmentAsync(ctx, dept.Id, ManagementPermission.EditDepartment));
    }

    [Fact]
    public async Task RevokingScopedShareAssignment_RemovesShareManagementRight()
    {
        var actor = SeedUser("delegate");
        var share = SeedShare("finance");
        var role = SeedRole("AclManager", ManagementPermission.ManageShareAcls);
        var assignment = new ScopedRoleAssignment(actor.Id, role.Id, ScopeType.Share, share.Id);
        await ScopedRoleRepo().CreateAsync(assignment);

        var ctx = ContextFor(actor);
        var mgmt = BuildManagementAuthService();

        Assert.True(await mgmt.CanManageShareAsync(ctx, share.Id, ManagementPermission.ManageShareAcls));

        await ScopedRoleRepo().DeleteAsync(assignment.Id);

        Assert.False(await mgmt.CanManageShareAsync(ctx, share.Id, ManagementPermission.ManageShareAcls));
    }

    [Fact]
    public async Task StrippingRolePermission_RevokesTheGrantEverywhere()
    {
        var actor = SeedUser("delegate");
        var role = SeedRole("ShareCreator", ManagementPermission.CreateShares);
        var assignment = new ScopedRoleAssignment(actor.Id, role.Id, ScopeType.Global, Guid.Empty);
        await ScopedRoleRepo().CreateAsync(assignment);

        var ctx = ContextFor(actor);
        var mgmt = BuildManagementAuthService();

        Assert.True(await mgmt.HasAnyPermissionAsync(ctx, ManagementPermission.CreateShares));

        // Strip the permission from the role itself (not the assignment).
        role.ManagementPermissions = ManagementPermission.None;
        await RoleRepo().UpdateAsync(role);

        // Re-resolve the service — in production each request gets fresh scoped repos, so
        // the check must read the updated role rather than a stale tracked instance.
        var mgmtAfter = BuildManagementAuthService();
        Assert.False(await mgmtAfter.HasAnyPermissionAsync(ctx, ManagementPermission.CreateShares));
    }

    [Fact]
    public async Task DepartmentScopedAdmin_CannotManageUnrelatedDepartment()
    {
        // A delegation scoped to one department must not leak to a sibling department.
        var actor = SeedUser("delegate");
        var scoped = SeedDepartment("Sales");
        var other = SeedDepartment("HR");
        var role = SeedRole("DeptEditor", ManagementPermission.EditDepartment);
        await ScopedRoleRepo().CreateAsync(
            new ScopedRoleAssignment(actor.Id, role.Id, ScopeType.Department, scoped.Id));

        var ctx = ContextFor(actor);
        var mgmt = BuildManagementAuthService();

        Assert.True(await mgmt.CanManageDepartmentAsync(ctx, scoped.Id, ManagementPermission.EditDepartment));
        Assert.False(await mgmt.CanManageDepartmentAsync(ctx, other.Id, ManagementPermission.EditDepartment));
    }
}
