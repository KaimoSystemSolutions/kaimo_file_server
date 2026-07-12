using Kaimo_File_Server.Core.Domain;
using Kaimo_File_Server.Core.Domain.Identity;
using Kaimo_File_Server.Core.Repositories;
using Kaimo_File_Server.Core.Security;
using Kaimo_File_Server.Core.Services;
using Moq;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Characterization tests for <see cref="ManagementAuthService"/> — the scoped
/// delegated-administration gate. They pin the two authority sources (direct roles →
/// Global scope, scoped assignments → Department/Share) and the scope resolution for
/// user, group, share and department management, so the shared-helper refactor cannot
/// change behaviour silently.
/// </summary>
public class ManagementAuthServiceTests
{
    private readonly Mock<IScopedRoleAssignmentRepository> _assignmentRepo = new();
    private readonly Mock<IDepartmentRepository> _departmentRepo = new();
    private readonly Mock<IRoleRepository> _roleRepo = new();
    private readonly Mock<IGroupRepository> _groupRepo = new();
    private readonly Mock<IShareRepository> _shareRepo = new();

    private readonly Guid _actorId = Guid.NewGuid();
    private readonly ManagementAuthService _sut;

    public ManagementAuthServiceTests()
    {
        // No scoped assignments and no descendants unless a test opts in.
        _assignmentRepo
            .Setup(r => r.GetEffectiveAssignmentsAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new List<ScopedRoleAssignment>());
        _departmentRepo
            .Setup(d => d.GetDescendantIdsAsync(It.IsAny<Guid>()))
            .ReturnsAsync(new HashSet<Guid>());

        _sut = new ManagementAuthService(
            _assignmentRepo.Object, _departmentRepo.Object, _roleRepo.Object,
            _groupRepo.Object, _shareRepo.Object);
    }

    // -- Helpers --

    private UserContext Actor(params Role[] directRoles)
        => new(new User(_actorId, "Actor", "actor", "h", "n"),
               new HashSet<Group>(), new HashSet<Role>(directRoles), new HashSet<string>());

    private static Role RoleWith(ManagementPermission perms)
        => new(Guid.NewGuid(), "Role", perms);

    /// <summary>Grants the actor a scoped (non-global) assignment carrying <paramref name="perms"/>.</summary>
    private void GrantScoped(ScopeType scopeType, Guid scopeId, ManagementPermission perms)
    {
        var roleId = Guid.NewGuid();
        _assignmentRepo
            .Setup(r => r.GetEffectiveAssignmentsAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new List<ScopedRoleAssignment>
            {
                new(_actorId, roleId, scopeType, scopeId)
            });
        _roleRepo.Setup(r => r.GetByIdAsync(roleId)).ReturnsAsync(new Role(roleId, "Scoped", perms));
    }

    // ═══════════════════ Share ═══════════════════

    [Fact]
    public async Task CanManageShare_GlobalDirectRole_ReturnsTrue()
    {
        var share = new ShareDefinition("s", "/s");
        _shareRepo.Setup(r => r.GetByIdAsync(share.Id)).ReturnsAsync(share);

        var ok = await _sut.CanManageShareAsync(
            Actor(RoleWith(ManagementPermission.ManageShareAcls)),
            share.Id, ManagementPermission.ManageShareAcls);

        Assert.True(ok);
    }

    [Fact]
    public async Task CanManageShare_WithoutRequiredPermission_ReturnsFalse()
    {
        var share = new ShareDefinition("s", "/s");
        _shareRepo.Setup(r => r.GetByIdAsync(share.Id)).ReturnsAsync(share);

        var ok = await _sut.CanManageShareAsync(
            Actor(RoleWith(ManagementPermission.CreateUsers)),   // unrelated permission
            share.Id, ManagementPermission.ManageShareAcls);

        Assert.False(ok);
    }

