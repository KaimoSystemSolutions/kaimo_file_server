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

    [Fact]
    public async Task SaveGroupAsync_PersistsRename()
    {
        var actor = SeedUser("admin");
        var group = SeedGroup("Team");
        var sut = BuildGlobalAdminSut(actor);
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Groups);
        await sut.SelectGroupAsync(sut.Groups.Single(g => g.Id == group.Id));
        await sut.StartEditGroupAsync();

        sut.EditGroupName = "Renamed Team";
        await sut.SaveGroupAsync();

        await using var db = NewContext();
        Assert.Equal("Renamed Team", (await db.Groups.FindAsync(group.Id))!.Name);
    }

    // ═══════════════════ Assignment ordering ═══════════════════

    [Fact]
    public async Task RoleScopedAssignments_ListGlobalAssignmentsFirst()
    {
        var actor = SeedUser("admin");
        var role = SeedRole("Delegate");
        var aaron = SeedUser("aaron");
        var zoe = SeedUser("zoe");
        var dept = SeedDepartment("Engineering");
        await ScopedRoleRepo().CreateAsync(ScopedRoleAssignment.ForDepartment(aaron.Id, role.Id, dept.Id));
        await ScopedRoleRepo().CreateAsync(ScopedRoleAssignment.Global(zoe.Id, role.Id));
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SwitchTabAsync(AdminTab.Roles);

        await sut.SelectRoleAsync(sut.Roles.Single(r => r.Id == role.Id));

        Assert.Equal([zoe.Name, aaron.Name], sut.RoleScopedAssignments.Select(a => a.PrincipalName));
    }

    [Fact]
    public async Task UserScopedAssignments_ListGlobalAssignmentsFirst()
    {
        var actor = SeedUser("admin");
        var alpha = SeedRole("Alpha");
        var zulu = SeedRole("Zulu");
        var target = SeedUser("bob");
        var dept = SeedDepartment("Engineering");
        await ScopedRoleRepo().CreateAsync(ScopedRoleAssignment.ForDepartment(target.Id, alpha.Id, dept.Id));
        await ScopedRoleRepo().CreateAsync(ScopedRoleAssignment.Global(target.Id, zulu.Id));
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));

        Assert.Equal(["Zulu", "Alpha"], sut.UserScopedAssignments.Select(a => a.RoleName));
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
}
