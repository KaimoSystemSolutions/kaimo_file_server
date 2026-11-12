using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Language;
using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Web.Tests.ViewModels.Users;

public partial class UserListViewModelDatabaseTests
{
    // ═══════════════════ Everyone membership on create ═══════════════════

    [Fact]
    public async Task CreateUserAsync_AddsUserToEveryoneGroup()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Sales");
        SeedGroupWithId(WellKnownGUIDs.GROUP_EVERYONE, "Everyone");

        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        sut.CreateUserName = "Dana";
        sut.CreateUserUsername = "dana";
        sut.CreateUserPassword = "Passw0rd!Long";
        sut.CreateUserDepartmentId = dept.Id;
        await sut.CreateUserAsync();

        await using var db = NewContext();
        var dana = await db.Users.SingleAsync(u => u.Username == "dana");
        Assert.True(await db.UserGroups.AnyAsync(
            ug => ug.UserId == dana.Id && ug.GroupId == WellKnownGUIDs.GROUP_EVERYONE));
    }

    // ═══════════════════ Protected (undeletable) system groups ═══════════════════

    [Fact]
    public async Task ConfirmDeleteGroupAsync_SystemGroup_IsBlocked()
    {
        var actor = SeedUser("admin");
        SeedGroupWithId(WellKnownGUIDs.GROUP_ADMINS, "Admins");

        var sut = BuildGlobalAdminSut(actor);
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.SelectGroupAsync(sut.Groups.Single(g => g.Id == WellKnownGUIDs.GROUP_ADMINS));
        await sut.ConfirmDeleteGroupAsync();

        Assert.Equal(Resources.Web_Error_SystemGroupUndeletable, sut.ErrorMessage);
        await using var db = NewContext();
        Assert.True(await db.Groups.AnyAsync(g => g.Id == WellKnownGUIDs.GROUP_ADMINS));
    }

    [Fact]
    public async Task GroupRepository_DeleteAsync_ProtectedGroup_Throws()
    {
        SeedGroupWithId(WellKnownGUIDs.GROUP_EVERYONE, "Everyone");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => GroupRepo().DeleteAsync(WellKnownGUIDs.GROUP_EVERYONE));
    }

    [Fact]
    public async Task StartEditGroupAsync_EveryoneGroup_IsBlocked()
    {
        var actor = SeedUser("admin");
        SeedGroupWithId(WellKnownGUIDs.GROUP_EVERYONE, "Everyone");

        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.SelectGroupAsync(sut.Groups.Single(g => g.Id == WellKnownGUIDs.GROUP_EVERYONE));
        await sut.StartEditGroupAsync();

        Assert.False(sut.IsEditing);
        Assert.False(sut.SelectedGroupIsEditable);
        Assert.Equal(Resources.Web_Error_EveryoneNotEditable, sut.ErrorMessage);
    }

    // A department group only takes members of exactly its department; joining never adds the
    // user to the department (that is done on the department or the user form).

    private async Task<UserListViewModel> EditGroupAsGlobalAdminAsync(Group group)
    {
        var sut = BuildGlobalAdminSut(SeedUser("admin"));
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.SelectGroupAsync(sut.Groups.Single(g => g.Id == group.Id));
        await sut.StartEditGroupAsync();
        return sut;
    }

    [Fact]
    public async Task StartEditGroup_DeptGroup_OffersOnlyDepartmentMembers()
    {
        var dept = SeedDepartment("Sales");
        var child = SeedDepartment("Sales-North", parentId: dept.Id);
        var group = SeedGroupInDept("Sales-Team", dept.Id);
        var bob = SeedUser("bob");
        var carol = SeedUser("carol");
        await DepartmentRepo().AddUserAsync(dept.Id, bob.Id);
        await DepartmentRepo().AddUserAsync(child.Id, carol.Id);
        var alice = SeedUser("alice");

        var sut = await EditGroupAsGlobalAdminAsync(group);

        Assert.Contains(sut.EditGroupMembers, m => m.Item.Id == bob.Id);
        Assert.DoesNotContain(sut.EditGroupMembers, m => m.Item.Id == carol.Id);
        Assert.DoesNotContain(sut.EditGroupMembers, m => m.Item.Id == alice.Id);
    }

    [Fact]
    public async Task SaveGroupAsync_DepartmentMemberJoins_DepartmentsUnchanged()
    {
        var dept = SeedDepartment("Sales");
        var group = SeedGroupInDept("Sales-Team", dept.Id);
        var bob = SeedUser("bob");
        await DepartmentRepo().AddUserAsync(dept.Id, bob.Id);

        var sut = await EditGroupAsGlobalAdminAsync(group);
        sut.EditGroupMembers.Single(m => m.Item.Id == bob.Id).IsChecked = true;
        await sut.SaveGroupAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.Equal(Resources.Web_Members_Saved, sut.SuccessMessage);
        await using var db = NewContext();
        Assert.True(await db.UserGroups.AnyAsync(ug => ug.GroupId == group.Id && ug.UserId == bob.Id));
        Assert.Equal(1, await db.DepartmentUsers.CountAsync(du => du.UserId == bob.Id));
    }

    [Fact]
    public async Task SaveGroupAsync_NonMemberForcedIntoDeptGroup_IsRefused()
    {
        var dept = SeedDepartment("Sales");
        var group = SeedGroupInDept("Sales-Team", dept.Id);
        var alice = SeedUser("alice", "Alice");

        var sut = await EditGroupAsGlobalAdminAsync(group);
        sut.EditGroupMembers.Add(new CheckboxItem<User>(alice, true));
        await sut.SaveGroupAsync();

        Assert.Equal(string.Format(Resources.Web_Group_MembersNotInDepartment, "Sales", "Alice"), sut.ErrorMessage);
        await using var db = NewContext();
        Assert.False(await db.UserGroups.AnyAsync(ug => ug.GroupId == group.Id && ug.UserId == alice.Id));
        Assert.False(await db.DepartmentUsers.AnyAsync(du => du.UserId == alice.Id));
    }
}
