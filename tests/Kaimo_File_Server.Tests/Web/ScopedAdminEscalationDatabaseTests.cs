using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// End-to-end regression tests for the escalation chains of the scoped-permission audit
/// (plans/scoped-management-permissions-audit.md, C1–C3). Unlike the other view-model suites,
/// authorization is NOT mocked here: the view models run against the real
/// <see cref="ManagementAuthService"/> and the real <see cref="UserContextFactory"/>, so the
/// tests prove that a department admin cannot reach a global admin or a foreign share through
/// the department, group or user editors.
///
/// World: departments "Own" (with child "OwnChild") and "Foreign"; the actor holds the seeded
/// <see cref="ManagementPermission.DepartmentAdmin"/> role at "Own"; a global admin exists.
/// </summary>
public partial class ScopedAdminEscalationDatabaseTests : DatabaseTestBase
{
    private readonly PasswordService _passwords = new();
    private readonly Mock<INtHashProtector> _ntHash = new();
    private readonly Mock<IConfigRepository> _config = new();

    private readonly Department _own;
    private readonly Department _ownChild;
    private readonly Department _foreign;
    private readonly User _deptAdmin;
    private readonly User _globalAdmin;

    public ScopedAdminEscalationDatabaseTests()
    {
        _ntHash.Setup(p => p.Protect(It.IsAny<string>())).Returns<string>(s => s);
        _config.Setup(c => c.GetAsync(PasswordPolicy.ConfigKey, It.IsAny<PasswordPolicy>()))
            .ReturnsAsync(PasswordPolicy.Default());

        _own = SeedDepartment("Own");
        _ownChild = SeedDepartment("OwnChild", parentId: _own.Id);
        _foreign = SeedDepartment("Foreign");

        _deptAdmin = SeedUser("deptadmin");
        AddToDepartment(_own, _deptAdmin);
        Assign(_deptAdmin.Id, SeedRole(ManagementPermission.DepartmentAdmin), ScopeType.Department, _own.Id);

        _globalAdmin = SeedUser("admin");
        Assign(_globalAdmin.Id, SeedRole(ManagementPermission.FullAdmin), ScopeType.Global, Guid.Empty);
    }

    // ─────────────────────── Fixture helpers ───────────────────────

    private Role SeedRole(ManagementPermission perms)
    {
        var role = new Role(Guid.NewGuid(), $"Role-{perms}", perms);
        using var db = NewContext();
        db.Roles.Add(role);
        db.SaveChanges();
        return role;
    }

