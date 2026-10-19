using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

public partial class UserListViewModelDatabaseTests
{
    // ═══════════════════ Scoped role assignments ═══════════════════

    [Fact]
    public async Task CreateAssignmentAsync_PersistsGlobalAssignment()
    {
        var actor = SeedUser("admin");
        var role = SeedRole("Delegate");
        var principal = SeedUser("delegatee");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Roles);
        await sut.SelectRoleAsync(sut.Roles.Single(r => r.Id == role.Id));

        sut.StartAddAssignment();
        sut.NewAssignmentScopeType = ScopeType.Global;
        sut.NewAssignmentPrincipalId = principal.Id;
        await sut.CreateAssignmentAsync();

        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(
            a => a.RoleId == role.Id && a.PrincipalId == principal.Id && a.ScopeType == ScopeType.Global));
    }

    [Fact]
    public async Task DeleteAssignmentAsync_RemovesAssignment()
    {
        var actor = SeedUser("admin");
        var role = SeedRole("Delegate");
        var principal = SeedUser("delegatee");
        var assignment = new ScopedRoleAssignment(principal.Id, role.Id, ScopeType.Global, Guid.Empty);
        await ScopedRoleRepo().CreateAsync(assignment);

        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Roles);
        await sut.SelectRoleAsync(sut.Roles.Single(r => r.Id == role.Id));

        await sut.DeleteAssignmentAsync(assignment.Id);

        await using var db = NewContext();
        Assert.False(await db.ScopedRoleAssignments.AnyAsync(a => a.Id == assignment.Id));
    }

    [Fact]
    public async Task CreateAssignmentAsync_FromUserProfile_PersistsChosenRoleAndRefreshesList()
    {
        var actor = SeedUser("admin");
        var role = SeedRole("Delegate");
        var target = SeedUser("bob");
        var dept = SeedDepartment("Engineering");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));

        sut.StartAddAssignment();
        sut.NewAssignmentRoleId = role.Id;
        sut.NewAssignmentPrincipalId = target.Id;
        sut.NewAssignmentScopeType = ScopeType.Department;
        sut.NewAssignmentScopeId = dept.Id;
        await sut.CreateAssignmentAsync();

        Assert.Null(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.RoleId == role.Id
            && a.PrincipalId == target.Id && a.ScopeType == ScopeType.Department && a.ScopeId == dept.Id));
        Assert.Contains(sut.UserScopedAssignments, a => a.RoleName == "Delegate" && a.ScopeName == "Engineering");
    }

    [Fact]
    public async Task CreateAssignmentAsync_NoRoleSelectedOrSelectable_SetsErrorAndPersistsNothing()
    {
        var actor = SeedUser("admin");
        var target = SeedUser("bob");
        var dept = SeedDepartment("Engineering");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));

        sut.StartAddAssignment();
        sut.NewAssignmentPrincipalId = target.Id;
        sut.NewAssignmentScopeType = ScopeType.Department;
        sut.NewAssignmentScopeId = dept.Id;
        await sut.CreateAssignmentAsync();

        Assert.Equal(Kaimo_File_Server.Core.Language.Resources.Web_Error_SelectRole, sut.ErrorMessage);
        await using var db = NewContext();
        Assert.False(await db.ScopedRoleAssignments.AnyAsync(a => a.PrincipalId == target.Id));
    }

    [Fact]
    public async Task StartAddAssignment_OnRoleDetail_PreselectsThatRole()
    {
        var actor = SeedUser("admin");
        var role = SeedRole("Delegate");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Roles);
        await sut.SelectRoleAsync(sut.Roles.Single(r => r.Id == role.Id));

        sut.StartAddAssignment();

        Assert.Equal(role.Id, sut.NewAssignmentRoleId);
    }

    // ═══════════════════ Group list scope filtering ═══════════════════

    [Fact]
    public async Task GroupsTab_ScopedManager_SeesOnlyGroupsInManagedDepartments()
    {
        // Regression: the group list must be filtered by the actor's GROUP-management
        // scope, not the department-view scope, and an out-of-scope group must NOT leak.
        var actor = SeedUser("mgr");
        var deptA = SeedDepartment("A");
        var deptB = SeedDepartment("B");
        var groupA = SeedGroupInDept("teamA", deptA.Id);
        var groupB = SeedGroupInDept("teamB", deptB.Id);

        var sut = BuildScopedGroupManagerSut(actor, managedDeptId: deptA.Id);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Groups);

        Assert.Contains(sut.Groups, g => g.Id == groupA.Id);
        Assert.DoesNotContain(sut.Groups, g => g.Id == groupB.Id);
    }

    private Group SeedGroupInDept(string name, Guid departmentId)
    {
        var group = new Group(Guid.NewGuid(), name, departmentId);
        using var db = NewContext();
        db.Groups.Add(group);
        db.SaveChanges();
        return group;
    }

    private UserListViewModel BuildScopedGroupManagerSut(User actor, Guid managedDeptId)
    {
        var ctx = ContextFor(actor);

        _mgmtAuth.Setup(m => m.HasGlobalPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(false);
        _mgmtAuth.Setup(m => m.HasAnyPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.GetAuthorizedDepartmentIdsAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.None());
        // Group-management scope is limited to a single department.
        _mgmtAuth.Setup(m => m.GetAuthorizedDepartmentIdsAnyAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.LimitedTo(new List<Guid> { managedDeptId }));

        return new UserListViewModel(
            UserRepo(), GroupRepo(), RoleRepo(), DepartmentRepo(), ScopedRoleRepo(),
            _passwords, _ntHash.Object, _mgmtAuth.Object,
            UserContextFactoryFor((actor.Username, ctx)).Object,
            AuthStateFor(actor.Username),
            NullLogger<UserListViewModel>.Instance,
            ShareRepo(), _config.Object);
    }
}
