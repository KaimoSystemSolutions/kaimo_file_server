using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Web.Components.ViewModels;
using Kaimo_File_Server.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Phase 3 of the scoped-permission audit, outside user management: system-wide pages need
/// global rights (F9), shares are created only where the actor may create them and unreferenced
/// folders are purged only with global DeleteShares (F10), and a department is deleted only by
/// whoever manages its parent (F12).
/// </summary>
public partial class ScopedAdminEscalationDatabaseTests
{
    private User SeedHolder(string username, ManagementPermission perms, ScopeType scopeType, Guid scopeId)
    {
        var user = SeedUser(username);
        AddToDepartment(_own, user);
        Assign(user.Id, SeedNamedRole($"{username}-role", perms), scopeType, scopeId);
        return user;
    }

    // ═══════════════════ F9: system-wide pages ═══════════════════

    private SecurityMonitorViewModel BuildSecurityMonitor(User actor)
        => new(new SecurityMonitor(), new Mock<ISecurityEventRepository>().Object, UserRepo(),
            BuildManagementAuthService(), new UserContextFactory(DbFactory), AuthStateFor(actor.Username),
            new ConfigurationBuilder().Build(), NullLogger<SecurityMonitorViewModel>.Instance);

    [Fact]
    public async Task SecurityMonitor_RequiresGlobalPermission()
    {
        var scoped = SeedHolder("scopedsec", ManagementPermission.ViewSecurityMonitor, ScopeType.Department, _own.Id);
        var global = SeedHolder("globalsec", ManagementPermission.ViewSecurityMonitor, ScopeType.Global, Guid.Empty);

        var denied = BuildSecurityMonitor(scoped);
        await denied.LoadAsync();
        Assert.False(denied.CanAccessPage);

        var allowed = BuildSecurityMonitor(global);
        await allowed.LoadAsync();
        Assert.True(allowed.CanAccessPage);
    }

    [Fact]
    public async Task ClientDevices_RequireGlobalPermission()
    {
        var scoped = SeedHolder("scopeddev", ManagementPermission.ManageClientDevices, ScopeType.Department, _own.Id);
        var global = SeedHolder("globaldev", ManagementPermission.ManageClientDevices, ScopeType.Global, Guid.Empty);

        var devices = new Mock<ISyncDeviceRepository>();
        ClientDeviceAdminViewModel Build(User actor) => new(
            AuthStateFor(actor.Username), new UserContextFactory(DbFactory), BuildManagementAuthService(),
            devices.Object, new Mock<IDeviceSyncProfileRepository>().Object,
            new Mock<IRefreshTokenRepository>().Object, ShareRepo(), TimeProvider.System,
            NullLogger<ClientDeviceAdminViewModel>.Instance);

        var denied = Build(scoped);
        await denied.LoadAsync();
        Assert.False(denied.CanManage);
        Assert.False(await denied.RevokeDeviceAsync(Guid.NewGuid()));
        devices.Verify(d => d.GetByIdAsync(It.IsAny<Guid>()), Times.Never);

        var allowed = Build(global);
        await allowed.LoadAsync();
        Assert.True(allowed.CanManage);
    }

    // ═══════════════════ F10: share creation and unreferenced folders ═══════════════════

