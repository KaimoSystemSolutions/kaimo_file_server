using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Phase 4 of the scoped-permission audit: how one evaluation combines the actor's roles (F14),
/// that group memberships are read fresh instead of from the actor snapshot (F15), and that an
/// evaluation scope caches lookups only while it is open (F16).
/// </summary>
public partial class ManagementAuthServiceTests
{
    // ═══════════════════ F14: per-role ALL-bits semantics ═══════════════════

    [Fact]
    public async Task CombinedBitCheck_RequiresOneRoleCarryingAllBits_WhileEffectiveSetIsTheUnion()
    {
        var actor = Actor(RoleWith(ManagementPermission.EditDepartment), RoleWith(ManagementPermission.ViewDepartment));
        const ManagementPermission both = ManagementPermission.EditDepartment | ManagementPermission.ViewDepartment;

        Assert.False(await _sut.HasGlobalPermissionAsync(actor, both));
        Assert.True(await _sut.HasGlobalPermissionAsync(actor, ManagementPermission.EditDepartment));
        Assert.True(await _sut.HasAnyPermissionAsync(actor, both));
        Assert.Equal(both, await _sut.GetEffectivePermissionsAtAsync(actor, ScopeType.Global, Guid.Empty));
    }

    // ═══════════════════ F15: fresh group memberships ═══════════════════

    /// <summary>A role granted globally to a group, only returned when the group is asked for.</summary>
    private Group GroupWithGlobalRole(ManagementPermission perms)
    {
        var group = new Group(Guid.NewGuid(), "Delegates");
        var role = RoleWith(perms);
        _roleRepo.Setup(r => r.GetByIdAsync(role.Id)).ReturnsAsync(role);
        _assignmentRepo
            .Setup(r => r.GetEffectiveAssignmentsAsync(_actorId, It.Is<IEnumerable<Guid>>(ids => ids.Contains(group.Id))))
            .ReturnsAsync([new ScopedRoleAssignment(group.Id, role.Id, ScopeType.Global, Guid.Empty)]);
        return group;
    }

    private UserContext ActorWithSnapshotGroups(params Group[] groups)
        => new(new User(_actorId, "Actor", "actor", "h", "n"), groups.ToHashSet(), [], []);

    [Fact]
    public async Task GroupRemovedAfterContextWasBuilt_NoLongerGrantsAuthority()
    {
        var group = GroupWithGlobalRole(ManagementPermission.ResetPasswords);
        var actor = ActorWithSnapshotGroups(group); // stale: the membership is gone in the store

        Assert.False(await _sut.HasGlobalPermissionAsync(actor, ManagementPermission.ResetPasswords));
    }

    [Fact]
    public async Task GroupJoinedAfterContextWasBuilt_GrantsAuthority()
    {
        var group = GroupWithGlobalRole(ManagementPermission.ResetPasswords);
        _userRepo.Setup(u => u.GetGroupsForUserAsync(_actorId)).ReturnsAsync([group]);
        var actor = ActorWithSnapshotGroups();

        Assert.True(await _sut.HasGlobalPermissionAsync(actor, ManagementPermission.ResetPasswords));
    }

    // ═══════════════════ F16: evaluation scope ═══════════════════

    [Fact]
    public async Task EvaluationScope_LoadsAssignmentsAndDepartmentTreeOnce()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept, ManagementPermission.EditDepartment);
        var actor = Actor();

        using (_sut.BeginEvaluationScope())
        {
            Assert.True(await _sut.CanManageDepartmentAsync(actor, dept, ManagementPermission.EditDepartment));
            Assert.False(await _sut.CanManageDepartmentAsync(actor, Guid.NewGuid(), ManagementPermission.EditDepartment));
            Assert.False(await _sut.CanManageDepartmentAsync(actor, Guid.NewGuid(), ManagementPermission.EditDepartment));
        }

        _assignmentRepo.Verify(r => r.GetEffectiveAssignmentsAsync(_actorId, It.IsAny<IEnumerable<Guid>>()), Times.Once);
        _userRepo.Verify(u => u.GetGroupsForUserAsync(_actorId), Times.Once);
        _departmentRepo.Verify(d => d.GetDescendantIdsAsync(dept), Times.Once);
    }

    [Fact]
    public async Task WithoutScope_EveryCheckReadsTheStoreAgain_SoRevocationIsImmediate()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept, ManagementPermission.EditDepartment);
        var actor = Actor();

        using (_sut.BeginEvaluationScope())
            Assert.True(await _sut.CanManageDepartmentAsync(actor, dept, ManagementPermission.EditDepartment));

        _assignmentRepo
            .Setup(r => r.GetEffectiveAssignmentsAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new List<ScopedRoleAssignment>());

        Assert.False(await _sut.CanManageDepartmentAsync(actor, dept, ManagementPermission.EditDepartment));
    }

    [Fact]
    public async Task SingleUserCheck_LoadsTheActorsAssignmentsOnce()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept, ManagementPermission.DepartmentAdmin);
        var actor = Actor();
        TargetIsMemberOf(dept);
        TargetHolds((ScopeType.Department, dept, ManagementPermission.ResetPasswords));

        Assert.True(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.ResetPasswords));

        _assignmentRepo.Verify(r => r.GetEffectiveAssignmentsAsync(_actorId, It.IsAny<IEnumerable<Guid>>()), Times.Once);
    }
}