    [Fact]
    public async Task CanManageShare_ShareScopedToThisShare_ReturnsTrue()
    {
        var share = new ShareDefinition("s", "/s");
        _shareRepo.Setup(r => r.GetByIdAsync(share.Id)).ReturnsAsync(share);
        GrantScoped(ScopeType.Share, share.Id, ManagementPermission.ManageShareAcls);

        var ok = await _sut.CanManageShareAsync(
            Actor(), share.Id, ManagementPermission.ManageShareAcls);

        Assert.True(ok);
    }

    [Fact]
    public async Task CanManageShare_ShareScopedToOtherShare_ReturnsFalse()
    {
        var share = new ShareDefinition("s", "/s");
        _shareRepo.Setup(r => r.GetByIdAsync(share.Id)).ReturnsAsync(share);
        GrantScoped(ScopeType.Share, Guid.NewGuid(), ManagementPermission.ManageShareAcls);

        var ok = await _sut.CanManageShareAsync(
            Actor(), share.Id, ManagementPermission.ManageShareAcls);

        Assert.False(ok);
    }

    [Fact]
    public async Task CanManageShare_DepartmentScopeOwningShare_ReturnsTrue()
    {
        var deptId = Guid.NewGuid();
        var share = new ShareDefinition("s", "/s", departmentId: deptId);
        _shareRepo.Setup(r => r.GetByIdAsync(share.Id)).ReturnsAsync(share);
        GrantScoped(ScopeType.Department, deptId, ManagementPermission.ManageShareAcls);

        var ok = await _sut.CanManageShareAsync(
            Actor(), share.Id, ManagementPermission.ManageShareAcls);

        Assert.True(ok);
    }

    [Fact]
    public async Task CanManageShare_DepartmentScopeAncestorOfShareDept_ReturnsTrue()
    {
        var parentDept = Guid.NewGuid();
        var childDept = Guid.NewGuid();
        var share = new ShareDefinition("s", "/s", departmentId: childDept);
        _shareRepo.Setup(r => r.GetByIdAsync(share.Id)).ReturnsAsync(share);
        _departmentRepo.Setup(d => d.GetDescendantIdsAsync(parentDept))
            .ReturnsAsync(new HashSet<Guid> { childDept });
        GrantScoped(ScopeType.Department, parentDept, ManagementPermission.ManageShareAcls);

        var ok = await _sut.CanManageShareAsync(
            Actor(), share.Id, ManagementPermission.ManageShareAcls);

        Assert.True(ok);
    }

    [Fact]
    public async Task CanManageShare_DepartmentScopeUnrelated_ReturnsFalse()
    {
        var share = new ShareDefinition("s", "/s", departmentId: Guid.NewGuid());
        _shareRepo.Setup(r => r.GetByIdAsync(share.Id)).ReturnsAsync(share);
        GrantScoped(ScopeType.Department, Guid.NewGuid(), ManagementPermission.ManageShareAcls);

        var ok = await _sut.CanManageShareAsync(
            Actor(), share.Id, ManagementPermission.ManageShareAcls);

        Assert.False(ok);
    }

    [Fact]
    public async Task CanManageShare_ShareNotFound_ReturnsFalse()
    {
        _shareRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((ShareDefinition?)null);

        var ok = await _sut.CanManageShareAsync(
            Actor(RoleWith(ManagementPermission.ManageShareAcls)),
            Guid.NewGuid(), ManagementPermission.ManageShareAcls);

        Assert.False(ok);
    }

    // ═══════════════════ Group ═══════════════════

    [Fact]
    public async Task CanManageGroup_GroupNotFound_ReturnsFalse()
    {
        _groupRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((Group?)null);

        var ok = await _sut.CanManageGroupAsync(
            Actor(RoleWith(ManagementPermission.ManageGroupMembers)),
            Guid.NewGuid(), ManagementPermission.ManageGroupMembers);

        Assert.False(ok);
    }

