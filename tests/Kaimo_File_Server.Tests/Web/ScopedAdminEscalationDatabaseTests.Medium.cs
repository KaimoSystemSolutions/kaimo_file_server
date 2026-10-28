using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Phase 3 of the scoped-permission audit, user-management part: the account toggles need their
/// own rights (F8), role definitions are deleted and demoted only within the actor's authority
/// (F11), and the recipient of a role assignment must be within the actor's reach (F13).
/// </summary>
public partial class ScopedAdminEscalationDatabaseTests
{
    private async Task<User> StoredUserAsync(User user)
    {
        await using var db = NewContext();
        return (await db.Users.FindAsync(user.Id))!;
    }

    // ═══════════════════ F8: enable/disable and file services ═══════════════════

    [Fact]
    public async Task ProfileEditorWithoutEnableDisable_CannotDisableUserOrChangeServices()
    {
        var editor = SeedUser("editor");
        AddToDepartment(_own, editor);
        Assign(editor.Id, SeedRole(ManagementPermission.EditUserProfiles | ManagementPermission.ResetPasswords),
            ScopeType.Department, _own.Id);
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);

        var sut = await EditUserAsync(editor, bob);
        Assert.False(sut.CanEditAccountEnabledForSelected);
        Assert.False(sut.CanEditServiceAccessForSelected);

        sut.EditUserIsEnabled = false;
        sut.SetEditUserService(UserServiceAccess.Web, false);
        sut.EditUserDescription = "edited";
        await sut.SaveUserAsync();

