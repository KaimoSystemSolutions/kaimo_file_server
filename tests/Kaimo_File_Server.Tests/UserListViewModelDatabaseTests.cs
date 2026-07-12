using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Regression tests pinning <see cref="UserListViewModel"/> mutations to real database
/// state. This is the widest-surface view model (users, groups, roles, scoped
/// assignments, password resets), so its methods are the most valuable to lock down:
/// a create that doesn't persist, a profile save that drops a field, a password reset
/// that stores the old hash, or a role permission edit that never saves are exactly
/// the kind of silent regressions this suite is built to catch.
///
/// A real <see cref="PasswordService"/> is used so password mutations are verified the
/// way production authenticates — by hashing and re-verifying, not by string equality.
/// </summary>
public class UserListViewModelDatabaseTests : DatabaseTestBase
{
    private readonly Mock<IManagementAuthService> _mgmtAuth = new();
    private readonly PasswordService _passwords = new();
    private readonly Mock<INtHashProtector> _ntHash = new();
    private readonly Mock<IConfigRepository> _config = new();

    public UserListViewModelDatabaseTests()
    {
        // Passthrough protector — the encryption round-trip is covered by its own suite.
        _ntHash.Setup(p => p.Protect(It.IsAny<string>())).Returns<string>(s => s);
        _ntHash.Setup(p => p.Unprotect(It.IsAny<string>())).Returns<string>(s => s);
        // Default password policy (min length 6, no character classes).
        _config.Setup(c => c.GetAsync(PasswordPolicy.ConfigKey, It.IsAny<PasswordPolicy>()))
            .ReturnsAsync(PasswordPolicy.Default());
    }

    private UserListViewModel BuildGlobalAdminSut(User actor)
    {
        var ctx = ContextFor(actor);

        _mgmtAuth.Setup(m => m.HasGlobalPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.HasAnyPermissionAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.CanManageUserAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.CanManageGroupAsync(It.IsAny<UserContext>(), It.IsAny<Guid>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.CanCreateUserInDepartmentAsync(It.IsAny<UserContext>(), It.IsAny<Guid>()))
            .ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.GetAuthorizedDepartmentIdsAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>()))
            .ReturnsAsync(AuthorizedScopeResult.Unrestricted());