    [Fact]
    public async Task CanManageGroup_DepartmentScopeOwningGroup_ReturnsTrue()
    {
        var deptId = Guid.NewGuid();
        var group = new Group(Guid.NewGuid(), "g", deptId);
        _groupRepo.Setup(r => r.GetByIdAsync(group.Id)).ReturnsAsync(group);
        GrantScoped(ScopeType.Department, deptId, ManagementPermission.ManageGroupMembers);

        var ok = await _sut.CanManageGroupAsync(
            Actor(), group.Id, ManagementPermission.ManageGroupMembers);

        Assert.True(ok);
    }

    [Fact]
    public async Task CanManageGroup_ShareScopeAssignment_DoesNotGrant()
    {
        var group = new Group(Guid.NewGuid(), "g", Guid.NewGuid());
        _groupRepo.Setup(r => r.GetByIdAsync(group.Id)).ReturnsAsync(group);
        GrantScoped(ScopeType.Share, Guid.NewGuid(), ManagementPermission.ManageGroupMembers);

        var ok = await _sut.CanManageGroupAsync(
            Actor(), group.Id, ManagementPermission.ManageGroupMembers);

        Assert.False(ok);
    }

    // ═══════════════════ User ═══════════════════

    [Fact]
    public async Task CanManageUser_GlobalDirectRole_ReturnsTrue()
    {
        var ok = await _sut.CanManageUserAsync(
            Actor(RoleWith(ManagementPermission.EditUserProfiles)),
            Guid.NewGuid(), ManagementPermission.EditUserProfiles);

        Assert.True(ok);
    }

    [Fact]
    public async Task CanManageUser_DepartmentScope_UserInDepartment_ReturnsTrue()
    {
        var deptId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        GrantScoped(ScopeType.Department, deptId, ManagementPermission.EditUserProfiles);
        _departmentRepo.Setup(d => d.IsUserInDepartmentOrDescendantAsync(targetUserId, deptId))
            .ReturnsAsync(true);

        var ok = await _sut.CanManageUserAsync(
            Actor(), targetUserId, ManagementPermission.EditUserProfiles);

        Assert.True(ok);
    }

    [Fact]
    public async Task CanManageUser_DepartmentScope_UserOutsideDepartment_ReturnsFalse()
    {
        var deptId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        GrantScoped(ScopeType.Department, deptId, ManagementPermission.EditUserProfiles);
        _departmentRepo.Setup(d => d.IsUserInDepartmentOrDescendantAsync(It.IsAny<Guid>(), It.IsAny<Guid>()))
            .ReturnsAsync(false);

        var ok = await _sut.CanManageUserAsync(
            Actor(), targetUserId, ManagementPermission.EditUserProfiles);

        Assert.False(ok);
    }

    // ═══════════════════ Department ═══════════════════

    [Fact]
    public async Task CanManageDepartment_DepartmentScopeSelf_ReturnsTrue()
    {
        var deptId = Guid.NewGuid();
        GrantScoped(ScopeType.Department, deptId, ManagementPermission.EditDepartment);

        var ok = await _sut.CanManageDepartmentAsync(
            Actor(), deptId, ManagementPermission.EditDepartment);

        Assert.True(ok);
    }

    [Fact]
    public async Task CanManageDepartment_DepartmentScopeDescendant_ReturnsTrue()
    {
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        GrantScoped(ScopeType.Department, parent, ManagementPermission.EditDepartment);
        _departmentRepo.Setup(d => d.GetDescendantIdsAsync(parent))
            .ReturnsAsync(new HashSet<Guid> { child });

        var ok = await _sut.CanManageDepartmentAsync(
            Actor(), child, ManagementPermission.EditDepartment);

        Assert.True(ok);
    }

    [Fact]
    public async Task CanManageDepartment_OutOfScope_ReturnsFalse()
    {
        GrantScoped(ScopeType.Department, Guid.NewGuid(), ManagementPermission.EditDepartment);

        var ok = await _sut.CanManageDepartmentAsync(
            Actor(), Guid.NewGuid(), ManagementPermission.EditDepartment);

        Assert.False(ok);
    }

