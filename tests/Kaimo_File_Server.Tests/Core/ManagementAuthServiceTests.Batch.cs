using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Batch support for pickers that check many users: the actor's user scope is resolved once per
/// covered department instead of once per user, and the target lookups of the rank check are
/// cached while an evaluation scope is open.
/// </summary>
public partial class ManagementAuthServiceTests
{
    private static User UserNamed(string name) => new(Guid.NewGuid(), name, name, "h", "n");

    private void DepartmentHasMembers(Guid departmentId, params User[] users)
        => _departmentRepo.Setup(d => d.GetUsersAsync(departmentId)).ReturnsAsync(users.ToList());

    // ═══════════════════ GetUserIdsInScopeAsync ═══════════════════

    [Fact]
    public async Task UserIdsInScope_GlobalAuthority_IsUnrestricted()
    {
        var actor = Actor(RoleWith(ManagementPermission.AssignRoles));

        Assert.Null(await _sut.GetUserIdsInScopeAsync(actor, ManagementPermission.AssignRoles));
    }

    [Fact]
    public async Task UserIdsInScope_DepartmentAuthority_ListsMembersOfTheDepartmentAndItsDescendants()
    {
        var dept = Guid.NewGuid();
        var child = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept, ManagementPermission.AssignDepartments);
        _departmentRepo.Setup(d => d.GetDescendantIdsAsync(dept)).ReturnsAsync([child]);
        var alice = UserNamed("alice");
        var bob = UserNamed("bob");
        DepartmentHasMembers(dept, alice);
        DepartmentHasMembers(child, bob);

        var ids = await _sut.GetUserIdsInScopeAsync(Actor(), ManagementPermission.AssignDepartments);

        Assert.Equal(new HashSet<Guid> { alice.Id, bob.Id }, ids);
        _departmentRepo.Verify(
            d => d.IsUserInDepartmentOrDescendantAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task UserIdsInScope_MissingBitOrShareScope_ReachesNobody()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept, ManagementPermission.EditDepartment);
        DepartmentHasMembers(dept, UserNamed("alice"));
        Assert.Empty((await _sut.GetUserIdsInScopeAsync(Actor(), ManagementPermission.AssignDepartments))!);

        GrantScoped(ScopeType.Share, Guid.NewGuid(), ManagementPermission.AssignRoles);
        Assert.Empty((await _sut.GetUserIdsInScopeAsync(Actor(), ManagementPermission.AssignRoles))!);
    }

    // ═══════════════════ Rank check inside an evaluation scope ═══════════════════

    [Fact]
    public async Task EvaluationScope_LoadsTheTargetsGroupsAndAssignmentsOnce()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept, ManagementPermission.DepartmentAdmin);
        var actor = Actor();
        TargetIsMemberOf(dept);
        TargetHolds((ScopeType.Department, dept, ManagementPermission.ResetPasswords));

        using (_sut.BeginEvaluationScope())
        {
            Assert.True(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.ResetPasswords));
            Assert.True(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.EnableDisableUsers));
        }

        _userRepo.Verify(u => u.GetGroupsForUserAsync(_targetId), Times.Once);
        _assignmentRepo.Verify(r => r.GetEffectiveAssignmentsAsync(_targetId, It.IsAny<IEnumerable<Guid>>()), Times.Once);
    }

    [Fact]
    public async Task WithoutEvaluationScope_TheTargetIsReadFreshOnEveryCheck()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept, ManagementPermission.DepartmentAdmin);
        var actor = Actor();
        TargetIsMemberOf(dept);

        Assert.True(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.ResetPasswords));
        TargetHolds((ScopeType.Global, Guid.Empty, ManagementPermission.ManageCertificates));
        Assert.False(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.ResetPasswords));
    }
}
