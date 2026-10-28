using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Side effects of an authorized change must not bypass the guards of the operation they
/// perform implicitly: deleting a department revokes its role assignments, leaving a department
/// drops the memberships of its groups, saving a department group adds its members to the
/// department, and moving a share changes the department that controls it.
/// </summary>
public partial class ScopedAdminEscalationDatabaseTests
{
    // ═══════════════════ Department delete: assignments and contents ═══════════════════

    [Fact]
    public async Task DeptAdmin_DeletingSubdepartmentWithAssignmentBeyondOwnRights_IsRefused()
    {
        // A global admin granted a right at the child that the parent's admin does not hold.
        var homes = SeedRole(ManagementPermission.AccessHomeContents);
        var bob = SeedUser("bob");
        Assign(bob.Id, homes, ScopeType.Department, _ownChild.Id);

        var sut = await SelectDepartmentAsync(_deptAdmin, _ownChild);
        sut.RequestDelete();
        await sut.ConfirmDeleteAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.NotNull(await StoredDepartmentAsync(_ownChild));
        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.ScopeId == _ownChild.Id));
    }

    [Fact]
    public async Task DeletingSubdepartment_MovesItsGroupsAndSharesToTheParent()
    {
        var group = SeedGroup("ChildTeam", _ownChild.Id);
        var share = SeedShare("childshare", departmentId: _ownChild.Id);

        var sut = await SelectDepartmentAsync(_deptAdmin, _ownChild);
        sut.RequestDelete();
        await sut.ConfirmDeleteAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.Equal(_own.Id, await GroupDepartmentAsync(group));
        Assert.Equal(_own.Id, await ShareDepartmentAsync(share));
    }

    // ═══════════════════ Leaving a department drops its group memberships ═══════════════════

    /// <summary>Bob is a member of <c>Own</c> and of a group of <c>Own</c> that grants a role.
    /// The role is within the department admin's own rights, but revoking it needs
    /// <see cref="ManagementPermission.AssignRoles"/>, which the seeded role lacks.</summary>
    private (User Bob, Group Group) SeedMemberOfRoleGrantingGroup()
    {
        var group = SeedGroup("Helpers", _own.Id);
        Assign(group.Id, SeedRole(ManagementPermission.ResetPasswords), ScopeType.Department, _own.Id);
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);
        SeedMembership(group, bob);
        return (bob, group);
    }

    [Fact]
    public async Task DeptAdmin_RemovingDepartmentMemberInRoleGrantingGroup_IsRefused()
    {
        var (bob, group) = SeedMemberOfRoleGrantingGroup();

        var sut = await EditDepartmentAsync(_deptAdmin, _own);
        sut.EditMembers.Single(m => m.Item.Id == bob.Id).IsChecked = false;
        await sut.SaveAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.True(await IsMemberAsync(_own, bob));
        Assert.True(await IsGroupMemberAsync(group, bob));
    }

    [Fact]
    public async Task DeptAdmin_RemovingGlobalAdminFromDepartment_IsRefused()
    {
        AddToDepartment(_own, _globalAdmin);

        var sut = await EditDepartmentAsync(_deptAdmin, _own);
        sut.EditMembers.Single(m => m.Item.Id == _globalAdmin.Id).IsChecked = false;
        await sut.SaveAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.True(await IsMemberAsync(_own, _globalAdmin));
    }

    [Fact]
    public async Task DeptAdmin_RemovingPlainDepartmentMember_DropsTheirDepartmentGroups()
    {
        var team = SeedGroup("Team", _own.Id);
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);
        SeedMembership(team, bob);

        var sut = await EditDepartmentAsync(_deptAdmin, _own);
        sut.EditMembers.Single(m => m.Item.Id == bob.Id).IsChecked = false;
        await sut.SaveAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.False(await IsMemberAsync(_own, bob));
        Assert.False(await IsGroupMemberAsync(team, bob));
    }

    [Fact]
    public async Task DeptAdmin_UncheckingDepartmentOfUserInRoleGrantingGroup_IsRefused()
    {
        var (bob, group) = SeedMemberOfRoleGrantingGroup();

        var sut = await EditUserAsync(_deptAdmin, bob);
        sut.EditUserDepartments.Single(d => d.Item.Id == _own.Id).IsChecked = false;
        await sut.SaveUserAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.True(await IsMemberAsync(_own, bob));
        Assert.True(await IsGroupMemberAsync(group, bob));
    }

    // ═══════════════════ Saving a group only pulls in joining members ═══════════════════

    [Fact]
    public async Task DeptAdmin_SavingGroup_DoesNotPullExistingForeignMemberIntoDepartment()
    {
        var group = SeedGroup("Team", _own.Id);
        var carol = SeedUser("carol");
        AddToDepartment(_foreign, carol);
        SeedMembership(group, carol); // e.g. the group was moved into Own by a global admin

        var sut = await EditGroupAsync(_deptAdmin, group);
        Assert.Null(sut.EditGroupDeptWarning);
        await sut.SaveGroupAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.True(await IsGroupMemberAsync(group, carol));
        Assert.False(await IsMemberAsync(_own, carol));
    }

    [Fact]
    public async Task DeptAdmin_SavingGroup_RechecksDepartmentMembershipOfJoiningUsers()
    {
        var group = SeedGroup("Team", _own.Id);
        var carol = SeedUser("carol");
        AddToDepartment(_own, carol);
        AddToDepartment(_foreign, carol);

        var sut = await EditGroupAsync(_deptAdmin, group);
        sut.EditGroupMembers.Single(m => m.Item.Id == carol.Id).IsChecked = true;
        // Carol leaves Own while the editor is open: the picker's member list is stale now.
        await DepartmentRepo().RemoveUserAsync(_own.Id, carol.Id);
        await sut.SaveGroupAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.False(await IsGroupMemberAsync(group, carol));
        Assert.False(await IsMemberAsync(_own, carol));
    }

    // ═══════════════════ Share department change: authority over the source ═══════════════════

    [Fact]
    public async Task ShareScopedManager_CannotMoveForeignShareIntoOwnDepartment()
    {
        var share = SeedShare("foreignshare", path: $"{Pool}/foreignshare", departmentId: _foreign.Id);
        var manager = SeedUser("mover");
        AddToDepartment(_own, manager);
        Assign(manager.Id, SeedRole(ManagementPermission.EditShareSettings), ScopeType.Share, share.Id);
        Assign(manager.Id, SeedRole(ManagementPermission.EditShareSettings), ScopeType.Department, _own.Id);

        var sut = BuildShareList(manager);
        await sut.LoadAsync();
        sut.SelectShare(sut.Shares.Single(s => s.Id == share.Id));
        Assert.False(sut.CanChangeSelectedShareDepartment);

        sut.EditShareDepartmentId = _own.Id;
        Assert.False(await sut.ChangeDepartmentAsync());
        Assert.Equal(_foreign.Id, await ShareDepartmentAsync(share));
    }

    [Fact]
    public async Task DepartmentShareManager_CanChangeDepartmentOfOwnShare()
    {
        var manager = SeedUser("sharemgr");
        AddToDepartment(_own, manager);
        Assign(manager.Id, SeedRole(ManagementPermission.ShareAdmin), ScopeType.Department, _own.Id);

        var (sut, _) = await SelectShareAsync(manager, _own.Id);

        Assert.True(sut.CanChangeSelectedShareDepartment);
    }
}