    // ═══════════════════ Create user in department ═══════════════════

    [Fact]
    public async Task CanCreateUserInDepartment_GlobalDirectRole_ReturnsTrue()
    {
        var ok = await _sut.CanCreateUserInDepartmentAsync(
            Actor(RoleWith(ManagementPermission.CreateUsers)), Guid.NewGuid());

        Assert.True(ok);
    }

    [Fact]
    public async Task CanCreateUserInDepartment_DepartmentScopeDescendant_ReturnsTrue()
    {
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        GrantScoped(ScopeType.Department, parent, ManagementPermission.CreateUsers);
        _departmentRepo.Setup(d => d.GetDescendantIdsAsync(parent))
            .ReturnsAsync(new HashSet<Guid> { child });

        var ok = await _sut.CanCreateUserInDepartmentAsync(Actor(), child);

        Assert.True(ok);
    }

    [Fact]
    public async Task CanCreateUserInDepartment_OutOfScope_ReturnsFalse()
    {
        GrantScoped(ScopeType.Department, Guid.NewGuid(), ManagementPermission.CreateUsers);

        var ok = await _sut.CanCreateUserInDepartmentAsync(Actor(), Guid.NewGuid());

        Assert.False(ok);
    }

    // ═══════════════════ Direct vs. delegated (same role) ═══════════════════

    [Fact]
    public async Task DirectGlobalRole_PlusScopedAssignmentOfSameRole_GlobalWins()
    {
        // A role assigned BOTH directly (user_roles → Global scope) AND as a
        // department-scoped assignment. Effective authority is the UNION, and the
        // direct grant already covers everything, so the result is Global.
        // Adding a department scope to a role the user ALSO holds directly does NOT
        // narrow it — the common mental-model trap in the assignment UI. The dedup in
        // GetEffectiveAssignmentsAsync drops the scoped copy in favour of the Global one.
        var roleId = Guid.NewGuid();
        var role = new Role(roleId, "DeptEditor", ManagementPermission.EditDepartment);
        var scopedDept = Guid.NewGuid();
        var otherDept = Guid.NewGuid();

        _assignmentRepo
            .Setup(r => r.GetEffectiveAssignmentsAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new List<ScopedRoleAssignment>
            {
                new(_actorId, roleId, ScopeType.Department, scopedDept)
            });
        _roleRepo.Setup(r => r.GetByIdAsync(roleId)).ReturnsAsync(role);

        // Same role held directly → synthetic Global scope.
        var actor = Actor(role);

        // A department OUTSIDE the scoped one is still manageable (Global wins).
        var ok = await _sut.CanManageDepartmentAsync(
            actor, otherDept, ManagementPermission.EditDepartment);

        Assert.True(ok);
    }

    [Fact]
    public async Task ScopedAssignmentOnly_WithoutDirectRole_RestrictsToScope()
    {
        // The counterpart: the SAME role held ONLY as a department-scoped assignment
        // (no direct grant) genuinely restricts to that department + descendants.
        var roleId = Guid.NewGuid();
        var role = new Role(roleId, "DeptEditor", ManagementPermission.EditDepartment);
        var scopedDept = Guid.NewGuid();
        var otherDept = Guid.NewGuid();

        _assignmentRepo
            .Setup(r => r.GetEffectiveAssignmentsAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<Guid>>()))
            .ReturnsAsync(new List<ScopedRoleAssignment>
            {
                new(_actorId, roleId, ScopeType.Department, scopedDept)
            });
        _roleRepo.Setup(r => r.GetByIdAsync(roleId)).ReturnsAsync(role);

        var actor = Actor(); // no direct roles

        Assert.True(await _sut.CanManageDepartmentAsync(
            actor, scopedDept, ManagementPermission.EditDepartment));
        Assert.False(await _sut.CanManageDepartmentAsync(
            actor, otherDept, ManagementPermission.EditDepartment));
    }
}