    private void Assign(Guid principalId, Role role, ScopeType scopeType, Guid scopeId)
    {
        using var db = NewContext();
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment(principalId, role.Id, scopeType, scopeId));
        db.SaveChanges();
    }

    private Group SeedGroup(string name, Guid? departmentId = null, Guid? id = null)
    {
        var group = new Group(id ?? Guid.NewGuid(), name, departmentId);
        using var db = NewContext();
        db.Groups.Add(group);
        db.SaveChanges();
        return group;
    }

    private void AddToDepartment(Department department, User user)
        => DepartmentRepo().AddUserAsync(department.Id, user.Id).GetAwaiter().GetResult();

    private Task<bool> IsMemberAsync(Department department, User user)
        => DepartmentRepo().IsUserInDepartmentAsync(user.Id, department.Id);

    private UserListViewModel BuildUserList(User actor)
        => new(UserRepo(), GroupRepo(), RoleRepo(), DepartmentRepo(), ScopedRoleRepo(),
            _passwords, _ntHash.Object, BuildManagementAuthService(),
            new UserContextFactory(DbFactory), AuthStateFor(actor.Username),
            NullLogger<UserListViewModel>.Instance, ShareRepo(), _config.Object);

    private DepartmentViewModel BuildDepartments(User actor)
        => new(DepartmentRepo(), UserRepo(), GroupRepo(), ShareRepo(), RoleRepo(), ScopedRoleRepo(),
            BuildManagementAuthService(), new UserContextFactory(DbFactory), AuthStateFor(actor.Username),
            NullLogger<DepartmentViewModel>.Instance);

    private async Task<DepartmentViewModel> EditDepartmentAsync(User actor, Department department)
    {
        var sut = BuildDepartments(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == department.Id));
        await sut.StartEditAsync();
        return sut;
    }

    // ═══════════════════ C1: password reset of a global admin ═══════════════════

    [Fact]
    public async Task DeptAdmin_CannotResetPasswordOfGlobalAdminInOwnDepartment()
    {
        AddToDepartment(_own, _globalAdmin);
        var sut = BuildUserList(_deptAdmin);
        await sut.LoadAsync();
        await sut.SelectUserAsync(_globalAdmin);

        Assert.False(sut.CanEditSelectedUser);
        Assert.False(sut.CanResetPasswordForSelected);

        sut.NewPassword = "N3wSecret!2026";
        sut.ConfirmPassword = "N3wSecret!2026";
        await sut.SaveUserAsync();

        Assert.NotNull(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.Equal("pw-hash", (await db.Users.FindAsync(_globalAdmin.Id))!.PasswordHash);
    }

    [Fact]
    public async Task DeptAdmin_CanStillResetPasswordOfPlainMember()
    {
        var bob = SeedUser("bob");
        AddToDepartment(_ownChild, bob);
        var sut = BuildUserList(_deptAdmin);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == bob.Id));
        await sut.StartEditUserAsync();

        sut.NewPassword = "N3wSecret!2026";
        sut.ConfirmPassword = "N3wSecret!2026";
        await sut.SaveUserAsync();

        Assert.Null(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.True(_passwords.VerifyPassword("N3wSecret!2026", (await db.Users.FindAsync(bob.Id))!.PasswordHash));
    }

    [Fact]
    public async Task DeptAdmin_CannotDeleteUserWhoAlsoBelongsToForeignDepartment()
    {
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);
        AddToDepartment(_foreign, bob);
        var sut = BuildUserList(_deptAdmin);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == bob.Id));

        Assert.False(sut.CanDeleteSelectedUser);
        await sut.ConfirmDeleteUserAsync();

        await using var db = NewContext();
        Assert.NotNull(await db.Users.FindAsync(bob.Id));
    }

    // ═══════════════════ C1: department membership ═══════════════════

    [Fact]
    public async Task DeptAdmin_AddingGlobalAdminAsDepartmentMember_IsRefused()
    {
        var sut = await EditDepartmentAsync(_deptAdmin, _own);

        Assert.DoesNotContain(sut.EditMembers, m => m.Item.Id == _globalAdmin.Id);

        // A forged circuit message could still tick the user: the save must refuse it.
        sut.EditMembers.Add(new CheckboxItem<User>(_globalAdmin, true));
        await sut.SaveAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.False(await IsMemberAsync(_own, _globalAdmin));
    }

    [Fact]
    public async Task DeptAdmin_AddingForeignUserAsDepartmentMember_IsRefused()
    {
        var carol = SeedUser("carol");
        AddToDepartment(_foreign, carol);
        var sut = await EditDepartmentAsync(_deptAdmin, _own);

        Assert.DoesNotContain(sut.EditMembers, m => m.Item.Id == carol.Id);
        sut.EditMembers.Add(new CheckboxItem<User>(carol, true));
        await sut.SaveAsync();

        Assert.False(await IsMemberAsync(_own, carol));
    }

    [Fact]
    public async Task DeptAdmin_CanAddMemberOfChildDepartment()
    {
        var bob = SeedUser("bob");
        AddToDepartment(_ownChild, bob);
        var sut = await EditDepartmentAsync(_deptAdmin, _own);

        sut.EditMembers.Single(m => m.Item.Id == bob.Id).IsChecked = true;
        await sut.SaveAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.True(await IsMemberAsync(_own, bob));
    }

    [Fact]
    public async Task EditDepartmentWithoutAssignDepartments_CannotChangeMembers_ButCanRename()
    {
        var editor = SeedUser("editor");
        AddToDepartment(_own, editor);
        Assign(editor.Id, SeedRole(ManagementPermission.EditDepartment | ManagementPermission.ViewDepartment
                                   | ManagementPermission.UserAdmin), ScopeType.Department, _own.Id);
        var bob = SeedUser("bob");
        AddToDepartment(_ownChild, bob);

        var sut = await EditDepartmentAsync(editor, _own);
        sut.EditMembers.Add(new CheckboxItem<User>(bob, true));
        await sut.SaveAsync();
        Assert.False(await IsMemberAsync(_own, bob));

        sut = await EditDepartmentAsync(editor, _own);
        sut.EditName = "Own renamed";
        await sut.SaveAsync();
        Assert.Null(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.Equal("Own renamed", (await db.Departments.FindAsync(_own.Id))!.Name);
    }

    [Fact]
    public void RoleEditor_OffersAssignDepartments()
        => Assert.Contains(UserListViewModel.PermissionGroups.SelectMany(g => g.Flags),
            f => f.Flag == ManagementPermission.AssignDepartments);

    [Fact]
    public async Task GlobalAdmin_CanAddAnyUserAsDepartmentMember()
    {
        var carol = SeedUser("carol");
        AddToDepartment(_foreign, carol);
        var sut = await EditDepartmentAsync(_globalAdmin, _own);

        sut.EditMembers.Single(m => m.Item.Id == carol.Id).IsChecked = true;
        await sut.SaveAsync();

        Assert.True(await IsMemberAsync(_own, carol));
    }
}
