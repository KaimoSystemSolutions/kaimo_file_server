using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Tests.Infrastructure;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Regression tests pinning <see cref="DepartmentViewModel"/> mutations to real database
/// state. Beyond simple CRUD, these cover the parts most prone to silent breakage:
/// the edit diff logic (membership add/remove, group/share re-assignment) and the
/// nullable default-permission bitmask that drives department-wide file access.
/// </summary>
public class DepartmentViewModelDatabaseTests : DatabaseTestBase
{
    private readonly Mock<IManagementAuthService> _mgmtAuth = new();

    private DepartmentViewModel BuildGlobalAdminSut(User actor)
    {
        var ctx = ContextFor(actor);
        _mgmtAuth.Setup(m => m.HasGlobalPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.FullAdmin))
            .ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.HasAnyPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.ViewDepartment))
            .ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.HasAnyPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.EditDepartment))
            .ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.CanManageDepartmentAsync(
                It.IsAny<UserContext>(), It.IsAny<Guid>(), ManagementPermission.EditDepartment))
            .ReturnsAsync(true);

        return new DepartmentViewModel(
            DepartmentRepo(), UserRepo(), GroupRepo(), ShareRepo(), RoleRepo(),
            _mgmtAuth.Object,
            UserContextFactoryFor((actor.Username, ctx)).Object,
            AuthStateFor(actor.Username),
            NullLogger<DepartmentViewModel>.Instance);
    }

    // ─────────────────────── Create ───────────────────────

    [Fact]
    public async Task CreateAsync_PersistsNewDepartment()
    {
        var actor = SeedUser("admin");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        sut.CreateName = "Engineering";
        sut.CreateDescription = "Builds things";
        await sut.CreateAsync();

        await using var db = NewContext();
        var row = await db.Departments.SingleOrDefaultAsync(d => d.Name == "Engineering");
        Assert.NotNull(row);
        Assert.Equal("Builds things", row!.Description);
    }

    [Fact]
    public async Task CreateAsync_WithBlankName_PersistsNothing()
    {
        var actor = SeedUser("admin");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        sut.CreateName = "   ";
        await sut.CreateAsync();

        Assert.NotNull(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.Empty(await db.Departments.ToListAsync());
    }

    // ─────────────────────── Save: entity fields ───────────────────────

    [Fact]
    public async Task SaveAsync_PersistsRenamedDepartment()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Old");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));
        await sut.StartEditAsync();

        sut.EditName = "New";
        sut.EditDescription = "desc";
        await sut.SaveAsync();

        await using var db = NewContext();
        var row = await db.Departments.FindAsync(dept.Id);
        Assert.Equal("New", row!.Name);
        Assert.Equal("desc", row.Description);
    }

    [Fact]
    public async Task SaveAsync_WithOwnPermission_PersistsBitmask()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Dept");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));
        await sut.StartEditAsync();

        sut.EditHasOwnPermission = true;
        sut.EditDefaultPermission = FilePermission.None;
        sut.TogglePermFlag(FilePermission.ReadAll, true); // simulate the checkbox UI
        await sut.SaveAsync();

        await using var db = NewContext();
        var row = await db.Departments.FindAsync(dept.Id);
        Assert.Equal((long)FilePermission.ReadAll, row!.DefaultFilePermission);
    }

    [Fact]
    public async Task SaveAsync_WithoutOwnPermission_PersistsNullDefault()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Dept", defaultFilePermission: (long)FilePermission.ReadAll);
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));
        await sut.StartEditAsync();

        sut.EditHasOwnPermission = false; // clears the override → inherit from parent
        await sut.SaveAsync();

        await using var db = NewContext();
        var row = await db.Departments.FindAsync(dept.Id);
        Assert.Null(row!.DefaultFilePermission);
    }

    // ─────────────────────── Save: membership diff ───────────────────────

    [Fact]
    public async Task SaveAsync_AddsAndRemovesMembers()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Dept");
        var alice = SeedUser("alice");
        var bob = SeedUser("bob");

        // Bob starts as a member; after the edit only Alice should remain.
        await DepartmentRepo().AddUserAsync(dept.Id, bob.Id);

        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));
        await sut.StartEditAsync();

        foreach (var m in sut.EditMembers)
            m.IsChecked = m.Item.Id == alice.Id; // check Alice, uncheck Bob
        await sut.SaveAsync();

        var members = await DepartmentRepo().GetUsersAsync(dept.Id);
        Assert.Contains(members, u => u.Id == alice.Id);
        Assert.DoesNotContain(members, u => u.Id == bob.Id);
    }

    // ─────────────────────── Save: share re-assignment ───────────────────────

    [Fact]
    public async Task SaveAsync_AssignsCheckedShareToDepartment()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Dept");
        var share = SeedShare("finance"); // defaults to Global department

        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));
        await sut.StartEditAsync();

        foreach (var s in sut.EditShares)
            s.IsChecked = s.Item.Id == share.Id;
        await sut.SaveAsync();

        await using var db = NewContext();
        Assert.Equal(dept.Id, (await db.ShareDefinitions.FindAsync(share.Id))!.DepartmentId);
    }

    // ─────────────────────── Delete ───────────────────────

    [Fact]
    public async Task ConfirmDeleteAsync_RemovesDepartmentAndReassignsShares()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Doomed");
        var share = SeedShare("orphan", departmentId: dept.Id);

        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));
        sut.RequestDelete();
        await sut.ConfirmDeleteAsync();

        await using var db = NewContext();
        Assert.False(await db.Departments.AnyAsync(d => d.Id == dept.Id));
        // Its share must survive, re-homed to the Global department (no orphan FK).
        Assert.Equal(WellKnownGUIDs.DEPARTMENT_GLOBAL,
            (await db.ShareDefinitions.FindAsync(share.Id))!.DepartmentId);
    }

    [Fact]
    public async Task ConfirmDeleteAsync_GlobalDepartment_IsRefused()
    {
        var actor = SeedUser("admin");
        // The Global department is protected — attempting to delete it must be rejected.
        var global = SeedDepartment("Global", id: WellKnownGUIDs.DEPARTMENT_GLOBAL);

        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == global.Id));
        sut.RequestDelete();
        await sut.ConfirmDeleteAsync();

        Assert.NotNull(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.True(await db.Departments.AnyAsync(d => d.Id == WellKnownGUIDs.DEPARTMENT_GLOBAL));
    }
}
