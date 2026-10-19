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

    [Fact]
    public async Task SaveGroupAsync_AddMemberToDeptGroup_AlsoAddsUserToDepartment()
    {
        // Bug regression: adding a user to a department-scoped group via the Groups tab must
        // also grant them that group's department, so the membership is not orphaned.
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Sales");
        var group = SeedGroupInDept("Sales-Team", dept.Id);
        var alice = SeedUser("alice");

        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.SelectGroupAsync(sut.Groups.Single(g => g.Id == group.Id));
        await sut.StartEditGroupAsync();

        foreach (var m in sut.EditGroupMembers)
            m.IsChecked = m.Item.Id == alice.Id;
        await sut.SaveGroupAsync();

        await using var db = NewContext();
        Assert.True(await db.UserGroups.AnyAsync(ug => ug.GroupId == group.Id && ug.UserId == alice.Id));
        Assert.True(await db.DepartmentUsers.AnyAsync(du => du.DepartmentId == dept.Id && du.UserId == alice.Id));
    }

    [Fact]
    public async Task EditGroupDeptWarning_SurfacesSelectedUsersMissingDepartment()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Sales");
        var group = SeedGroupInDept("Sales-Team", dept.Id);
        var alice = SeedUser("alice", "Alice");

        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.SelectGroupAsync(sut.Groups.Single(g => g.Id == group.Id));
        await sut.StartEditGroupAsync();

        Assert.Null(sut.EditGroupDeptWarning); // nothing selected yet

        foreach (var m in sut.EditGroupMembers)
            m.IsChecked = m.Item.Id == alice.Id;

        Assert.NotNull(sut.EditGroupDeptWarning);
        Assert.Contains("Alice", sut.EditGroupDeptWarning);
        Assert.Contains("Sales", sut.EditGroupDeptWarning);
    }
}
