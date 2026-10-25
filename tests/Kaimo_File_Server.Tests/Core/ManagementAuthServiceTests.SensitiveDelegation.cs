using Kaimo_File_Server.Core.Security;
using Xunit;

namespace Kaimo_File_Server.Tests;

/// <summary>
/// Sensitive permissions (<see cref="ManagementPermission.AccessHomeContents"/>) can only be
/// passed on by actors who hold them system-wide: a department-scoped holder may use the right
/// but never delegate it, not even within its own department.
/// </summary>
public partial class ManagementAuthServiceTests
{
    [Fact]
    public async Task CanAssignRole_SensitiveBit_DepartmentScopedHolder_CannotDelegate()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept,
            ManagementPermission.AssignRoles | ManagementPermission.AccessHomeContents);

        Assert.False(await _sut.CanAssignRoleAsync(
            Actor(), ManagementPermission.AccessHomeContents, ScopeType.Department, dept));
    }

    [Fact]
    public async Task CanAssignRole_SensitiveBit_GlobalHolder_CanDelegateToDepartment()
    {
        var ok = await _sut.CanAssignRoleAsync(
            Actor(RoleWith(ManagementPermission.AssignRoles | ManagementPermission.AccessHomeContents)),
            ManagementPermission.AccessHomeContents, ScopeType.Department, Guid.NewGuid());

        Assert.True(ok);
    }

    [Fact]
    public async Task CanAssignRole_SensitiveBit_GlobalAssignRolesButBitOnlyInDepartment_CannotDelegate()
    {
        var dept = Guid.NewGuid();
        GrantMany(
            (ScopeType.Global, Guid.Empty, ManagementPermission.AssignRoles),
            (ScopeType.Department, dept, ManagementPermission.AccessHomeContents));

        Assert.False(await _sut.CanAssignRoleAsync(
            Actor(), ManagementPermission.AccessHomeContents, ScopeType.Department, dept));
    }

    [Fact]
    public async Task CanAssignRole_NonSensitiveBits_StayDelegableByDepartmentHolders()
    {
        var dept = Guid.NewGuid();
        GrantScoped(ScopeType.Department, dept,
            ManagementPermission.AssignRoles | ManagementPermission.AccessHomeContents | ManagementPermission.UserAdmin);

        Assert.True(await _sut.CanAssignRoleAsync(
            Actor(), ManagementPermission.UserAdmin, ScopeType.Department, dept));
    }
}
