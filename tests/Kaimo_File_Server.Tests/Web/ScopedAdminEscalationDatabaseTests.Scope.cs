using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Helpers;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Kaimo_File_Server.Infrastructure.Services;
using Kaimo_File_Server.Web.Components.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Phase 2 of the scoped-permission audit: a scoped admin may only place departments, groups
/// and shares where it already holds the matching authority (F4, F6, F7), and changes to group
/// memberships are bounded by the actor's authority over the group and the roles it grants (F5).
/// </summary>
public partial class ScopedAdminEscalationDatabaseTests
{
    private async Task<Guid?> ParentOfAsync(Department department)
        => (await DepartmentRepo().GetByIdAsync(department.Id))!.ParentDepartmentId;

    // ═══════════════════ F4: department hierarchy ═══════════════════

    [Fact]
    public async Task DeptAdmin_MovingOwnDepartmentUnderForeignParent_IsRefused()
    {
        var sut = await EditDepartmentAsync(_deptAdmin, _own);

        Assert.DoesNotContain(sut.AvailableParents, d => d.Id == _foreign.Id);
        sut.EditParentId = _foreign.Id;
        await sut.SaveAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.Null(await ParentOfAsync(_own));
    }

    [Fact]
    public async Task DeptAdmin_MovingChildDepartmentToTopLevel_IsRefused()
    {
        var sut = await EditDepartmentAsync(_deptAdmin, _ownChild);

        sut.EditParentId = null;
        await sut.SaveAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.Equal(_own.Id, await ParentOfAsync(_ownChild));
    }

    [Fact]
    public async Task DeptAdmin_MovingChildBetweenOwnSubdepartments_IsAllowed()
    {
        var sibling = SeedDepartment("OwnSibling", parentId: _own.Id);
        var sut = await EditDepartmentAsync(_deptAdmin, _ownChild);

        Assert.Contains(sut.AvailableParents, d => d.Id == sibling.Id);
        sut.EditParentId = sibling.Id;
        await sut.SaveAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.Equal(sibling.Id, await ParentOfAsync(_ownChild));
    }

    [Fact]
    public async Task Parent_CycleOrUnknownDepartment_IsRefusedEvenForGlobalAdmin()
    {
        var sut = await EditDepartmentAsync(_globalAdmin, _own);
        sut.EditParentId = _ownChild.Id; // own descendant → cycle
        await sut.SaveAsync();
        Assert.NotNull(sut.ErrorMessage);

        sut = await EditDepartmentAsync(_globalAdmin, _own);
        sut.EditParentId = Guid.NewGuid(); // does not exist
        await sut.SaveAsync();
        Assert.NotNull(sut.ErrorMessage);

        Assert.Null(await ParentOfAsync(_own));
    }

    [Fact]
    public async Task GlobalAdmin_CanMoveDepartmentUnderAnotherParent()
    {
        var sut = await EditDepartmentAsync(_globalAdmin, _own);

        sut.EditParentId = _foreign.Id;
        await sut.SaveAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.Equal(_foreign.Id, await ParentOfAsync(_own));
    }

    [Fact]
    public async Task DeptAdmin_CreatesDepartmentsOnlyBelowOwnScope()
    {
        var sut = BuildDepartments(_deptAdmin);
        await sut.LoadAsync();
        sut.StartCreate();

        Assert.Equal(_own.Id, sut.CreateParentId);
        Assert.DoesNotContain(sut.CreateParentOptions, d => d.Id == _foreign.Id);
        Assert.False(sut.CanCreateTopLevel);

        foreach (var (name, parent) in new (string, Guid?)[] { ("TopLevel", null), ("UnderForeign", _foreign.Id) })
        {
            sut.StartCreate();
            sut.CreateName = name;
            sut.CreateParentId = parent;
            await sut.CreateAsync();
            Assert.NotNull(sut.ErrorMessage);
        }

        sut.StartCreate();
        sut.CreateName = "UnderOwn";
        sut.CreateParentId = _own.Id;
        await sut.CreateAsync();

        await using var db = NewContext();
        Assert.False(await db.Departments.AnyAsync(d => d.Name == "TopLevel" || d.Name == "UnderForeign"));
        Assert.Equal(_own.Id, (await db.Departments.SingleAsync(d => d.Name == "UnderOwn")).ParentDepartmentId);
    }

    // ═══════════════════ F6: group creation ═══════════════════

