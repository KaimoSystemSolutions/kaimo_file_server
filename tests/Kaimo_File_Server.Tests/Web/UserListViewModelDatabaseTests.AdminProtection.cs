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
    // ═══════════════════ Last-admin protection ═══════════════════

    private Role SeedAdminRole(string name = "Administrator")
        => SeedRole(name, ManagementPermission.FullAdmin, system: true);

    private ScopedRoleAssignment SeedGlobalAssignment(Guid principalId, Guid roleId)
    {
        var assignment = new ScopedRoleAssignment(principalId, roleId, ScopeType.Global, Guid.Empty);
        using var db = NewContext();
        db.ScopedRoleAssignments.Add(assignment);
        db.SaveChanges();
        return assignment;
    }

    private void SeedGroupMember(Guid groupId, Guid userId)
    {
        using var db = NewContext();
        db.UserGroups.Add(new UserGroup(userId, groupId));
        db.SaveChanges();
    }

    [Fact]
    public async Task SaveUserAsync_SoleAdminLeavesAdminsGroup_IsBlocked()
    {
        // The Admins group membership is the admin switch of the user form (it mirrors the
        // system-wide Administrator role), so leaving it as the last admin must be refused.
        var admin = SeedUser("admin");
        var adminRole = SeedWellKnownAdminRole();
        SeedGroupWithId(WellKnownGUIDs.GROUP_ADMINS, "Admins");
        var assignment = SeedGlobalAssignment(admin.Id, adminRole.Id);
        SeedGroupMember(WellKnownGUIDs.GROUP_ADMINS, admin.Id);

        var sut = BuildGlobalAdminSut(admin);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == admin.Id));
        await sut.StartEditUserAsync();

        foreach (var g in sut.EditUserGroups) g.IsChecked = false;
        await sut.SaveUserAsync();

        Assert.Equal(Resources.Web_Error_LastAdmin, sut.ErrorMessage);
        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.Id == assignment.Id));
    }

    [Fact]
    public async Task SaveUserAsync_SecondAdminExists_LeavingAdminsGroupRevokesAdminRole()
    {
        var admin = SeedUser("admin");
        var admin2 = SeedUser("admin2");
        var adminRole = SeedWellKnownAdminRole();
        SeedGroupWithId(WellKnownGUIDs.GROUP_ADMINS, "Admins");
        var ownAssignment = SeedGlobalAssignment(admin.Id, adminRole.Id);
        SeedGroupMember(WellKnownGUIDs.GROUP_ADMINS, admin.Id);
        SeedGlobalAssignment(admin2.Id, adminRole.Id);
        SeedGroupMember(WellKnownGUIDs.GROUP_ADMINS, admin2.Id);

        var sut = BuildGlobalAdminSut(admin);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == admin.Id));
        await sut.StartEditUserAsync();

        foreach (var g in sut.EditUserGroups) g.IsChecked = false;
        await sut.SaveUserAsync();

        Assert.NotEqual(Resources.Web_Error_LastAdmin, sut.ErrorMessage);
        await using var db = NewContext();
        Assert.False(await db.ScopedRoleAssignments.AnyAsync(a => a.Id == ownAssignment.Id));
    }

    [Fact]
    public async Task SaveUserAsync_OtherActorDisablesSoleAdmin_IsBlocked()
    {
        // The actor manages the admin's profile without being a global admin themselves
        // (e.g. a scoped grant); disabling the only enabled global admin locks everyone out.
        var actor = SeedUser("helpdesk");
        var admin = SeedUser("admin");
        var adminRole = SeedAdminRole();
        SeedGlobalAssignment(admin.Id, adminRole.Id);

        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == admin.Id));
        await sut.StartEditUserAsync();

        sut.EditUserIsEnabled = false;
        await sut.SaveUserAsync();

        Assert.Equal(Resources.Web_Error_LastAdmin, sut.ErrorMessage);
        await using var db = NewContext();
        Assert.True((await db.Users.FindAsync(admin.Id))!.IsEnabled);
    }

    [Fact]
    public async Task ConfirmDeleteUserAsync_SoleAdmin_IsBlocked()
    {
        var admin = SeedUser("admin");
        var adminRole = SeedAdminRole();
        SeedGlobalAssignment(admin.Id, adminRole.Id);

        var sut = BuildGlobalAdminSut(admin);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == admin.Id));
        sut.RequestDeleteUser();
        await sut.ConfirmDeleteUserAsync();

        Assert.Equal(Resources.Web_Error_LastAdmin, sut.ErrorMessage);
        await using var db = NewContext();
        Assert.True(await db.Users.AnyAsync(u => u.Id == admin.Id));
    }

    [Fact]
    public async Task DeleteAssignmentAsync_SoleGlobalAdminAssignment_IsBlocked()
    {
        var admin = SeedUser("admin");
        var adminRole = SeedAdminRole();
        var assignment = SeedGlobalAssignment(admin.Id, adminRole.Id);

        var sut = BuildGlobalAdminSut(admin);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Roles);
        await sut.SelectRoleAsync(sut.Roles.Single(r => r.Id == adminRole.Id));

        await sut.DeleteAssignmentAsync(assignment.Id);

        Assert.Equal(Resources.Web_Error_LastAdmin, sut.ErrorMessage);
        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.Id == assignment.Id));
    }

    [Fact]
    public async Task DeleteAssignmentAsync_SecondAdminExists_IsAllowed()
    {
        var admin = SeedUser("admin");
        var admin2 = SeedUser("admin2");
        var adminRole = SeedAdminRole();
        var ownAssignment = SeedGlobalAssignment(admin.Id, adminRole.Id);
        SeedGlobalAssignment(admin2.Id, adminRole.Id);

        var sut = BuildGlobalAdminSut(admin);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Roles);
        await sut.SelectRoleAsync(sut.Roles.Single(r => r.Id == adminRole.Id));

        await sut.DeleteAssignmentAsync(ownAssignment.Id);

        Assert.NotEqual(Resources.Web_Error_LastAdmin, sut.ErrorMessage);
        await using var db = NewContext();
        Assert.False(await db.ScopedRoleAssignments.AnyAsync(a => a.Id == ownAssignment.Id));
    }

    [Fact]
    public async Task SaveUserAsync_AdminInheritedFromGroup_PlainSaveIsAllowed()
    {
        // The user is a global admin ONLY through group membership. A plain profile save
        // must NOT be blocked — the group-inherited admin survives.
        var admin = SeedUser("admin");
        var adminRole = SeedAdminRole();
        var adminGroup = SeedGroup("Admins");
        SeedGroupMember(adminGroup.Id, admin.Id);
        SeedGlobalAssignment(adminGroup.Id, adminRole.Id);

        var sut = BuildGlobalAdminSut(admin);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == admin.Id));
        await sut.StartEditUserAsync();

        sut.EditUserDescription = "changed";
        await sut.SaveUserAsync();

        Assert.NotEqual(Resources.Web_Error_LastAdmin, sut.ErrorMessage);
    }

    // ═══════════════════ Admin role <-> Admins group coupling ═══════════════════

    // The coupling keys on the canonical well-known ids, so tests seed those.
    private Role SeedWellKnownAdminRole()
    {
        var role = new Role(WellKnownGUIDs.ROLE_ADMIN, "Administrator", ManagementPermission.FullAdmin, true);
        using var db = NewContext();
        db.Roles.Add(role);
        db.SaveChanges();
        return role;
    }

    private Group SeedGroupWithId(Guid id, string name)
    {
        var group = new Group(id, name);
        using var db = NewContext();
        db.Groups.Add(group);
        db.SaveChanges();
        return group;
    }

    [Fact]
    public async Task SaveUserAsync_JoinAdminsGroup_GrantsAdminRole()
    {
        var actor = SeedUser("admin");
        var adminRole = SeedWellKnownAdminRole();
        SeedGroupWithId(WellKnownGUIDs.GROUP_ADMINS, "Admins");
        var target = SeedUser("bob");

        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));
        await sut.StartEditUserAsync();
        foreach (var g in sut.EditUserGroups) g.IsChecked = g.Item.Id == WellKnownGUIDs.GROUP_ADMINS;
        await sut.SaveUserAsync();

        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.PrincipalId == target.Id
            && a.RoleId == adminRole.Id && a.ScopeType == ScopeType.Global));
    }

    [Fact]
    public async Task SaveGroupAsync_AddUserToAdmins_GrantsAdminRole()
    {
        var actor = SeedUser("admin");
        SeedWellKnownAdminRole();
        SeedGroupWithId(WellKnownGUIDs.GROUP_ADMINS, "Admins");
        var target = SeedUser("bob");

        var sut = BuildGlobalAdminSut(actor);
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.SelectGroupAsync(sut.Groups.Single(g => g.Id == WellKnownGUIDs.GROUP_ADMINS));
        await sut.StartEditGroupAsync();
        foreach (var m in sut.EditGroupMembers) m.IsChecked = m.Item.Id == target.Id;
        await sut.SaveGroupAsync();

        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a =>
            a.PrincipalId == target.Id && a.RoleId == WellKnownGUIDs.ROLE_ADMIN
            && a.ScopeType == ScopeType.Global));
    }

    [Fact]
    public async Task DeleteAssignmentAsync_RemoveAdminRole_RemovesUserFromAdminsGroup()
    {
        // Two admins so the last-admin guard permits the removal; both are consistent
        // (direct role assignment + Admins membership) as the runtime keeps them.
        var actor = SeedUser("admin");
        var admin2 = SeedUser("admin2");
        var adminRole = SeedWellKnownAdminRole();
        SeedGroupWithId(WellKnownGUIDs.GROUP_ADMINS, "Admins");
        SeedGlobalAssignment(actor.Id, adminRole.Id);
        SeedGroupMember(WellKnownGUIDs.GROUP_ADMINS, actor.Id);
        var a2 = SeedGlobalAssignment(admin2.Id, adminRole.Id);
        SeedGroupMember(WellKnownGUIDs.GROUP_ADMINS, admin2.Id);

        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Roles);
        await sut.SelectRoleAsync(sut.Roles.Single(r => r.Id == adminRole.Id));
        await sut.DeleteAssignmentAsync(a2.Id);

        Assert.NotEqual(Resources.Web_Error_LastAdmin, sut.ErrorMessage);
        await using var db = NewContext();
        Assert.False(await db.UserGroups.AnyAsync(
            ug => ug.UserId == admin2.Id && ug.GroupId == WellKnownGUIDs.GROUP_ADMINS));
    }

    [Fact]
    public async Task SaveGroupAsync_EmptyingAdminsGroup_IsBlocked()
    {
        var actor = SeedUser("admin");
        var adminRole = SeedWellKnownAdminRole();
        SeedGroupWithId(WellKnownGUIDs.GROUP_ADMINS, "Admins");
        SeedGlobalAssignment(actor.Id, adminRole.Id);
        SeedGroupMember(WellKnownGUIDs.GROUP_ADMINS, actor.Id);

        var sut = BuildGlobalAdminSut(actor);
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.SelectGroupAsync(sut.Groups.Single(g => g.Id == WellKnownGUIDs.GROUP_ADMINS));
        await sut.StartEditGroupAsync();
        foreach (var m in sut.EditGroupMembers) m.IsChecked = false; // try to remove everyone
        await sut.SaveGroupAsync();

        Assert.Equal(Resources.Web_Error_LastAdmin, sut.ErrorMessage);
        await using var db = NewContext();
        Assert.True(await db.UserGroups.AnyAsync(
            ug => ug.GroupId == WellKnownGUIDs.GROUP_ADMINS && ug.UserId == actor.Id));
    }
}
