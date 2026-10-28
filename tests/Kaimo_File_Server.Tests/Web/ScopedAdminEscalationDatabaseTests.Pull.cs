using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Web.Components.ViewModels;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// C2/C3: a department admin must not pull a user, group or share into their department unless
/// they already control it — neither through a department-group membership edit (which adds the
/// member to the department) nor through the group/share assignment of the department editor.
/// </summary>
public partial class ScopedAdminEscalationDatabaseTests
{
    private async Task<UserListViewModel> EditGroupAsync(User actor, Group group)
    {
        var sut = BuildUserList(actor);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.SelectGroupAsync(sut.Groups.Single(g => g.Id == group.Id));
        await sut.StartEditGroupAsync();
        return sut;
    }

    private async Task<bool> IsGroupMemberAsync(Group group, User user)
        => (await GroupRepo().GetMembersAsync(group.Id)).Any(m => m.Id == user.Id);

    private async Task<Guid> ShareDepartmentAsync(ShareDefinition share)
        => (await ShareRepo().GetByIdAsync(share.Id))!.DepartmentId;

    private async Task<Guid> GroupDepartmentAsync(Group group)
        => (await GroupRepo().GetByIdAsync(group.Id))!.DepartmentId;

    // ═══════════════════ C2: group membership pulls into the department ═══════════════════

    [Fact]
    public async Task DeptAdmin_AddingGlobalAdminToDepartmentGroup_IsRefused()
    {
        var group = SeedGroup("Team", _own.Id);
        var sut = await EditGroupAsync(_deptAdmin, group);

        Assert.DoesNotContain(sut.EditGroupMembers, m => m.Item.Id == _globalAdmin.Id);
        sut.EditGroupMembers.Add(new CheckboxItem<User>(_globalAdmin, true));
        await sut.SaveGroupAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.False(await IsGroupMemberAsync(group, _globalAdmin));
        Assert.False(await IsMemberAsync(_own, _globalAdmin));
    }

    [Fact]
    public async Task DeptAdmin_AddingMemberOfChildDepartmentToGroup_AddsThemToTheDepartment()
    {
        var group = SeedGroup("Team", _own.Id);
        var bob = SeedUser("bob");
        AddToDepartment(_ownChild, bob);
        var sut = await EditGroupAsync(_deptAdmin, group);

        sut.EditGroupMembers.Single(m => m.Item.Id == bob.Id).IsChecked = true;
        await sut.SaveGroupAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.True(await IsGroupMemberAsync(group, bob));
        Assert.True(await IsMemberAsync(_own, bob));
    }

    // ═══════════════════ C3: shares ═══════════════════

    [Fact]
    public async Task DeptAdmin_PullingForeignOrGlobalShareIntoDepartment_IsRefused()
    {
        var foreignShare = SeedShare("foreign", departmentId: _foreign.Id);
        var globalShare = SeedShare("globalshare");
        var sut = await EditDepartmentAsync(_deptAdmin, _own);

        Assert.DoesNotContain(sut.EditShares, s => s.Item.Id == foreignShare.Id || s.Item.Id == globalShare.Id);
        sut.EditShares.Add(new CheckboxItem<ShareDefinition>(foreignShare, true));
        sut.EditShares.Add(new CheckboxItem<ShareDefinition>(globalShare, true));
        await sut.SaveAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.Equal(_foreign.Id, await ShareDepartmentAsync(foreignShare));
        Assert.Equal(WellKnownGUIDs.DEPARTMENT_GLOBAL, await ShareDepartmentAsync(globalShare));
    }

    [Fact]
    public async Task DeptAdmin_CanMoveShareOfChildDepartmentIntoOwnDepartment()
    {
        var childShare = SeedShare("child", departmentId: _ownChild.Id);
        var sut = await EditDepartmentAsync(_deptAdmin, _own);

        sut.EditShares.Single(s => s.Item.Id == childShare.Id).IsChecked = true;
        await sut.SaveAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.Equal(_own.Id, await ShareDepartmentAsync(childShare));
    }

    [Fact]
    public async Task HomeFolderShare_IsNeverOfferedOrMoved()
    {
        var homes = SeedShare("userhomes");
        await using (var db = NewContext())
        {
            (await db.ShareDefinitions.FindAsync(homes.Id))!.IsUserHomes = true;
            await db.SaveChangesAsync();
        }
        var sut = await EditDepartmentAsync(_globalAdmin, _own);

        Assert.DoesNotContain(sut.EditShares, s => s.Item.Id == homes.Id);
        sut.EditShares.Add(new CheckboxItem<ShareDefinition>(homes, true));
        await sut.SaveAsync();

        Assert.Equal(WellKnownGUIDs.DEPARTMENT_GLOBAL, await ShareDepartmentAsync(homes));
    }

    // ═══════════════════ C3: groups ═══════════════════

    [Fact]
    public async Task DeptAdmin_PullingAdminsOrForeignGroupIntoDepartment_IsRefused()
    {
        var admins = SeedGroup("Admins", id: WellKnownGUIDs.GROUP_ADMINS);
        var foreignGroup = SeedGroup("Foreign team", _foreign.Id);
        var sut = await EditDepartmentAsync(_deptAdmin, _own);

        Assert.DoesNotContain(sut.EditGroups, g => g.Item.Id == admins.Id || g.Item.Id == foreignGroup.Id);
        sut.EditGroups.Add(new CheckboxItem<Group>(admins, true));
        sut.EditGroups.Add(new CheckboxItem<Group>(foreignGroup, true));
        await sut.SaveAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.Equal(WellKnownGUIDs.DEPARTMENT_GLOBAL, await GroupDepartmentAsync(admins));
        Assert.Equal(_foreign.Id, await GroupDepartmentAsync(foreignGroup));
    }

    [Fact]
    public async Task GlobalAdmin_CannotMoveProtectedGroups_ButCanMoveOrdinaryOnes()
    {
        var admins = SeedGroup("Admins", id: WellKnownGUIDs.GROUP_ADMINS);
        var ordinary = SeedGroup("Ordinary");
        var sut = await EditDepartmentAsync(_globalAdmin, _own);

        Assert.DoesNotContain(sut.EditGroups, g => g.Item.Id == admins.Id);
        sut.EditGroups.Single(g => g.Item.Id == ordinary.Id).IsChecked = true;
        sut.EditGroups.Add(new CheckboxItem<Group>(admins, true));
        await sut.SaveAsync();

        Assert.Equal(_own.Id, await GroupDepartmentAsync(ordinary));
        Assert.Equal(WellKnownGUIDs.DEPARTMENT_GLOBAL, await GroupDepartmentAsync(admins));
    }
}