    [Fact]
    public async Task DeptAdmin_CreatesGroupsOnlyInOwnScope()
    {
        var sut = BuildUserList(_deptAdmin);
        await sut.LoadAsync();
        sut.StartCreateGroup();

        Assert.NotEqual(WellKnownGUIDs.DEPARTMENT_GLOBAL, sut.CreateGroupDepartmentId);
        Assert.DoesNotContain(sut.AuthorizedDepartmentsForGroupCreate,
            d => d.Id == _foreign.Id || d.Id == WellKnownGUIDs.DEPARTMENT_GLOBAL);

        foreach (var (name, dept) in new[] { ("InForeign", _foreign.Id), ("InGlobal", WellKnownGUIDs.DEPARTMENT_GLOBAL) })
        {
            sut.StartCreateGroup();
            sut.CreateGroupName = name;
            sut.CreateGroupDepartmentId = dept;
            await sut.CreateGroupAsync();
            Assert.NotNull(sut.ErrorMessage);
        }

        sut.StartCreateGroup();
        sut.CreateGroupName = "InChild";
        sut.CreateGroupDepartmentId = _ownChild.Id;
        await sut.CreateGroupAsync();

        await using var db = NewContext();
        Assert.False(await db.Groups.AnyAsync(g => g.Name == "InForeign" || g.Name == "InGlobal"));
        Assert.Equal(_ownChild.Id, (await db.Groups.SingleAsync(g => g.Name == "InChild")).DepartmentId);
    }

    // ═══════════════════ F5: group memberships ═══════════════════

    private async Task<UserListViewModel> EditUserAsync(User actor, User target)
    {
        var sut = BuildUserList(actor);
        await sut.LoadAsync();
        await sut.SelectUserAsync(sut.Users.Single(u => u.Id == target.Id));
        await sut.StartEditUserAsync();
        return sut;
    }

    private void SeedMembership(Group group, User user)
        => GroupRepo().AddMemberAsync(group.Id, user.Id).GetAwaiter().GetResult();

    [Fact]
    public async Task DeptAdmin_EditingUser_CannotLeaveOrJoinGroupsOutsideScope()
    {
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);
        var readers = SeedGroup("Readers");            // Global department
        var otherReaders = SeedGroup("OtherReaders");  // Global department
        SeedMembership(readers, bob);

        var sut = await EditUserAsync(_deptAdmin, bob);
        Assert.DoesNotContain(sut.EditUserGroups, g => g.Item.Id == readers.Id || g.Item.Id == otherReaders.Id);