    /// <summary>Runs <paramref name="test"/> with a real, temporary storage pool.</summary>
    private async Task WithPoolAsync(Func<string, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "kaimo-tests", Guid.NewGuid().ToString("N"));
        var pool = Path.Combine(root, "pool-01");
        Directory.CreateDirectory(pool);
        try { await test(pool); }
        finally { Directory.Delete(root, recursive: true); }
    }

    private async Task<ShareListViewModel> LoadShareListAsync(User actor, string pool)
    {
        var sut = new ShareListViewModel(ShareRepo(), UserRepo(), GroupRepo(), AclRepo(), FileMetadataRepo(),
            new Mock<IAclService>().Object, BuildManagementAuthService(),
            new UserContextFactory(DbFactory), new ShareLockManager(), AuthStateFor(actor.Username),
            NullLogger<ShareListViewModel>.Instance, [pool], departmentRepo: DepartmentRepo());
        await sut.LoadAsync();
        return sut;
    }

    private async Task<ShareDefinition?> StoredShareAsync(string name)
    {
        await using var db = NewContext();
        return await db.ShareDefinitions.SingleOrDefaultAsync(s => s.Name == name);
    }

    [Fact]
    public Task DepartmentShareManager_CreatesSharesOnlyInOwnScope() => WithPoolAsync(async pool =>
    {
        var manager = SeedHolder("sharemgr", ManagementPermission.ShareAdmin, ScopeType.Department, _own.Id);
        var sut = await LoadShareListAsync(manager, pool);

        Assert.True(sut.CanCreateShare);
        Assert.Contains(sut.CreateShareDepartments, d => d.Id == _ownChild.Id);
        Assert.DoesNotContain(sut.CreateShareDepartments,
            d => d.Id == _foreign.Id || d.Id == WellKnownGUIDs.DEPARTMENT_GLOBAL);

        foreach (var (name, target) in new[] { ("inforeign", _foreign.Id), ("inglobal", WellKnownGUIDs.DEPARTMENT_GLOBAL) })
        {
            sut.StartCreate();
            sut.NewShareName = name;
            sut.CreateShareDepartmentId = target; // forged
            Assert.False(await sut.CreateShareAsync());
            Assert.Null(await StoredShareAsync(name));
        }

        sut.StartCreate();
        Assert.Contains(sut.CreateShareDepartments, d => d.Id == sut.CreateShareDepartmentId);
        sut.NewShareName = "inchild";
        sut.CreateShareDepartmentId = _ownChild.Id;
        Assert.True(await sut.CreateShareAsync());
        Assert.Equal(_ownChild.Id, (await StoredShareAsync("inchild"))!.DepartmentId);
    });

    [Fact]
    public Task ActorWithoutCreateShares_CannotCreateShare() => WithPoolAsync(async pool =>
    {
        var aclManager = SeedHolder("aclmgr", ManagementPermission.ManageShareAcls, ScopeType.Department, _own.Id);
        var sut = await LoadShareListAsync(aclManager, pool);

        Assert.False(sut.CanCreateShare);
        sut.NewShareName = "sneaky";
        sut.CreateShareDepartmentId = _own.Id;
        Assert.False(await sut.CreateShareAsync());
        Assert.Null(await StoredShareAsync("sneaky"));
    });

    [Fact]
    public Task GlobalAdmin_CreatesShareInGlobalByDefault() => WithPoolAsync(async pool =>
    {
        var sut = await LoadShareListAsync(_globalAdmin, pool);
        sut.StartCreate();
        sut.NewShareName = "plain";
        Assert.True(await sut.CreateShareAsync());
        Assert.Equal(WellKnownGUIDs.DEPARTMENT_GLOBAL, (await StoredShareAsync("plain"))!.DepartmentId);
    });

    [Fact]
    public Task PurgingUnreferencedFolder_RequiresGlobalDeleteShares() => WithPoolAsync(async pool =>
    {
        var orphan = Path.Combine(pool, "orphan");
        Directory.CreateDirectory(orphan);
        await File.WriteAllTextAsync(Path.Combine(orphan, "data.txt"), "keep me");

        var linkManager = SeedHolder("linkmgr", ManagementPermission.ManageShareLinks, ScopeType.Global, Guid.Empty);
        var denied = await LoadShareListAsync(linkManager, pool);
        Assert.False(denied.CanPurgeUnreferencedShares);
        Assert.False(await denied.PurgeUnreferencedFolderAsync(orphan));
        Assert.True(Directory.Exists(orphan));

        var deleter = SeedHolder("deleter", ManagementPermission.DeleteShares, ScopeType.Global, Guid.Empty);
        var allowed = await LoadShareListAsync(deleter, pool);
        Assert.True(allowed.CanPurgeUnreferencedShares);
        Assert.True(await allowed.PurgeUnreferencedFolderAsync(orphan));
        Assert.False(Directory.Exists(orphan));
    });

    // ═══════════════════ F12: department deletion ═══════════════════

    private async Task<DepartmentViewModel> SelectDepartmentAsync(User actor, Department department)
    {
        var sut = BuildDepartments(actor);
        await sut.LoadAsync();
        await sut.SelectAsync(sut.Departments.Single(d => d.Id == department.Id));
        return sut;
    }

    private async Task<Department?> StoredDepartmentAsync(Department department)
        => await DepartmentRepo().GetByIdAsync(department.Id);

    [Fact]
    public async Task DeptAdmin_CannotDeleteTheDepartmentTheAssignmentSitsOn()
    {
        var sut = await SelectDepartmentAsync(_deptAdmin, _own);
        Assert.False(sut.CanDelete);

        sut.RequestDelete();
        await sut.ConfirmDeleteAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.NotNull(await StoredDepartmentAsync(_own));
    }

    [Fact]
    public async Task DeptAdmin_CanDeleteSubdepartment_ChildrenMoveUp_DescendantAssignmentsStay()
    {
        var grandchild = SeedDepartment("OwnGrandchild", parentId: _ownChild.Id);
        var helper = SeedRole(ManagementPermission.ResetPasswords);
        var bob = SeedUser("bob");
        Assign(bob.Id, helper, ScopeType.Department, _ownChild.Id);
        Assign(bob.Id, helper, ScopeType.Department, grandchild.Id);

        var sut = await SelectDepartmentAsync(_deptAdmin, _ownChild);
        Assert.True(sut.CanDelete);

        sut.RequestDelete();
        await sut.ConfirmDeleteAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.Null(await StoredDepartmentAsync(_ownChild));
        Assert.Equal(_own.Id, (await StoredDepartmentAsync(grandchild))!.ParentDepartmentId);
        await using var db = NewContext();
        Assert.False(await db.ScopedRoleAssignments.AnyAsync(a => a.ScopeId == _ownChild.Id));
        Assert.True(await db.ScopedRoleAssignments.AnyAsync(a => a.ScopeId == grandchild.Id));
    }

    [Fact]
    public async Task GlobalAdmin_CanDeleteTopLevelDepartment()
    {
        var sut = await SelectDepartmentAsync(_globalAdmin, _foreign);
        Assert.True(sut.CanDelete);

        sut.RequestDelete();
        await sut.ConfirmDeleteAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.Null(await StoredDepartmentAsync(_foreign));
    }
}
