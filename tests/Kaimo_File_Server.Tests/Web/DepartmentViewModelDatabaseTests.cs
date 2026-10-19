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

        _mgmtAuth.Setup(m => m.HasAnyPermissionAsync(It.IsAny<UserContext>(), ManagementPermission.AssignRoles))
            .ReturnsAsync(true);
        _mgmtAuth.Setup(m => m.CanAssignRoleAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>(),
                It.IsAny<ScopeType>(), It.IsAny<Guid>()))
            .ReturnsAsync(true);

        return new DepartmentViewModel(
            DepartmentRepo(), UserRepo(), GroupRepo(), ShareRepo(), RoleRepo(), ScopedRoleRepo(),
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

    [Fact]
    public async Task CreateAsync_WithExistingName_ShowsNameTakenMessage()
    {
        var actor = SeedUser("admin");
        SeedDepartment("Engineering");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        sut.CreateName = " Engineering ";
        await sut.CreateAsync();

        Assert.Equal(Kaimo_File_Server.Core.Language.Resources.Web_Dept_NameTaken, sut.ErrorMessage);
        await using var db = NewContext();
        Assert.Single(await db.Departments.Where(d => d.Name == "Engineering").ToListAsync());
    }

    // ─────────────────────── Save: entity fields ───────────────────────

    [Fact]
    public async Task SaveAsync_RenameToExistingName_ShowsNameTakenMessage()
    {
        var actor = SeedUser("admin");
        SeedDepartment("Sales");
        var dept = SeedDepartment("Old");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));
        await sut.StartEditAsync();

        sut.EditName = "Sales";
        await sut.SaveAsync();

        Assert.Equal(Kaimo_File_Server.Core.Language.Resources.Web_Dept_NameTaken, sut.ErrorMessage);
        Assert.True(sut.IsEditing);
        await using var db = NewContext();
        Assert.Equal("Old", (await db.Departments.FindAsync(dept.Id))!.Name);
    }

    [Fact]
    public async Task SaveAsync_KeepingOwnName_IsNotADuplicate()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Old");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));
        await sut.StartEditAsync();

        sut.EditName = "Old";
        sut.EditDescription = "changed";
        await sut.SaveAsync();

        Assert.Null(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.Equal("changed", (await db.Departments.FindAsync(dept.Id))!.Description);
    }

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
    // ─────────────────────── Department-scoped role assignments ───────────────────────

    private Role SeedRole(string name, ManagementPermission perms = ManagementPermission.None)
    {
        var role = new Role(Guid.NewGuid(), name, perms, false);
        using var db = NewContext();
        db.Roles.Add(role);
        db.SaveChanges();
        return role;
    }

    [Fact]
    public async Task CreateAssignmentAsync_PersistsDepartmentScopedAssignmentAndListsIt()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Engineering");
        var role = SeedRole("DeptAdmin");
        var principal = SeedUser("bob");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));

        await sut.StartAddAssignmentAsync();
        sut.NewAssignmentRoleId = role.Id;
        sut.NewAssignmentPrincipalId = principal.Id;
        await sut.CreateAssignmentAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.False(sut.IsAddingAssignment);
        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.RoleId == role.Id
            && a.PrincipalId == principal.Id && a.ScopeType == ScopeType.Department && a.ScopeId == dept.Id));
        Assert.Contains(sut.RoleAssignments, a => a.RoleName == "DeptAdmin" && a.PrincipalName == principal.Name);
    }

    [Fact]
    public async Task CreateAssignmentAsync_WithoutAssignAuthorityAtDepartment_IsBlocked()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Engineering");
        var role = SeedRole("DeptAdmin");
        var principal = SeedUser("bob");
        var sut = BuildGlobalAdminSut(actor);
        _mgmtAuth.Setup(m => m.CanAssignRoleAsync(It.IsAny<UserContext>(), It.IsAny<ManagementPermission>(),
                ScopeType.Department, dept.Id))
            .ReturnsAsync(false);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));

        await sut.StartAddAssignmentAsync();
        sut.NewAssignmentRoleId = role.Id;
        sut.NewAssignmentPrincipalId = principal.Id;
        await sut.CreateAssignmentAsync();

        Assert.NotNull(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.False(await db.ScopedRoleAssignments.AnyAsync(a => a.PrincipalId == principal.Id));
    }

    [Fact]
    public async Task CreateAssignmentAsync_MissingRoleOrPrincipal_SetsError()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Engineering");
        var principal = SeedUser("bob");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));

        await sut.StartAddAssignmentAsync();
        sut.NewAssignmentPrincipalId = principal.Id;
        await sut.CreateAssignmentAsync();
        Assert.NotNull(sut.ErrorMessage);

        await using var db = NewContext();
        Assert.False(await db.ScopedRoleAssignments.AnyAsync(a => a.PrincipalId == principal.Id));
    }

    [Fact]
    public async Task DeleteAssignmentAsync_RemovesDepartmentAssignment()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Engineering");
        var role = SeedRole("DeptAdmin");
        var principal = SeedUser("bob");
        var assignment = ScopedRoleAssignment.ForDepartment(principal.Id, role.Id, dept.Id);
        await ScopedRoleRepo().CreateAsync(assignment);
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));
        Assert.Single(sut.RoleAssignments);

        await sut.DeleteAssignmentAsync(assignment.Id);

        Assert.Empty(sut.RoleAssignments);
        await using var db = NewContext();
        Assert.False(await db.ScopedRoleAssignments.AnyAsync(a => a.Id == assignment.Id));
    }

    [Fact]
    public async Task DeleteAssignmentAsync_AssignmentOfOtherScope_IsRejected()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Engineering");
        var role = SeedRole("Global");
        var principal = SeedUser("bob");
        var assignment = ScopedRoleAssignment.Global(principal.Id, role.Id);
        await ScopedRoleRepo().CreateAsync(assignment);
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));

        await sut.DeleteAssignmentAsync(assignment.Id);

        Assert.NotNull(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.Id == assignment.Id));
    }

    // ─────────────────────── Global department = system-wide scope ───────────────────────

    private Department SeedGlobal() => SeedDepartment("Global", id: WellKnownGUIDs.DEPARTMENT_GLOBAL);

    private Role SeedAdminRole()
    {
        var role = new Role(WellKnownGUIDs.ROLE_ADMIN, "Administrator", ManagementPermission.FullAdmin, true);
        using var db = NewContext();
        db.Roles.Add(role);
        db.SaveChanges();
        return role;
    }

    [Fact]
    public async Task GlobalDepartment_ListsSystemWideAssignmentsAsOwn()
    {
        var actor = SeedUser("admin");
        var global = SeedGlobal();
        var role = SeedRole("Auditor");
        var principal = SeedUser("bob");
        await ScopedRoleRepo().CreateAsync(ScopedRoleAssignment.Global(principal.Id, role.Id));
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        await sut.SelectAsync(sut.Departments.Single(d => d.Id == global.Id));

        var item = Assert.Single(sut.RoleAssignments);
        Assert.Equal("Auditor", item.RoleName);
        Assert.Null(item.InheritedFrom);
    }

    [Fact]
    public async Task CreateAssignmentAsync_OnGlobalDepartment_StoresSystemWideAssignment()
    {
        var actor = SeedUser("admin");
        var global = SeedGlobal();
        var role = SeedRole("Auditor");
        var principal = SeedUser("bob");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == global.Id));

        await sut.StartAddAssignmentAsync();
        sut.NewAssignmentRoleId = role.Id;
        sut.NewAssignmentPrincipalId = principal.Id;
        await sut.CreateAssignmentAsync();

        Assert.Null(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.PrincipalId == principal.Id
            && a.ScopeType == ScopeType.Global && a.ScopeId == Guid.Empty));
        Assert.Contains(sut.RoleAssignments, a => a.RoleName == "Auditor");
    }

    [Fact]
    public async Task DeleteAssignmentAsync_OnGlobalDepartment_RemovesSystemWideAssignment()
    {
        var actor = SeedUser("admin");
        var global = SeedGlobal();
        var role = SeedRole("Auditor");
        var principal = SeedUser("bob");
        var assignment = ScopedRoleAssignment.Global(principal.Id, role.Id);
        await ScopedRoleRepo().CreateAsync(assignment);
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == global.Id));

        await sut.DeleteAssignmentAsync(assignment.Id);

        Assert.Null(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.False(await db.ScopedRoleAssignments.AnyAsync(a => a.Id == assignment.Id));
    }

    [Fact]
    public async Task DeleteAssignmentAsync_OnGlobalDepartment_SoleAdmin_IsBlocked()
    {
        var admin = SeedUser("admin");
        var global = SeedGlobal();
        var adminRole = SeedAdminRole();
        var assignment = ScopedRoleAssignment.Global(admin.Id, adminRole.Id);
        await ScopedRoleRepo().CreateAsync(assignment);
        var sut = BuildGlobalAdminSut(admin);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == global.Id));

        await sut.DeleteAssignmentAsync(assignment.Id);

        Assert.Equal(Kaimo_File_Server.Core.Language.Resources.Web_Error_LastAdmin, sut.ErrorMessage);
        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.Id == assignment.Id));
    }

    [Fact]
    public async Task SubDepartment_ShowsAssignmentsInheritedFromParentAndGlobal()
    {
        var actor = SeedUser("admin");
        SeedGlobal();
        var parent = SeedDepartment("Engineering");
        var child = SeedDepartment("Backend", parentId: parent.Id);
        var role = SeedRole("DeptAdmin");
        var lead = SeedUser("lead");
        var auditor = SeedUser("auditor");
        var dev = SeedUser("dev");
        await ScopedRoleRepo().CreateAsync(ScopedRoleAssignment.ForDepartment(lead.Id, role.Id, parent.Id));
        await ScopedRoleRepo().CreateAsync(ScopedRoleAssignment.Global(auditor.Id, role.Id));
        await ScopedRoleRepo().CreateAsync(ScopedRoleAssignment.ForDepartment(dev.Id, role.Id, child.Id));
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        await sut.SelectAsync(sut.Departments.Single(d => d.Id == child.Id));

        Assert.Contains(sut.RoleAssignments, a => a.PrincipalName == dev.Name && a.InheritedFrom is null);
        Assert.Contains(sut.RoleAssignments, a => a.PrincipalName == lead.Name && a.InheritedFrom == "Engineering");
        Assert.Contains(sut.RoleAssignments, a => a.PrincipalName == auditor.Name && a.InheritedFrom == "Global");
    }

    [Fact]
    public async Task SubDepartment_ListsGlobalAssignmentsFirst()
    {
        var actor = SeedUser("admin");
        SeedGlobal();
        var dept = SeedDepartment("Engineering");
        var alpha = SeedRole("Alpha");
        var zulu = SeedRole("Zulu");
        var dev = SeedUser("dev");
        await ScopedRoleRepo().CreateAsync(ScopedRoleAssignment.ForDepartment(dev.Id, alpha.Id, dept.Id));
        await ScopedRoleRepo().CreateAsync(ScopedRoleAssignment.Global(dev.Id, zulu.Id));
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));

        Assert.Equal(["Zulu", "Alpha"], sut.RoleAssignments.Select(a => a.RoleName));
    }

    [Fact]
    public async Task DeleteAssignmentAsync_InheritedFromParent_IsRejected()
    {
        var actor = SeedUser("admin");
        var parent = SeedDepartment("Engineering");
        var child = SeedDepartment("Backend", parentId: parent.Id);
        var role = SeedRole("DeptAdmin");
        var lead = SeedUser("lead");
        var assignment = ScopedRoleAssignment.ForDepartment(lead.Id, role.Id, parent.Id);
        await ScopedRoleRepo().CreateAsync(assignment);
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == child.Id));

        await sut.DeleteAssignmentAsync(assignment.Id);

        Assert.NotNull(sut.ErrorMessage);
        await using var db = NewContext();
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.Id == assignment.Id));
    }

    [Fact]
    public async Task ConfirmDeleteAsync_RemovesTheDepartmentsRoleAssignments()
    {
        var actor = SeedUser("admin");
        var dept = SeedDepartment("Doomed");
        var role = SeedRole("DeptAdmin");
        var principal = SeedUser("bob");
        var assignment = ScopedRoleAssignment.ForDepartment(principal.Id, role.Id, dept.Id);
        await ScopedRoleRepo().CreateAsync(assignment);
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));

        sut.RequestDelete();
        await sut.ConfirmDeleteAsync();

        await using var db = NewContext();
        Assert.False(await db.ScopedRoleAssignments.AnyAsync(a => a.Id == assignment.Id));
    }

    [Fact]
    public async Task SaveAsync_GlobalDepartment_NeverStoresFileDefaultOrParent()
    {
        var actor = SeedUser("admin");
        var global = SeedGlobal();
        var other = SeedDepartment("Sales");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == global.Id));
        await sut.StartEditAsync();

        sut.EditHasOwnPermission = true;
        sut.TogglePermFlag(FilePermission.ReadAll, true);
        sut.EditParentId = other.Id;
        await sut.SaveAsync();

        await using var db = NewContext();
        var row = await db.Departments.FindAsync(global.Id);
        Assert.Null(row!.DefaultFilePermission);
        Assert.Null(row.ParentDepartmentId);
    }

    [Fact]
    public async Task CreateAsync_WithGlobalAsParent_StoresTopLevelDepartment()
    {
        var actor = SeedUser("admin");
        SeedGlobal();
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        sut.CreateName = "Sales";
        sut.CreateParentId = WellKnownGUIDs.DEPARTMENT_GLOBAL;
        await sut.CreateAsync();

        await using var db = NewContext();
        Assert.Null((await db.Departments.SingleAsync(d => d.Name == "Sales")).ParentDepartmentId);
    }

    [Fact]
    public async Task StartEditAsync_AvailableParents_ExcludeGlobal()
    {
        var actor = SeedUser("admin");
        SeedGlobal();
        var dept = SeedDepartment("Sales");
        SeedDepartment("Marketing");
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == dept.Id));

        await sut.StartEditAsync();

        Assert.DoesNotContain(sut.AvailableParents, d => d.Id == WellKnownGUIDs.DEPARTMENT_GLOBAL);
        Assert.Contains(sut.AvailableParents, d => d.Name == "Marketing");
    }

    [Fact]
    public async Task Tree_TopLevelDepartmentsHangBelowGlobal()
    {
        var actor = SeedUser("admin");
        SeedGlobal();
        var sales = SeedDepartment("Sales");
        var emea = SeedDepartment("EMEA", parentId: sales.Id);
        var sut = BuildGlobalAdminSut(actor);
        await sut.LoadAsync();

        var tree = sut.GetTreeOrderedDepartments();

        Assert.Equal(WellKnownGUIDs.DEPARTMENT_GLOBAL, tree[0].Department.Id);
        Assert.Equal(0, tree[0].Depth);
        Assert.True(tree[0].HasChildren);
        Assert.Equal(1, tree.Single(n => n.Department.Id == sales.Id).Depth);
        Assert.Equal(2, tree.Single(n => n.Department.Id == emea.Id).Depth);
        Assert.Equal(1, sut.GetChildCount(tree[0].Department));
    }
}