        sut.EditUserGroups.Add(new CheckboxItem<Group>(readers, false));     // forged removal
        sut.EditUserGroups.Add(new CheckboxItem<Group>(otherReaders, true)); // forged join
        await sut.SaveUserAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.True(await IsGroupMemberAsync(readers, bob));
        Assert.False(await IsGroupMemberAsync(otherReaders, bob));
    }

    [Fact]
    public async Task DeptAdmin_EditingUser_KeepsUnshownGroups_AndChangesOwnGroups()
    {
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);
        var readers = SeedGroup("Readers"); // Global department, not manageable by the actor
        var team = SeedGroup("Team", _own.Id);
        var squad = SeedGroup("Squad", _own.Id);
        SeedMembership(readers, bob);
        SeedMembership(team, bob);

        var sut = await EditUserAsync(_deptAdmin, bob);
        sut.EditUserGroups.Single(g => g.Item.Id == team.Id).IsChecked = false;
        sut.EditUserGroups.Single(g => g.Item.Id == squad.Id).IsChecked = true;
        await sut.SaveUserAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.True(await IsGroupMemberAsync(readers, bob));
        Assert.False(await IsGroupMemberAsync(team, bob));
        Assert.True(await IsGroupMemberAsync(squad, bob));
    }

    [Fact]
    public async Task DeptAdmin_RemovingMemberFromGroupThatGrantsGlobalRole_IsRefused()
    {
        var group = SeedGroup("Cert team", _own.Id);
        Assign(group.Id, SeedRole(ManagementPermission.ManageCertificates), ScopeType.Global, Guid.Empty);
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);
        SeedMembership(group, bob);

        var sut = await EditGroupAsync(_deptAdmin, group);
        sut.EditGroupMembers.Single(m => m.Item.Id == bob.Id).IsChecked = false;
        await sut.SaveGroupAsync();

        Assert.NotNull(sut.ErrorMessage);
        Assert.True(await IsGroupMemberAsync(group, bob));
    }

    [Fact]
    public async Task DelegatingDeptAdmin_RemovingMemberFromGroupWithRoleInOwnScope_IsAllowed()
    {
        // Revoking a role needs the right to assign it (AssignRoles), exactly like joining.
        var delegator = SeedUser("delegator");
        AddToDepartment(_own, delegator);
        Assign(delegator.Id, SeedRole(ManagementPermission.DepartmentAdmin | ManagementPermission.AssignRoles),
            ScopeType.Department, _own.Id);
        var group = SeedGroup("Helpers", _own.Id);
        Assign(group.Id, SeedRole(ManagementPermission.ResetPasswords), ScopeType.Department, _own.Id);
        var bob = SeedUser("bob");
        AddToDepartment(_own, bob);
        SeedMembership(group, bob);

        var sut = await EditGroupAsync(delegator, group);
        sut.EditGroupMembers.Single(m => m.Item.Id == bob.Id).IsChecked = false;
        await sut.SaveGroupAsync();

        Assert.Null(sut.ErrorMessage);
        Assert.False(await IsGroupMemberAsync(group, bob));
    }

    // ═══════════════════ F7: share department change ═══════════════════

    private const string Pool = "/data/pool-01";

    private ShareListViewModel BuildShareList(User actor)
        => new(ShareRepo(), UserRepo(), GroupRepo(), AclRepo(), FileMetadataRepo(),
            new Mock<IAclService>().Object, BuildManagementAuthService(),
            new UserContextFactory(DbFactory), new ShareLockManager(), AuthStateFor(actor.Username),
            NullLogger<ShareListViewModel>.Instance, [Pool], departmentRepo: DepartmentRepo());

    private async Task<(ShareListViewModel Sut, ShareDefinition Share)> SelectShareAsync(User actor, Guid departmentId)
    {
        var name = $"s{Guid.NewGuid():N}"[..12];
        var share = SeedShare(name, path: $"{Pool}/{name}", departmentId: departmentId);
        var sut = BuildShareList(actor);
        await sut.LoadAsync();
        sut.SelectShare(sut.Shares.Single(s => s.Id == share.Id));
        return (sut, share);
    }

    [Fact]
    public async Task DepartmentShareManager_MovesSharesOnlyWithinOwnScope()
    {
        var manager = SeedUser("sharemgr");
        AddToDepartment(_own, manager);
        Assign(manager.Id, SeedRole(ManagementPermission.ShareAdmin), ScopeType.Department, _own.Id);

        foreach (var target in new[] { _foreign.Id, WellKnownGUIDs.DEPARTMENT_GLOBAL })
        {
            var (sut, share) = await SelectShareAsync(manager, _own.Id);
            Assert.DoesNotContain(sut.ShareTargetDepartments, d => d.Id == target);
            sut.EditShareDepartmentId = target;
            Assert.False(await sut.ChangeDepartmentAsync());
            Assert.Equal(_own.Id, await ShareDepartmentAsync(share));
        }

        var (allowed, ownShare) = await SelectShareAsync(manager, _own.Id);
        allowed.EditShareDepartmentId = _ownChild.Id;
        Assert.True(await allowed.ChangeDepartmentAsync());
        Assert.Equal(_ownChild.Id, await ShareDepartmentAsync(ownShare));
    }

    [Fact]
    public async Task ShareScopedManager_CannotMoveTheShareToAnyDepartment()
    {
        var share = SeedShare("scoped", path: $"{Pool}/scoped", departmentId: _own.Id);
        var manager = SeedUser("scopedmgr");
        Assign(manager.Id, SeedRole(ManagementPermission.ShareAdmin), ScopeType.Share, share.Id);
        var sut = BuildShareList(manager);
        await sut.LoadAsync();
        sut.SelectShare(sut.Shares.Single(s => s.Id == share.Id));

        sut.EditShareDepartmentId = _ownChild.Id;
        Assert.False(await sut.ChangeDepartmentAsync());
        Assert.Equal(_own.Id, await ShareDepartmentAsync(share));
    }

    [Fact]
    public async Task ShareDepartmentChange_ToUnknownDepartment_IsRefused()
    {
        var (sut, share) = await SelectShareAsync(_globalAdmin, _own.Id);

        sut.EditShareDepartmentId = Guid.NewGuid();
        Assert.False(await sut.ChangeDepartmentAsync());
        Assert.Equal(_own.Id, await ShareDepartmentAsync(share));
    }
}
