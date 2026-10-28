using Kaimo_File_Server.Core.Domain.Department;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Security;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// User management is bounded by the TARGET's authority, not only by the actor's scope: an
/// actor may only act on a user whose every role assignment the actor itself covers. Without
/// this, a department admin could reset the password of a global admin who also happens to be
/// a member of the department. Deleting a user additionally requires authority over every
/// department the user belongs to, and adding a user to a department requires
/// <see cref="ManagementPermission.AssignDepartments"/> plus authority over the user.
/// </summary>
public partial class ManagementAuthServiceTests
{
    private readonly Guid _targetId = Guid.NewGuid();

    /// <summary>
    /// Registers the target's own effective assignments. Must run AFTER the actor setup:
    /// the actor helpers match any user id, this one only the target.
    /// </summary>
    private void TargetHolds(params (ScopeType ScopeType, Guid ScopeId, ManagementPermission Perms)[] grants)
    {
        var assignments = new List<ScopedRoleAssignment>();
        foreach (var (scopeType, scopeId, perms) in grants)
        {
            var role = RoleWith(perms);
            _roleRepo.Setup(r => r.GetByIdAsync(role.Id)).ReturnsAsync(role);
            assignments.Add(new ScopedRoleAssignment(_targetId, role.Id, scopeType, scopeId));
        }
        _assignmentRepo
            .Setup(r => r.GetEffectiveAssignmentsAsync(_targetId, It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(assignments);
    }

    private void TargetIsMemberOf(Guid scopeDepartmentId)
        => _departmentRepo
            .Setup(d => d.IsUserInDepartmentOrDescendantAsync(_targetId, scopeDepartmentId))
            .ReturnsAsync(true);

    private void TargetDepartments(params Guid[] departmentIds)
        => _departmentRepo
            .Setup(d => d.GetDepartmentsForUserAsync(_targetId))
            .ReturnsAsync(departmentIds.Select(id => new Department($"d-{id}") { Id = id }).ToList());

    // ═══════════════════ Target rank ═══════════════════

    [Fact]
    public async Task CanManageUser_DepartmentAdmin_TargetHoldsGlobalRole_ReturnsFalse()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept, ManagementPermission.DepartmentAdmin);
        var actor = Actor();
        TargetIsMemberOf(dept);
        TargetHolds((ScopeType.Global, Guid.Empty, ManagementPermission.ManageCertificates));

        Assert.False(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.ResetPasswords));
    }

    [Fact]
    public async Task CanManageUser_DepartmentAdmin_TargetIsAdminOfSiblingDepartment_ReturnsFalse()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept, ManagementPermission.DepartmentAdmin);
        var actor = Actor();
        TargetIsMemberOf(dept);
        TargetHolds((ScopeType.Department, Guid.NewGuid(), ManagementPermission.UserAdmin));

        Assert.False(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.EditUserProfiles));
    }

    [Fact]
    public async Task CanManageUser_DepartmentAdmin_TargetHoldsSubsetRoleInSameDepartment_ReturnsTrue()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept, ManagementPermission.DepartmentAdmin);
        var actor = Actor();
        TargetIsMemberOf(dept);
        TargetHolds((ScopeType.Department, dept, ManagementPermission.ResetPasswords));

        Assert.True(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.ResetPasswords));
    }

    [Fact]
    public async Task CanManageUser_DepartmentAdmin_TargetHoldsRoleInDescendant_ReturnsTrue()
    {
        var dept = Guid.NewGuid();
        var child = Guid.NewGuid();
        _departmentRepo.Setup(d => d.GetDescendantIdsAsync(dept)).ReturnsAsync(new HashSet<Guid> { child });
        GrantScoped(ScopeType.Department, dept, ManagementPermission.DepartmentAdmin);
        var actor = Actor();
        TargetIsMemberOf(dept);
        TargetHolds((ScopeType.Department, child, ManagementPermission.EditUserProfiles));

        Assert.True(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.EditUserProfiles));
    }

    [Fact]
    public async Task CanManageUser_GlobalAdmin_TargetIsGlobalAdmin_ReturnsTrue()
    {
        var actor = Actor(RoleWith(ManagementPermission.FullAdmin));
        TargetHolds((ScopeType.Global, Guid.Empty, ManagementPermission.FullAdmin));

        Assert.True(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.ResetPasswords));
    }

    [Fact]
    public async Task CanManageUser_GlobalUserManager_TargetIsGlobalAdmin_ReturnsFalse()
    {
        var actor = Actor(RoleWith(ManagementPermission.UserAdmin));
        TargetHolds((ScopeType.Global, Guid.Empty, ManagementPermission.FullAdmin));

        Assert.False(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.ResetPasswords));
    }

    [Fact]
    public async Task CanManageUser_TargetIsGlobalAdminThroughGroup_ReturnsFalse()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept, ManagementPermission.DepartmentAdmin);
        var actor = Actor();
        TargetIsMemberOf(dept);

        var adminGroup = new Group(Guid.NewGuid(), "Admins");
        _userRepo.Setup(u => u.GetGroupsForUserAsync(_targetId)).ReturnsAsync(new List<Group> { adminGroup });
        var adminRole = RoleWith(ManagementPermission.FullAdmin);
        _roleRepo.Setup(r => r.GetByIdAsync(adminRole.Id)).ReturnsAsync(adminRole);
        _assignmentRepo
            .Setup(r => r.GetEffectiveAssignmentsAsync(_targetId,
                It.Is<IEnumerable<Guid>>(ids => ids.Contains(adminGroup.Id))))
            .ReturnsAsync(new List<ScopedRoleAssignment>
            {
                new(adminGroup.Id, adminRole.Id, ScopeType.Global, Guid.Empty)
            });

        Assert.False(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.ResetPasswords));
    }

    // ═══════════════════ Delete needs every department ═══════════════════

    [Fact]
    public async Task CanManageUser_Delete_TargetAlsoInForeignDepartment_ReturnsFalse()
    {
        var own = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        GrantScoped(ScopeType.Department, own, ManagementPermission.UserAdmin);
        var actor = Actor();
        TargetIsMemberOf(own);
        TargetDepartments(own, foreign);

        Assert.False(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.DeleteUsers));
        // Editing still only needs one department in scope.
        Assert.True(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.EditUserProfiles));
    }

    [Fact]
    public async Task CanManageUser_Delete_ParentCoversEveryDepartment_ReturnsTrue()
    {
        var parent = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        _departmentRepo.Setup(d => d.GetDescendantIdsAsync(parent)).ReturnsAsync(new HashSet<Guid> { a, b });
        GrantScoped(ScopeType.Department, parent, ManagementPermission.UserAdmin);
        var actor = Actor();
        TargetIsMemberOf(parent);
        TargetDepartments(a, b);

        Assert.True(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.DeleteUsers));
    }

    [Fact]
    public async Task CanManageUser_Delete_GlobalActor_TargetWithoutDepartment_ReturnsTrue()
    {
        var actor = Actor(RoleWith(ManagementPermission.UserAdmin));

        Assert.True(await _sut.CanManageUserAsync(actor, _targetId, ManagementPermission.DeleteUsers));
    }

    // ═══════════════════ Add user to department ═══════════════════

    [Fact]
    public async Task CanAddUserToDepartment_WithoutAssignDepartments_ReturnsFalse()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept,
            ManagementPermission.EditDepartment | ManagementPermission.EditUserProfiles);
        var actor = Actor();
        TargetIsMemberOf(dept);

        Assert.False(await _sut.CanAddUserToDepartmentAsync(actor, _targetId, dept));
    }

    [Fact]
    public async Task CanAddUserToDepartment_UserOutsideActorsReach_ReturnsFalse()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept, ManagementPermission.AssignDepartments);
        var actor = Actor();

        Assert.False(await _sut.CanAddUserToDepartmentAsync(actor, _targetId, dept));
    }

    [Fact]
    public async Task CanAddUserToDepartment_UserInDescendant_ReturnsTrue()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept, ManagementPermission.AssignDepartments);
        var actor = Actor();
        TargetIsMemberOf(dept); // via a descendant department

        Assert.True(await _sut.CanAddUserToDepartmentAsync(actor, _targetId, dept));
    }

    [Fact]
    public async Task CanAddUserToDepartment_ReachableUserOutranksActor_ReturnsFalse()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept, ManagementPermission.AssignDepartments);
        var actor = Actor();
        TargetIsMemberOf(dept);
        TargetHolds((ScopeType.Global, Guid.Empty, ManagementPermission.FullAdmin));

        Assert.False(await _sut.CanAddUserToDepartmentAsync(actor, _targetId, dept));
    }

    [Fact]
    public async Task CanAddUserToDepartment_GlobalActor_AnyUser_ReturnsTrue()
    {
        var actor = Actor(RoleWith(ManagementPermission.FullAdmin));

        Assert.True(await _sut.CanAddUserToDepartmentAsync(actor, _targetId, Guid.NewGuid()));
    }

    // ═══════════════════ OutranksUserAsync (rank only, no department scope) ═══════════════════

    [Fact]
    public async Task OutranksUser_EqualGlobalAdmins_ReturnsTrue()
    {
        var actor = Actor(RoleWith(ManagementPermission.FullAdmin));
        TargetHolds((ScopeType.Global, Guid.Empty, ManagementPermission.FullAdmin));

        Assert.True(await _sut.OutranksUserAsync(actor, _targetId));
    }

    [Fact]
    public async Task OutranksUser_SupersetInSameDepartment_ReturnsTrue_WithoutMembership()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept, ManagementPermission.DepartmentAdmin);
        var actor = Actor();
        TargetHolds((ScopeType.Department, dept, ManagementPermission.ResetPasswords));

        Assert.True(await _sut.OutranksUserAsync(actor, _targetId));
    }

    [Fact]
    public async Task OutranksUser_TargetHoldsBroaderRole_ReturnsFalse()
    {
        var actor = Actor(RoleWith(ManagementPermission.UserAdmin));
        TargetHolds((ScopeType.Global, Guid.Empty, ManagementPermission.FullAdmin));

        Assert.False(await _sut.OutranksUserAsync(actor, _targetId));
    }

    [Fact]
    public async Task OutranksUser_TargetWithoutAssignments_ReturnsTrue()
    {
        var actor = Actor();
        TargetHolds();

        Assert.True(await _sut.OutranksUserAsync(actor, _targetId));
    }

    // ═══════════════════ Presets ═══════════════════

    [Fact]
    public void DepartmentAdminPreset_IncludesAssignDepartments()
        => Assert.True(ManagementPermission.DepartmentAdmin.HasFlag(ManagementPermission.AssignDepartments));
}