        return new UserListViewModel(
            UserRepo(), GroupRepo(), RoleRepo(), DepartmentRepo(), ScopedRoleRepo(),
            _passwords, _ntHash.Object, _mgmtAuth.Object,
            UserContextFactoryFor((actor.Username, ctx)).Object,
            AuthStateFor(actor.Username),
            NullLogger<UserListViewModel>.Instance,
            ShareRepo(), _config.Object);
    }

    private Group SeedGroup(string name)
    {
        var group = new Group(Guid.NewGuid(), name);
        using var db = NewContext();
        db.Groups.Add(group);
        db.SaveChanges();
        return group;
    }

    private Role SeedRole(string name, ManagementPermission perms = ManagementPermission.None, bool system = false)
    {
        var role = new Role(Guid.NewGuid(), name, perms, system);
        using var db = NewContext();
        db.Roles.Add(role);
        db.SaveChanges();
        return role;
    }

    // ═══════════════════ Create user ═══════════════════

    [Fact]
    public async Task CreateUserAsync_PersistsUserWithProfileAndPassword()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Sales");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        sut.CreateUserName = "Charlie";
        sut.CreateUserUsername = "charlie";
        sut.CreateUserPassword = "Passw0rd!";
        sut.CreateUserEmail = "charlie@example.com";
        sut.CreateUserIsEnabled = true;
        sut.CreateUserDepartmentId = dept.Id;
        await sut.CreateUserAsync();

        await using var db = NewContext();
        var row = await db.Users.SingleOrDefaultAsync(u => u.Username == "charlie");
        Assert.NotNull(row);
        Assert.Equal("Charlie", row!.Name);
        Assert.Equal("charlie@example.com", row.Email);
        Assert.True(_passwords.VerifyPassword("Passw0rd!", row.PasswordHash)); // real hash stored
        // And the user was attached to the chosen department.
        Assert.True(await db.DepartmentUsers.AnyAsync(du => du.UserId == row.Id && du.DepartmentId == dept.Id));
    }

    [Fact]
    public async Task CreateUserAsync_DuplicateUsername_IsRejected()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Sales");
        SeedUser("charlie");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        sut.CreateUserName = "Charlie";
        sut.CreateUserUsername = "charlie";
        sut.CreateUserPassword = "Passw0rd!";
        sut.CreateUserDepartmentId = dept.Id;
        await sut.CreateUserAsync();

        Assert.NotNull(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.Username == "charlie"));
    }

    [Fact]
    public async Task CreateUserAsync_TooShortPassword_PersistsNothing()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Sales");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        sut.CreateUserName = "Charlie";
        sut.CreateUserUsername = "charlie";
        sut.CreateUserPassword = "abc"; // below min length 6
        sut.CreateUserDepartmentId = dept.Id;
        await sut.CreateUserAsync();

        Assert.NotNull(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.False(await db.Users.AnyAsync(u => u.Username == "charlie"));
    }

    // ═══════════════════ Create group / role ═══════════════════

    [Fact]
    public async Task CreateGroupAsync_PersistsGroup()
    {
        var actor = SeedUser("admin");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        sut.CreateGroupName = "Marketing";
        await sut.CreateGroupAsync();

        await using var db = NewContext();
        Assert.True(await db.Groups.AnyAsync(g => g.Name == "Marketing"));
    }

    [Fact]
    public async Task CreateRoleAsync_PersistsRole()
    {
        var actor = SeedUser("admin");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        sut.CreateRoleName = "Auditor";
        await sut.CreateRoleAsync();

        await using var db = NewContext();
        Assert.True(await db.Roles.AnyAsync(r => r.Name == "Auditor"));
    }

    // ═══════════════════ Save user ═══════════════════

    [Fact]
    public async Task SaveUserAsync_PersistsProfileChanges()
    {
        var actor = SeedUser("admin");
        var target = SeedUser("bob", "Bob", isEnabled: true);
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));
        await sut.StartEditUserAsync();

        sut.EditUserName = "Bobby";
        sut.EditUserEmail = "bobby@example.com";
        sut.EditUserIsEnabled = false; // disable the account
        await sut.SaveUserAsync();

        await using var db = NewContext();
        var row = await db.Users.FindAsync(target.Id);
        Assert.Equal("Bobby", row!.Name);
        Assert.Equal("bobby@example.com", row.Email);
        Assert.False(row.IsEnabled);
    }

    [Fact]
    public async Task SaveUserAsync_WithNewPassword_PersistsNewHash()
    {
        var actor = SeedUser("admin");
        var target = SeedUser("bob");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));
        await sut.StartEditUserAsync();

        sut.NewPassword = "N3wSecret!";
        sut.ConfirmPassword = "N3wSecret!";
        await sut.SaveUserAsync();

        await using var db = NewContext();
        var row = await db.Users.FindAsync(target.Id);
        Assert.True(_passwords.VerifyPassword("N3wSecret!", row!.PasswordHash));
    }

    [Fact]
    public async Task SaveUserAsync_MismatchedPassword_DoesNotChangeHash()
    {
        var actor = SeedUser("admin");
        var target = SeedUser("bob");
        var originalHash = (await NewContext().Users.FindAsync(target.Id))!.PasswordHash;
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));
        await sut.StartEditUserAsync();

        sut.NewPassword = "N3wSecret!";
        sut.ConfirmPassword = "different";
        await sut.SaveUserAsync();

        Assert.NotNull(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.Equal(originalHash, (await db.Users.FindAsync(target.Id))!.PasswordHash);
    }

    [Fact]
    public async Task SaveUserAsync_AssignsCheckedRoles()
    {
        var actor = SeedUser("admin");
        var target = SeedUser("bob");
        var role = SeedRole("Reviewer");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));
        await sut.StartEditUserAsync();

        foreach (var r in sut.EditUserRoles)
            r.IsChecked = r.Item.Id == role.Id;
        await sut.SaveUserAsync();

        await using var db = NewContext();
        Assert.True(await db.UserRoles.AnyAsync(ur => ur.UserId == target.Id && ur.RoleId == role.Id));
    }

    // ═══════════════════ Save group ═══════════════════

    [Fact]
    public async Task SaveGroupAsync_PersistsMembership()
    {
        var actor = SeedUser("admin");
        var group = SeedGroup("Team");
        var alice = SeedUser("alice");
        var sut = BuildGlobalAdminSut(actor);
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.SelectGroupAsync(sut.Groups.Single(g => g.Id == group.Id));
        await sut.StartEditGroupAsync();

        foreach (var m in sut.EditGroupMembers)
            m.IsChecked = m.Item.Id == alice.Id;
        await sut.SaveGroupAsync();

        await using var db = NewContext();
        Assert.True(await db.UserGroups.AnyAsync(ug => ug.GroupId == group.Id && ug.UserId == alice.Id));
    }

    // ═══════════════════ Save role ═══════════════════

    [Fact]
    public async Task SaveRoleAsync_PersistsPermissionBitmask()
    {
        var actor = SeedUser("admin");
        var role = SeedRole("Custom");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Roles);
        await sut.SelectRoleAsync(sut.Roles.Single(r => r.Id == role.Id));
        await sut.StartEditRoleAsync();

        sut.ToggleEditPermission(ManagementPermission.CreateUsers, true);
        sut.ToggleEditPermission(ManagementPermission.DeleteUsers, true);
        await sut.SaveRoleAsync();

        await using var db = NewContext();
        var row = await db.Roles.FindAsync(role.Id);
        Assert.True(row!.ManagementPermissions.HasFlag(ManagementPermission.CreateUsers));
        Assert.True(row.ManagementPermissions.HasFlag(ManagementPermission.DeleteUsers));
    }

    [Fact]
    public async Task SaveRoleAsync_SystemRole_KeepsPermissionsImmutable()
    {
        var actor = SeedUser("admin");
        var role = SeedRole("Administrator", ManagementPermission.FullAdmin, system: true);
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Roles);
        await sut.SelectRoleAsync(sut.Roles.Single(r => r.Id == role.Id));
        await sut.StartEditRoleAsync();

        sut.EditRolePermissions = ManagementPermission.None; // attempt to strip a system role
        await sut.SaveRoleAsync();

        await using var db = NewContext();
        var row = await db.Roles.FindAsync(role.Id);
        Assert.Equal(ManagementPermission.FullAdmin, row!.ManagementPermissions); // unchanged
    }

    // ═══════════════════ Delete ═══════════════════

    [Fact]
    public async Task ConfirmDeleteUserAsync_RemovesUser()
    {
        var actor = SeedUser("admin");
        var target = SeedUser("bob");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));
        sut.RequestDeleteUser();
        await sut.ConfirmDeleteUserAsync();

        await using var db = NewContext();
        Assert.False(await db.Users.AnyAsync(u => u.Id == target.Id));
    }

    [Fact]
    public async Task ConfirmDeleteRoleAsync_RemovesRole()
    {
        var actor = SeedUser("admin");
        var role = SeedRole("Temp");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Roles);
        await sut.SelectRoleAsync(sut.Roles.Single(r => r.Id == role.Id));
        sut.RequestDeleteRole();
        await sut.ConfirmDeleteRoleAsync();

        await using var db = NewContext();
        Assert.False(await db.Roles.AnyAsync(r => r.Id == role.Id));
    }

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
