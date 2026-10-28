using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Adding a member to a group grants the group's roles, so both membership editors (group side
/// and user side) refuse additions to groups whose roles the actor could not assign directly.
/// Removing members stays possible.
/// </summary>
public partial class UserListViewModelDatabaseTests
{
    private Group SeedGroupWithRole(string name, ManagementPermission perms)
    {
        var group = SeedGroup(name);
        var role = SeedRole(name + "-role", perms);
        using var db = NewContext();
        db.ScopedRoleAssignments.Add(new ScopedRoleAssignment(group.Id, role.Id, ScopeType.Global, Guid.Empty));
        db.SaveChanges();
        return group;
    }

    private void DenyAssigning(ManagementPermission perms)
        => _mgmtAuth.Setup(m => m.CanAssignRoleAsync(It.IsAny<UserContext>(), perms,
                It.IsAny<ScopeType>(), It.IsAny<Guid>()))
            .ReturnsAsync(false);

    private async Task<bool> IsMemberAsync(Guid groupId, Guid userId)
    {
        await using var db = NewContext();
        return await db.UserGroups.AnyAsync(ug => ug.GroupId == groupId && ug.UserId == userId);
    }

    private async Task<UserListViewModel> EditGroupAsync(User actor, Group group)
    {
        var sut = BuildGlobalAdminSut(actor);
        DenyAssigning(ManagementPermission.AccessHomeContents);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.SelectGroupAsync(sut.Groups.Single(g => g.Id == group.Id));
        await sut.StartEditGroupAsync();
        return sut;
    }

    [Fact]
    public async Task SaveGroupAsync_AddingMemberToGroupWithUnassignableRole_IsRefused()
    {
        var actor = SeedUser("groupadmin");
        var group = SeedGroupWithRole("auditors", ManagementPermission.AccessHomeContents);
        var target = SeedUser("bob");

        var sut = await EditGroupAsync(actor, group);
        foreach (var m in sut.EditGroupMembers) m.IsChecked = m.Item.Id == target.Id;
        await sut.SaveGroupAsync();

        Assert.Equal(Resources.ResourceManager.GetString("Web_Error_GroupGrantsUnassignableRoles"), sut.ErrorMessage);
        Assert.NotNull(sut.ErrorMessage);
        Assert.False(await IsMemberAsync(group.Id, target.Id));
    }

    [Fact]
    public async Task SaveGroupAsync_RemovingMemberFromGroupWithUnassignableRole_IsRefused()
    {
        // Leaving revokes the group's roles, so it is bounded like revoking an assignment
        // (plans/scoped-management-permissions-audit.md, F5).
        var actor = SeedUser("groupadmin");
        var group = SeedGroupWithRole("auditors", ManagementPermission.AccessHomeContents);
        var target = SeedUser("bob");
        await GroupRepo().AddMemberAsync(group.Id, target.Id);

        var sut = await EditGroupAsync(actor, group);
        foreach (var m in sut.EditGroupMembers) m.IsChecked = false;
        await sut.SaveGroupAsync();

        Assert.Equal(Resources.ResourceManager.GetString("Web_Error_GroupGrantsUnrevokableRoles"), sut.ErrorMessage);
        Assert.True(await IsMemberAsync(group.Id, target.Id));
    }

    [Fact]
    public async Task SaveUserAsync_JoiningGroupWithUnassignableRole_IsRefused()
    {
        var actor = SeedUser("useradmin");
        var group = SeedGroupWithRole("auditors", ManagementPermission.AccessHomeContents);
        var target = SeedUser("bob");

        var sut = BuildGlobalAdminSut(actor);
        DenyAssigning(ManagementPermission.AccessHomeContents);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));
        await sut.StartEditUserAsync();
        foreach (var g in sut.EditUserGroups) g.IsChecked = g.Item.Id == group.Id;
        await sut.SaveUserAsync();

        Assert.Equal(Resources.ResourceManager.GetString("Web_Error_GroupGrantsUnassignableRoles"), sut.ErrorMessage);
        Assert.NotNull(sut.ErrorMessage);
        Assert.False(await IsMemberAsync(group.Id, target.Id));
    }
}
