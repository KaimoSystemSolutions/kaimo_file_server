using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Pins the single write path for role assignments. The Global department is the only
/// "Global" the UI knows: a department-scoped assignment on it is stored as
/// <see cref="ScopeType.Global"/>, so it carries the full system-wide meaning (system bits,
/// role ACLs, admin coupling and the last-admin guard) whichever page created it.
/// </summary>
public class RoleAssignmentServiceTests : DatabaseTestBase
{
    private readonly Mock<IManagementAuthService> _mgmtAuth = new();

    public RoleAssignmentServiceTests()
    {
        _mgmtAuth.Setup(m => m.CanAssignRoleAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>(),
                It.IsAny<ScopeType>(), It.IsAny<Guid>()))
            .ReturnsAsync(true);
    }

    private RoleAssignmentService BuildSut()
        => new(ScopedRoleRepo(), RoleRepo(), UserRepo(), GroupRepo(), _mgmtAuth.Object);

    private Role SeedRole(string name, ManagementPermission perms = ManagementPermission.None, Guid? id = null)
    {
        var role = new Role(id ?? Guid.NewGuid(), name, perms, false);
        using var db = NewContext();
        db.Roles.Add(role);
        db.SaveChanges();
        return role;
    }

    private void SeedAdminsGroup()
    {
        using var db = NewContext();
        db.Groups.Add(new Group(WellKnownGUIDs.GROUP_ADMINS, "Admins"));
        db.SaveChanges();
    }

    [Fact]
    public async Task CreateAsync_DepartmentScopeOnGlobalDepartment_IsStoredAsGlobal()
    {
        var actor = SeedUser("admin");
        var principal = SeedUser("bob");
        var role = SeedRole("Reviewer");

        var error = await BuildSut().CreateAsync(
            ContextFor(actor), principal.Id, role, ScopeType.Department, WellKnownGUIDs.DEPARTMENT_GLOBAL);

        Assert.Null(error);
        await using var db = NewContext();
        var row = await db.ScopedRoleAssignments.SingleAsync(a => a.PrincipalId == principal.Id);
        Assert.Equal(ScopeType.Global, row.ScopeType);
        Assert.Equal(Guid.Empty, row.ScopeId);
    }

    [Fact]
    public async Task CreateAsync_GlobalDepartment_ElevationGateSeesGlobalScope()
    {
        var actor = SeedUser("admin");
        var principal = SeedUser("bob");
        var role = SeedRole("Reviewer");
        _mgmtAuth.Setup(m => m.CanAssignRoleAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>(),
                ScopeType.Global, Guid.Empty))
            .ReturnsAsync(false);

        var error = await BuildSut().CreateAsync(
            ContextFor(actor), principal.Id, role, ScopeType.Department, WellKnownGUIDs.DEPARTMENT_GLOBAL);

        Assert.Equal(Resources.Web_Error_NoPermission, error);
        await using var db = NewContext();
        Assert.False(await db.ScopedRoleAssignments.AnyAsync(a => a.PrincipalId == principal.Id));
    }

    [Fact]
    public async Task CreateAsync_OtherDepartment_StaysDepartmentScoped()
    {
        var actor = SeedUser("admin");
        var principal = SeedUser("bob");
        var role = SeedRole("Reviewer");
        var dept = SeedDepartment("Sales");

        await BuildSut().CreateAsync(ContextFor(actor), principal.Id, role, ScopeType.Department, dept.Id);

        await using var db = NewContext();
        var row = await db.ScopedRoleAssignments.SingleAsync(a => a.PrincipalId == principal.Id);
        Assert.Equal(ScopeType.Department, row.ScopeType);
        Assert.Equal(dept.Id, row.ScopeId);
    }

    [Fact]
    public async Task CreateAsync_AdminRoleOnGlobalDepartment_AddsUserToAdminsGroup()
    {
        var actor = SeedUser("admin");
        var principal = SeedUser("bob");
        var adminRole = SeedRole("Administrator", ManagementPermission.FullAdmin, WellKnownGUIDs.ROLE_ADMIN);
        SeedAdminsGroup();

        await BuildSut().CreateAsync(
            ContextFor(actor), principal.Id, adminRole, ScopeType.Department, WellKnownGUIDs.DEPARTMENT_GLOBAL);

        await using var db = NewContext();
        Assert.True(await db.UserGroups.AnyAsync(
            ug => ug.UserId == principal.Id && ug.GroupId == WellKnownGUIDs.GROUP_ADMINS));
    }

    [Fact]
    public async Task DeleteAsync_SoleGlobalAdmin_IsBlocked()
    {
        var admin = SeedUser("admin");
        var adminRole = SeedRole("Administrator", ManagementPermission.FullAdmin, WellKnownGUIDs.ROLE_ADMIN);
        var assignment = ScopedRoleAssignment.Global(admin.Id, adminRole.Id);
        await ScopedRoleRepo().CreateAsync(assignment);

        var error = await BuildSut().DeleteAsync(ContextFor(admin), assignment);

        Assert.Equal(Resources.Web_Error_LastAdmin, error);
        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.Id == assignment.Id));
    }

    [Fact]
    public async Task DeleteAsync_AdminWithSecondAdmin_RemovesAssignmentAndAdminsMembership()
    {
        var admin = SeedUser("admin");
        var admin2 = SeedUser("admin2");
        var adminRole = SeedRole("Administrator", ManagementPermission.FullAdmin, WellKnownGUIDs.ROLE_ADMIN);
        SeedAdminsGroup();
        await ScopedRoleRepo().CreateAsync(ScopedRoleAssignment.Global(admin.Id, adminRole.Id));
        var second = ScopedRoleAssignment.Global(admin2.Id, adminRole.Id);
        await ScopedRoleRepo().CreateAsync(second);
        await GroupRepo().AddMemberAsync(WellKnownGUIDs.GROUP_ADMINS, admin2.Id);

        var error = await BuildSut().DeleteAsync(ContextFor(admin), second);

        Assert.Null(error);
        await using var db = NewContext();
        Assert.False(await db.ScopedRoleAssignments.AnyAsync(a => a.Id == second.Id));
        Assert.False(await db.UserGroups.AnyAsync(
            ug => ug.UserId == admin2.Id && ug.GroupId == WellKnownGUIDs.GROUP_ADMINS));
    }

    [Fact]
    public async Task DeleteAsync_WithoutAuthorityAtScope_IsBlocked()
    {
        var actor = SeedUser("admin");
        var principal = SeedUser("bob");
        var role = SeedRole("Reviewer");
        var assignment = ScopedRoleAssignment.Global(principal.Id, role.Id);
        await ScopedRoleRepo().CreateAsync(assignment);
        _mgmtAuth.Setup(m => m.CanAssignRoleAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>(),
                ScopeType.Global, Guid.Empty))
            .ReturnsAsync(false);

        var error = await BuildSut().DeleteAsync(ContextFor(actor), assignment);

        Assert.Equal(Resources.Web_Error_NoPermission, error);
        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.Id == assignment.Id));
    }
}