        Assert.Null(sut.ErrorMessage);
        var stored = await StoredUserAsync(bob);
        Assert.Equal("edited", stored.Description);
        Assert.True(stored.IsEnabled);
        Assert.Equal(UserServiceAccess.All, stored.ServiceAccess);
    }

    [Fact]
    public async Task ProfileEditorWithoutResetPasswords_CannotChangeMayChangePasswordFlag()
    {
        var editor = SeedUser("editor");
        AddToDepartment(_own, editor);
        Assign(editor.Id, SeedRole(ManagementPermission.EditUserProfiles | ManagementPermission.EnableDisableUsers),
            ScopeType.Department, _own.Id);
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);

        var sut = await EditUserAsync(editor, bob);
        Assert.False(sut.CanEditCanChangePasswordForSelected);
        Assert.True(sut.CanEditAccountEnabledForSelected);

        sut.EditUserCanChangePassword = false;
        sut.EditUserIsEnabled = false;
        await sut.SaveUserAsync();

        Assert.Null(sut.ErrorMessage);
        var stored = await StoredUserAsync(bob);
        Assert.True(stored.CanChangePassword);
        Assert.False(stored.IsEnabled);
    }

    [Fact]
    public async Task DeptAdmin_CanDisableUserAndChangeServices()
    {
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);

        var sut = await EditUserAsync(_deptAdmin, bob);
        Assert.True(sut.CanEditAccountEnabledForSelected);
        Assert.True(sut.CanEditServiceAccessForSelected);
        Assert.True(sut.CanEditCanChangePasswordForSelected);

        sut.EditUserIsEnabled = false;
        sut.EditUserCanChangePassword = false;
        sut.SetEditUserService(UserServiceAccess.Web, false);
        await sut.SaveUserAsync();

        Assert.Null(sut.ErrorMessage);
        var stored = await StoredUserAsync(bob);
        Assert.False(stored.IsEnabled);
        Assert.False(stored.CanChangePassword);
        Assert.False(stored.ServiceAccess.HasFlag(UserServiceAccess.Web));
    }

    [Fact]
    public void RoleEditor_OffersEnableDisableUsers()
        => Assert.Contains(UserListViewModel.PermissionGroups.SelectMany(g => g.Flags),
            f => f.Flag == ManagementPermission.EnableDisableUsers);

    // ═══════════════════ F11: role definitions ═══════════════════

    private Role SeedNamedRole(string name, ManagementPermission perms, bool isSystem = false)
    {
        var role = new Role(Guid.NewGuid(), name, perms, isSystem);
        using var db = NewContext();
        db.Roles.Add(role);
        db.SaveChanges();
        return role;
    }

    private async Task<UserListViewModel> SelectRoleAsync(User actor, Role role)
    {
        var sut = BuildUserList(actor);
        await sut.LoadAsync();
        await sut.SelectRoleAsync(role);
        return sut;
    }

    private async Task<Role?> StoredRoleAsync(Role role)
    {
        await using var db = NewContext();
        return await db.Roles.FindAsync(role.Id);
    }

    /// <summary>A global role editor: AssignRoles and user management, nothing else.</summary>
    private User SeedGlobalRoleEditor()
    {
        var editor = SeedUser("roleeditor");
        Assign(editor.Id, SeedRole(ManagementPermission.AssignRoles | ManagementPermission.UserAdmin),
            ScopeType.Global, Guid.Empty);
        return editor;
    }

    [Fact]
    public async Task DeletingSystemRole_IsRefusedServerSide()
    {
        var system = SeedNamedRole("SystemThing", ManagementPermission.ResetPasswords, isSystem: true);
        var sut = await SelectRoleAsync(_globalAdmin, system);

        sut.RequestDeleteRole();
        await sut.ConfirmDeleteRoleAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.NotNull(await StoredRoleAsync(system));
    }

    [Fact]
    public async Task DeletingRoleWithBitsBeyondOwnGlobalPermissions_IsRefused()
    {
        var editor = SeedGlobalRoleEditor();
        var certRole = SeedNamedRole("Certs", ManagementPermission.ManageCertificates);

        var sut = await SelectRoleAsync(editor, certRole);
        sut.RequestDeleteRole();
        await sut.ConfirmDeleteRoleAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.NotNull(await StoredRoleAsync(certRole));
    }

    [Fact]
    public async Task DeletingRoleWithinOwnPermissions_RemovesRoleAndItsAssignments()
    {
        var editor = SeedGlobalRoleEditor();
        var helper = SeedNamedRole("Helper", ManagementPermission.ResetPasswords);
        var bob = SeedUser("bob");
        Assign(bob.Id, helper, ScopeType.Department, _own.Id);

        var sut = await SelectRoleAsync(editor, helper);
        sut.RequestDeleteRole();
        await sut.ConfirmDeleteRoleAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.Null(await StoredRoleAsync(helper));
        await using var db = NewContext();
        Assert.False(await db.ScopedRoleAssignments.AnyAsync(a => a.RoleId == helper.Id));
    }

    [Fact]
    public async Task RemovingBitsBeyondOwnGlobalPermissions_IsRefused()
    {
        var editor = SeedGlobalRoleEditor();
        var mixed = SeedNamedRole("Mixed", ManagementPermission.ResetPasswords | ManagementPermission.ManageCertificates);

        var sut = await SelectRoleAsync(editor, mixed);
        await sut.StartEditRoleAsync();
        sut.EditRolePermissions = ManagementPermission.ResetPasswords;
        await sut.SaveRoleAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.Equal(mixed.ManagementPermissions, (await StoredRoleAsync(mixed))!.ManagementPermissions);
    }

    [Fact]
    public async Task DeletingOrDemotingTheOnlyAdminRole_IsRefused()
    {
        // The fixture's global admin holds a custom FullAdmin role and is the only admin.
        var adminRole = await RoleOfAsync(_globalAdmin);

        var sut = await SelectRoleAsync(_globalAdmin, adminRole);
        sut.RequestDeleteRole();
        await sut.ConfirmDeleteRoleAsync();
        Assert.NotNull(sut.ErrorMessage);
        Assert.NotNull(await StoredRoleAsync(adminRole));

        sut = await SelectRoleAsync(_globalAdmin, adminRole);
        await sut.StartEditRoleAsync();
        sut.EditRolePermissions = adminRole.ManagementPermissions & ~ManagementPermission.ViewSecurityMonitor;
        await sut.SaveRoleAsync();
        Assert.NotNull(sut.ErrorMessage);
        Assert.Equal(adminRole.ManagementPermissions, (await StoredRoleAsync(adminRole))!.ManagementPermissions);
    }

    [Fact]
    public async Task DeletingAdminRole_IsAllowedWhileAnotherAdminSourceRemains()
    {
        var spareRole = SeedNamedRole("Spare admin", ManagementPermission.FullAdmin);
        var other = SeedUser("otheradmin");
        Assign(other.Id, spareRole, ScopeType.Global, Guid.Empty);

        var sut = await SelectRoleAsync(_globalAdmin, spareRole);
        sut.RequestDeleteRole();
        await sut.ConfirmDeleteRoleAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.Null(await StoredRoleAsync(spareRole));
    }

    private async Task<Role> RoleOfAsync(User user)
    {
        await using var db = NewContext();
        var roleId = (await db.ScopedRoleAssignments.FirstAsync(a => a.PrincipalId == user.Id)).RoleId;
        return (await db.Roles.FindAsync(roleId))!;
    }

    // ═══════════════════ F13: assignment recipients ═══════════════════

    /// <summary>Department admin of "Own" who may also delegate roles there.</summary>
    private User SeedDelegator()
    {
        var delegator = SeedUser("delegator");
        AddToDepartment(_own, delegator);
        Assign(delegator.Id, SeedRole(ManagementPermission.DepartmentAdmin | ManagementPermission.AssignRoles),
            ScopeType.Department, _own.Id);
        return delegator;
    }

    private async Task<DepartmentViewModel> StartDepartmentAssignmentAsync(User actor, Department department, Role role)
    {
        var sut = BuildDepartments(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == department.Id));
        await sut.StartAddAssignmentAsync();
        sut.NewAssignmentRoleId = role.Id;
        return sut;
    }

    private async Task<bool> HasAssignmentAsync(Guid principalId, Role role)
    {
        await using var db = NewContext();
        return await db.ScopedRoleAssignments.AnyAsync(a => a.PrincipalId == principalId && a.RoleId == role.Id);
    }

    [Fact]
    public async Task Delegator_CannotGrantRoleToUserOutsideOwnReach()
    {
        var delegator = SeedDelegator();
        var helper = SeedNamedRole("Helper", ManagementPermission.ResetPasswords);
        var carol = SeedUser("carol");
        AddToDepartment(_foreign, carol);

        var sut = await StartDepartmentAssignmentAsync(delegator, _own, helper);
        Assert.DoesNotContain(sut.AllUsers, u => u.Id == carol.Id || u.Id == _globalAdmin.Id);

        sut.NewAssignmentPrincipalId = carol.Id; // forged
        await sut.CreateAssignmentAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.False(await HasAssignmentAsync(carol.Id, helper));
    }

    [Fact]
    public async Task Delegator_CannotGrantRoleToGlobalGroup()
    {
        var delegator = SeedDelegator();
        var helper = SeedNamedRole("Helper", ManagementPermission.ResetPasswords);
        var readers = SeedGroup("Readers"); // Global department
        var team = SeedGroup("Team", _own.Id);

        var sut = await StartDepartmentAssignmentAsync(delegator, _own, helper);
        Assert.DoesNotContain(sut.AllGroups, g => g.Id == readers.Id);
        Assert.Contains(sut.AllGroups, g => g.Id == team.Id);

        sut.NewAssignmentPrincipalId = readers.Id; // forged
        await sut.CreateAssignmentAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.False(await HasAssignmentAsync(readers.Id, helper));
    }

    [Fact]
    public async Task Delegator_CanGrantRoleToMemberOfDescendant()
    {
        var delegator = SeedDelegator();
        var helper = SeedNamedRole("Helper", ManagementPermission.ResetPasswords);
        var bob = SeedUser("bob");
        AddToDepartment(_ownChild, bob);

        var sut = await StartDepartmentAssignmentAsync(delegator, _own, helper);
        Assert.Contains(sut.AllUsers, u => u.Id == bob.Id);
        sut.NewAssignmentPrincipalId = bob.Id;
        await sut.CreateAssignmentAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.True(await HasAssignmentAsync(bob.Id, helper));
    }

    [Fact]
    public async Task Delegator_CanGrantChildScopedRoleToMemberOfOwnDepartment()
    {
        // The recipient sits outside the target scope (OwnChild) but within the actor's reach (Own).
        var delegator = SeedDelegator();
        var helper = SeedNamedRole("Helper", ManagementPermission.ResetPasswords);
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);

        var sut = await StartDepartmentAssignmentAsync(delegator, _ownChild, helper);
        sut.NewAssignmentPrincipalId = bob.Id;
        await sut.CreateAssignmentAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.True(await HasAssignmentAsync(bob.Id, helper));
    }

    [Fact]
    public async Task GlobalAdmin_CanGrantRoleToAnyone()
    {
        var helper = SeedNamedRole("Helper", ManagementPermission.ResetPasswords);
        var carol = SeedUser("carol");
        AddToDepartment(_foreign, carol);
        var readers = SeedGroup("Readers");

        foreach (var principal in new[] { carol.Id, readers.Id })
        {
            var sut = await StartDepartmentAssignmentAsync(_globalAdmin, _foreign, helper);
            Assert.Contains(principal, sut.AllUsers.Select(u => u.Id).Concat(sut.AllGroups.Select(g => g.Id)));
            sut.NewAssignmentPrincipalId = principal;
            await sut.CreateAssignmentAsync();
            Assert.Null(sut.ErrorMessage);
            Assert.True(await HasAssignmentAsync(principal, helper));
        }
    }

    [Fact]
    public async Task ShareScopedDelegate_CannotGrantRolesToAnyone()
    {
        var share = SeedShare("scoped", path: "/data/pool-01/scoped", departmentId: _own.Id);
        var delegate_ = SeedUser("sharedelegate");
        AddToDepartment(_own, delegate_);
        Assign(delegate_.Id, SeedRole(ManagementPermission.ManageShareAcls | ManagementPermission.AssignRoles),
            ScopeType.Share, share.Id);
        var aclRole = SeedNamedRole("ACL helper", ManagementPermission.ManageShareAcls);
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);

        var sut = BuildUserList(delegate_);
        await sut.LoadAsync();
        sut.StartAddAssignment();
        await sut.LoadAssignmentFormDataAsync();
        Assert.DoesNotContain(sut.AllUsers, u => u.Id == bob.Id);

        sut.NewAssignmentRoleId = aclRole.Id;
        sut.NewAssignmentPrincipalId = bob.Id;
        sut.NewAssignmentScopeType = ScopeType.Share;
        sut.NewAssignmentScopeId = share.Id;
        await sut.CreateAssignmentAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.False(await HasAssignmentAsync(bob.Id, aclRole));
    }
}
